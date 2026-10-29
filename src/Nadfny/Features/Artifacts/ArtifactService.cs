using System.Collections.Concurrent;
using System.Threading.Channels;
using Nadfny.Core;

namespace Nadfny.Features.Artifacts;

public sealed record ArtifactInfo(string Path, string Kind, string Tool, string Project, string ProjectPath, long Bytes, long Files, DateTime? LastWriteUtc, DateTime? ProjectLastUsedUtc);

public sealed class ArtifactService
{
    private const int MaxDepth = 14;
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly ConcurrentDictionary<string, long> _known = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ArtifactInfo>> ScanAsync(IReadOnlyList<string> roots, Action<ArtifactInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        _known.Clear();
        var found = Channel.CreateUnbounded<Hit>(new UnboundedChannelOptions { SingleWriter = false });
        var results = new ConcurrentBag<ArtifactInfo>();
        var lastUsed = new ConcurrentDictionary<string, Lazy<DateTime?>>(StringComparer.OrdinalIgnoreCase);
        var hits = 0;
        var sized = 0;
        var walking = true;

        var sizing = Task.Run(async () =>
        {
            await Parallel.ForEachAsync(found.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, (hit, token) =>
            {
                var size = DirectorySizer.MeasureSequential(hit.Path, token);
                Interlocked.Increment(ref sized);
                if (!walking) progress.CompleteOne(hit.Path);
                if (size.Bytes == 0) return ValueTask.CompletedTask;
                var info = new ArtifactInfo(hit.Path, hit.Rule.Kind, hit.Rule.Tool, Path.GetFileName(hit.Project), hit.Project,
                    size.Bytes, size.Files, size.LastWriteUtc == DateTime.MinValue ? null : size.LastWriteUtc,
                    lastUsed.GetOrAdd(hit.Project, p => new Lazy<DateTime?>(() => ProjectActivity.LastUsedUtc(p, token))).Value);
                _known[info.Path] = info.Bytes;
                results.Add(info);
                onFound(info);
                return ValueTask.CompletedTask;
            });
        }, ct);

        try
        {
            progress.SetPhase("Searching folders");
            await TreeWalker.RunAsync(roots, MaxDepth, (dir, children) => Visit(dir, children, found.Writer, ref hits), progress, ct);
        }
        finally
        {
            found.Writer.TryComplete();
        }

        // Walk done: the bar now tracks folders still being measured.
        walking = false;
        progress.SetPhase("Measuring folders", Volatile.Read(ref hits));
        for (var i = Volatile.Read(ref sized); i > 0; i--) progress.CompleteOne();
        await sizing;
        progress.Flush();
        return results.OrderByDescending(r => r.Bytes).ToList();
    }

    /// <summary>Read-only: what the guard would say about each folder, without deleting anything.</summary>
    public Task<IReadOnlyList<GuardVerdict>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        ArtifactGuard.EvaluateAsync(paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToList(), ct);

    public async Task<(int Removed, long Bytes, IReadOnlyList<string> Failed)> RemoveAsync(IReadOnlyList<string> paths, OpReporter op, CancellationToken ct)
    {
        var failed = new ConcurrentBag<string>();
        long bytes = 0;
        var removed = 0;

        var known = new List<string>();
        foreach (var p in paths)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            if (_known.ContainsKey(full)) known.Add(full);
            else failed.Add($"{full}: not part of the last scan");
        }

        // Source-code guard runs right before deleting, so it also catches files added after the scan.
        op.Phase($"Checking {known.Count} folders for source code");
        var verdicts = await ArtifactGuard.EvaluateAsync(known, ct);
        foreach (var v in verdicts.Where(v => !v.Deletable)) failed.Add($"{v.Path}: {v.Reason}");
        var deletable = verdicts.Where(v => v.Deletable).Select(v => v.Path).ToList();
        var expected = deletable.Sum(p => _known.TryGetValue(p, out var size) ? size : 0);

        await op.DeleteWithProgressAsync($"Deleting {deletable.Count} folders", expected, counter =>
            Parallel.ForEach(deletable, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, full =>
            {
                if (!_known.TryGetValue(full, out var size)) return;
                try
                {
                    SafeDeleter.DeletePermanently(full, ct, counter);
                    _known.TryRemove(full, out _);
                    Interlocked.Add(ref bytes, size);
                    Interlocked.Increment(ref removed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed.Add($"{full}: {ex.Message}");
                }
            }), ct);
        return (removed, bytes, failed.ToList());
    }

    private static IEnumerable<string> Visit(string dir, IReadOnlyList<TreeWalker.Entry> children, ChannelWriter<Hit> writer, ref int hits)
    {
        var names = new HashSet<string>(children.Select(c => Path.GetFileName(c.Path)), StringComparer.OrdinalIgnoreCase);
        // Dot-folders directly in the user profile are tool installs (.vscode, .nvm, .cargo...): nothing there is project output.
        var inProfileRoot = string.Equals(Path.TrimEndingDirectorySeparator(dir), UserProfile, StringComparison.OrdinalIgnoreCase);
        var descend = new List<string>();
        foreach (var c in children)
        {
            if (!c.IsDir) continue;
            var name = Path.GetFileName(c.Path);
            if (inProfileRoot && name.StartsWith('.')) continue;
            if (ArtifactRules.MatchFolder(c.Path, names) is { } rule)
            {
                writer.TryWrite(new Hit(c.Path, rule, dir)); // never descend into an artifact (nested node_modules belong to it)
                Interlocked.Increment(ref hits);
            }
            else
            {
                descend.Add(c.Path);
            }
        }
        return descend;
    }

    private sealed record Hit(string Path, ArtifactRule Rule, string Project);
}
