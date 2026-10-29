using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nadfny.Features.Apps;

/// <summary>One entry under a Windows "Uninstall" registry key, as Apps &amp; Features sees it.</summary>
public sealed record UninstallEntry(
    string Key,
    string KeyName,
    string? DisplayName,
    string? Publisher,
    string? Version,
    string? InstallDate,
    string? InstallLocation,
    string? UninstallString,
    string? DisplayIcon,
    long? EstimatedKb,
    bool SystemComponent,
    bool WindowsInstaller,
    string? ParentKeyName,
    string? ReleaseType);

/// <summary>Reads Win32 apps from the same registry keys Apps &amp; Features lists, and turns them into candidates.</summary>
public static partial class UninstallRegistry
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Folders launchers install games into; an app installed under one of them is a game.
    private static readonly (string Marker, string Launcher)[] GameMarkers =
    [
        (@"\steamapps\", "Steam"),
        (@"\Epic Games\", "Epic Games"),
        (@"\GOG Galaxy\Games\", "GOG"),
        (@"\GOG Games\", "GOG"),
        (@"\EA Games\", "EA app"),
        (@"\Origin Games\", "EA app"),
        (@"\Ubisoft Game Launcher\games\", "Ubisoft Connect"),
        (@"\Riot Games\", "Riot"),
        (@"\XboxGames\", "Xbox"),
        (@"\Battle.net\", "Battle.net"),
        // Repack installers, and a plain "Games" folder, hold games whatever installed them.
        (@"\DODI-Repacks\", "Repack"),
        (@"\FitGirl Repacks\", "Repack"),
        (@"\Games\", "Games folder"),
    ];

    public static IReadOnlyList<UninstallEntry> Read()
    {
        var list = new List<UninstallEntry>();
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, list);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, list);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, list);
        return list;
    }

    /// <summary>True when the uninstall key is still there (the app has not been removed yet).</summary>
    public static bool Exists(string key)
    {
        var (hive, view, sub) = SplitKey(key);
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var k = root.OpenSubKey(sub);
        return k is not null;
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, List<UninstallEntry> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(name);
                if (k is null) continue;
                into.Add(new UninstallEntry(
                    $"{hive}|{view}|{UninstallPath}\\{name}", name,
                    S(k, "DisplayName"), S(k, "Publisher"), S(k, "DisplayVersion"), S(k, "InstallDate"),
                    S(k, "InstallLocation"), S(k, "UninstallString"), S(k, "DisplayIcon"),
                    k.GetValue("EstimatedSize") is int kb ? kb : null,
                    k.GetValue("SystemComponent") is int sc && sc == 1,
                    k.GetValue("WindowsInstaller") is int wi && wi == 1,
                    S(k, "ParentKeyName"), S(k, "ReleaseType")));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    private static string? S(RegistryKey k, string name) => k.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static (RegistryHive, RegistryView, string) SplitKey(string key)
    {
        var parts = key.Split('|', 3);
        return (Enum.Parse<RegistryHive>(parts[0]), Enum.Parse<RegistryView>(parts[1]), parts[2]);
    }

    /// <summary>
    /// What Apps &amp; Features shows: named entries that can be uninstalled, without system components, updates and
    /// patches. The 64-bit and 32-bit views can list the same app twice; the first one wins.
    /// </summary>
    public static IReadOnlyList<AppCandidate> ToCandidates(IEnumerable<UninstallEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppCandidate>();
        foreach (var e in entries)
        {
            if (e.DisplayName is null || e.UninstallString is null || e.SystemComponent || e.ParentKeyName is not null) continue;
            if (e.ReleaseType is { } rt && (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) || rt.Contains("Security", StringComparison.OrdinalIgnoreCase))) continue;
            if (!seen.Add($"{e.DisplayName}|{e.Version}")) continue;

            var location = InstallFolder(e);
            var launcher = GameLauncher(location, e.UninstallString);
            list.Add(new AppCandidate(
                e.Key, e.DisplayName, e.Publisher, e.Version, ParseInstallDate(e.InstallDate), AppSource.Win32,
                launcher is not null, launcher, location, e.EstimatedKb is { } kb && kb > 0 ? kb * 1024L : null,
                PlanFor(e), [], []));
        }
        return list;
    }

    /// <summary>The app's own folder: InstallLocation, or the folder of its icon when that is a plain program folder.</summary>
    internal static string? InstallFolder(UninstallEntry e)
    {
        if (e.InstallLocation is { } loc) return IsUsableFolder(loc.Trim('"')) ? Path.TrimEndingDirectorySeparator(loc.Trim('"')) : null;
        if (e.DisplayIcon is { } icon && !e.WindowsInstaller)
        {
            var file = icon.Split(',')[0].Trim('"');
            var dir = Path.GetDirectoryName(file);
            var uninstaller = e.UninstallString is { } u ? SplitCommand(u).File : null;
            if (dir is not null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsUsableFolder(dir)
                && uninstaller is not null && Core.SafeDeleter.IsUnder(uninstaller, dir)) return dir;
        }
        return null;
    }

    // Never treat a drive root, Windows or a Program Files root itself as one app's folder.
    private static bool IsUsableFolder(string path)
    {
        if (path.Length < 4 || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(path);
        if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true) return false;
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        return !roots.Any(r => r.Length > 0 && r.Equals(full, StringComparison.OrdinalIgnoreCase))
            && !full.Contains(@"\Setup Bootstrap\", StringComparison.OrdinalIgnoreCase)
            && !full.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? GameLauncher(string? location, string? uninstall)
    {
        if (GameFolderScanner.IsLauncherFolder(location)) return null;
        var haystack = $"{location}\\ {uninstall}";
        foreach (var (marker, launcher) in GameMarkers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase)) return launcher;
        }
        return null;
    }

    internal static UninstallPlan? PlanFor(UninstallEntry e)
    {
        var cmd = e.UninstallString!;
        if (MsiProductCode(cmd) is { } code) return new UninstallPlan(UninstallPlan.Msi, code, null, e.Key);
        if (e.WindowsInstaller && Guid.TryParse(e.KeyName, out _)) return new UninstallPlan(UninstallPlan.Msi, e.KeyName, null, e.Key);
        if (cmd.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase) && cmd.Contains("steam://", StringComparison.OrdinalIgnoreCase)) return null; // handled by the Steam library
        var (file, args) = SplitCommand(cmd);
        return file is null ? null : new UninstallPlan(UninstallPlan.Command, file, args, e.Key);
    }

    /// <summary>"MsiExec.exe /I{GUID}" or "/X{GUID}" -> the product code.</summary>
    internal static string? MsiProductCode(string cmd)
    {
        var m = MsiPattern().Match(cmd);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Splits an uninstall command into the program and its arguments. Handles a quoted path, and the common
    /// unquoted form with spaces ("C:\Program Files\X\uninst.exe /S") by cutting after the first ".exe".
    /// </summary>
    internal static (string? File, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end < 0 ? (null, "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) return (null, "");
        var cut = exe + 4;
        return (cmd[..cut], cmd[cut..].Trim());
    }

    /// <summary>Registry install dates are "yyyyMMdd"; anything else is dropped.</summary>
    internal static string? ParseInstallDate(string? raw) =>
        raw is not null && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"msiexec(?:\.exe)?\s.*?/[IX]\s*(\{[0-9A-F-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiPattern();
}
