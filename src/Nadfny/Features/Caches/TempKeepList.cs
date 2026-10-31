using System.Text.RegularExpressions;

namespace Nadfny.Features.Caches;

/// <summary>Folder-name patterns the user wants Temp clearing to leave alone. Only "*" is a wildcard; matching ignores case.</summary>
public static class TempKeepList
{
    public const int MaxEntries = 200;
    public const int MaxLength = 100;

    private static readonly char[] InvalidChars = ['\\', '/', ':', '<', '>', '"', '|', '?', '\0'];

    /// <summary>The trimmed pattern, or an ArgumentException with a message fit to show the user.</summary>
    public static string Normalize(string? pattern)
    {
        var text = pattern?.Trim() ?? "";
        if (text.Length == 0) throw new ArgumentException("Type a folder name to keep.");
        if (text.Length > MaxLength) throw new ArgumentException($"Keep names can be up to {MaxLength} characters.");
        if (text.IndexOfAny(InvalidChars) >= 0 || text.Any(char.IsControl))
            throw new ArgumentException("Use a folder or file name only, with no slashes or special characters. Use * for any text.");
        return text;
    }

    public static IReadOnlyList<string> Add(IReadOnlyList<string> current, string? pattern)
    {
        var text = Normalize(pattern);
        if (current.Contains(text, StringComparer.OrdinalIgnoreCase)) return current;
        if (current.Count >= MaxEntries) throw new ArgumentException($"The keep list is full ({MaxEntries} names). Remove one first.");
        return [.. current, text];
    }

    public static IReadOnlyList<string> Remove(IReadOnlyList<string> current, string? pattern) =>
        current.Where(p => !p.Equals(pattern?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Drops anything a hand-edited settings file holds that would not pass validation.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? loaded)
    {
        IReadOnlyList<string> result = [];
        foreach (var item in loaded ?? [])
        {
            try { result = Add(result, item); }
            catch (ArgumentException) { /* skip invalid entries */ }
        }
        return result;
    }

    public static bool Matches(string pattern, string name) => Compile([pattern])(name);

    /// <summary>Builds the patterns once so thousands of names can be tested cheaply.</summary>
    public static Func<string, bool> Compile(IReadOnlyList<string> patterns)
    {
        var regexes = patterns
            // Non-backtracking: a pattern like a*a*a*…b cannot make matching slow.
            .Select(p => new Regex("^" + string.Join(".*", Regex.Replace(p, @"\*+", "*").Split('*').Select(Regex.Escape)) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking))
            .ToArray();
        return name => regexes.Any(r => r.IsMatch(name));
    }
}
