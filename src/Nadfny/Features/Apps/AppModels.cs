namespace Nadfny.Features.Apps;

/// <summary>Where an app came from, which also decides how it is uninstalled.</summary>
public static class AppSource
{
    public const string Win32 = "win32";
    public const string Store = "store";
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Folder = "folder";
}

/// <summary>A folder that belongs to an app: its settings and data, or a cache inside them.</summary>
public sealed record AppFolder(string Path, long Bytes, bool Deletable);

/// <summary>One installed app or game, as shown in the Apps view.</summary>
public sealed record AppInfo(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    string? InstalledOn,
    string Source,
    bool IsGame,
    string? Launcher,
    string? InstallLocation,
    long? AppBytes,
    long DataBytes,
    long CacheBytes,
    IReadOnlyList<AppFolder> Data,
    IReadOnlyList<AppFolder> Caches,
    bool CanUninstall,
    string? Note,
    bool Broken = false,
    string? Drive = null) // "Games (D:)": the drive the program folder is on
{
    public long Bytes => (AppBytes ?? 0) + DataBytes;
}

/// <summary>An app before it is measured: identity, how to remove it, and the names its data folders may use.</summary>
public sealed record AppCandidate(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    string? InstalledOn,
    string Source,
    bool IsGame,
    string? Launcher,
    string? InstallLocation,
    long? EstimatedBytes,
    UninstallPlan? Uninstall,
    IReadOnlyList<string> KnownData,
    IReadOnlyList<string> KnownCaches,
    string? Note = null,
    UninstallPlan? QuietUninstall = null,
    string? SharedInstallLocation = null);

/// <summary>How to remove an app, and the thing (<see cref="Probe"/>) whose disappearance proves it is gone.</summary>
public sealed record UninstallPlan(string Kind, string Target, string? Arguments = null, string? Probe = null)
{
    public const string Command = "command";     // run Target with Arguments; Probe = its uninstall registry key
    public const string Msi = "msi";             // msiexec /x Target (product code); Probe = registry key
    public const string Appx = "appx";           // Remove-AppxPackage Target (package full name)
    public const string Url = "url";             // open a launcher URL; Probe = a manifest file, or null when it cannot be watched
    public const string Folder = "folder";       // move Target to the Recycle Bin
}
