using System.Runtime.InteropServices;
using Nadfny.Core;
using Nadfny.Features.Finder;

namespace Nadfny.Tests;

public sealed class FinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-finder-tests-" + Guid.NewGuid().ToString("N"));
    public FinderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => SafeDeleter.DeletePermanently(_root);

    private string Write(string name, int length = 1024 * 1024, byte middle = 0, int age = 0)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[length];
        data[length / 2] = middle;
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        return path;
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("Program Files", true)]
    [InlineData("Program Files (x86)", true)]
    [InlineData("ProgramData", true)]
    [InlineData("System Volume Information", true)]
    [InlineData("$Recycle.Bin", true)]
    [InlineData("My Windows", false)]
    public void SkipDirectory_UsesExactNames(string name, bool skip) => Assert.Equal(skip, FinderRules.SkipDirectory(name));

    [Fact]
    public async Task LargeFiles_ThresholdIsStrictAndRootsDoNotRepeat()
    {
        Write("exact.bin", 100 * 1024 * 1024);
        var large = Write("large.bin", 100 * 1024 * 1024 + 1);
        Write("Windows/hidden.bin", 100 * 1024 * 1024 + 1);
        Write("pagefile.sys", 100 * 1024 * 1024 + 1);
        var service = new FinderService(() => _root);
        var items = await service.ScanAsync("large", [_root, _root], 100, _ => { }, ScanProgress.None, default);
        Assert.Equal(large, Assert.Single(items.Files).Path);
    }

    [Fact]
    public async Task Downloads_OnlyOldImmediateChildrenAndGroupsByType()
    {
        Write("setup.EXE", 10, age: 100);
        Write("archive.zip", 20, age: 100);
        Write("fresh.pdf", 30, age: 1);
        Write("folder/nested.txt", 40, age: 100);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "folder"), DateTime.UtcNow.AddDays(-100));
        var report = await new FinderService(() => _root).ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        Assert.Equal(3, report.Files.Count);
        Assert.Contains(report.Files, f => f.Type == "Installers" && f.Bytes == 10);
        Assert.Contains(report.Files, f => f.Type == "Archives" && f.Bytes == 20);
        Assert.Contains(report.Files, f => f.IsDirectory && f.Bytes == 40);
        Assert.DoesNotContain(report.Files, f => f.Name == "fresh.pdf" || f.Name == "nested.txt");
    }

    [Fact]
    public async Task Duplicates_FullHashSeparatesSameSizeAndPartialHashMatches()
    {
        Write("a.bin"); Write("b.bin"); Write("different.bin", middle: 1);
        Write("small-a.bin", 100); Write("small-b.bin", 100);
        var report = await new FinderService(() => _root).ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(1024 * 1024, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Name == "different.bin");
    }

    [Fact]
    public async Task Duplicates_HardLinksAreOneCopy()
    {
        var a = Write("a.bin");
        Assert.True(CreateHardLinkW(Path.Combine(_root, "link.bin"), a, IntPtr.Zero));
        var service = new FinderService(() => _root);
        var report = await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public async Task Remove_RefusesAllCopiesUnknownPathsAndMissingKeeper()
    {
        Write("a.bin"); Write("b.bin");
        var service = new FinderService(() => _root);
        var report = await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        var copies = Assert.Single(report.Groups).Files;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("duplicates", copies.Select(f => f.Path).ToList(), OpReporter.None, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("duplicates", [Path.Combine(_root, "unknown")], OpReporter.None, default));
        SafeDeleter.DeletePermanently(copies[1].Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("duplicates", [copies[0].Path], OpReporter.None, default));
        Assert.True(File.Exists(copies[0].Path));
    }

    [Fact]
    public async Task Remove_RecyclesOnlyScannedTempChild()
    {
        var path = Write("old.txt", 10, age: 100);
        var service = new FinderService(() => _root);
        await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", [_root], OpReporter.None, default));
        var result = await service.RemoveAsync("downloads", [path], OpReporter.None, default);
        Assert.Single(result.Removed);
        Assert.False(File.Exists(path));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Remove_ChangedFileStaysAndCancelledScanDoesNotAuthorizeRemoval()
    {
        var path = Write("old.txt", 10, age: 100);
        var service = new FinderService(() => _root);
        await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        File.AppendAllText(path, "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", [path], OpReporter.None, default));
        Write("another-old.txt", 10, age: 100);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync("downloads", [], 90, _ => cancel.Cancel(), ScanProgress.None, cancel.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", [path], OpReporter.None, default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Downloads_ChangedDescendantMakesOldFolderRecent()
    {
        Write("old-folder/new.txt", 10);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "old-folder"), DateTime.UtcNow.AddDays(-100));
        var report = await new FinderService(() => _root).ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Downloads_OldFolderWithNewerButStillOldContentsCanBeRecycled()
    {
        Write("old-folder/item.txt", 10, age: 95);
        var folder = Path.Combine(_root, "old-folder");
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-100));
        var service = new FinderService(() => _root);
        var report = await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        Assert.Single(report.Files);
        var result = await service.RemoveAsync("downloads", [folder], OpReporter.None, default);
        Assert.Single(result.Removed);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("a.msi", "Installers")]
    [InlineData("a.7z", "Archives")]
    [InlineData("a.mp4", "Videos")]
    [InlineData("a.png", "Images")]
    [InlineData("a.pdf", "Documents")]
    [InlineData("a.bin", "Other")]
    public void Downloads_TypeGroups(string path, string type) => Assert.Equal(type, FinderRules.Type(path));

    [Fact]
    public async Task Downloads_SystemFilesAreNotOffered()
    {
        var path = Write("desktop.ini", 10, age: 100);
        File.SetAttributes(path, FileAttributes.System | FileAttributes.Hidden);
        var report = await new FinderService(() => _root).ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Remove_KeeperDisappearsAfterPreflightReportsFailure()
    {
        Write("a.bin"); Write("b.bin");
        var service = new FinderService(() => _root);
        var report = await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        var files = Assert.Single(report.Groups).Files;
        var op = new OpReporter("test", _ =>
        {
            if (File.Exists(files[1].Path)) SafeDeleter.DeletePermanently(files[1].Path);
        });
        var result = await service.RemoveAsync("duplicates", [files[0].Path], op, default);
        Assert.Empty(result.Removed);
        Assert.Equal(files[0].Path, Assert.Single(result.Failed));
        Assert.True(File.Exists(files[0].Path));
    }

    [Fact]
    public async Task Remove_KeepOneAlsoAppliesToDownloadsFolders()
    {
        Write("copies/a.bin", age: 100); Write("copies/b.bin", age: 100);
        var folder = Path.Combine(_root, "copies");
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-100));
        var service = new FinderService(() => _root);
        await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", [folder], OpReporter.None, default));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Remove_SequentialRequestsStillKeepLastCopy()
    {
        Write("a.bin"); Write("b.bin"); Write("c.bin");
        var service = new FinderService(() => _root);
        var report = await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        var files = Assert.Single(report.Groups).Files;
        Assert.Single((await service.RemoveAsync("duplicates", [files[0].Path], OpReporter.None, default)).Removed);
        Assert.Single((await service.RemoveAsync("duplicates", [files[1].Path], OpReporter.None, default)).Removed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("duplicates", [files[2].Path], OpReporter.None, default));
        Assert.True(File.Exists(files[2].Path));
    }

    [Fact]
    public async Task Duplicates_RechecksHardLinksBetweenHashStages()
    {
        var a = Write("a.bin");
        var b = Write("b.bin");
        var stamp = DateTime.UtcNow.AddDays(-100);
        File.SetLastWriteTimeUtc(a, stamp);
        File.SetLastWriteTimeUtc(b, stamp);
        var progress = new ScanProgress("test", p =>
        {
            if (p.Phase != "Verifying matching files" || p.Done != 0) return;
            SafeDeleter.DeletePermanently(b);
            Assert.True(CreateHardLinkW(b, a, IntPtr.Zero));
        });
        var report = await new FinderService(() => _root).ScanAsync("duplicates", [_root], 0, _ => { }, progress, default);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public async Task Duplicates_CancelledRescanKeepsPreviousSafetyForOtherTabs()
    {
        Write("a.bin", age: 100); Write("b.bin", age: 100);
        var service = new FinderService(() => _root);
        await service.ScanAsync("duplicates", [_root], 0, _ => { }, ScanProgress.None, default);
        var downloads = await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        using var cancel = new CancellationTokenSource();
        var progress = new ScanProgress("test", _ => cancel.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync("duplicates", [_root], 0, _ => { }, progress, cancel.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", downloads.Files.Select(f => f.Path).ToList(), OpReporter.None, default));
        Assert.All(downloads.Files, f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public async Task Downloads_NestedRenameInvalidatesFolderEvenWithSameTotalAndNewestWrite()
    {
        var path = Write("old-folder/nested/item.txt", 10, age: 100);
        var folder = Path.Combine(_root, "old-folder");
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-100));
        var service = new FinderService(() => _root);
        await service.ScanAsync("downloads", [], 90, _ => { }, ScanProgress.None, default);
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "renamed.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("downloads", [folder], OpReporter.None, default));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Downloads_VanishedChildDoesNotStopScan()
    {
        var a = Write("a.txt", 10, age: 100);
        var b = Write("b.txt", 10, age: 100);
        var report = await new FinderService(() => _root).ScanAsync("downloads", [], 90,
            item => SafeDeleter.DeletePermanently(item.File!.Path == a ? b : a), ScanProgress.None, default);
        Assert.Single(report.Files);
    }

    [Theory]
    [InlineData("large", 1)]
    [InlineData("downloads", 1)]
    [InlineData("duplicates", 100)]
    [InlineData("anything", 0)]
    public async Task Scan_RejectsUnsupportedFilter(string mode, int value) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FinderService(() => _root).ScanAsync(mode, [_root], value, _ => { }, ScanProgress.None, default));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
}
