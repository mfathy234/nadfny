using System.IO.Enumeration;
using Nadfny.Core;

namespace Nadfny.Features.Finder;

public static class FinderRules
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "$Recycle.Bin"
    };
    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys"
    };

    public static bool SkipDirectory(string name) => SkippedDirectories.Contains(name);

    internal static bool SkipFile(string name, FileAttributes attributes) => SkippedFiles.Contains(name) ||
        (attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000)) != 0;

    public static string Type(string path, bool directory = false) => directory ? "Other" : Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" or ".msi" => "Installers",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" => "Archives",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" => "Videos",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".svg" or ".bmp" => "Images",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".xlsx" or ".xls" or ".ppt" or ".pptx" or ".csv" or ".md" => "Documents",
        _ => "Other"
    };

    internal static bool SafeScanPath(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (SkipDirectory(dir.Name) || (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)) return false;
        return true;
    }

    internal static IEnumerable<FinderFile> Enumerate(string root, long minimumLength, ScanProgress progress, CancellationToken ct)
    {
        if (!Directory.Exists(root) || !SafeScanPath(root)) yield break;
        var entries = new FileSystemEnumerable<FinderFile>(root,
            (ref FileSystemEntry e) => FromFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                ct.ThrowIfCancellationRequested();
                progress.Visit(e.ToFullPath());
                return (e.Attributes & FileAttributes.ReparsePoint) == 0 && !SkipDirectory(e.FileName.ToString());
            },
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                ct.ThrowIfCancellationRequested();
                return !e.IsDirectory && e.Length >= minimumLength && !SkipFile(e.FileName.ToString(), e.Attributes);
            }
        };
        using var iterator = entries.GetEnumerator();
        while (true)
        {
            FinderFile item;
            try
            {
                if (!iterator.MoveNext()) yield break;
                item = iterator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
            yield return item;
        }
    }

    internal static FinderFile FromFile(string path, long length, DateTime write) =>
        new(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", DirectorySizer.OnDisk(new FileInfo(path)), length, write, Type(path), false);
}
