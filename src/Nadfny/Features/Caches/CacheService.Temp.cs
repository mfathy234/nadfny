using Nadfny.Core;

namespace Nadfny.Features.Caches;

/// <summary>The Temp breakdown: what is inside Temp grouped by name pattern, and clearing one group at a time.</summary>
public sealed partial class CacheService
{
    public const string BrokenLinksId = "::broken-links";
    public const string AiToolsId = "::ai-tools";
    public const string KeptByYouId = "::kept-by-you";
    private const int TopGroups = 12;

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _tempGroups = new Dictionary<string, IReadOnlyList<string>>();

    private sealed class Tally(string id, string pattern, string kind)
    {
        public readonly List<string> Paths = [];
        public int Count, RecentCount;
        public long Bytes, RecentBytes;

        public void Add(string path, long bytes, bool recent)
        {
            Paths.Add(path);
            Count++;
            Bytes += bytes;
            if (!recent) return;
            RecentCount++;
            RecentBytes += bytes;
        }

        public TempGroup ToGroup() => new(id, pattern, kind, Count, Bytes, RecentCount, RecentBytes);
    }

    public async Task<TempBreakdown> BreakdownAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = TempDefinition();
            return await Task.Run(() => BreakdownCore(def.Path, ct), ct);
        }
        finally { _gate.Release(); }
    }

    private CacheDefinition TempDefinition()
    {
        var def = _scanned.GetValueOrDefault(TempId) ?? throw new InvalidOperationException("Scan caches again before opening the Temp breakdown.");
        if (!Directory.Exists(def.Path)) throw new InvalidOperationException("The temp folder no longer exists. Scan caches again.");
        SafeDeleter.EnsureDeletable(def.Path);
        CheckTempRoot(def);
        return def;
    }

    private TempBreakdown BreakdownCore(string root, CancellationToken ct)
    {
        var keepList = _keepList();
        var userKept = TempKeepList.Compile(keepList);
        var keepAfter = (DateTime.UtcNow - RecentWindow).Ticks;
        var entries = DirectorySizer.Shallow(root).Select(c => (IsDir: c.IsDir, c.Path, Length: c.Length, c.WriteTicks, Link: false))
            .Concat(DirectorySizer.DanglingLinks(root).Select(l => (IsDir: false, Path: l, Length: 0L, WriteTicks: 0L, Link: true)))
            .ToArray();

        var measured = new (long Bytes, long LastTicks)[entries.Length];
        Parallel.For(0, entries.Length, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var e = entries[i];
            if (!e.IsDir) { measured[i] = (e.Length, e.WriteTicks); return; }
            var size = DirectorySizer.MeasureSequential(e.Path, ct);
            measured[i] = (size.Bytes, Math.Max(size.LastWriteUtc.Ticks, e.WriteTicks));
        });

        var tallies = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var name = Path.GetFileName(e.Path);
            var (id, pattern, kind) = Classify(name, e.IsDir, e.Link, userKept);
            if (!tallies.TryGetValue(id, out var tally)) tallies[id] = tally = new Tally(id, pattern, kind);
            tally.Add(e.Path, measured[i].Bytes, measured[i].LastTicks > keepAfter);
        }

        var clearable = tallies.Values.Where(t => t.ToGroup().Kind is "normal" or "links").ToList();
        _tempGroups = clearable.ToDictionary(t => t.ToGroup().Id, t => (IReadOnlyList<string>)t.Paths, StringComparer.OrdinalIgnoreCase);
        return Summarize(root, clearable, tallies.Values.Where(t => t.ToGroup().Kind is "ai" or "user"), keepList);
    }

    private static (string Id, string Pattern, string Kind) Classify(string name, bool isDir, bool isLink, Func<string, bool> userKept)
    {
        if (TempKeepRules.IsAiToolFolder(name)) return (AiToolsId, "AI tools", "ai");
        if (userKept(name)) return (KeptByYouId, "Kept by you", "user");
        if (isLink) return (BrokenLinksId, "Broken links", "links");
        var pattern = TempGroupName.Of(name, isDir);
        return (pattern, pattern, "normal");
    }

    private static TempBreakdown Summarize(string root, List<Tally> clearable, IEnumerable<Tally> kept, IReadOnlyList<string> keepList)
    {
        var all = clearable.Select(t => t.ToGroup()).ToList();
        var names = all.Where(g => g.Kind == "normal").OrderByDescending(g => g.Bytes).ThenByDescending(g => g.Count).ToList();
        var shown = names.Take(TopGroups).Concat(all.Where(g => g.Kind == "links")).ToList();
        var rest = names.Skip(TopGroups).ToList();
        TempOther? other = rest.Count == 0 ? null : new(rest.Count, rest.Sum(g => g.Count), rest.Sum(g => g.Bytes), rest.Sum(g => g.RecentBytes));
        return new TempBreakdown(root, shown, other, kept.Select(t => t.ToGroup()).OrderBy(g => g.Kind).ToList(), keepList) { All = all };
    }

    /// <summary>Clears one breakdown group. Only entries that were in the group when the breakdown was made and still match are removed.</summary>
    public async Task<long> ClearTempGroupAsync(string groupId, OpReporter op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var members = _tempGroups.GetValueOrDefault(groupId)
                ?? throw new InvalidOperationException("Open the Temp breakdown again before clearing this group.");
            var def = TempDefinition();
            var userKept = TempKeepList.Compile(_keepList());
            var entries = members.Select(path => StillInGroup(def.Path, groupId, path, userKept)).OfType<(bool, string, long, long)>().ToList();
            var targets = await PickTargetsAsync(entries, keepsRecent: true, op, ct);
            return await DeleteTargetsAsync("Deleting from Temp", targets, op, ct);
        }
        finally { _gate.Release(); }
    }

    private static (bool IsDir, string Path, long Length, long WriteTicks)? StillInGroup(string root, string groupId, string path, Func<string, bool> userKept)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Path.TrimEndingDirectorySeparator(parent).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(path);
        if (IsKeptName(name, userKept)) return null;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var isDir = attributes.HasFlag(FileAttributes.Directory);
        var isLink = isDir && attributes.HasFlag(FileAttributes.ReparsePoint);
        var writeTicks = (isDir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)).Ticks;
        if (groupId == BrokenLinksId) return isLink && DirectorySizer.IsDangling(path) ? (false, path, 0L, 0L) : null;
        if (isLink || !TempGroupName.Of(name, isDir).Equals(groupId, StringComparison.OrdinalIgnoreCase)) return null;
        return isDir ? (true, path, 0L, writeTicks) : (false, path, DirectorySizer.OnDisk(new FileInfo(path)), writeTicks);
    }
}
