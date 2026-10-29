using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nadfny.Core;
using Nadfny.Features.Explore;

namespace Nadfny.Features.History;

public sealed class HistoryService
{
    private readonly string _root;
    private readonly Func<DateTime> _clock;
    private readonly Func<IReadOnlyList<HistoryDrive>> _drives;
    private readonly long _cap;
    private readonly object _sync = new();
    private Task _pending = Task.CompletedTask;
    private DateTime _lastTimestamp;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public HistoryService(string? root = null, Func<DateTime>? clock = null,
        Func<IReadOnlyList<HistoryDrive>>? drives = null, long cap = 5 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nadfny", "history"));
        _clock = clock ?? (() => DateTime.UtcNow);
        _drives = drives ?? (() => ExploreService.Drives().Select(d => new HistoryDrive(d.Name, d.TotalBytes, d.FreeBytes)).ToArray());
        _cap = cap;
    }

    private Task<T> Queue<T>(Func<Task<T>> action)
    {
        lock (_sync)
        {
            var previous = _pending;
            var next = Task.Run(async () =>
            {
                try { await previous; }
                catch (Exception ex) { Debug.WriteLine(ex); }
                return await action();
            });
            _pending = next;
            return next;
        }
    }

    public Task RecordAsync(HistoryCategory? category) => Queue(async () =>
    {
        try
        {
            CheckFolder();
            Directory.CreateDirectory(_root);
            var now = _clock();
            // Consecutive scans can finish within one clock tick.
            if (now <= _lastTimestamp) now = _lastTimestamp.AddTicks(1);
            _lastTimestamp = now;
            var compact = category is null ? null : category with
            {
                Items = HistoryGrowth.UniqueItems(category.Items).OrderByDescending(i => i.Bytes).Take(200).ToArray()
            };
            var snapshot = new HistorySnapshot(now, _drives(), compact);
            var data = JsonSerializer.Serialize(snapshot, Options);
            var filename = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";
            // A single oversized record must not displace the entire history.
            if (Encoding.UTF8.GetByteCount(data) + 2 <= _cap)
                await File.AppendAllTextAsync(Path.Combine(_root, filename), "\n" + data + "\n");
            Prune(now);
        }
        catch (Exception ex) { Debug.WriteLine($"History snapshot: {ex}"); }
        return true;
    });

    public Task<IReadOnlyList<HistorySnapshot>> ReadAsync() => Queue(() => Task.FromResult(Read()));

    public Task<HistoryReport> ReportAsync() => Queue(() =>
    {
        var snapshots = Read();
        long size = 0;
        try { size = Files().Sum(f => f.Length); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return Task.FromResult(HistoryGrowth.Compute(snapshots, _root, size));
    });

    public Task ClearAsync() => Queue(() =>
    {
        CheckFolder();
        SafeDeleter.DeletePermanently(_root);
        return Task.FromResult(true);
    });

    private IReadOnlyList<HistorySnapshot> Read()
    {
        var result = new List<HistorySnapshot>();
        try
        {
            CheckFolder();
            Prune(_clock());
            foreach (var file in Files()) result.AddRange(ReadFile(file));
        }
        catch (Exception ex) { Debug.WriteLine($"History read: {ex}"); }
        return result.OrderBy(s => s.Timestamp).ToArray();
    }

    private FileInfo[] Files() => !Directory.Exists(_root) ? [] : new DirectoryInfo(_root).GetFiles("*.jsonl")
        .Where(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

    private void Prune(DateTime now)
    {
        var files = Files();
        var size = files.Sum(f => f.Length);
        var cutoff = now.AddDays(-90);
        foreach (var file in files)
        {
            var day = DateTime.ParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var length = file.Length;
            if (day < cutoff.Date || (size > _cap && file != files[^1]))
            {
                SafeDeleter.DeletePermanently(file.FullName);
                size -= length;
            }
            else if (size > _cap || day == cutoff.Date)
            {
                var lines = ReadFile(file).Where(s => s.Timestamp >= cutoff)
                    .Select(s => JsonSerializer.Serialize(s, Options)).ToArray();
                long kept = 0;
                var start = lines.Length;
                while (start > 0 && kept + Encoding.UTF8.GetByteCount(lines[start - 1]) + 1 <= _cap)
                    kept += Encoding.UTF8.GetByteCount(lines[--start]) + 1;
                File.WriteAllText(file.FullName, string.Concat(lines.Skip(start).Select(line => line + "\n")));
                size += kept - length;
            }
        }
    }

    private static IReadOnlyList<HistorySnapshot> ReadFile(FileInfo file)
    {
        var result = new List<HistorySnapshot>();
        foreach (var line in File.ReadLines(file.FullName))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var snapshot = JsonSerializer.Deserialize<HistorySnapshot>(line, Options);
                if (snapshot is { Drives: not null } && (snapshot.Category is null || snapshot.Category.Items is not null)) result.Add(snapshot);
            }
            catch (JsonException ex) { Debug.WriteLine($"History read: {ex}"); }
        }
        return result;
    }

    private void CheckFolder()
    {
        // Never read or write through a redirected history directory or parent.
        for (var dir = new DirectoryInfo(_root); dir != null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("History folder is redirected.");
    }
}
