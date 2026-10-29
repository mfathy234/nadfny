using Nadfny.Core;

namespace Nadfny.Features.Finder;

public sealed class FinderService(Func<string>? downloads = null)
{
    private readonly Func<string> _downloads = downloads ?? FinderNative.Downloads;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, FinderReport> _reports = new();
    private IReadOnlyList<DuplicateGroup> _duplicateSafety = [];

    public async Task<FinderReport> ScanAsync(string mode, IReadOnlyList<string> roots, int value,
        Action<FinderItem> emit, ScanProgress progress, CancellationToken ct)
    {
        Validate(mode, value);
        await _gate.WaitAsync(ct);
        try
        {
            _reports.Remove(mode);
            progress.SetPhase(mode == "downloads" ? "Checking Downloads" : "Looking through scan locations");
            var files = new List<FinderFile>();
            if (mode == "downloads")
            {
                var root = SafeDeleter.Canonical(_downloads());
                var cutoff = DateTime.UtcNow.AddDays(-value);
                foreach (var child in DirectorySizer.Shallow(root))
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Visit(child.Path);
                    var item = DownloadChild(child, root, cutoff, ct);
                    if (item is null) continue;
                    files.Add(item);
                    emit(new(mode, item, null));
                }
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var minimum = mode == "large" ? value * 1024L * 1024 : 1024 * 1024;
                foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var file in FinderRules.Enumerate(SafeDeleter.Canonical(root), minimum, progress, ct))
                    {
                        if (!seen.Add(file.Path) || file.Bytes == 0 || (mode == "large" && file.Bytes <= minimum)) continue;
                        files.Add(file);
                        if (mode == "large") emit(new(mode, file, null));
                    }
            }
            var groups = mode == "duplicates"
                ? await DuplicateFinder.FindAsync(files, g => emit(new(mode, null, g)), progress, ct) : [];
            ct.ThrowIfCancellationRequested();
            var report = new FinderReport(mode == "duplicates" ? groups.SelectMany(g => g.Files).ToList() : files.OrderByDescending(f => f.Bytes).ToList(), groups);
            _reports[mode] = report;
            if (mode == "duplicates") _duplicateSafety = groups;
            progress.Flush();
            return report;
        }
        finally { _gate.Release(); }
    }

    public async Task<FinderRemoval> RemoveAsync(string mode, IReadOnlyList<string> paths, OpReporter op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_reports.TryGetValue(mode, out var report)) throw new InvalidOperationException("Scan this tab before removing files.");
            var selected = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
                report.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("That item is not in the latest scan.")).ToList();
            foreach (var file in selected)
            {
                SafeDeleter.EnsureDeletable(file.Path);
                if (!Current(file)) throw new InvalidOperationException("An item changed since the scan. Scan again before removing it.");
            }
            await ValidateKeepers(selected, ct);
            var removed = new List<string>();
            var failed = new List<string>();
            long bytes = 0;
            op.Phase("Moving to the Recycle Bin", selected.Sum(f => f.Bytes));
            foreach (var file in selected)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await ValidateKeepers([file], ct);
                    if (!Current(file)) throw new IOException("The item changed since the scan.");
                    await Task.Run(() => SafeDeleter.MoveToRecycleBin(file.Path), ct);
                    if (File.Exists(file.Path) || Directory.Exists(file.Path)) throw new IOException("The item could not be moved.");
                    removed.Add(file.Path);
                    bytes += file.Bytes;
                    // Invalidate every tab's reference, including descendants of a Downloads folder.
                    foreach (var key in _reports.Keys.ToList())
                    {
                        var previous = _reports[key];
                        _reports[key] = previous with { Files = previous.Files.Where(f => !SafeDeleter.IsUnder(f.Path, file.Path)).ToList() };
                    }
                }
                catch (OperationCanceledException)
                {
                    failed.AddRange(selected.SkipWhile(f => f.Path != file.Path).Select(f => f.Path));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { failed.Add(file.Path); }
                op.Report(bytes, file.Path);
            }
            op.Flush();
            return new(removed, failed, bytes);
        }
        finally { _gate.Release(); }
    }

    private async Task ValidateKeepers(IReadOnlyList<FinderFile> selected, CancellationToken ct)
    {
        bool Removing(FinderFile file) => selected.Any(s => SafeDeleter.IsUnder(file.Path, s.Path));
        foreach (var group in _duplicateSafety.Where(g => g.Files.Any(Removing)))
        {
            var keepers = group.Files.Where(f => !Removing(f) && Current(f)).ToList();
            var valid = false;
            foreach (var keeper in keepers)
            {
                try
                {
                    using var stream = DuplicateFinder.Open(keeper.Path);
                    if (await DuplicateFinder.HashAsync(stream, false, ct) == group.Id && Current(keeper)) { valid = true; break; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (!valid) throw new InvalidOperationException("Keep at least one unchanged copy of every duplicate group. Scan again if a copy is missing.");
        }
    }

    private static bool Current(FinderFile file)
    {
        try
        {
            if (!file.IsDirectory) return DuplicateFinder.Unchanged(file);
            if (!Directory.Exists(file.Path) || !FinderRules.SafeScanPath(file.Path)) return false;
            return Directory.GetLastWriteTimeUtc(file.Path) == file.LastWriteUtc && FolderSnapshot.Capture(file.Path, default) == file.Fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static FinderFile? DownloadChild((bool IsDir, string Path, long Length, long WriteTicks) child, string root, DateTime cutoff, CancellationToken ct)
    {
        try
        {
            if (FinderRules.SkipFile(Path.GetFileName(child.Path), File.GetAttributes(child.Path)) ||
                (child.IsDir && !FinderRules.SafeScanPath(child.Path)) || child.WriteTicks > cutoff.Ticks) return null;
            var size = child.IsDir ? DirectorySizer.MeasureSequential(child.Path, ct) : default;
            if (child.IsDir && size.LastWriteUtc > cutoff) return null;
            return new FinderFile(child.Path, Path.GetFileName(child.Path), root,
                child.IsDir ? size.Bytes : child.Length, child.IsDir ? 0 : new FileInfo(child.Path).Length,
                new DateTime(child.WriteTicks, DateTimeKind.Utc), FinderRules.Type(child.Path, child.IsDir), child.IsDir,
                child.IsDir ? FolderSnapshot.Capture(child.Path, ct) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Validate(string mode, int value)
    {
        if (mode switch { "large" => value is 100 or 500 or 1024, "downloads" => value is 30 or 90 or 180, "duplicates" => value == 0, _ => false }) return;
        throw new InvalidOperationException("Choose a supported Finder tab and filter.");
    }
}
