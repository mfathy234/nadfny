using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nadfny.Features.Caches;

public sealed record TempGroup(string Id, string Pattern, string Kind, int Count, long Bytes, int RecentCount, long RecentBytes);
public sealed record TempOther(int Groups, int Count, long Bytes, long RecentBytes);

public sealed record TempBreakdown(string Path, IReadOnlyList<TempGroup> Groups, TempOther? Other,
    IReadOnlyList<TempGroup> Kept, IReadOnlyList<string> KeepList)
{
    /// <summary>Every clearable group, for saving to history. Not sent to the UI.</summary>
    [JsonIgnore] public IReadOnlyList<TempGroup> All { get; init; } = [];
}

/// <summary>Turns a Temp entry name into the pattern it is grouped under: random suffixes become "*", loose files group by extension.</summary>
public static partial class TempGroupName
{
    [GeneratedRegex("([-_.]+)")]
    private static partial Regex Separators();

    /// <summary>Folders whose whole name is random ("3KmkaCDwb97F7dLkxohtX9LTBpd"): one group, never a keep pattern.</summary>
    public const string RandomNames = "::random-names";

    public static string Of(string name, bool isDir)
    {
        if (!isDir && Path.GetExtension(name) is { Length: > 1 } ext) return "*" + ext.ToLowerInvariant();
        return IsRandomName(name) ? RandomNames : Pattern(name);
    }

    // Long, letters and digits only, with digits: a generated id, not a program's name.
    private static bool IsRandomName(string name) =>
        name.Length >= 16 && name.All(char.IsAsciiLetterOrDigit) && name.Any(char.IsAsciiDigit)
        && (name.All(char.IsAsciiHexDigit) || (name.Any(char.IsAsciiLetterUpper) && name.Any(char.IsAsciiLetterLower)));

    private static string Pattern(string name)
    {
        var parts = Separators().Split(name);
        var result = new StringBuilder();
        var separator = "";
        var endsWithStar = false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) { separator = parts[i]; continue; }
            var token = parts[i];
            if (token.Length == 0) continue;
            // The first token is the program's own name, so it is always kept.
            if (result.Length > 0 && IsRandom(token))
            {
                if (!endsWithStar) result.Append(separator).Append('*');
                endsWithStar = true;
            }
            else
            {
                result.Append(separator).Append(token);
                endsWithStar = false;
            }
            separator = "";
        }
        return result.Append(separator).ToString();
    }

    private static bool IsRandom(string token)
    {
        if (token.All(char.IsAsciiDigit)) return true;
        if (!token.All(char.IsAsciiLetterOrDigit)) return false;
        return (token.Length >= 4 && token.Any(char.IsAsciiDigit)) || (token.Length >= 8 && token.All(char.IsAsciiHexDigit));
    }
}
