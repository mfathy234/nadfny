using System.Collections.Concurrent;
using Nadfny.Core;
using Nadfny.Features.Caches;
using Nadfny.Features.Explore;

namespace Nadfny.Features.Apps;

public sealed record CacheClearResult(long Freed, bool WasRunning);
public sealed record UninstallResult(bool Removed, IReadOnlyList<AppFolder> Leftovers);

/// <summary>Installed apps and games: what they take on disk (program, data, caches), clearing caches, uninstalling.</summary>
public sealed partial class AppService
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AppCandidate>>> _candidates;
    private readonly Func<IReadOnlyList<string>> _dataRoots;
    private readonly Func<IReadOnlyList<string>> _gameRoots;
    private readonly ConcurrentDictionary<string, (AppCandidate Candidate, AppInfo Info)> _apps = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _leftovers = new();
    private DriveName _drives = new(); // fresh each scan, so a renamed drive shows its new label

    /// <param name="candidates">Override where apps come from (tests use dummy apps).</param>
    /// <param name="dataRoots">Override the AppData/ProgramData roots searched for data folders.</param>
    /// <param name="gameRoots">Override the folders searched for downloaded games (default: every local drive).</param>
    public AppService(
        Func<CancellationToken, Task<IReadOnlyList<AppCandidate>>>? candidates = null,
        Func<IReadOnlyList<string>>? dataRoots = null,
        Func<IReadOnlyList<string>>? gameRoots = null,
        Func<UninstallPlan, OpReporter, CancellationToken, Task<bool>>? runner = null,
        Func<string, string, IReadOnlyCollection<string>, CancellationToken, Task>? registryRemoval = null)
    {
        _candidates = candidates ?? InstalledAsync;
        _dataRoots = dataRoots ?? AppDataLocator.DefaultRoots;
        _gameRoots = gameRoots ?? (() => ExploreService.Drives().Select(d => d.Name).ToList());
        _runner = runner ?? AppUninstaller.RunAsync;
        _registryRemoval = registryRemoval ?? AppRegistryRemoval.RemoveAsync;
    }

    public async Task<IReadOnlyList<AppInfo>> ScanAsync(Action<AppInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        await _uninstallGate.WaitAsync(ct);
        try { return await ScanCoreAsync(onFound, progress, ct); }
        finally { _uninstallGate.Release(); }
    }

    private async Task<IReadOnlyList<AppInfo>> ScanCoreAsync(Action<AppInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        _forcedPreviews.Clear();
        _drives = new DriveName();
        progress.SetPhase("Reading installed apps", 0);
        var candidates = ShareFolders((await _candidates(ct))
            .Select(c => c.InstallLocation is { } loc ? c with { InstallLocation = SafeDeleter.Canonical(loc) } : c));

        progress.SetPhase("Looking for downloaded games", 0);
        var known = candidates.Select(c => c.InstallLocation).OfType<string>()
            .Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = await Task.Run(() => GameFolderScanner.Find(_gameRoots(), known, ct), ct);
        candidates.AddRange(games.Select(GameFolderScanner.ToCandidate));

        progress.SetPhase("Matching app data", 0);
        // The Caches view's folders, and the tool folders holding them (Local\Yarn for Local\Yarn\Cache), are not app data.
        var excluded = CacheService.Definitions().Select(d => Path.TrimEndingDirectorySeparator(d.Path))
            .SelectMany(p => new[] { p, Path.GetDirectoryName(p) ?? p })
            .Append(StoreApps.PackagesFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataByApp = AppDataLocator.Match(candidates, AppDataLocator.Index(_dataRoots(), excluded));

        _apps.Clear();
        var results = new ConcurrentBag<AppInfo>();
        progress.SetPhase("Measuring apps", candidates.Count);
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, (c, token) =>
        {
            var info = Measure(c, dataByApp.TryGetValue(c.Id, out var d) ? d : [], token)
                with { Drive = _drives.For(c.InstallLocation ?? c.SharedInstallLocation) };
            _apps[c.Id] = (c, info);
            results.Add(info);
            progress.CompleteOne(c.Name);
            onFound(info);
            return ValueTask.CompletedTask;
        });
        progress.Flush();
        return results.OrderByDescending(a => a.Bytes).ToList();
    }

    /// <summary>
    /// Several entries can point at one folder (NVIDIA's components, Office's parts). The folder is sized for the first
    /// only; the others say they share it and never offer it as their own leftover.
    /// </summary>
    internal static List<AppCandidate> ShareFolders(IEnumerable<AppCandidate> candidates)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppCandidate>();
        foreach (var c in candidates)
        {
            if (c.InstallLocation is not { } loc || owners.TryAdd(loc, c.Name)) { list.Add(c); continue; }
            list.Add(c with { InstallLocation = null, SharedInstallLocation = loc, EstimatedBytes = null, Note = c.Note ?? $"Shares its folder with {owners[loc]}, where its size is counted." });
        }
        return list;
    }

    private static async Task<IReadOnlyList<AppCandidate>> InstalledAsync(CancellationToken ct)
    {
        var store = StoreApps.ReadAsync(ct);
        var launchers = GameLibraries.Read();
        var registered = GameLibraries.RegisteredGameFolders();
        var launcherFolders = launchers.Select(l => l.InstallLocation).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var win32 = UninstallRegistry.ToCandidates(UninstallRegistry.Read())
            // Steam and Epic games are listed from their own libraries, which know the real folder and size.
            .Where(c => !c.Id.Contains(@"\Steam App ", StringComparison.OrdinalIgnoreCase) && !(c.InstallLocation is { } l && launcherFolders.Contains(l)))
            .Select(c => registered.FirstOrDefault(r => c.InstallLocation is { } l && SafeDeleter.IsUnder(l, r.Path)) is { Launcher: { } launcher }
                ? c with { IsGame = true, Launcher = launcher } : c);

        return [.. win32, .. launchers, .. await store];
    }

    internal static AppInfo Measure(AppCandidate c, IReadOnlyList<string> matchedData, CancellationToken ct)
    {
        // An entry whose folder is gone takes no space, whatever its estimate says.
        long? appBytes = c.InstallLocation is { } gone && c.Source is AppSource.Win32 && !Directory.Exists(gone) ? 0 : c.EstimatedBytes;
        // Store apps live in WindowsApps, which cannot be read; their size comes from the data folder alone.
        if (c.InstallLocation is { } loc && Directory.Exists(loc) && !loc.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            var measured = DirectorySizer.MeasureSequential(loc, ct).Bytes;
            if (measured > 0) appBytes = measured;
        }

        var dataPaths = c.KnownData.Concat(matchedData).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var data = dataPaths.Select(p => new AppFolder(p, DirectorySizer.MeasureSequential(p, ct).Bytes, IsDeletable(p))).ToList();

        var cachePaths = c.KnownCaches.Where(Directory.Exists)
            .Concat(dataPaths.SelectMany(p => AppDataLocator.FindCaches(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var caches = cachePaths.Select(p => new AppFolder(p, DirectorySizer.MeasureSequential(p, ct).Bytes, IsDeletable(p)))
            .Where(f => f.Bytes > 0).ToList();

        // Caches inside a data folder are already in its size; a cache elsewhere (Steam's shader cache) is added.
        var dataBytes = data.Sum(f => f.Bytes) + caches.Where(f => !dataPaths.Any(p => SafeDeleter.IsUnder(f.Path, p))).Sum(f => f.Bytes);
        var self = (c.InstallLocation ?? c.SharedInstallLocation) is { } l && SafeDeleter.IsUnder(AppContext.BaseDirectory, l);
        var missing = c.InstallLocation is { } m && c.Source is AppSource.Win32 && !Directory.Exists(m);
        // An installer that is not a known launcher can still have put a game there (engine files give it away).
        var isGame = c.IsGame || c.Source is AppSource.Win32 && c.InstallLocation is { } g && !missing && !GameFolderScanner.IsLauncherFolder(g) && GameFolderScanner.LooksLikeGame(g);

        return new AppInfo(c.Id, c.Name, c.Publisher, c.Version, c.InstalledOn, c.Source, isGame, c.Launcher, c.InstallLocation,
            appBytes, dataBytes, caches.Where(f => f.Deletable).Sum(f => f.Bytes), data, caches,
            CanUninstall: c.Uninstall is not null && !self,
            Note: self ? "This is Nadfny. Uninstall it from Windows Settings."
                : missing ? "Its folder is already gone. The uninstaller may only clean up the Windows entry."
                : c.Note,
            Broken: !self && IsBroken(c));
    }

    private static bool IsDeletable(string path)
    {
        try { SafeDeleter.EnsureDeletable(path); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Empties the app's cache folders. Files the app has open are skipped.</summary>
    public async Task<CacheClearResult> ClearCacheAsync(string id, OpReporter op, CancellationToken ct)
    {
        var (c, info) = Get(id);
        var running = AppUninstaller.IsRunning(c.InstallLocation);
        var targets = info.Caches.Where(f => f.Deletable && Directory.Exists(f.Path))
            .SelectMany(f => DirectorySizer.Shallow(f.Path).Select(x => x.Path)).ToList();

        long freed = 0;
        await op.DeleteWithProgressAsync($"Clearing {info.Name}'s cache", info.CacheBytes, counter =>
        {
            Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, t =>
            {
                try { SafeDeleter.DeletePermanently(t, ct, counter); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* in use: skip */ }
            });
            freed = counter.Bytes;
        }, ct);

        var left = info.Caches.Select(f => f with { Bytes = Directory.Exists(f.Path) ? DirectorySizer.MeasureSequential(f.Path, ct).Bytes : 0 }).ToList();
        _apps[id] = (c, info with { Caches = left, CacheBytes = left.Where(f => f.Deletable).Sum(f => f.Bytes), DataBytes = Math.Max(0, info.DataBytes - freed) });
        return new CacheClearResult(freed, running);
    }

    /// <summary>Runs the app's uninstaller; once it is gone, lists the folders it left behind.</summary>
    public async Task<UninstallResult> UninstallAsync(string id, OpReporter op, CancellationToken ct, bool quiet = false)
    {
        await _uninstallGate.WaitAsync(ct);
        try { return await UninstallCoreAsync(id, quiet, op, ct); }
        finally { _uninstallGate.Release(); }
    }

    private async Task<UninstallResult> UninstallCoreAsync(string id, bool quiet, OpReporter op, CancellationToken ct)
    {
        var (c, info) = Get(id);
        if (!info.CanUninstall || c.Uninstall is null) throw new InvalidOperationException(info.Note ?? "This app cannot be uninstalled from here.");
        if (info.Broken) throw new InvalidOperationException("Use Remove entry and leftovers for this broken entry.");
        var plan = quiet ? c.Uninstall.Kind == UninstallPlan.Msi ? c.Uninstall with { Arguments = "/qb" }
            : c.QuietUninstall is { Kind: UninstallPlan.Command } q && File.Exists(q.Target) ? q : c.Uninstall : c.Uninstall;
        var removed = await _runner(plan, op, ct);
        if (!removed) return new UninstallResult(false, []);

        _apps.TryRemove(id, out _);
        var leftovers = Leftovers(c, info);
        _leftovers[id] = leftovers.Where(f => f.Deletable).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new UninstallResult(true, leftovers);
    }

    /// <summary>Data and program folders still on disk after uninstalling, never one another app also uses.</summary>
    internal IReadOnlyList<AppFolder> Leftovers(AppCandidate c, AppInfo info)
    {
        if (c.Source is AppSource.Folder or AppSource.Store) return []; // the folder itself was removed / Windows removes package data
        var others = _apps.Values.Where(a => a.Candidate.Id != c.Id)
            .SelectMany(a => a.Info.Data.Select(d => d.Path).Append(a.Info.InstallLocation).Append(a.Candidate.SharedInstallLocation)).OfType<string>().ToList();
        var paths = info.Data.Select(d => d.Path).Append(c.InstallLocation).Append(c.SharedInstallLocation).OfType<string>()
            .Where(Directory.Exists)
            .Select(SafeDeleter.Canonical)
            .Where(p => !others.Any(o => SafeDeleter.IsUnder(o, p) || SafeDeleter.IsUnder(p, o)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var path in paths.OrderBy(p => p.Length))
            if (!roots.Any(root => SafeDeleter.IsUnder(path, root))) roots.Add(path);
        return roots.Select(p => new AppFolder(p, DirectorySizer.MeasureSequential(p).Bytes, IsDeletable(p))).ToList();
    }

    /// <summary>Moves leftovers to the Recycle Bin. Only folders offered by <see cref="UninstallAsync"/> are accepted.</summary>
    public async Task<(int Removed, IReadOnlyList<string> Failed)> RemoveLeftoversAsync(string id, IReadOnlyList<string> paths, OpReporter op, CancellationToken ct)
    {
        await _uninstallGate.WaitAsync(ct);
        try { return await RemoveLeftoversCoreAsync(id, paths, op, ct); }
        finally { _uninstallGate.Release(); }
    }

    private async Task<(int Removed, IReadOnlyList<string> Failed)> RemoveLeftoversCoreAsync(string id, IReadOnlyList<string> paths, OpReporter op, CancellationToken ct)
    {
        if (!_leftovers.TryGetValue(id, out var allowed)) throw new InvalidOperationException("Nothing to clean up for that app.");
        var failed = new List<string>();
        var removed = 0;
        op.Phase("Moving leftovers to the Recycle Bin", paths.Count);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!allowed.Contains(path)) { failed.Add(path); continue; }
            var others = _apps.Values.Where(a => a.Candidate.Id != id)
                .SelectMany(a => a.Info.Data.Select(d => d.Path).Append(a.Info.InstallLocation).Append(a.Candidate.SharedInstallLocation)).OfType<string>();
            if (others.Any(o => SafeDeleter.IsUnder(o, path) || SafeDeleter.IsUnder(path, o))) { failed.Add(path); continue; }
            try
            {
                await Task.Run(() => SafeDeleter.MoveToRecycleBin(path), ct);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { failed.Add(path); }
            op.Report(removed + failed.Count, path);
        }
        _leftovers.TryRemove(id, out _);
        return (removed, failed);
    }

    private (AppCandidate, AppInfo) Get(string id) =>
        _apps.TryGetValue(id, out var a) ? a : throw new InvalidOperationException("That app is no longer in the list. Rescan and try again.");
}
