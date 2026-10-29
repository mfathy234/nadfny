namespace Nadfny.Features.History;

public sealed record HistoryItem(string Id, string Name, long Bytes);
public sealed record HistoryCategory(string Id, long TotalBytes, IReadOnlyList<HistoryItem> Items);
public sealed record HistoryDrive(string Name, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}
public sealed record HistorySnapshot(DateTime Timestamp, IReadOnlyList<HistoryDrive> Drives, HistoryCategory? Category);
public sealed record ItemGrowth(string Category, string Id, string Name, long Bytes, DateTime Since, bool IsNew, long CurrentBytes);
public sealed record CategoryGrowth(string Category, long Bytes, DateTime Since);
public sealed record DriveChange(string Name, long FreeBytes, DateTime Since);
public sealed record HistoryReport(string Path, long StoredBytes, DateTime? Since,
    IReadOnlyList<ItemGrowth> Growth, IReadOnlyList<ItemGrowth> Hints,
    IReadOnlyList<CategoryGrowth> Categories, IReadOnlyList<DriveChange> Drives,
    IReadOnlyList<HistorySnapshot> Series);

public static class HistoryGrowth
{
    public static HistoryReport Compute(IReadOnlyList<HistorySnapshot> snapshots, string path, long storedBytes)
    {
        var growth = new List<ItemGrowth>();
        var hints = new List<ItemGrowth>();
        var categories = new List<CategoryGrowth>();
        foreach (var group in snapshots.Where(s => s.Category != null).GroupBy(s => s.Category!.Id))
        {
            var pair = group.OrderByDescending(s => s.Timestamp).Take(2).ToArray();
            if (pair.Length < 2) continue;
            var current = pair[0].Category!;
            var previous = pair[1].Category!;
            categories.Add(new(group.Key, current.TotalBytes - previous.TotalBytes, pair[1].Timestamp));
            var oldItems = UniqueItems(previous.Items).ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var item in UniqueItems(current.Items))
            {
                var known = oldItems.TryGetValue(item.Id, out var old);
                var delta = item.Bytes - (old?.Bytes ?? 0);
                if (delta <= 0) continue;
                var change = new ItemGrowth(group.Key, item.Id, item.Name, delta, pair[1].Timestamp, !known, item.Bytes);
                growth.Add(change);
                // An item outside the previous top 200 has no measured baseline.
                if (known && delta > 100 * 1024 * 1024 && delta > old!.Bytes * 0.1) hints.Add(change);
            }
        }
        var drives = new List<DriveChange>();
        foreach (var drive in snapshots.SelectMany(s => s.Drives.Select(d => (s.Timestamp, Drive: d))).GroupBy(x => x.Drive.Name))
        {
            var pair = drive.OrderByDescending(x => x.Timestamp).Take(2).ToArray();
            if (pair.Length == 2) drives.Add(new(drive.Key, pair[0].Drive.FreeBytes - pair[1].Drive.FreeBytes, pair[1].Timestamp));
        }
        var series = snapshots.GroupBy(s => s.Timestamp.Date).Select(g => g.Last() with { Category = null }).ToArray();
        var since = categories.Select(c => (DateTime?)c.Since).Min() ?? drives.Select(d => (DateTime?)d.Since).Min();
        return new(path, storedBytes, since, growth.OrderByDescending(g => g.Bytes).Take(5).ToArray(), hints,
            categories.OrderByDescending(c => c.Bytes).ToArray(), drives, series);
    }

    public static IEnumerable<HistoryItem> UniqueItems(IEnumerable<HistoryItem> items) => items
        .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(i => i.Bytes)!);
}
