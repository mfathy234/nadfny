using Nadfny.Features.Windows;

namespace Nadfny.Tests;

/// <summary>The elevated Disk Cleanup script is built only from a fixed allowlist; it is never run in tests.</summary>
public sealed class WindowsCleanupTests
{
    [Fact]
    public void BuildScript_SelectsOnlyTheRequestedAllowlistedHandlers()
    {
        var script = WindowsCleanupService.BuildScript(["delivery", "updates"]);

        Assert.Contains("'Delivery Optimization Files'", script);
        Assert.Contains("'Update Cleanup'", script);
        Assert.DoesNotContain("Previous Installations", script);
        Assert.Contains("cleanmgr.exe", script);
        Assert.Contains("/sagerun:742", script);
    }

    [Fact]
    public void BuildScript_ClearsItsSelectionBeforeAndAfterRunning()
    {
        var script = WindowsCleanupService.BuildScript(["setup"]);
        var removals = script.Split("Remove-ItemProperty").Length - 1;
        Assert.Equal(2, removals); // leftover flags from an earlier run can never widen what gets cleaned
    }

    [Fact]
    public void Allowlist_NeverIncludesDriverPackagesOrDefender() =>
        Assert.DoesNotContain(WindowsCleanupService.AllowedHandlers, h => h.Contains("Driver") || h.Contains("Defender"));

    [Fact]
    public void BuildScript_RejectsAnEmptySelection() =>
        Assert.Throws<InvalidOperationException>(() => WindowsCleanupService.BuildScript(["not-a-real-id"]));

    [Fact]
    public async Task CleanAsync_RejectsUnknownIdsBeforeAskingForAdmin() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsCleanupService().CleanAsync(["../../evil"], Nadfny.Core.OpReporter.None, CancellationToken.None));

    [Fact]
    public async Task ScanAsync_ListsOnlyCategoriesThisWindowsHas()
    {
        var items = await new WindowsCleanupService().ScanAsync(CancellationToken.None);
        Assert.All(items, i => Assert.Contains(i.Id, new[] { "previous", "updates", "delivery", "setup" }));
        Assert.DoesNotContain(items, i => i.Id == "previous" && !Directory.Exists(@"C:\Windows.old"));
    }
}
