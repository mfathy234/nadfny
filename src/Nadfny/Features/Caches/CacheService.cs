using Nadfny.Core;

namespace Nadfny.Features.Caches;

public sealed record CacheDefinition(string Id, string Name, string Path, string Note);
public sealed record CacheInfo(string Id, string Name, string Path, string Note, long Bytes);

/// <summary>Package-manager and tool caches. Clearing empties the folder; the tool re-downloads on demand.</summary>
public sealed partial class CacheService
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);
    private readonly Func<IReadOnlyList<CacheDefinition>> _definitions;
    private readonly bool _resolveGo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, CacheDefinition> _scanned = new Dictionary<string, CacheDefinition>();

    /// <param name="definitions">Override the cache list (tests point it at dummy folders).</param>
    public CacheService(Func<IReadOnlyList<CacheDefinition>>? definitions = null)
    {
        _definitions = definitions ?? (() => Definitions());
        _resolveGo = definitions is null;
    }

    public static IReadOnlyList<CacheDefinition> Definitions(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var home = environment("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = environment("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = environment("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        List<CacheDefinition> definitions =
        [
            new("nuget", "NuGet packages", Path.Combine(home, ".nuget", "packages"), "Restored again on the next build. Private feeds need working credentials."),
            new("nuget-http", "NuGet HTTP cache", Path.Combine(local, "NuGet", "v3-cache"), "Download cache for package metadata."),
            new("npm", "npm cache", Path.Combine(local, "npm-cache"), "Packages are fetched again on the next install."),
            new("pnpm", "pnpm store", Path.Combine(local, "pnpm", "store"), "Packages are fetched again on the next install."),
            new("yarn", "Yarn cache", Path.Combine(local, "Yarn", "Cache"), "Packages are fetched again on the next install."),
            new("playwright", "Playwright browsers", Path.Combine(local, "ms-playwright"), "Run npx playwright install to get them back."),
            new("pip", "pip cache", Path.Combine(local, "pip", "Cache"), "Wheels are downloaded again when needed."),
            new("gradle", "Gradle caches", Path.Combine(home, ".gradle", "caches"), "Dependencies are downloaded again on the next build."),
            new("maven", "Maven repository", Path.Combine(home, ".m2", "repository"), "Downloaded again on the next build. Artifacts you built with mvn install are lost."),
            new("cargo", "Cargo registry", Path.Combine(home, ".cargo", "registry"), "Crates are downloaded again on the next build."),
            new("temp", "Temporary files", Path.GetTempPath(), "Anything changed in the last 24 hours, and files in use, are kept."),
        ];
        AddDeveloperCaches(definitions, home, local, roaming, environment);
        return definitions;
    }

    public async Task<IReadOnlyList<CacheInfo>> ScanAsync(Action<CacheInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ScanCoreAsync(onFound, progress, ct); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<CacheInfo>> ScanCoreAsync(Action<CacheInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        var list = new System.Collections.Concurrent.ConcurrentBag<CacheInfo>();
        var produced = new System.Collections.Concurrent.ConcurrentDictionary<string, CacheDefinition>();
        _scanned = new Dictionary<string, CacheDefinition>();
        var definitions = _definitions();
        if (_resolveGo)
        {
            var go = await ProcessRunner.RunAsync("go", ["env", "GOMODCACHE"], timeout: TimeSpan.FromSeconds(5), ct: ct);
            var path = go.StdOut.Trim();
            if (go.Ok && Path.IsPathFullyQualified(path))
                definitions = definitions.Select(d => d.Id == "go-mod" ? d with { Path = path } : d).ToList();
        }
        // GOMODCACHE can point anywhere; clearing empties the folder, so only accept one that holds a module cache.
        definitions = definitions.Where(d => d.Id != "go-mod" || Directory.Exists(Path.Combine(d.Path, "cache", "download"))).ToList();
        var present = definitions.Where(d => Directory.Exists(d.Path)).ToList();
        progress.SetPhase("Measuring caches", present.Count);
        await Parallel.ForEachAsync(present, ct, (d, token) =>
        {
            var bytes = d.Id.StartsWith("conda-", StringComparison.Ordinal)
                ? Children(d).Sum(c => c.IsDir ? DirectorySizer.MeasureSequential(c.Path, token).Bytes : c.Length)
                : DirectorySizer.MeasureSequential(d.Path, token).Bytes;
            // Stable ids: history compares sizes across scans by id. Clearing still only accepts ids from this scan.
            var info = new CacheInfo(d.Id, d.Name, d.Path, d.Note, bytes);
            produced[info.Id] = d;
            list.Add(info);
            progress.CompleteOne(d.Path);
            onFound(info);
            return ValueTask.CompletedTask;
        });
        progress.Flush();
        ct.ThrowIfCancellationRequested();
        _scanned = produced.ToDictionary();
        return list.OrderByDescending(c => c.Bytes).ToList();
    }

    /// <param name="expectedPath">The folder the user saw; refused when the last scan resolved this id somewhere else.</param>
    public async Task<long> ClearAsync(string id, OpReporter op, CancellationToken ct, string? expectedPath = null)
    {
        await _gate.WaitAsync(ct);
        try { return await ClearCoreAsync(id, expectedPath, op, ct); }
        finally { _gate.Release(); }
    }

    private async Task<long> ClearCoreAsync(string id, string? expectedPath, OpReporter op, CancellationToken ct)
    {
        var def = _scanned.GetValueOrDefault(id) ?? throw new InvalidOperationException("Scan caches again before clearing this item.");
        if (expectedPath is not null && !Path.TrimEndingDirectorySeparator(expectedPath).Equals(Path.TrimEndingDirectorySeparator(def.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This cache moved since you looked at it. Scan caches again before clearing it.");
        if (!Directory.Exists(def.Path)) return 0;
        SafeDeleter.EnsureDeletable(def.Path);
        var isTemp = def.Id == "temp";
        if (isTemp && !Path.TrimEndingDirectorySeparator(def.Path).EndsWith("Temp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The temp folder points somewhere unexpected ({def.Path}), so it was not cleared.");

        var children = Children(def);
        var targets = new List<(string Path, long Bytes)>();
        var keepAfter = DateTime.UtcNow - RecentWindow;
        op.Phase(isTemp ? "Checking which files are still in use" : "Preparing", children.Count);
        await Task.Run(() =>
        {
            var checkedCount = 0;
            foreach (var (isDir, child, length, writeTicks) in children)
            {
                ct.ThrowIfCancellationRequested();
                var size = isDir ? DirectorySizer.MeasureSequential(child, ct) : new DirectorySizer.SizeResult(length, 1, new DateTime(writeTicks, DateTimeKind.Utc));
                op.Report(++checkedCount, child);
                // Temp is shared with running programs: keep anything with a file written recently, at any depth.
                if (isTemp && (size.LastWriteUtc > keepAfter || new DateTime(writeTicks, DateTimeKind.Utc) > keepAfter)) continue;
                targets.Add((child, size.Bytes));
            }
        }, ct);

        long freed = 0;
        await op.DeleteWithProgressAsync($"Deleting {def.Name}", targets.Sum(t => t.Bytes), counter =>
        {
            Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, t =>
            {
                try { SafeDeleter.DeletePermanently(t.Path, ct, counter); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* in use or protected: skip */ }
            });
            freed = counter.Bytes; // counts only files that were really deleted
        }, ct);
        return freed;
    }

    private static List<(bool IsDir, string Path, long Length, long WriteTicks)> Children(CacheDefinition definition) =>
        DirectorySizer.Shallow(definition.Path).Where(c => !definition.Id.StartsWith("conda-", StringComparison.Ordinal) ||
            (c.IsDir ? Path.GetFileName(c.Path).Equals("cache", StringComparison.OrdinalIgnoreCase)
                : c.Path.EndsWith(".conda", StringComparison.OrdinalIgnoreCase) || c.Path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))).ToList();
}
