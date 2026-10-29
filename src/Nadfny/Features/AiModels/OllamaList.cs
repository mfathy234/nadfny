using System.Globalization;
using System.Text.RegularExpressions;

namespace Nadfny.Features.AiModels;

public sealed record OllamaModel(string Name, string Id, long Bytes, string Modified);

/// <summary>Parses `ollama list` output. Names are checked against an allow-list before they reach `ollama rm`.</summary>
public static class OllamaList
{
    // NAME, ID, SIZE and MODIFIED columns. The header has no digits in the SIZE slot, so it never matches.
    private static readonly Regex Row = new(@"^(\S+)\s+(\S+)\s+(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)\s+(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Starts with a letter or digit so a name can never be read as a flag (-rf).
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<OllamaModel> Parse(string output)
    {
        var models = new List<OllamaModel>();
        foreach (var raw in output.Split('\n'))
        {
            var match = Row.Match(raw.Trim());
            if (!match.Success || !IsSafeName(match.Groups[1].Value)) continue;
            models.Add(new(match.Groups[1].Value, match.Groups[2].Value,
                ParseSize(match.Groups[3].Value, match.Groups[4].Value), match.Groups[5].Value.Trim()));
        }
        return models;
    }

    public static bool IsSafeName(string name) => SafeName.IsMatch(name);

    private static long ParseSize(string number, string unit)
    {
        var power = unit.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, "TB" => 4, _ => 0 };
        return (long)(double.Parse(number, CultureInfo.InvariantCulture) * Math.Pow(1024, power));
    }
}
