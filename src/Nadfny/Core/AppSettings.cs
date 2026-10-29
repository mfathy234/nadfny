using System.Text.Json;

namespace Nadfny.Core;

public sealed record AppSettings(IReadOnlyList<string> Roots)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nadfny", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json.Options) is { Roots.Count: > 0 } s)
                return s with { Roots = s.Roots.Where(Directory.Exists).ToList() };
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
