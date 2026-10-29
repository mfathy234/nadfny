using System.IO.Enumeration;

namespace Nadfny.Core;

/// <summary>Fast, allocation-light recursive size calculation. Skips junctions and symlinks so a tree is never counted twice.</summary>
public static class DirectorySizer
{
    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions ShallowOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

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
            (ref FileSystemEntry entry) => (entry.Length, entry.LastWriteTimeUtc.UtcTicks),
            RecursiveOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
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
                (ref FileSystemEntry entry) => (entry.IsDirectory, entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcTicks),
                ShallowOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
