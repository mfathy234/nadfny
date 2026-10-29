using Nadfny.Core;
using Nadfny.Features.Apps;

namespace Nadfny.Tests;

public sealed class UninstallPlusTests
{
    private const string Key = @"CurrentUser|Default|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example";
    private static AppCandidate App(string id) => new(id, id, null, null, null, AppSource.Win32, false, null,
        null, null, new(UninstallPlan.Msi, "{12345678-1234-1234-1234-123456789ABC}", Probe: id), [], []);

    [Fact]
    public void QuietCommand_IsParsedSeparately()
    {
        var entry = new UninstallEntry(Key, "Example", "Example", null, null, null, null,
            @"""C:\Example\remove.exe"" /normal", null, null, false, false, null, null,
            @"""C:\Example\quiet.exe"" /silent");
        var candidate = Assert.Single(UninstallRegistry.ToCandidates([entry]));
        Assert.Equal(@"C:\Example\quiet.exe", candidate.QuietUninstall!.Target);
        Assert.Equal("/silent", candidate.QuietUninstall.Arguments);
        Assert.Equal(Key, candidate.QuietUninstall.Probe);
    }

    [Fact]
    public void MsiQuiet_UsesBasicProgressArguments() =>
        Assert.Equal(["/x", "{12345678-1234-1234-1234-123456789ABC}", "/qb"],
            AppUninstaller.MsiArguments("{12345678-1234-1234-1234-123456789ABC}", true));

    [Fact]
    public void BrokenEntry_DetectsMissingCommandOrFolder()
    {
        var app = App(Key);
        Assert.False(AppService.IsBroken(app));
        Assert.True(AppService.IsBroken(app with { Uninstall = new(UninstallPlan.Command, @"C:\missing-example\remove.exe") }));
        Assert.True(AppService.IsBroken(app with { InstallLocation = @"C:\missing-example" }));
        Assert.True(AppService.IsBroken(app with { SharedInstallLocation = @"C:\missing-example" }));
        Assert.False(AppService.IsBroken(app with { Source = AppSource.Store, InstallLocation = @"C:\missing-example" }));
    }

    [Fact]
    public void SharedInstallLocation_CannotBypassSelfUninstallGuard()
    {
        var info = AppService.Measure(App(Key) with { SharedInstallLocation = AppContext.BaseDirectory }, [], CancellationToken.None);
        Assert.False(info.CanUninstall);
    }

    [Theory]
    [InlineData(@"CurrentUser|Default|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"CurrentUser|Default|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example\Child")]
    [InlineData(@"CurrentUser|Default|SOFTWARE\Other\Example")]
    [InlineData(@"Users|Default|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example")]
    public void RegistryArguments_RejectUnsafeKeysEvenIfScanned(string key) =>
        Assert.Throws<InvalidOperationException>(() => AppRegistryRemoval.Arguments(key, [key], "delete"));

    [Fact]
    public void RegistryArguments_RequireExactScannedKeyAndFixedArguments()
    {
        Assert.Throws<InvalidOperationException>(() => AppRegistryRemoval.Arguments(Key, [], "delete"));
        Assert.Equal(["delete", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example", "/f"],
            AppRegistryRemoval.Arguments(Key, [Key], "delete"));
        Assert.Equal(["export", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example", @"C:\backup.reg"],
            AppRegistryRemoval.Arguments(Key, [Key], "export", @"C:\backup.reg"));
    }

    [Fact]
    public async Task Queue_ContinuesAfterFailureAndCancellationInOrder()
    {
        var order = new List<string>();
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App("one"), App("two"), App("three")]),
            () => [], () => [], runner: (plan, op, ct) =>
            {
                order.Add(plan.Probe!);
                if (order.Count == 1) throw new InvalidOperationException("Failed");
                if (order.Count == 2) throw new OperationCanceledException("Declined");
                return Task.FromResult(true);
            });
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var results = await service.UninstallQueueAsync(["one", "two", "three"], true, OpReporter.None, CancellationToken.None);
        Assert.Equal(["one", "two", "three"], results.Select(r => r.Id));
        Assert.Equal([false, false, true], results.Select(r => r.Removed));
        Assert.Equal(["one", "two", "three"], order);
    }

    [Fact]
    public async Task ForcedRemoval_RecyclesOnlyPreviewedUnsharedTempFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var own = Directory.CreateDirectory(Path.Combine(root, "Own")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(root, "Shared")).FullName;
        File.WriteAllText(Path.Combine(own, "data.txt"), "personal data");
        File.WriteAllText(Path.Combine(shared, "data.txt"), "shared data");
        var broken = App(Key) with { Uninstall = new(UninstallPlan.Command, Path.Combine(root, "missing.exe")), KnownData = [own, shared] };
        string? backup = null;
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([broken, App("other") with { KnownData = [shared] }]),
            () => [], () => [], registryRemoval: (key, path, scanned, ct) =>
            {
                Assert.Equal(Key, key);
                Assert.Contains(Key, scanned);
                Assert.True(Directory.Exists(own));
                backup = path;
                return Task.CompletedTask;
            });
        try
        {
            await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
            var preview = await service.PreviewForcedRemovalAsync(Key, CancellationToken.None);
            Assert.Equal(own, Assert.Single(preview.Folders).Path);
            var result = await service.ForceRemoveAsync(Key, OpReporter.None, CancellationToken.None);
            Assert.Equal(preview.BackupPath, backup);
            Assert.Equal(backup, result.BackupPath);
            Assert.Equal(1, result.Removed);
            Assert.Empty(result.Failed);
            Assert.False(Directory.Exists(own));
            Assert.True(File.Exists(Path.Combine(shared, "data.txt")));
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    [Fact]
    public async Task ForcedRemoval_BackupFailureKeepsEntryAndData()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-backup-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var broken = App(Key) with { Uninstall = new(UninstallPlan.Command, Path.Combine(root, "missing.exe")), KnownData = [root] };
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([broken]), () => [], () => [],
            registryRemoval: (_, _, _, _) => throw new IOException("Backup failed"));
        try
        {
            await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
            await service.PreviewForcedRemovalAsync(Key, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => service.ForceRemoveAsync(Key, OpReporter.None, CancellationToken.None));
            Assert.True(Directory.Exists(root));
            Assert.NotNull(await service.PreviewForcedRemovalAsync(Key, CancellationToken.None));
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    [Fact]
    public async Task NormalUninstall_KeepsInstallFolderSharedByAnotherEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-shared-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>(
            [App("one") with { InstallLocation = root }, App("two") with { InstallLocation = root }]),
            () => [], () => [], runner: (_, _, _) => Task.FromResult(true));
        try
        {
            await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
            var result = await service.UninstallAsync("one", OpReporter.None, CancellationToken.None);
            Assert.True(result.Removed);
            Assert.Empty(result.Leftovers);
            Assert.True(Directory.Exists(root));
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    [Fact]
    public async Task Queue_OffersSharedInstallFolderOnceItsLastEntryIsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-shared-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>(
            [App("one") with { InstallLocation = root }, App("two") with { InstallLocation = root }]),
            () => [], () => [], runner: (_, _, _) => Task.FromResult(true));
        try
        {
            await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
            var results = await service.UninstallQueueAsync(["one", "two"], true, OpReporter.None, CancellationToken.None);
            Assert.Empty(results[0].Leftovers);
            Assert.Equal(root, Assert.Single(results[1].Leftovers).Path);
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    [Fact]
    public void Registry32_UsesPhysicalWowKeyForBackupAndDelete()
    {
        const string key = @"LocalMachine|Registry32|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example";
        var args = AppRegistryRemoval.Arguments(key, [key], "export", @"C:\backup.reg");
        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Example", args[1]);
        Assert.Equal(args[1], AppRegistryRemoval.Arguments(key, [key], "delete")[1]);
    }

    [Fact]
    public async Task Queue_UsesRegisteredQuietCommandWhenAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-quiet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "uninstall.exe");
        File.WriteAllText(exe, "dummy");
        UninstallPlan? actual = null;
        var quiet = new UninstallPlan(UninstallPlan.Command, exe, "/silent", Key);
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App(Key) with {
            Uninstall = new(UninstallPlan.Command, exe, "/normal", Key), QuietUninstall = quiet }]), () => [], () => [],
            runner: (plan, _, _) => { actual = plan; return Task.FromResult(true); });
        try
        {
            await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
            await service.UninstallQueueAsync([Key], true, OpReporter.None, CancellationToken.None);
            Assert.Equal(quiet, actual);
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    [Fact]
    public async Task Queue_PausesWhenPreviousUninstallerMayStillBeActive()
    {
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App("one"), App("two")]),
            () => [], () => [], runner: (_, _, _) => throw new UninstallPendingException("Finish the open uninstaller"));
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var result = Assert.Single(await service.UninstallQueueAsync(["one", "two"], true, OpReporter.None, CancellationToken.None));
        Assert.Equal("one", result.Id);
        Assert.True(result.NeedsReview);
        Assert.False(result.Removed);
    }

    [Fact]
    public async Task Completion_WaitsForProcessExitAfterEntryDisappears()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "127.0.0.1", "-n", "2" }) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        Assert.True(await AppUninstaller.WaitForCompletionAsync(process, () => false, OpReporter.None, CancellationToken.None));
        Assert.True(process.HasExited);
    }
}
