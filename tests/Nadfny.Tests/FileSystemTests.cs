using Nadfny.Core;
using Nadfny.Features.Artifacts;

namespace Nadfny.Tests;

/// <summary>Works on a throwaway folder under %TEMP%; every test cleans up after itself.</summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-tests-" + Guid.NewGuid().ToString("N"));

    public FileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Measure_SumsAllFilesRecursively()
    {
        Write("a.txt", 100);
        Write(@"x\b.bin", 2000);
        Write(@"x\y\z\c.bin", 30);

        var r = DirectorySizer.Measure(_root);

        Assert.Equal(2130, r.Bytes);
        Assert.Equal(3, r.Files);
    }

    [Fact]
    public void Measure_MissingFolderIsZero() =>
        Assert.Equal(0, DirectorySizer.Measure(Path.Combine(_root, "nope")).Bytes);

    [Fact]
    public void DeletePermanently_RemovesReadOnlyFiles()
    {
        var file = Write(@"tree\pack\objects\ro.pack", 10);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        SafeDeleter.DeletePermanently(Path.Combine(_root, "tree"));

        Assert.False(Directory.Exists(Path.Combine(_root, "tree")));
    }

    [Fact]
    public void DeletePermanently_RemovesJunctionButNeverItsTarget()
    {
        var keep = Write(@"keep\important.txt", 5);
        var tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        // A junction (what pnpm and many tools create) needs no admin rights, unlike a symlink.
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", Path.Combine(tree, "link"), Path.Combine(_root, "keep") },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(tree, "link", "")), "junction was not created");

        SafeDeleter.DeletePermanently(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void DeletePermanently_RemovesJunctionWhoseTargetIsGone()
    {
        var target = Path.Combine(_root, "target");
        Write(@"target\a.txt", 5);
        var link = Path.Combine(_root, "link");
        TestLinks.Junction(link, target);
        Directory.Delete(target, true);

        SafeDeleter.DeletePermanently(link);

        Assert.False(Directory.Exists(link));
        Assert.Empty(new DirectoryInfo(_root).GetFileSystemInfos());
    }

    [Fact]
    public void DeletePermanently_NeverFollowsAJunctionWhoseTargetExists()
    {
        var keep = Write(@"target\sub\important.txt", 5);
        var link = Path.Combine(_root, "link");
        TestLinks.Junction(link, Path.Combine(_root, "target"));

        SafeDeleter.DeletePermanently(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(keep));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Program Files\nodejs")]
    [InlineData(@"C:\ProgramData\Docker")]
    public void EnsureDeletable_RefusesSystemLocations(string path) =>
        Assert.Throws<InvalidOperationException>(() => SafeDeleter.EnsureDeletable(path));

    [Fact]
    public void EnsureDeletable_RefusesUserProfileRootAndAppData()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Throws<InvalidOperationException>(() => SafeDeleter.EnsureDeletable(home));
        Assert.Throws<InvalidOperationException>(() => SafeDeleter.EnsureDeletable(Path.Combine(home, "AppData")));
    }

    [Fact]
    public void EnsureDeletable_AllowsOrdinaryProjectFolders() =>
        SafeDeleter.EnsureDeletable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Work", "old-project"));

    [Theory]
    [InlineData(@"C:\Work\App", @"C:\Work", true)]
    [InlineData(@"C:\Work", @"C:\Work\", true)]
    [InlineData(@"C:\Workshop\App", @"C:\Work", false)]
    [InlineData(@"C:\Other", @"C:\Work", false)]
    [InlineData(@"C:\Work\App", @"C:\", true)]   // drive roots keep their trailing separator
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"D:\Work", @"C:\", false)]
    public void IsUnder_ComparesWholeSegments(string path, string root, bool expected) =>
        Assert.Equal(expected, SafeDeleter.IsUnder(path, root));

    [Fact]
    public void EffectiveRoots_DropsRootsNestedInsideAnother()
    {
        var inner = Path.Combine(_root, "Work");
        Directory.CreateDirectory(inner);
        var roots = TreeWalker.EffectiveRoots([inner, _root, _root + "\\"]);
        Assert.Equal([Path.GetFullPath(_root)], roots.Select(Path.TrimEndingDirectorySeparator));
    }

    [Fact]
    public void EffectiveRoots_DropsFoldersInsideAScannedDrive()
    {
        var drive = Path.GetPathRoot(_root)!;
        var roots = TreeWalker.EffectiveRoots([_root, drive]);
        Assert.Equal([drive], roots);
    }

    [Fact]
    public async Task TreeWalker_SkipsGitAndAppDataAndVisitsEverythingElse()
    {
        Write(@"a\b\c\file.txt", 1);
        Write(@"a\.git\objects\x", 1);
        Write(@"AppData\Local\y", 1);
        var visited = new System.Collections.Concurrent.ConcurrentBag<string>();
        var progress = new ScanProgress("t", _ => { });

        await TreeWalker.RunAsync([_root], 10, (dir, children) =>
        {
            visited.Add(Path.GetFileName(dir));
            return children.Where(c => c.IsDir).Select(c => c.Path);
        }, progress, CancellationToken.None);

        Assert.Contains("c", visited);
        Assert.DoesNotContain(".git", visited);
        Assert.DoesNotContain("objects", visited);
        Assert.DoesNotContain("Local", visited);
        var snap = progress.Snapshot();
        Assert.Equal(snap.Total, snap.Done);
        Assert.True(snap.Folders > 0);
    }

    [Fact]
    public async Task ArtifactScan_FindsOutputAndOnlyRemovesWhatItFound()
    {
        Write(@"web\package.json", 10);
        Write(@"web\node_modules\left-pad\index.js", 500);
        Write(@"web\node_modules\nested\node_modules\deep.js", 100);
        Write(@"api\Api.csproj", 10);
        Write(@"api\bin\Debug\Api.dll", 4000);
        Write(@"scripts\bin\deploy.sh", 50); // no .csproj sibling: real source, must not match
        var service = new ArtifactService();

        var snapshots = new List<ScanProgressSnapshot>();
        var progress = new ScanProgress("artifacts", s => { lock (snapshots) snapshots.Add(s); });

        var found = await service.ScanAsync([_root], _ => { }, progress, CancellationToken.None);

        var last = snapshots[^1];
        Assert.Equal("Measuring folders", last.Phase);
        Assert.Equal(last.Total, last.Done);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, a => a.Kind == "node_modules" && a.Bytes == 600);
        Assert.Contains(found, a => a.Kind == "bin" && a.Bytes == 4000);

        var outsider = Path.Combine(_root, "scripts", "bin");
        var (removed, bytes, failed) = await service.RemoveAsync([.. found.Select(a => a.Path), outsider], OpReporter.None, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal(4600, bytes);
        Assert.Single(failed);
        Assert.True(Directory.Exists(outsider));
        Assert.False(Directory.Exists(Path.Combine(_root, "web", "node_modules")));
    }
}
