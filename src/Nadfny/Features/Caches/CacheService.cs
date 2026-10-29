using Nadfny.Core;

namespace Nadfny.Features.Caches;

public sealed record CacheDefinition(string Id, string Name, string Path, string Note);
public sealed record CacheInfo(string Id, string Name, string Path, string Note, long Bytes);

/// <summary>Package-manager and tool caches. Clearing empties the folder; the tool re-downloads on demand.</summary>
public sealed class CacheService
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);
    private readonly Func<IReadOnlyList<CacheDefinition>> _definitions;

    /// <param name="definitions">Override the cache list (tests point it at dummy folders).</param>
    public CacheService(Func<IReadOnlyList<CacheDefinition>>? definitions = null) => _definitions = definitions ?? Definitions;

    public static IReadOnlyList<CacheDefinition> Definitions()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
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
    }

    public async Task<IReadOnlyList<CacheInfo>> ScanAsync(Action<CacheInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        var list = new System.Collections.Concurrent.ConcurrentBag<CacheInfo>();
        var present = _definitions().Where(d => Directory.Exists(d.Path)).ToList();
        progress.SetPhase("Measuring caches", present.Count);
        await Parallel.ForEachAsync(present, ct, (d, token) =>
        {
            var size = DirectorySizer.Measure(d.Path, token);
            var info = new CacheInfo(d.Id, d.Name, d.Path, d.Note, size.Bytes);
            list.Add(info);
            progress.CompleteOne(d.Path);
            onFound(info);
            return ValueTask.CompletedTask;
        });
        progress.Flush();
        return list.OrderByDescending(c => c.Bytes).ToList();
    }

    public async Task<long> ClearAsync(string id, OpReporter op, CancellationToken ct)
    {
        var def = _definitions().FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException($"Unknown cache: {id}");
        if (!Directory.Exists(def.Path)) return 0;
        var isTemp = def.Id == "temp";
        if (isTemp && !Path.TrimEndingDirectorySeparator(def.Path).EndsWith("Temp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The temp folder points somewhere unexpected ({def.Path}), so it was not cleared.");

        var children = DirectorySizer.Shallow(def.Path).ToList();
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
}
