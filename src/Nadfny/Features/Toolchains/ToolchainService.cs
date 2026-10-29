using System.Collections.Concurrent;
using Nadfny.Core;

namespace Nadfny.Features.Toolchains;

public sealed record ToolchainInfo(string Id, string Kind, string Name, string Path, long Bytes, string Note,
    bool CanRemove, bool Recycle, bool Old = false, bool Pinned = false, string? Command = null);
public sealed record ToolchainReport(IReadOnlyList<ToolchainInfo> Items, IReadOnlyList<string> Warnings);

public sealed class ToolchainService
{
    private readonly ToolchainLocations _locations;
    private readonly Func<CancellationToken, Task<ProcessResult>> _listSdks;
    private IReadOnlyDictionary<string, ToolchainInfo> _scanned = new Dictionary<string, ToolchainInfo>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolchainService(ToolchainLocations? locations = null, Func<CancellationToken, Task<ProcessResult>>? listSdks = null)
    {
        _locations = locations ?? ToolchainLocations.Current();
        _listSdks = listSdks ?? (ct => ProcessRunner.RunAsync("dotnet", ["--list-sdks"], timeout: TimeSpan.FromSeconds(10), ct: ct));
    }

    public async Task<ToolchainReport> ScanAsync(IReadOnlyList<string> roots, Action<ToolchainInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _scanned = new Dictionary<string, ToolchainInfo>();
            var warnings = new List<string>();
            var result = await _listSdks(ct);
            if (!result.Ok) warnings.Add("Could not list .NET SDKs. dotnet may be unavailable.");
            progress.SetPhase("Checking global.json in scan locations");
            var pins = await Task.Run(() => DotnetSdks.FindPins(roots, ct), ct);
            if (!pins.Complete) warnings.Add("Some scan locations could not be checked for global.json, or exceeded six levels. No .NET SDKs are marked old. Projects outside your scan locations were not checked.");
            else warnings.Add("SDK pins were checked in your scan locations, up to six levels. Projects outside these locations were not checked.");
            var sdks = DotnetSdks.Parse(result.Ok ? result.StdOut : "");
            var old = pins.Complete ? DotnetSdks.OldVersions(sdks, pins.Versions) : new HashSet<string>();
            var items = new ConcurrentBag<ToolchainInfo>();
            foreach (var sdk in sdks)
            {
                var pinned = pins.Versions.Contains(sdk.Version);
                var isOld = old.Contains(sdk.Version) && !sdk.Preview;
                var path = Path.Combine(sdk.BasePath, sdk.Version);
                var info = new ToolchainInfo(Guid.NewGuid().ToString("N"), "dotnet", ".NET SDK " + sdk.Version, path,
                    Measure(path, ct),
                    pinned ? "Pinned by global.json in a scan location. Keep this SDK." : isOld
                        ? "A newer patch in this feature band is installed. Review your projects before uninstalling. The .NET Uninstall Tool is required; Visual Studio may keep SDKs it needs."
                        : "Kept: newest patch in its feature band, preview, or project pins could not be fully checked.",
                    false, false, isOld, pinned, isOld ? $"dotnet-core-uninstall remove {sdk.Version} --sdk" : null);
                items.Add(info);
                onFound(info);
            }
            var folders = ToolchainFolders.Discover(_locations);
            progress.SetPhase("Measuring SDKs and tools", folders.Count);
            await Parallel.ForEachAsync(folders, ct, (folder, token) =>
            {
                var removable = ToolchainFolders.CanRemove(folder.Path, _locations.Home);
                var info = new ToolchainInfo(Guid.NewGuid().ToString("N"), folder.Kind, folder.Name, folder.Path,
                    Measure(folder.Path, token),
                    folder.Note + (removable ? "" : " Removal is unavailable because this folder is outside your profile or uses a link."), removable, folder.Recycle, folder.Kind == "jetbrains");
                items.Add(info);
                onFound(info);
                progress.CompleteOne(folder.Path);
                return ValueTask.CompletedTask;
            });
            ct.ThrowIfCancellationRequested();
            var completed = items.OrderByDescending(x => x.Bytes).ToArray();
            _scanned = completed.ToDictionary(x => x.Id);
            progress.Flush();
            return new(completed, warnings);
        }
        finally { _gate.Release(); }
    }

    public async Task<long> RemoveAsync(string id, OpReporter op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var item = _scanned.GetValueOrDefault(id);
            if (item is null || !item.CanRemove || item.Kind == "dotnet" || !ToolchainFolders.CanRemove(item.Path, _locations.Home))
                throw new InvalidOperationException("Scan tools again and choose a removable per-user folder.");
            SafeDeleter.EnsureDeletable(item.Path);
            if (item.Recycle)
            {
                op.Phase("Moving to the Recycle Bin");
                await Task.Run(() => SafeDeleter.MoveToRecycleBin(item.Path), ct);
                _scanned = _scanned.Where(x => x.Key != id).ToDictionary(x => x.Key, x => x.Value);
                return 0;
            }
            long freed = 0;
            await op.DeleteWithProgressAsync("Removing " + item.Name, item.Bytes, counter =>
            {
                SafeDeleter.DeletePermanently(item.Path, ct, counter);
                freed = counter.Bytes;
            }, ct);
            _scanned = _scanned.Where(x => x.Key != id).ToDictionary(x => x.Key, x => x.Value);
            return freed;
        }
        finally { _gate.Release(); }
    }

    private static long Measure(string path, CancellationToken ct)
    {
        try { return Directory.Exists(path) ? DirectorySizer.MeasureSequential(path, ct).Bytes : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
