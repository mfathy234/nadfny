using System.Collections.Concurrent;
using System.Diagnostics;
using Nadfny.Core;
using Nadfny.Features.Worktrees;

namespace Nadfny.Tests;

public sealed class DeletionProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-progress-" + Guid.NewGuid().ToString("N"));

    public DeletionProgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private void Files(string folder, int count, int size)
    {
        for (var i = 0; i < count; i++)
        {
            var path = P(folder, $"d{i % 7}", $"f{i}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }
    }

    private void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-c", "user.name=t", "-c", "user.email=t@example.invalid", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task DeleteWithProgress_ReportsBytesUpToExactlyTheTotal()
    {
        Files("big", 400, 2048);
        var events = new ConcurrentQueue<OpProgress>();
        var op = new OpReporter("t", events.Enqueue);

        await op.DeleteWithProgressAsync("Deleting files", 400 * 2048, c => SafeDeleter.DeletePermanently(P("big"), default, c), CancellationToken.None);

        var deleting = events.Where(e => e.Phase == "Deleting files").ToList();
        Assert.All(deleting, e => Assert.InRange(e.Done, 0, e.Total));
        Assert.Equal(400 * 2048, deleting[^1].Done);
        Assert.False(Directory.Exists(P("big")));
    }

    [Fact]
    public async Task PendingDeletes_MoveAsideIsInstantAndResumeFinishesTheJob()
    {
        Files("node_modules", 50, 100);
        var aside = PendingDeletes.MoveAside(P("node_modules"));

        Assert.False(Directory.Exists(P("node_modules")));
        Assert.True(PendingDeletes.IsAside(aside));
        Assert.True(Directory.Exists(aside)); // as if the app closed right here

        await PendingDeletes.ResumeAsync();

        Assert.False(Directory.Exists(aside));
    }

    [Fact]
    public async Task WorktreeRemoval_RenamesDeletesWithProgressAndLeavesOtherWorktreesAlone()
    {
        Directory.CreateDirectory(P("repo"));
        Git(P("repo"), "init", "-q", "-b", "main");
        File.WriteAllText(P("repo", ".gitignore"), "node_modules/\n");
        Git(P("repo"), "add", ".");
        Git(P("repo"), "commit", "-qm", "init");
        Git(P("repo"), "worktree", "add", "-q", P("wt-big"), "-b", "big");
        Git(P("repo"), "worktree", "add", "-q", P("wt-other"), "-b", "other");
        Files(Path.Combine("wt-big", "node_modules"), 300, 4096);

        var service = new WorktreeService();
        await service.ScanAsync([_root], _ => { }, ScanProgress.None, CancellationToken.None);
        var events = new ConcurrentQueue<OpProgress>();

        await service.RemoveAsync(P("wt-big"), deleteBranch: true, allowUnsaved: true, new OpReporter("wt", events.Enqueue), CancellationToken.None);

        Assert.False(Directory.Exists(P("wt-big")));
        Assert.Empty(Directory.GetDirectories(_root, ".nadfny-deleting-*")); // nothing left behind
        Assert.True(Directory.Exists(P("wt-other", ".git")) || File.Exists(P("wt-other", ".git")));
        Assert.Contains(events, e => e.Phase == "Detaching from git");
        var last = events.Last(e => e.Phase == "Deleting files");
        Assert.True(last.Total > 300 * 4096 && last.Done == last.Total, $"{last.Done}/{last.Total}");
    }
}
