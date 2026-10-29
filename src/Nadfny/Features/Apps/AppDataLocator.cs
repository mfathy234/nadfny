using System.Text.RegularExpressions;
using Nadfny.Core;

namespace Nadfny.Features.Apps;

/// <summary>A folder under AppData or ProgramData that may belong to an app: "Roaming\Notepad++" or "Local\Google\Chrome".</summary>
public sealed record DataDir(string Path, string Key, string? ParentKey);

/// <summary>
/// Maps apps to their settings and data folders by name. Only exact matches of the normalized name count
/// ("Notepad++ (64-bit x64)" and "Notepad++" both become "notepad"), so a folder is never guessed from a partial
/// match. Vendor folders are searched one level down ("Local\Google\Chrome" for Google's Chrome).
/// </summary>
public static partial class AppDataLocator
{
    // Folders that hold many vendors' or Windows' things, or names too generic to belong to one app.
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "packages", "programs", "temp", "tmp", "comms", "connecteddevicesplatform", "d3dscache", "crashdumps",
        "publishers", "virtualstore", "packagecache", "applicationdata", "history", "peerdistrepub", "placeholdertilelogofolder",
        "windows", "usoshared", "usoprivate", "ssh", "softwaredistribution", "regid", "package", "common", "commonfiles",
        "app", "apps", "application", "bin", "data", "config", "settings", "cache", "caches", "logs", "log", "install",
        "installer", "setup", "update", "updater", "current", "launcher", "client", "user", "users", "default", "x64", "x86",
        "program", "plugins", "tools", "local", "roaming", "locallow", "desktop", "documents", "downloads", "nadfny",
    };

    // Cache folder names apps (and the Chromium/Electron runtime most of them use) fill with regenerable files.
    private static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache",
        "GrShaderCache", "ShaderCache", "CachedData", "CachedExtensionVSIXs", "CacheStorage", "ScriptCache", "INetCache",
        "Crashpad", "CrashDumps", "Temp", "tmp",
    };

    public static IReadOnlyList<string> DefaultRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "", "LocalLow"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    /// <summary>Every top-level folder of each root, and the folders inside each of those (for vendor folders).</summary>
    public static IReadOnlyList<DataDir> Index(IEnumerable<string> roots, ISet<string> excluded)
    {
        var list = new List<DataDir>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var top in SafeDirs(root))
            {
                if (excluded.Contains(top)) continue;
                var key = Normalize(Path.GetFileName(top));
                if (key.Length == 0) continue;
                if (!Shared.Contains(key)) list.Add(new DataDir(top, key, null));
                foreach (var child in SafeDirs(top))
                {
                    if (excluded.Contains(child)) continue;
                    var childKey = Normalize(Path.GetFileName(child));
                    if (childKey.Length >= 3 && !Shared.Contains(childKey)) list.Add(new DataDir(child, childKey, key));
                }
            }
        }
        return list;
    }

    /// <summary>
    /// App id -> its data folders. A folder goes to the app whose name matches it most specifically; on a tie it goes to
    /// every app tied, so it counts as shared and is never offered as one app's leftover.
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> Match(IEnumerable<AppCandidate> apps, IReadOnlyList<DataDir> dirs)
    {
        var byKey = dirs.ToLookup(d => d.Key);
        var best = new Dictionary<string, (List<string> AppIds, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (app.Source is AppSource.Store) continue; // its data folder is known exactly
            var vendor = VendorKeys(app.Publisher);
            foreach (var key in NameKeys(app))
            {
                foreach (var dir in byKey[key])
                {
                    // A vendor's sub-folder only counts under that vendor ("Google\Chrome" for Google, not "Foo\Chrome").
                    if (dir.ParentKey is not null && !vendor.Contains(dir.ParentKey) && dir.ParentKey != key) continue;
                    var score = key.Length + (dir.ParentKey is null ? 0 : 10);
                    if (!best.TryGetValue(dir.Path, out var current) || score > current.Score) best[dir.Path] = ([app.Id], score);
                    else if (score == current.Score && !current.AppIds.Contains(app.Id)) current.AppIds.Add(app.Id);
                }
            }
        }
        // A folder inside one already claimed by the same app is counted in the parent's size already.
        var result = new Dictionary<string, List<string>>();
        foreach (var (path, (appIds, _)) in best.OrderBy(kv => kv.Key.Length))
        {
            foreach (var appId in appIds)
            {
                var list = result.TryGetValue(appId, out var l) ? l : result[appId] = [];
                if (!list.Any(p => SafeDeleter.IsUnder(path, p))) list.Add(path);
            }
        }
        return result;
    }

    /// <summary>Cache folders inside an app's data folder, a few levels down ("User Data\Default\Cache").</summary>
    public static IReadOnlyList<string> FindCaches(string dataFolder, int maxDepth = 5, int maxVisited = 4000)
    {
        var found = new List<string>();
        var visited = 0;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((dataFolder, 0));
        while (queue.Count > 0 && visited < maxVisited)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var child in SafeDirs(dir))
            {
                visited++;
                if (CacheNames.Contains(Path.GetFileName(child))) { found.Add(child); continue; }
                if (depth + 1 < maxDepth) queue.Enqueue((child, depth + 1));
            }
        }
        return found;
    }

    internal static IEnumerable<string> NameKeys(AppCandidate app)
    {
        var keys = new HashSet<string>();
        void Add(string? s)
        {
            var k = Normalize(s);
            if (k.Length >= 3 && !Shared.Contains(k)) keys.Add(k);
        }
        Add(app.Name);
        foreach (var vendor in VendorWords(app.Publisher))
        {
            if (app.Name.StartsWith(vendor + " ", StringComparison.OrdinalIgnoreCase)) Add(app.Name[(vendor.Length + 1)..]);
        }
        if (app.InstallLocation is { } loc && app.Source is not AppSource.Steam and not AppSource.Folder) Add(Path.GetFileName(loc));
        return keys;
    }

    private static HashSet<string> VendorKeys(string? publisher) => VendorWords(publisher).Select(Normalize).Where(k => k.Length > 0).ToHashSet();

    // "Google LLC" -> ["Google LLC", "Google"]; the first word is what vendors name their folders after.
    private static IEnumerable<string> VendorWords(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) yield break;
        var trimmed = CompanySuffix().Replace(publisher, "").Trim().TrimEnd(',', '.');
        yield return trimmed;
        var first = trimmed.Split(' ', 2)[0];
        if (first != trimmed) yield return first;
    }

    /// <summary>Lowercase letters and digits only, without versions, architecture and edition noise.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = Noise().Replace(name, " ");
        s = Version().Replace(s, " ");
        return new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    [GeneratedRegex(@"\((?:x64|x86|64-bit|32-bit|64 bit|32 bit|x64 [^)]*|64-bit x64|user|machine)\)|\b(?:x64|x86|amd64|arm64|64-bit|32-bit)\b|[™®©]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\bv?\d+(?:\.\d+)+\b")]
    private static partial Regex Version();

    [GeneratedRegex(@",?\s*\b(?:Inc|LLC|Ltd|Limited|Corporation|Corp|GmbH|AG|SA|S\.?r\.?o|Co|Pty|Team|Software|Technologies|Technology)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();
}
