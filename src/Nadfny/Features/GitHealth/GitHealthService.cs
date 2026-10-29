using System.Collections.Concurrent;
using Nadfny.Core;

namespace Nadfny.Features.GitHealth;

/// <summary>
/// Repository housekeeping: scans every main repository under the scan locations, then runs the three cleanups
/// (git gc, LFS prune, deleting branches gone from the remote). Actions only touch repositories and branches
/// that the last scan reported; git steps on one repository run one at a time.
/// </summary>
public sealed class GitHealthService
{
    private const int ParallelRepos = 4;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BranchTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, RepoHealth> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _lfsInstalled;

    public async Task<IReadOnlyList<RepoHealth>> ScanAsync(IReadOnlyList<string> roots, Action<RepoHealth> onFound, ScanProgress progress, CancellationToken ct)
    {
        var repos = await GitRepoFinder.FindAsync(roots, progress, ct);

        progress.SetPhase("Checking repositories", repos.Count);
        _lfsInstalled = await RepoHealthReader.IsLfsInstalledAsync(ct);
        _repos.Clear();
        var results = new ConcurrentBag<RepoHealth>();
        await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = ParallelRepos, CancellationToken = ct }, async (repo, token) =>
        {
            var health = await RepoHealthReader.AnalyzeAsync(repo, _lfsInstalled, token);
            Remember(health);
            results.Add(health);
            progress.CompleteOne(repo);
            onFound(health);
        });

        progress.Flush();
        return results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<CleanupResult> GcAsync(string id, OpReporter op, CancellationToken ct)
    {
        var repo = Known(id).Path;
        var gate = Gate(repo);
        await gate.WaitAsync(ct);
        try
        {
            op.Phase("Measuring .git");
            var before = DirectorySizer.Measure(Path.Combine(repo, ".git"), ct).Bytes;
            op.Phase("Running git gc");
            var res = await ProcessRunner.RunAsync("git", ["-C", repo, "gc"], repo, WriteTimeout, ct);
            if (!res.Ok) throw new InvalidOperationException($"git gc did not finish: {res.Error}");
            op.Phase("Measuring .git");
            var health = await RepoHealthReader.AnalyzeAsync(repo, _lfsInstalled, ct);
            Remember(health);
            return new CleanupResult(before, health);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CleanupResult> LfsPruneAsync(string id, OpReporter op, CancellationToken ct)
    {
        var repo = Known(id).Path;
        var gate = Gate(repo);
        await gate.WaitAsync(ct);
        try
        {
            op.Phase("Checking Git LFS");
            if (!await RepoHealthReader.IsLfsInstalledAsync(ct))
                throw new InvalidOperationException("Git LFS is not installed, so its cache cannot be pruned.");
            op.Phase("Measuring LFS cache");
            var before = RepoHealthReader.MeasureLfs(repo, ct);
            op.Phase("Pruning LFS cache");
            var res = await ProcessRunner.RunAsync("git", ["-C", repo, "lfs", "prune"], repo, WriteTimeout, ct);
            if (!res.Ok) throw new InvalidOperationException($"git lfs prune did not finish: {res.Error}");
            op.Phase("Measuring .git");
            var health = await RepoHealthReader.AnalyzeAsync(repo, _lfsInstalled, ct);
            Remember(health);
            return new CleanupResult(before, health);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BranchCleanupResult> DeleteBranchesAsync(string id, IReadOnlyList<string> names, OpReporter op, CancellationToken ct)
    {
        var known = Known(id);
        var requested = names.Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count == 0) throw new InvalidOperationException("No branches were selected.");
        var unknown = requested.FirstOrDefault(n => !known.GoneBranches.Contains(n));
        if (unknown is not null) throw new InvalidOperationException($"{unknown} is not in the last check. Check repositories again and retry.");

        var gate = Gate(known.Path);
        var deleted = new List<string>();
        var kept = new List<KeptBranch>();
        await gate.WaitAsync(ct);
        try
        {
            op.Phase("Checking current branch and worktrees", requested.Count);
            var checkedOut = await CheckedOutAsync(known.Path, ct);
            foreach (var name in requested)
            {
                ct.ThrowIfCancellationRequested();
                if (checkedOut.Contains(name))
                    kept.Add(new KeptBranch(name, "It is checked out, so it was kept."));
                else
                {
                    // Lower-case -d only: git refuses branches with commits that are not in the current branch.
                    var res = await ProcessRunner.RunAsync("git", ["-C", known.Path, "branch", "-d", name], known.Path, BranchTimeout, ct);
                    if (res.Ok) deleted.Add(name);
                    else kept.Add(new KeptBranch(name, Reason(res)));
                }
                op.Report(deleted.Count + kept.Count);
            }
        }
        finally
        {
            gate.Release();
        }

        var health = await RepoHealthReader.AnalyzeAsync(known.Path, _lfsInstalled, ct);
        Remember(health);
        return new BranchCleanupResult(deleted, kept, health);
    }

    /// <summary>Refuses to continue when the worktree list cannot be read: then nothing can be proven safe.</summary>
    private static async Task<IReadOnlySet<string>> CheckedOutAsync(string repo, CancellationToken ct)
    {
        var worktrees = await RepoHealthReader.Git(repo, ct, "worktree", "list", "--porcelain");
        if (!worktrees.Ok) throw new InvalidOperationException($"Could not read the worktree list, so nothing was deleted. {worktrees.Error}");
        var head = await RepoHealthReader.Git(repo, ct, "symbolic-ref", "--short", "HEAD");
        var set = GitHealthParser.CheckedOutBranches(worktrees.StdOut).ToHashSet(StringComparer.Ordinal);
        if (head.Ok && head.StdOut.Trim() is { Length: > 0 } current) set.Add(current);
        return set;
    }

    private static string Reason(ProcessResult res)
    {
        if (res.StdErr.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
            return "It has commits that are not in the current branch, so git kept it.";
        var first = res.Error.Split('\n')[0].Trim();
        return first.Length > 0 ? first : "git could not delete it.";
    }

    private RepoHealth Known(string id) =>
        _repos.TryGetValue(id, out var repo)
            ? repo
            : throw new InvalidOperationException("That repository was not part of the last check. Check repositories again and retry.");

    private void Remember(RepoHealth health) => _repos[health.Path] = health;

    private SemaphoreSlim Gate(string repo) => _repoLocks.GetOrAdd(repo, _ => new SemaphoreSlim(1, 1));
}
