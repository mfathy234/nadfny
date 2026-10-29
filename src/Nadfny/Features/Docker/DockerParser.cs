using System.Globalization;
using System.Text.Json;

namespace Nadfny.Features.Docker;

public sealed record DockerImage(string Id, string Repository, string Tag, long Bytes, long UniqueBytes, int Containers, string CreatedSince);
public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, long Bytes);
public sealed record DockerVolume(string Name, long Bytes, int Links, bool Anonymous);
public sealed record DockerCacheSummary(long Bytes, long ReclaimableBytes, int Entries);
public sealed record DockerUsage(
    IReadOnlyList<DockerImage> Images,
    IReadOnlyList<DockerContainer> Containers,
    IReadOnlyList<DockerVolume> Volumes,
    DockerCacheSummary BuildCache);

public static class DockerParser
{
    private static readonly (string Unit, double Factor)[] Units =
    [
        ("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("KB", 1e3), ("B", 1),
    ];

    /// <summary>Docker prints decimal (SI) sizes such as "1.304GB", "121.9kB", "0B".</summary>
    public static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim();
        var paren = s.IndexOf(' ');
        if (paren > 0) s = s[..paren]; // "4.899GB (28%)"
        foreach (var (unit, factor) in Units)
        {
            if (!s.EndsWith(unit, StringComparison.Ordinal)) continue;
            return double.TryParse(s[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? (long)Math.Round(v * factor)
                : 0;
        }
        return 0;
    }

    /// <summary>Parses <c>docker system df -v --format "{{json .}}"</c>.</summary>
    public static DockerUsage ParseVerboseDf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var images = Array(root, "Images").Select(i => new DockerImage(
            Str(i, "ID"), Str(i, "Repository"), Str(i, "Tag"),
            ParseSize(Str(i, "Size")), ParseSize(Str(i, "UniqueSize")),
            Int(i, "Containers"), Str(i, "CreatedSince"))).ToList();

        var containers = Array(root, "Containers").Select(c => new DockerContainer(
            Str(c, "ID"), Str(c, "Names"), Str(c, "Image"), Str(c, "State"), Str(c, "Status"),
            ParseSize(Str(c, "Size")))).ToList();

        var volumes = Array(root, "Volumes").Select(v => new DockerVolume(
            Str(v, "Name"), ParseSize(Str(v, "Size")), Int(v, "Links"),
            Str(v, "Labels").Contains("com.docker.volume.anonymous", StringComparison.Ordinal))).ToList();

        long cacheBytes = 0, reclaimable = 0;
        var entries = 0;
        foreach (var b in Array(root, "BuildCache"))
        {
            var size = ParseSize(Str(b, "Size"));
            entries++;
            if (Str(b, "Shared") == "true") continue; // shared layers are counted under images
            cacheBytes += size;
            if (Str(b, "InUse") != "true") reclaimable += size;
        }

        return new DockerUsage(images, containers, volumes, new DockerCacheSummary(cacheBytes, reclaimable, entries));
    }

    public sealed record SummaryRow(string Type, long Bytes, long ReclaimableBytes, int Total, int Active);

    /// <summary>Parses <c>docker system df --format "{{json .}}"</c> (one JSON object per line). These are Docker's own totals.</summary>
    public static IReadOnlyList<SummaryRow> ParseSummary(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('{'))
            .Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                var r = doc.RootElement;
                return new SummaryRow(Str(r, "Type"), ParseSize(Str(r, "Size")), ParseSize(Str(r, "Reclaimable")), Int(r, "TotalCount"), Int(r, "Active"));
            })
            .ToList();

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : [];

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        } : "";

    private static int Int(JsonElement el, string name) => int.TryParse(Str(el, name), out var n) ? n : 0;
}
