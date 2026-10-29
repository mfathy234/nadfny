namespace Nadfny.Core;

/// <summary>Live tally of what a delete has removed so far; read from another thread for progress.</summary>
public sealed class DeleteCounter
{
    private long _bytes, _files;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Files => Interlocked.Read(ref _files);
    internal void Add(long bytes) { Interlocked.Add(ref _bytes, bytes); Interlocked.Increment(ref _files); }
}

/// <summary>Deletes directory trees quickly and robustly (read-only files, long paths, parallel subtrees).</summary>
public static class SafeDeleter
{
    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    public static void DeletePermanently(string path, CancellationToken ct = default, DeleteCounter? counter = null)
    {
        EnsureDeletable(path);
        if (File.Exists(path))
        {
            var length = new FileInfo(path).Length;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            counter?.Add(length);
            return;
        }
        if (!Directory.Exists(path)) return;
        DeleteTree(new DirectoryInfo(path), ct, counter);
    }

    /// <summary>
    /// Sends to the Recycle Bin via the shell. No second "are you sure" (the app already confirmed), but
    /// FOF_WANTNUKEWARNING makes Windows ask before it would delete permanently (item too big for the bin).
    /// </summary>
    public static void MoveToRecycleBin(string path)
    {
        EnsureDeletable(path);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var op = new ShFileOp
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING, // no FOF_SILENT: Windows shows progress for big folders
        };
        var rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) throw new OperationCanceledException("Cancelled. Nothing was deleted.");
        if (rc != 0) throw new IOException($"Windows could not move it to the Recycle Bin (error 0x{rc:X}).");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_WANTNUKEWARNING = 0x4000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOp op);

    /// <summary>
    /// Refuses drive roots, anything inside Windows / Program Files / ProgramData, and any protected
    /// folder (profile, Desktop, Documents, AppData...) or a parent of one, such as C:\Users.
    /// </summary>
    public static void EnsureDeletable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("No path given.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a drive root.");
        foreach (var p in ProtectedRoots)
        {
            if (IsUnder(p, full)) // the protected folder itself, or one of its parents
                throw new InvalidOperationException($"Refusing to delete a protected folder: {full}");
        }
        foreach (var tree in SystemTrees)
        {
            if (IsUnder(full, tree)) throw new InvalidOperationException($"Refusing to delete inside {tree}.");
        }
    }

    private static readonly string[] SystemTrees =
        new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A drive root keeps its separator ("C:\"), so it is already the prefix; anything else needs one appended.
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Workers per delete. Capped so several deletes at once never starve the thread pool (which would freeze
    /// progress updates and the UI); the SSD's metadata throughput is the real limit anyway.
    /// </summary>
    private static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 3, 2, 6);

    /// <summary>
    /// Flat, bounded delete: one walk lists every file, a fixed number of workers delete them, then folders are
    /// removed deepest-first. No nested parallel loops, so thread use stays constant however deep the tree is.
    /// </summary>
    private static void DeleteTree(DirectoryInfo root, CancellationToken ct, DeleteCounter? counter)
    {
        // Junctions/symlinks: remove the link, never the target.
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) { root.Delete(false); return; }

        var files = new List<FileInfo>();
        var dirs = new List<DirectoryInfo>(); // pre-order: parents before children
        var links = new List<DirectoryInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            dirs.Add(dir);
            FileSystemInfo[] children;
            try { children = dir.GetFileSystemInfos(); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var child in children)
            {
                if (child is FileInfo f) files.Add(f);
                else if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) links.Add((DirectoryInfo)child);
                else stack.Push((DirectoryInfo)child);
            }
        }

        Exception? firstError = null;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct }, f =>
        {
            try
            {
                var length = f.Length; // read before Delete(): deleting clears the cached size
                if ((f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    f.Attributes = FileAttributes.Normal;
                f.Delete();
                counter?.Add(length);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.CompareExchange(ref firstError, ex, null); // keep going; report the first cause (usually a locked file)
            }
        });

        foreach (var link in links) link.Delete(false);
        for (var i = dirs.Count - 1; i >= 0; i--)
        {
            var dir = dirs[i];
            try
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReadOnly)) dir.Attributes = FileAttributes.Directory;
                RetryOnce(() => dir.Delete(false));
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException ex) when (firstError is not null) { _ = ex; } // not empty because a file was locked
        }

        if (firstError is not null) throw new IOException(firstError.Message, firstError);
    }

    private static void RetryOnce(Action action)
    {
        try { action(); }
        catch (IOException)
        {
            Thread.Sleep(120); // AV scanners and indexers briefly hold handles
            action();
        }
    }

    private static string[] BuildProtectedRoots()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
        };
        return folders.Select(Environment.GetFolderPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.TrimEndingDirectorySeparator(p))
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")])
            .ToArray();
    }
}
