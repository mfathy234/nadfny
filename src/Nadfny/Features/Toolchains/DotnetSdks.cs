using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nadfny.Features.Toolchains;

public sealed record DotnetSdk(string Version, string BasePath, int Major, int Minor, int Patch, bool Preview);
public sealed record SdkPins(IReadOnlySet<string> Versions, bool Complete);

public static class DotnetSdks
{
    private static readonly Regex SdkLine = new(@"^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?\s+\[(.+)\]$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj" };

    public static IReadOnlyList<DotnetSdk> Parse(string output)
    {
        var sdks = new List<DotnetSdk>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = SdkLine.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
                !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) continue;
            var version = string.Join(".", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) + match.Groups[4].Value;
            sdks.Add(new(version, match.Groups[5].Value, major, minor, patch, match.Groups[4].Success));
        }
        return sdks.Distinct().ToArray();
    }

    public static IReadOnlySet<string> OldVersions(IReadOnlyList<DotnetSdk> sdks, IReadOnlySet<string> pins)
    {
        var old = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var band in sdks.Where(s => !s.Preview).GroupBy(s => (s.Major, s.Minor, Band: s.Patch / 100)))
        {
            var newest = band.Max(s => s.Patch);
            foreach (var sdk in band.Where(s => s.Patch < newest && !pins.Contains(s.Version))) old.Add(sdk.Version);
        }
        return old;
    }

    public static SdkPins FindPins(IReadOnlyList<string> roots, CancellationToken ct, int maxDepth = 6)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        var pending = new Stack<(string Path, int Depth)>(roots.Select(r => (r, 0)));
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(entry.Path);
                if (!visited.Add(path)) continue;
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) { complete = false; continue; }
                var global = Path.Combine(path, "global.json");
                if (File.Exists(global))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(global), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (json.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version))
                    {
                        var value = version.GetString();
                        if (value is not null && Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)) versions.Add(value);
                        else complete = false;
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    if (Skipped.Contains(Path.GetFileName(child))) continue;
                    if (entry.Depth >= maxDepth) { complete = false; continue; }
                    pending.Push((child, entry.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            { complete = false; }
        }
        return new(versions, complete);
    }
}
