namespace Nadfny.Features.Apps;

/// <summary>
/// Minimal reader for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf):
/// quoted keys followed by a quoted value or a { block }. Keys are case-insensitive.
/// </summary>
public sealed class Vdf
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _values.Keys;

    public string? this[string key] => _values.TryGetValue(key, out var v) ? v as string : null;

    public Vdf? Block(string key) => _values.TryGetValue(key, out var v) ? v as Vdf : null;

    public IEnumerable<Vdf> Blocks => _values.Values.OfType<Vdf>();

    public static Vdf Parse(string text)
    {
        var pos = 0;
        return ReadBlock(text, ref pos);
    }

    private static Vdf ReadBlock(string text, ref int pos)
    {
        var block = new Vdf();
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key is null or "}") return block;
            var value = NextToken(text, ref pos);
            if (value is null) return block;
            if (value == "{") block._values[key] = ReadBlock(text, ref pos);
            else block._values[key] = value;
        }
    }

    // Returns a quoted string's contents, "{" or "}", or null at the end. Skips // comments.
    private static string? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c is '{' or '}') { pos++; return c.ToString(); }
            if (c == '"')
            {
                var sb = new System.Text.StringBuilder();
                pos++;
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length) { pos++; sb.Append(text[pos] == 'n' ? '\n' : text[pos]); }
                    else sb.Append(text[pos]);
                    pos++;
                }
                pos++;
                return sb.ToString();
            }
            // Unquoted token (rare): read to whitespace.
            var start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}')) pos++;
            return text[start..pos];
        }
        return null;
    }
}
