using System.Text.RegularExpressions;

namespace Nadfny.Features.Wsl;

/// <summary>Reads wsl.exe's text. Redirected output is UTF-16, which arrives decoded as UTF-8 with a NUL after each character.</summary>
public static class WslOutput
{
    private static readonly Regex VersionNumber = new(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);

    public static string Clean(string text) => text.Replace("\0", "").Trim();

    /// <summary>
    /// The "WSL version: 2.1.5.0" line. Localized Windows keeps "WSL" and the number, so the line is found by both.
    /// WSLg and kernel lines come after it and are skipped.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        foreach (var line in output.Replace("\0", "").Split('\n'))
        {
            if (!line.Contains("WSL", StringComparison.OrdinalIgnoreCase) || line.Contains("WSLg", StringComparison.OrdinalIgnoreCase)) continue;
            var number = VersionNumber.Match(line);
            if (number.Success && Version.TryParse(number.Value, out var version)) return version;
        }
        return null;
    }

    /// <summary>Automatic shrinking (wsl --manage --set-sparse) needs WSL 2.0 or newer.</summary>
    public static bool SupportsSparse(Version? version) => version is not null && version >= new Version(2, 0);
}
