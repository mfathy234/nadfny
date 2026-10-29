using System.Collections.Concurrent;
using System.Diagnostics;
using Nadfny.Core;
using Nadfny.Features.GitHealth;

namespace Nadfny.Tests;

/// <summary>Runs git only inside temp repositories and a temp bare "remote"; never touches real repositories.</summary>
public sealed class GitHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-health-" + Guid.NewGuid().ToString("N"));

    public GitHealthServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
    }

    private void Commit(string repo, string file, string text)
    {
        File.WriteAllText(Path.Combine(repo, file), text);
        Git(repo, "add", file);
        Git(repo, "commit", "-qm", $"edit {file}");
    }

    /// <summary>
    /// A repo with a pushed main branch, and three branches on the remote that get deleted there:
    /// "gone-merged" (in main), "gone-unmerged" (has its own commit) and "still-here" (never deleted).
    /// After `fetch --prune`, the first two show as [gone].
    /// </summary>
    private (string Repo, string Remote) CreateRepoWithGoneBranches()
    {
        var remote = P("remote.git");
        var repo = P("work", "repo");
        Directory.CreateDirectory(repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", remote);
        Git(repo, "init", "-q", "-b", "main", ".");
        Git(repo, "remote", "add", "origin", remote);
        Commit(repo, "readme.txt", "one");
        Git(repo, "push", "-q", "-u", "origin", "main");

        Git(repo, "branch", "gone-merged");
        Git(repo, "push", "-q", "-u", "origin", "gone-merged");

        Git(repo, "switch", "-q", "-c", "gone-unmerged");
        Commit(repo, "unmerged.txt", "work not in main");
        Git(repo, "push", "-q", "-u", "origin", "gone-unmerged");
        Git(repo, "switch", "-q", "main");

        Git(repo, "branch", "still-here");
        Git(repo, "push", "-q", "-u", "origin", "still-here");

        Git(repo, "push", "-q", "origin", "--delete", "gone-merged", "gone-unmerged");
        Git(repo, "fetch", "-q", "--prune", "origin");
        return (repo, remote);
    }

    private async Task<RepoHealth> ScanRepo(GitHealthService service, string repo)
    {
        var all = await service.ScanAsync([_root], _ => { }, ScanProgress.None, CancellationToken.None);
        return Assert.Single(all, h => h.Path.Equals(repo, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_FindsBranchesGoneFromTheRemoteButNotLiveOnes()
    {
        var (repo, _) = CreateRepoWithGoneBranches();

        var health = await ScanRepo(new GitHealthService(), repo);

        Assert.Equal("repo", health.Name);
        Assert.Equal(["gone-merged", "gone-unmerged"], health.GoneBranches);
        Assert.Null(health.Stashes);
    }

    [Fact]
    public async Task DeleteBranchesAsync_DeletesMergedGoneBranchAndKeepsUnmergedOne()
    {
        var (repo, _) = CreateRepoWithGoneBranches();
        var service = new GitHealthService();
        await ScanRepo(service, repo);

        var result = await service.DeleteBranchesAsync(repo, ["gone-merged", "gone-unmerged"], OpReporter.None, CancellationToken.None);

        Assert.Equal(["gone-merged"], result.Deleted);
        var kept = Assert.Single(result.Kept);
        Assert.Equal("gone-unmerged", kept.Name);
        Assert.Contains("not in the current branch", kept.Reason);
        Assert.Equal(["gone-unmerged"], result.Health.GoneBranches);

        var branches = await Read(repo, "for-each-ref", "--format=%(refname:lstrip=2)", "refs/heads");
        Assert.DoesNotContain("gone-merged", branches);
        Assert.Contains("gone-unmerged", branches);
        Assert.Contains("still-here", branches);
    }

    [Fact]
    public async Task DeleteBranchesAsync_RefusesNamesThatWereNotInTheLastScan()
    {
        var (repo, _) = CreateRepoWithGoneBranches();
        var service = new GitHealthService();
        await ScanRepo(service, repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteBranchesAsync(repo, ["still-here"], OpReporter.None, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteBranchesAsync(P("elsewhere"), ["gone-merged"], OpReporter.None, CancellationToken.None));

        var branches = await Read(repo, "for-each-ref", "--format=%(refname:lstrip=2)", "refs/heads");
        Assert.Contains("gone-merged", branches);
        Assert.Contains("still-here", branches);
    }

    [Fact]
    public async Task GcAsync_RunsGitGcAndReportsTheRepository()
    {
        var (repo, _) = CreateRepoWithGoneBranches();
        var service = new GitHealthService();
        await ScanRepo(service, repo);

        var result = await service.GcAsync(repo, OpReporter.None, CancellationToken.None);

        Assert.True(result.BeforeBytes > 0);
        Assert.True(result.Health.GitBytes > 0);
        Assert.Null(result.Health.Gc);
    }

    private static async Task<string[]> Read(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-C", repo, .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
