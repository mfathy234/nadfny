using Nadfny.Core;

namespace Nadfny.Features.Explore;

public sealed record DriveSummary(string Name, string Label, long TotalBytes, long FreeBytes);
public sealed record ExploreEntry(string Path, string Name, bool IsDirectory, long? Bytes, long Files, DateTime? LastWriteUtc);

public sealed class ExploreService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DriveSummary> Drives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveSummary(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace))
            .ToList();

    /// <summary>Lists a folder: files are sized immediately, sub-folders are streamed in as their sizes finish.</summary>
    public async Task<IReadOnlyList<ExploreEntry>> ListAsync(string path, Action<ExploreEntry> onSized, CancellationToken ct)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Folder not found: {path}");
        var children = DirectorySizer.Shallow(path);
        var files = children.Where(c => !c.IsDir)
            .Select(c => new ExploreEntry(c.Path, Path.GetFileName(c.Path), false, c.Length, 1, new DateTime(c.WriteTicks, DateTimeKind.Utc)))
            .ToList();
        var dirs = children.Where(c => c.IsDir).Select(c => c.Path).ToList();

        var sized = new System.Collections.Concurrent.ConcurrentBag<ExploreEntry>();
        await Parallel.ForEachAsync(dirs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, (dir, token) =>
        {
            var r = DirectorySizer.MeasureSequential(dir, token);
            var entry = new ExploreEntry(dir, Path.GetFileName(dir), true, r.Bytes, r.Files, r.LastWriteUtc == DateTime.MinValue ? null : r.LastWriteUtc);
            sized.Add(entry);
            _sizes[dir] = r.Bytes;
            onSized(entry);
            return ValueTask.CompletedTask;
        });

        return sized.Concat(files).OrderByDescending(e => e.Bytes ?? 0).ToList();
    }

    public async Task DeleteAsync(string path, bool permanent, OpReporter op, CancellationToken ct)
    {
        if (!permanent)
        {
            // Windows shows its own progress window for large folders; we show the step.
            op.Phase("Moving to the Recycle Bin");
            await Task.Run(() => SafeDeleter.MoveToRecycleBin(path), ct);
            return;
        }
        var expected = _sizes.TryGetValue(path, out var known) ? known : File.Exists(path) ? new FileInfo(path).Length : 0;
        await op.DeleteWithProgressAsync("Deleting permanently", expected, counter => SafeDeleter.DeletePermanently(path, ct, counter), ct);
        _sizes.TryRemove(path, out _);
    }
}
