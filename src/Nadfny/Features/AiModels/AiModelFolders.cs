namespace Nadfny.Features.AiModels;

public sealed record AiModelFolder(string Source, string Name, string Kind, string Path, string Note, bool Recycle);

/// <summary>Model folders on disk: Hugging Face hub cache entries and LM Studio publisher/model folders.</summary>
internal static class AiModelFolders
{
    public const string HuggingFaceSource = "Hugging Face";
    public const string LmStudioSource = "LM Studio";
    private const string HuggingFaceNote = "Deleted permanently. Hugging Face downloads it again on next use.";
    private const string LmStudioNote = "Moved to the Recycle Bin. Emptying the Bin deletes it permanently.";

    public static IReadOnlyList<AiModelFolder> HuggingFace(string hub)
    {
        var items = new List<AiModelFolder>();
        foreach (var path in Children(hub))
        {
            if (TryParseHuggingFaceName(Path.GetFileName(path), out var kind, out var name))
                items.Add(new(HuggingFaceSource, name, kind, path, HuggingFaceNote, false));
        }
        return items;
    }

    /// <summary>Maps `models--org--name` to kind "model" and name "org/name" (and `datasets--...` to "dataset").</summary>
    public static bool TryParseHuggingFaceName(string folder, out string kind, out string name)
    {
        kind = "";
        name = "";
        var prefix = folder.StartsWith("models--", StringComparison.Ordinal) ? "models--"
            : folder.StartsWith("datasets--", StringComparison.Ordinal) ? "datasets--" : null;
        if (prefix is null) return false;
        var mapped = folder[prefix.Length..].Replace("--", "/");
        if (string.IsNullOrWhiteSpace(mapped.Trim('/'))) return false;
        kind = prefix == "models--" ? "model" : "dataset";
        name = mapped;
        return true;
    }

    public static IReadOnlyList<AiModelFolder> LmStudio(IEnumerable<string> roots)
    {
        var items = new List<AiModelFolder>();
        foreach (var root in roots)
        foreach (var publisher in Children(root))
        foreach (var model in Children(publisher))
            items.Add(new(LmStudioSource, Path.GetFileName(publisher) + "/" + Path.GetFileName(model), "model", model, LmStudioNote, true));
        return items;
    }

    private static IReadOnlyList<string> Children(string path)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Where(p => !new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
