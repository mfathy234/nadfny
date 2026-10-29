using System.Text.RegularExpressions;
using Nadfny.Core;

namespace Nadfny.Features.Wsl;

/// <summary>Turns registry entries into distributions and finds each WSL 2 distribution's disk file.</summary>
public static class WslDistroParser
{
    // Docker Desktop's own distributions are sized and compacted by the Docker section.
    private static readonly string[] DockerDistros = ["docker-desktop", "docker-desktop-data"];
    private static readonly Regex GuidId = new(@"^\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static IReadOnlyList<WslDistro> Parse(IEnumerable<LxssEntry> entries) =>
        entries
            .Where(e => GuidId.IsMatch(e.Id) && e.Version is 1 or 2)
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !DockerDistros.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => Describe(e, e.Version!.Value))
            .ToList();

    private static WslDistro Describe(LxssEntry e, int version)
    {
        var vhdx = version == 2 ? FindDiskFile(FolderOf(e.BasePath)) : null;
        if (vhdx is null) return new WslDistro(e.Id, e.Name!, version, null, 0, false);

        var file = new FileInfo(vhdx);
        return new WslDistro(e.Id, e.Name!, version, vhdx, DirectorySizer.OnDisk(file), (file.Attributes & FileAttributes.SparseFile) != 0);
    }

    /// <summary>BasePath can carry the \\?\ prefix; it is removed so the path can be used as a normal file path.</summary>
    internal static string? FolderOf(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath)) return null;
        var path = basePath.Trim();
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[ExtendedUncPrefix.Length..];
        else if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) path = path[ExtendedPrefix.Length..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>ext4.vhdx is the usual name. Otherwise the folder's only .vhdx is used; with several, none is guessed.</summary>
    internal static string? FindDiskFile(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return null;
        var ext4 = Path.Combine(folder, "ext4.vhdx");
        if (File.Exists(ext4)) return ext4;
        try
        {
            var others = Directory.EnumerateFiles(folder, "*.vhdx").Take(2).ToList();
            return others.Count == 1 ? others[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
