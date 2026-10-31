using System.Text.Json;
using Nadfny.Features.Caches;

namespace Nadfny.Core;

public sealed record AppSettings(IReadOnlyList<string> Roots, IReadOnlyList<string>? TempKeep = null)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nadfny", "settings.json");

    /// <summary>Folder-name patterns Temp clearing leaves alone. Empty for settings files saved before this existed.</summary>
    public IReadOnlyList<string> KeepNames => TempKeep ?? [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json.Options) is { } s)
            {
                var keep = TempKeepList.Clean(s.TempKeep);
                return s.Roots is { Count: > 0 }
                    ? s with { Roots = s.Roots.Where(Directory.Exists).ToList(), TempKeep = keep }
                    : Defaults() with { TempKeep = keep };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* fall back to defaults */ }
        return Defaults();
    }

    public static AppSettings Defaults() => new(DefaultRoots());

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
    }

    private static IReadOnlyList<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string[] guesses =
        [
            Path.Combine(desktop, "Work"), Path.Combine(home, "source", "repos"), Path.Combine(home, "repos"),
            Path.Combine(home, "dev"), Path.Combine(home, "code"), Path.Combine(home, "projects"), @"C:\src", @"C:\dev",
        ];
        var found = guesses.Where(Directory.Exists).ToList();
        return found.Count > 0 ? found : [home];
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
