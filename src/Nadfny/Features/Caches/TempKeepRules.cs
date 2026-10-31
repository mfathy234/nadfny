using System.Text.RegularExpressions;

namespace Nadfny.Features.Caches;

/// <summary>Folders in Temp that clearing leaves alone because an AI tool keeps sessions, downloads or scratch files there.</summary>
public static partial class TempKeepRules
{
    /// <summary>Names shown to the user when explaining what is kept.</summary>
    public const string AiToolNames = "Claude, Codex, Cursor, Copilot, Ollama, LM Studio";

    // The tool name, then a separator, a digit or the end: "claude", "ClaudeCode", "codex-trial", "ollama2048" but not "claudette".
    [GeneratedRegex(@"^(claude(-?code)?|codex|cursor|copilot|github-copilot|openai|anthropic|ollama|lm-?studio|gemini|chatgpt|windsurf|aider)([-_. ]|\d|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiToolPattern();

    public static bool IsAiToolFolder(string name) => AiToolPattern().IsMatch(name);

    /// <summary>Nadfny's own downloaded update: it waits here until the user chooses Restart to update.</summary>
    public const string UpdateFolder = "Nadfny-update";

    public static bool IsNadfnyFolder(string name) => name.Equals(UpdateFolder, StringComparison.OrdinalIgnoreCase);
}
