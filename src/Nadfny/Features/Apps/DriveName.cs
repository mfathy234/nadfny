using System.Collections.Concurrent;

namespace Nadfny.Features.Apps;

/// <summary>Names the drive an app is installed on the way Explorer does: "Games (D:)", or "Local Disk (C:)" without a label.</summary>
public sealed class DriveName
{
    private readonly Func<string, string?> _readLabel;
    // Lazy: parallel scan workers asking about the same slow drive wait for one read instead of each probing it.
    private readonly ConcurrentDictionary<string, Lazy<string>> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="readLabel">Reads a drive's volume label from its root (tests pass a fake).</param>
    public DriveName(Func<string, string?>? readLabel = null) => _readLabel = readLabel ?? ReadVolumeLabel;

    /// <summary>The drive's display name, or null when the path is not on a local drive letter.</summary>
    public string? For(string? path) => RootOf(path) is { } root ? _names.GetOrAdd(root, r => new Lazy<string>(() => Describe(r))).Value : null;

    public static string Format(string root, string? label)
    {
        var letter = root[..2].ToUpperInvariant();
        return string.IsNullOrWhiteSpace(label) ? $"Local Disk ({letter})" : $"{label.Trim()} ({letter})";
    }

    public static string? RootOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return null;
        return char.ToUpperInvariant(path[0]) + @":\";
    }

    private string Describe(string root)
    {
        try { return Format(root, _readLabel(root)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return root[..2]; }
    }

    private static string? ReadVolumeLabel(string root)
    {
        var drive = new DriveInfo(root);
        // A disconnected network drive can block for seconds, and it is not a local disk: show just the letter.
        if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory || !drive.IsReady)
            throw new IOException($"{root} is not a ready local drive.");
        return drive.VolumeLabel;
    }
}
