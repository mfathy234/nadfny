using System.Collections.Concurrent;
using Nadfny.Core;

namespace Nadfny.Features.Worktrees;

public sealed record WorktreeInfo(
    string Path,
    string Name,
    string RepoPath,
    string RepoName,
    string? Branch,
    string? Head,
    bool Detached,
    bool Locked,
    bool Missing,
    bool DriveOffline,
    long Bytes,
    long Files,
    DateTime? LastCommitUtc,
    DateTime? LastWriteUtc,
    bool StatusKnown,
    int ChangedFiles,
    int Ahead,
    bool HasUpstream,
    bool Merged);

public sealed class WorktreeService
{
    private readonly ConcurrentDictionary<string, (string Repo, long Bytes)> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<WorktreeInfo>> ScanAsync(IReadOnlyList<string> roots, Action<WorktreeInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        var repos = await GitRepoFinder.FindAsync(roots, progress, ct);

        progress.SetPhase("Listing worktrees", repos.Count);
        var candidates = new ConcurrentDictionary<string, (string Repo, WorktreeEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(repos,new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (repo, token) =>
        {
            var res = await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "list", "--porcelain"], repo, TimeSpan.FromSeconds(20), token);
            progress.CompleteOne(repo);
            if (!res.Ok) return;
            // Skip the main worktree (index 0): removing it means deleting the repository itself.
            foreach (var e in WorktreeParser.ParsePorcelain(res.StdOut).Skip(1).Where(e => !e.Bare))
                candidates.TryAdd(e.Path, (repo, e));
        });

        progress.SetPhase("Measuring worktrees", candidates.Count);
        var defaultRefs = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentBag<WorktreeInfo>();
        _known.Clear();

