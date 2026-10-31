using System.IO.Enumeration;

namespace Nadfny.Core;

/// <summary>Fast, allocation-light recursive size calculation. Skips junctions and symlinks so a tree is never counted twice.</summary>
public static class DirectorySizer
{
    // Reparse points are not skipped wholesale: cloud placeholders (OneDrive Files On-Demand) are reparse *files* and
    // must still be listed. Linked *folders* (junctions, symlinks, mount points) are never entered.
    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions ShallowOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private static bool IsLinkedFolder(ref FileSystemEntry entry) => entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    public readonly record struct SizeResult(long Bytes, long Files, DateTime LastWriteUtc);

    /// <summary>Sizes a directory, fanning out across its immediate children in parallel.</summary>
    public static SizeResult Measure(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return new SizeResult(0, 0, DateTime.MinValue);

        long bytes = 0, files = 0, lastTicks = 0;
        var topLevel = new List<string>();

        foreach (var (isDir, full, length, write) in Shallow(path))
        {
            if (isDir) { topLevel.Add(full); continue; }
            bytes += length;
            files++;
            lastTicks = Math.Max(lastTicks, write);
        }

        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        try
        {
            Parallel.ForEach(topLevel, parallel, dir =>
            {
                var r = MeasureSequential(dir, ct);
                Interlocked.Add(ref bytes, r.Bytes);
                Interlocked.Add(ref files, r.Files);
                long ticks = r.LastWriteUtc.Ticks, seen;
                while (ticks > (seen = Interlocked.Read(ref lastTicks)) &&
                       Interlocked.CompareExchange(ref lastTicks, ticks, seen) != seen) { }
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }

        return new SizeResult(bytes, files, lastTicks == 0 ? DateTime.MinValue : new DateTime(lastTicks, DateTimeKind.Utc));
    }

    public static SizeResult MeasureSequential(string path, CancellationToken ct = default)
    {
        long bytes = 0, files = 0, lastTicks = 0;
        var e = new FileSystemEnumerable<(long Len, long Ticks)>(path,
            (ref FileSystemEntry entry) => (OnDisk(ref entry), entry.LastWriteTimeUtc.UtcTicks),
            RecursiveOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLinkedFolder(ref entry),
        };
        try
        {
            foreach (var (len, ticks) in e)
            {
                if ((++files & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                bytes += len;
                if (ticks > lastTicks) lastTicks = ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* vanished mid-scan */ }
        return new SizeResult(bytes, files, lastTicks == 0 ? DateTime.MinValue : new DateTime(lastTicks, DateTimeKind.Utc));
    }

    /// <summary>Immediate children of a directory: (isDirectory, fullPath, length, lastWriteTicks).</summary>
    public static IEnumerable<(bool IsDir, string Path, long Length, long WriteTicks)> Shallow(string path)
    {
        try
        {
            return new FileSystemEnumerable<(bool, string, long, long)>(path,
                (ref FileSystemEntry entry) => (entry.IsDirectory, entry.ToFullPath(), entry.IsDirectory ? 0 : OnDisk(ref entry), entry.LastWriteTimeUtc.UtcTicks),
                ShallowOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !IsLinkedFolder(ref entry),
            }.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Folder links (junctions, symlinks) directly inside a directory whose target no longer exists. Never follows them.</summary>
    public static IEnumerable<string> DanglingLinks(string path)
    {
        try
        {
            return new FileSystemEnumerable<string>(path,
                (ref FileSystemEntry entry) => entry.ToFullPath(),
                ShallowOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => IsLinkedFolder(ref entry),
            }.ToList().Where(IsDangling).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A junction or symlink to a folder that is gone.</summary>
    public static bool IsDangling(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            // Directory.Exists on the link itself says true even when its target is gone, so look at the final target.
            return dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && dir.ResolveLinkTarget(returnFinalTarget: true) is { Exists: false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Cloud placeholders: the file is listed, but its data lives online (FILE_ATTRIBUTE_RECALL_ON_OPEN / _ON_DATA_ACCESS).
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;
    private const FileAttributes Packed = FileAttributes.SparseFile | FileAttributes.Compressed;

    /// <summary>
    /// A file's size on disk. Sparse files (virtual disks, emulator images) and NTFS-compressed files report a logical
    /// length far above what they use, so those ask Windows for the allocated size; cloud-only files use nothing.
    /// </summary>
    private static long OnDisk(ref FileSystemEntry entry)
    {
        var attrs = entry.Attributes;
        if ((attrs & CloudOnly) != 0) return 0;
        return (attrs & Packed) == 0 ? entry.Length : Allocated(entry.ToFullPath(), entry.Length);
    }

    /// <inheritdoc cref="OnDisk(ref FileSystemEntry)"/>
    public static long OnDisk(FileInfo f)
    {
        var attrs = f.Attributes;
        if ((attrs & CloudOnly) != 0) return 0;
        return (attrs & Packed) == 0 ? f.Length : Allocated(f.FullName, f.Length);
    }

    private static long Allocated(string path, long fallback)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        return low == uint.MaxValue && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0 ? fallback : ((long)high << 32) | low;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);
}
