namespace Nadfny.Features.Apps;

/// <summary>
/// Finds games that were downloaded and unpacked rather than installed (D:\Games\Racing\Some Game) by the files
/// their engine leaves next to the executable. Walks each drive a few levels deep and stops at the first match.
/// </summary>
public static class GameFolderScanner
{
    public const int MaxDepth = 5;

    // Files that only a game ships: engine runtimes and store/platform SDKs.
    private static readonly HashSet<string> SignatureFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
        "data.win",                                  // GameMaker
        "steam_api.dll", "steam_api64.dll", "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll",
        "Galaxy.dll", "Galaxy64.dll", "uplay_r1_loader64.dll", "upc_r2_loader64.dll",
        "bink2w64.dll", "binkw32.dll", "binkw64.dll",
        "Game.rgss3a", "Game.rgss2a", "Game.rgssad", // RPG Maker XP/VX/VX Ace
    };

    // Never descended into: system, tool and developer folders, where a stray steam_api.dll means nothing.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "$WinREAgent",
        "AppData", "node_modules", ".git", ".nuget", ".gradle", ".m2", ".cargo", ".vs", "obj", "bin",
        "WindowsApps", "WpSystem", "Microsoft", "Common Files", "dotnet", "Windows Kits", "Microsoft Visual Studio",
        "steamapps", "Epic Games", "XboxGames", "SteamLibrary",
    };

    // Launchers ship the same runtimes as games, and live next to them; they are apps, not games.
    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "Battle.net", "Ubisoft Game Launcher", "EA Desktop", "Origin", "GOG Galaxy", "Epic Games Launcher",
        "Riot Client", "Rockstar Games Launcher", "Play Games", "Xbox",
    };

    public static bool IsLauncherFolder(string? dir) =>
        dir is not null && LauncherNames.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)));

    /// <summary>Folders that look like a game, outside the folders already known from installers and launchers (case-insensitive set).</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> roots, ISet<string> known, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var root in roots) Walk(root, 0, known, found, ct);
        return found;
    }

    private static void Walk(string dir, int depth, ISet<string> known, List<string> found, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (known.Contains(Path.TrimEndingDirectorySeparator(dir))) return; // walked top-down, so the exact folder is enough
        string[] files, dirs;
        try
        {
            var info = new DirectoryInfo(dir);
            if (depth > 0 && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            files = Directory.GetFiles(dir);
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return; }

        if (depth > 0 && LooksLikeGame(dir, files.Select(Path.GetFileName).OfType<string>().ToList(), dirs.Select(Path.GetFileName).OfType<string>().ToList()))
        {
            found.Add(dir);
            return;
        }
        if (depth >= MaxDepth) return;
        if (IsProject(dirs, files)) return;
        foreach (var child in dirs)
        {
            var name = Path.GetFileName(child);
            if (SkipNames.Contains(name) || LauncherNames.Contains(name) || name.StartsWith('.')) continue;
            Walk(child, depth + 1, known, found, ct);
        }
    }

    /// <summary>True when the folder an installer put an app in looks like a game.</summary>
    public static bool LooksLikeGame(string dir)
    {
        try { return LooksLikeGame(dir, Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToList(), Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>True when a folder's own files and sub-folders carry a game engine's signature.</summary>
    internal static bool LooksLikeGame(string dir, IReadOnlyList<string> files, IReadOnlyList<string> dirs)
    {
        var hasExe = files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (!hasExe) return false;
        if (files.Any(SignatureFiles.Contains)) return true;
        if (files.Any(f => f.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))) return true;
        // Unity: Game.exe next to Game_Data.
        if (files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && dirs.Contains(Path.GetFileNameWithoutExtension(f) + "_Data", StringComparer.OrdinalIgnoreCase))) return true;
        // Godot: Game.exe next to Game.pck.
        if (files.Any(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) && files.Contains(Path.ChangeExtension(f, ".exe"), StringComparer.OrdinalIgnoreCase))) return true;
        // Unreal: Engine\ plus a project folder with Content\Paks.
        if (dirs.Contains("Engine", StringComparer.OrdinalIgnoreCase)
            && dirs.Any(d => Directory.Exists(Path.Combine(dir, d, "Content", "Paks")))) return true;
        // Ren'Py: renpy\ + game\.  RPG Maker MV/MZ: Game.exe + www\ (or js\ + img\).
        if (dirs.Contains("renpy", StringComparer.OrdinalIgnoreCase) && dirs.Contains("game", StringComparer.OrdinalIgnoreCase)) return true;
        if (files.Contains("Game.exe", StringComparer.OrdinalIgnoreCase) && (dirs.Contains("www", StringComparer.OrdinalIgnoreCase)
            || dirs.Contains("img", StringComparer.OrdinalIgnoreCase) && dirs.Contains("js", StringComparer.OrdinalIgnoreCase))) return true;
        return false;
    }

    // A git repo or an engine project (Unity: Assets + ProjectSettings; Unreal: *.uproject; Godot: project.godot).
    private static bool IsProject(IReadOnlyList<string> dirs, IReadOnlyList<string> files) =>
        dirs.Any(d => Path.GetFileName(d).Equals(".git", StringComparison.OrdinalIgnoreCase))
        || files.Any(f => Path.GetFileName(f).Equals(".git", StringComparison.OrdinalIgnoreCase))
        || dirs.Any(d => Path.GetFileName(d).Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase)) && dirs.Any(d => Path.GetFileName(d).Equals("Assets", StringComparison.OrdinalIgnoreCase))
        || files.Any(f => f.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("project.godot", StringComparison.OrdinalIgnoreCase));

    /// <summary>"D:\Games\Racing\Need.For.Speed.v1.2" -> "Need For Speed".</summary>
    internal static string DisplayName(string dir)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[._\s-]+v?\d+([._]\d+)+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[._]+", " ");
        return cleaned.Trim() is { Length: > 0 } c ? c : name;
    }

    /// <summary>The folder above the game, shown as its category ("Racing" for D:\Games\Racing\X).</summary>
    internal static string? Category(string dir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        if (parent is null || Path.GetPathRoot(parent)?.Equals(parent, StringComparison.OrdinalIgnoreCase) == true) return null;
        return Path.GetFileName(parent);
    }

    public static AppCandidate ToCandidate(string dir) =>
        new($"folder:{dir.ToLowerInvariant()}", DisplayName(dir), null, null,
            Directory.GetCreationTimeUtc(dir).ToString("yyyy-MM-dd"), AppSource.Folder, true, Category(dir) is { } c ? $"Folder · {c}" : "Folder",
            dir, null, new UninstallPlan(UninstallPlan.Folder, dir), [], [],
            "Not installed by a setup program. Removing it moves the folder to the Recycle Bin.");
}