        await Parallel.ForEachAsync(candidates.Values, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2), CancellationToken = ct }, async (c, token) =>
        {
            var info = await DescribeAsync(c.Repo, c.Entry, defaultRefs, token);
            _known[info.Path] = (info.RepoPath, info.Bytes);
            results.Add(info);
            progress.CompleteOne(info.Path);
            onFound(info);
        });

        progress.Flush();
        return results.OrderByDescending(r => r.Bytes).ToList();
    }

    /// <summary>
    /// Removes a worktree fast: the folder is renamed aside (instant on the same drive), git forgets that one
    /// worktree, then the files are deleted in parallel while progress streams to the UI.
    /// Unless <paramref name="allowUnsaved"/> is set, git status is re-checked first so edits made after the scan,
    /// or work that could not be verified, are never deleted silently.
    /// </summary>
    public async Task<string> RemoveAsync(string path, bool deleteBranch, bool allowUnsaved, OpReporter op, CancellationToken ct)
    {
        var full = WorktreeParser.NormalizePath(Path.GetFullPath(path));
        if (!_known.TryGetValue(full, out var known))
            throw new InvalidOperationException("That worktree was not part of the last scan. Rescan and try again.");
        var repo = known.Repo;

        if (!allowUnsaved && Directory.Exists(full))
        {
            op.Phase("Checking for unsaved work");
            var status = await ReadStatusAsync(full, ct);
            if (status is null) throw new InvalidOperationException("Could not read its git status, so it was kept. Rescan and try again.");
            if (status.ChangedFiles > 0 || status.Ahead > 0)
                throw new InvalidOperationException("It has new changes since the scan, so it was kept. Rescan to review them.");
        }

        string? branch = null;
        if (deleteBranch && Directory.Exists(full))
        {
            var b = await ProcessRunner.RunAsync("git", ["-C", full, "rev-parse", "--abbrev-ref", "HEAD"], repo, TimeSpan.FromSeconds(10), ct);
            if (b.Ok && b.StdOut.Trim() is { Length: > 0 } name && name != "HEAD") branch = name;
        }

        // git commands on one repository run one at a time; the slow part (deleting files) runs in parallel.
        var gate = _repoLocks.GetOrAdd(repo, _ => new SemaphoreSlim(1, 1));
        string? aside = null;
        await gate.WaitAsync(ct);
        try
        {
            op.Phase("Detaching from git");
            if (Directory.Exists(full))
            {
                try { aside = PendingDeletes.MoveAside(full); }
                catch (IOException ex)
                {
                    throw new InvalidOperationException($"A program is using files in it (an editor, terminal or dev server?). Close it and try again. {ex.Message}");
                }
            }

            // With the folder gone, this only drops git's record of this one worktree; others are untouched.
            var res = await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "remove", "--force", "--force", full], repo, TimeSpan.FromMinutes(2), ct);
            if (!res.Ok && await IsStillListedAsync(repo, full, ct))
            {
                if (aside is not null)
                {
                    // Never leave it journaled: the next start would delete a worktree git still knows about.
                    PendingDeletes.Complete(aside);
                    try { Directory.Move(aside, full); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException($"git could not remove it ({res.Error}), and the folder could not be moved back. It is safe at {aside}. {ex.Message}");
                    }
                }
                throw new InvalidOperationException($"git could not remove it: {res.Error}");
            }
        }
        finally
        {
            gate.Release();
        }

        _known.TryRemove(full, out _);
        if (aside is not null)
        {
            await op.DeleteWithProgressAsync("Deleting files", known.Bytes, counter => SafeDeleter.DeletePermanently(aside, ct, counter), ct);
            PendingDeletes.Complete(aside);
        }

        if (branch is null) return "Worktree removed.";
        await gate.WaitAsync(ct);
        try
        {
            var del = await ProcessRunner.RunAsync("git", ["-C", repo, "branch", "-D", branch], repo, TimeSpan.FromSeconds(20), ct);
            return del.Ok ? $"Worktree and branch {branch} removed." : $"Worktree removed. Branch {branch} kept: {del.Error}";
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> IsStillListedAsync(string repo, string full, CancellationToken ct)
    {
        var list = await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "list", "--porcelain"], repo, TimeSpan.FromSeconds(20), ct);
        return !list.Ok || WorktreeParser.ParsePorcelain(list.StdOut).Any(e => e.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<GitStatusSummary?> ReadStatusAsync(string worktree, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("git", ["-C", worktree, "status", "--porcelain=v2", "--branch", "--untracked-files=all"], worktree, TimeSpan.FromSeconds(60), ct);
        return res.Ok ? WorktreeParser.ParseStatusV2(res.StdOut) : null;
    }

    private static async Task<WorktreeInfo> DescribeAsync(string repo, WorktreeEntry e, ConcurrentDictionary<string, string?> defaultRefs, CancellationToken ct)
    {
        var name = Path.GetFileName(e.Path);
        var repoName = Path.GetFileName(repo);
        if (!Directory.Exists(e.Path))
        {
            var driveOnline = Path.GetPathRoot(e.Path) is { } root && Directory.Exists(root);
            return new WorktreeInfo(e.Path, name, repo, repoName, e.Branch, e.Head, e.Detached, e.Locked, driveOnline, !driveOnline,
                0, 0, null, null, false, 0, 0, false, false);
        }

        var sizeTask = Task.Run(() => DirectorySizer.Measure(e.Path, ct), ct);
        var statusTask = ReadStatusAsync(e.Path, ct);
        var logTask = ProcessRunner.RunAsync("git", ["-C", e.Path, "log", "-1", "--format=%ct"], e.Path, TimeSpan.FromSeconds(10), ct);
        var defaultRef = await GetDefaultRefAsync(repo, defaultRefs, ct);
        var mergedTask = defaultRef is null || e.Head is null
            ? Task.FromResult(new ProcessResult(1, "", ""))
            : ProcessRunner.RunAsync("git", ["-C", repo, "merge-base", "--is-ancestor", e.Head, defaultRef], repo, TimeSpan.FromSeconds(15), ct);

        await Task.WhenAll(sizeTask, statusTask, logTask, mergedTask);
        var size = sizeTask.Result;
        var status = statusTask.Result;
        DateTime? lastCommit = long.TryParse(logTask.Result.StdOut.Trim(), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime : null;

        return new WorktreeInfo(e.Path, name, repo, repoName, e.Branch, e.Head, e.Detached, e.Locked, false, false,
            size.Bytes, size.Files, lastCommit, size.LastWriteUtc == DateTime.MinValue ? null : size.LastWriteUtc,
            status is not null, status?.ChangedFiles ?? 0, status?.Ahead ?? 0, status?.HasUpstream ?? false, mergedTask.Result.Ok);
    }

    private static async Task<string?> GetDefaultRefAsync(string repo, ConcurrentDictionary<string, string?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(repo, out var cached)) return cached;
        string? found = null;
        var head = await ProcessRunner.RunAsync("git", ["-C", repo, "symbolic-ref", "--short", "refs/remotes/origin/HEAD"], repo, TimeSpan.FromSeconds(10), ct);
        if (head.Ok && head.StdOut.Trim().Length > 0) found = head.StdOut.Trim();
        else
        {
            foreach (var candidate in new[] { "origin/main", "origin/master", "origin/develop" })
            {
                var v = await ProcessRunner.RunAsync("git", ["-C", repo, "rev-parse", "--verify", "--quiet", candidate], repo, TimeSpan.FromSeconds(10), ct);
                if (v.Ok) { found = candidate; break; }
            }
        }
        cache[repo] = found;
        return found;
    }
}
