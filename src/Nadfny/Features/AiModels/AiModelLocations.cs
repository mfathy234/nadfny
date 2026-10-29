namespace Nadfny.Features.AiModels;

/// <summary>Where each local AI tool keeps its model files. Environment overrides count only when they are absolute.</summary>
public sealed record AiModelLocations(string OllamaModels, string HuggingFaceHub, IReadOnlyList<string> LmStudioRoots)
{
    public static AiModelLocations Current() => Resolve(Environment.GetEnvironmentVariable);

    public static AiModelLocations Resolve(Func<string, string?> environment)
    {
        var home = Absolute(environment("USERPROFILE")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hfHome = Absolute(environment("HF_HOME")) ?? Path.Combine(home, ".cache", "huggingface");
        return new(
            Absolute(environment("OLLAMA_MODELS")) ?? Path.Combine(home, ".ollama", "models"),
            Absolute(environment("HF_HUB_CACHE")) ?? Path.Combine(hfHome, "hub"),
            [Path.Combine(home, ".lmstudio", "models"), Path.Combine(home, ".cache", "lm-studio", "models")]);
    }

    private static string? Absolute(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed) ? null : Path.GetFullPath(trimmed);
    }
}
