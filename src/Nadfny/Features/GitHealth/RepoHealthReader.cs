using Nadfny.Core;

namespace Nadfny.Features.GitHealth;

/// <summary>Reads the health of one repository with read-only git commands. Never changes anything.</summary>
public static class RepoHealthReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    public static string LfsObjectsDir(string repo) => Path.Combine(repo, ".git", "lfs", "objects");

    public static async Task<bool> IsLfsInstalledAsync(CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("git", ["lfs", "version"], null, TimeSpan.FromSeconds(20), ct);
        return res.Ok;
    }

    public static async Task<RepoHealth> AnalyzeAsync(string repo, bool lfsInstalled, CancellationToken ct)
    {
        var gitDir = Path.Combine(repo, ".git");
        var sizeTask = Task.Run(() => DirectorySizer.Measure(gitDir, ct).Bytes, ct);
        var lfsTask = Task.Run(() => MeasureLfs(repo, ct), ct);
        var countTask = Git(repo, ct, "count-objects", "-v");
        var refsTask = Git(repo, ct, "for-each-ref", "--format=%(refname:lstrip=2)%09%(upstream:track)%09%(upstream:short)", "refs/heads");
        var worktreesTask = Git(repo, ct, "worktree", "list", "--porcelain");
        var headTask = Git(repo, ct, "symbolic-ref", "--short", "HEAD");
        var stashTask = Git(repo, ct, "stash", "list", "--format=%ct");
        await Task.WhenAll(sizeTask, lfsTask, countTask, refsTask, worktreesTask, headTask, stashTask);

        var counts = countTask.Result.Ok ? GitHealthParser.ParseCountObjects(countTask.Result.StdOut) : null;
        var gc = counts is { } c && (c.Count > 0 || c.Garbage > 0)
            ? new GcFinding(c.Count, c.SizeKiB * 1024, c.Garbage, c.SizeGarbageKiB * 1024, c.Packs)
            : null;

        var lfsBytes = lfsTask.Result;
        var lfs = lfsBytes > 0 ? new LfsFinding(lfsBytes, lfsInstalled) : null;

        var excluded = CheckedOutSet(worktreesTask.Result, headTask.Result);
        var gone = refsTask.Result.Ok
            ? GitHealthParser.GoneBranches(GitHealthParser.ParseBranchTracks(refsTask.Result.StdOut), excluded)
            : Array.Empty<string>();

        var (stashCount, oldestStash) = stashTask.Result.Ok ? GitHealthParser.ParseStashDates(stashTask.Result.StdOut) : (0, null);
        var stashFinding = stashCount > 0 ? new StashFinding(stashCount, oldestStash) : null;

        return new RepoHealth(repo, Path.GetFileName(repo), sizeTask.Result, gc, lfs, gone, stashFinding);
    }

    /// <summary>Branches that must never be deleted: the current one and anything checked out in a worktree.</summary>
    private static IReadOnlySet<string> CheckedOutSet(ProcessResult worktrees, ProcessResult head)
    {
        var set = worktrees.Ok ? new HashSet<string>(GitHealthParser.CheckedOutBranches(worktrees.StdOut), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (head.Ok && head.StdOut.Trim() is { Length: > 0 } current) set.Add(current);
        return set;
    }

    public static long MeasureLfs(string repo, CancellationToken ct)
    {
        var dir = LfsObjectsDir(repo);
        return Directory.Exists(dir) ? DirectorySizer.Measure(dir, ct).Bytes : 0;
    }

    public static Task<ProcessResult> Git(string repo, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunAsync("git", ["-C", repo, .. args], repo, ReadTimeout, ct);
}
