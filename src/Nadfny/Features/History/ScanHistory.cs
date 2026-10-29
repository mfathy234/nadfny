using System.Diagnostics;
using Nadfny.Features.Apps;
using Nadfny.Features.Artifacts;
using Nadfny.Features.Caches;
using Nadfny.Features.Docker;
using Nadfny.Features.Worktrees;

namespace Nadfny.Features.History;

public sealed class ScanHistory
{
    private readonly HistoryService _history;
    private readonly Action _changed;

    public ScanHistory(Action changed, HistoryService? history = null)
    {
        _history = history ?? new();
        _changed = changed;
        Save(null);
    }

    public Task<HistoryReport> ReportAsync() => _history.ReportAsync();
    public async Task<bool> ClearAsync()
    {
        await _history.ClearAsync();
        _changed();
        return true;
    }

    public async Task<T> CaptureAsync<T>(Task<T> scan, CancellationToken ct)
    {
        var result = await scan;
        ct.ThrowIfCancellationRequested();
        try { Save(Category(result)); }
        catch (Exception ex) { Debug.WriteLine($"History capture: {ex}"); }
        return result;
    }

    private void Save(HistoryCategory? category)
    {
        var write = _history.RecordAsync(category);
        _ = NotifyAsync(write);
    }

    private async Task NotifyAsync(Task write)
    {
        try { await write; _changed(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private static HistoryCategory? Category<T>(T result) => result switch
    {
        IReadOnlyList<WorktreeInfo> rows => From("worktrees", rows.Select(w => new HistoryItem(w.Path, w.Name, w.Bytes))),
        IReadOnlyList<ArtifactInfo> rows => From("artifacts", rows.Select(a => new HistoryItem(a.Path, a.Path, a.Bytes))),
        IReadOnlyList<CacheInfo> rows => From("caches", rows.Select(c => new HistoryItem(c.Id, c.Name, c.Bytes))),
        IReadOnlyList<AppInfo> rows => From("apps", rows.Select(a => new HistoryItem(a.Id, a.Name, a.Bytes))),
        DockerReport r when r.Running && r.Message is null => new("docker",
            r.ImagesBytes + r.ContainersBytes + r.VolumesBytes + r.BuildCacheBytes,
            r.Images.Select(i => new HistoryItem("image:" + i.Id, $"{i.Repository}:{i.Tag}", i.UniqueBytes))
                .Concat(r.Containers.Select(c => new HistoryItem("container:" + c.Id, c.Name, c.Bytes)))
                .Concat(r.Volumes.Select(v => new HistoryItem("volume:" + v.Name, v.Name, v.Bytes)))
                .Append(new("build-cache", "Build cache", r.BuildCacheBytes)).ToArray()),
        _ => null
    };

    private static HistoryCategory From(string id, IEnumerable<HistoryItem> items)
    {
        var list = items.ToArray();
        return new(id, list.Sum(i => i.Bytes), list);
    }
}
