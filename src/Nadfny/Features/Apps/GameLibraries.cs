using System.Text.Json;
using Microsoft.Win32;

namespace Nadfny.Features.Apps;

/// <summary>Games from launcher libraries that do not (reliably) show up in Apps &amp; Features: Steam and Epic.</summary>
public static class GameLibraries
{
    public static IReadOnlyList<AppCandidate> Read()
    {
        var list = new List<AppCandidate>();
        try { list.AddRange(Steam(SteamRoot())); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { list.AddRange(Epic(EpicManifests())); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Install folders other launchers record in the registry (GOG, Ubisoft), used to tell games from apps.</summary>
    public static IReadOnlyList<(string Path, string Launcher)> RegisteredGameFolders()
    {
        var list = new List<(string, string)>();
        AddValues(list, @"SOFTWARE\GOG.com\Games", "path", "GOG");
        AddValues(list, @"SOFTWARE\Ubisoft\Launcher\Installs", "InstallDir", "Ubisoft Connect");
        return list;
    }

    private static void AddValues(List<(string, string)> into, string key, string value, string launcher)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = root.OpenSubKey(key);
                if (k is null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    using var game = k.OpenSubKey(name);
                    if (game?.GetValue(value) is string p && p.Length > 3)
                        into.Add((Path.TrimEndingDirectorySeparator(p.Replace('/', '\\')), launcher));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    private static string? SteamRoot()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var path = k?.GetValue("SteamPath") as string;
        if (path is not null) return Path.GetFullPath(path.Replace('/', '\\'));
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Every library in libraryfolders.vdf (plus Steam's own folder), one candidate per appmanifest.</summary>
    internal static IEnumerable<AppCandidate> Steam(string? steamRoot)
    {
        if (steamRoot is null) yield break;
        var libraries = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var vdf = Vdf.Parse(File.ReadAllText(vdfPath));
            var folders = vdf.Block("libraryfolders") ?? vdf;
            libraries.AddRange(folders.Blocks.Select(b => b["path"]).OfType<string>());
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries.Select(l => Path.TrimEndingDirectorySeparator(Path.GetFullPath(l))).Where(seen.Add))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var game = SteamGame(lib, manifest, File.ReadAllText(manifest));
                if (game is not null) yield return game;
            }
        }
    }

    internal static AppCandidate? SteamGame(string library, string manifestPath, string acf)
    {
        var state = Vdf.Parse(acf).Block("AppState");
        if (state?["appid"] is not { } appId || state["name"] is not { } name || state["installdir"] is not { } dir) return null;
        if (appId.Length == 0 || !appId.All(char.IsAsciiDigit) || dir.IndexOfAny(['\\', '/', ':']) >= 0 || dir.Contains("..")) return null;
        // Steamworks redistributables are "apps" too; they are not games anyone wants to see.
        if (name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase)) return null;
        var installed = long.TryParse(state["LastUpdated"], out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd") : null;
        var apps = Path.Combine(library, "steamapps");
        return new AppCandidate(
            $"steam:{appId}", name, null, null, installed, AppSource.Steam, true, "Steam",
            Path.Combine(apps, "common", dir),
            long.TryParse(state["SizeOnDisk"], out var size) && size > 0 ? size : null,
            new UninstallPlan(UninstallPlan.Url, $"steam://uninstall/{appId}", null, manifestPath),
            [], [Path.Combine(apps, "shadercache", appId)]);
    }

    private static string EpicManifests() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    internal static IEnumerable<AppCandidate> Epic(string manifests)
    {
        if (!Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            var game = EpicGame(File.ReadAllText(file));
            if (game is not null) yield return game;
        }
    }

    internal static AppCandidate? EpicGame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("DisplayName") is not { } name || S("InstallLocation") is not { } location || S("AppName") is not { } appName) return null;
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
            var isApp = r.TryGetProperty("bIsApplication", out var a) && a.ValueKind == JsonValueKind.True;
            long? size = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var n) && n > 0 ? n : null;
            // Epic has no uninstall URL; its library page is where games are removed.
            return new AppCandidate($"epic:{appName}", name, null, S("AppVersionString"), null, AppSource.Epic, !isApp, "Epic Games",
                Path.TrimEndingDirectorySeparator(location.Replace('/', '\\')), size,
                new UninstallPlan(UninstallPlan.Url, "com.epicgames.launcher://store/library"), [], [],
                "Opens your Epic Games library, where you remove it.");
        }
        catch (JsonException) { return null; }
    }
}
