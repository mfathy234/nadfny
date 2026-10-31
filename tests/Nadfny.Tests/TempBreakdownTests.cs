using Nadfny.Core;
using Nadfny.Features.Caches;
using Nadfny.Features.History;

namespace Nadfny.Tests;

internal static class TestLinks
{
    /// <summary>A junction needs no admin rights, unlike a symlink.</summary>
    public static void Junction(string link, string target)
    {
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "junction was not created");
    }
}

public sealed class TempGroupNameTests
{
    [Theory]
    [InlineData("dawsha-e2e-0Q4X1d", true, "dawsha-e2e-*")]
    [InlineData("dawsha-e2e-k3m4n5", true, "dawsha-e2e-*")]
    [InlineData("dawsha-remote-controller-0Q4X1d", true, "dawsha-remote-controller-*")]
    [InlineData("cg-bridge-12345", true, "cg-bridge-*")]
    [InlineData("orch-3f2a9c1d", true, "orch-*")]
    [InlineData("claude", true, "claude")]
    [InlineData("DockerDesktop", true, "DockerDesktop")]
    [InlineData("files-workspaces-ab12cd-alias", true, "files-workspaces-*-alias")]
    [InlineData("scoped_dir1234_5678", true, "scoped_*")]
    [InlineData("setup.log", false, "*.log")]
    [InlineData("Trace.LOG", false, "*.log")]
    [InlineData("README", false, "README")]
    public void Of_GroupsByNamePattern(string name, bool isDir, string expected) =>
        Assert.Equal(expected, TempGroupName.Of(name, isDir));
}

public sealed class TempKeepListTests
{
    [Theory]
    [InlineData("dawsha-e2e-*", "dawsha-e2e-abc", true)]
    [InlineData("DAWSHA-*", "dawsha-e2e-abc", true)]
    [InlineData("*.log", "x.LOG", true)]
    [InlineData("claude", "claudette", false)]
    [InlineData("a.b", "aXb", false)]
    public void Matches_UsesStarWildcardsIgnoringCase(string pattern, string name, bool expected) =>
        Assert.Equal(expected, TempKeepList.Matches(pattern, name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("bad?")]
    public void Normalize_RejectsInvalidPatterns(string pattern) =>
        Assert.Throws<ArgumentException>(() => TempKeepList.Normalize(pattern));

    [Fact]
    public void Normalize_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => TempKeepList.Normalize(new string('a', 101)));

    [Fact]
    public void Add_IgnoresDuplicatesAndCapsTheList()
    {
        var one = TempKeepList.Add([], " Foo-* ");
        Assert.Equal(["Foo-*"], one);
        Assert.Equal(one, TempKeepList.Add(one, "foo-*"));

        var full = Enumerable.Range(0, TempKeepList.MaxEntries).Select(i => $"n{i}").ToList();
        Assert.Throws<ArgumentException>(() => TempKeepList.Add(full, "extra"));
    }

    [Fact]
    public void Remove_DropsThePatternIgnoringCase() =>
        Assert.Equal(["b"], TempKeepList.Remove(["a", "b"], "A"));

    [Fact]
    public void Settings_WithoutKeepListHaveAnEmptyOne()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"roots":["C:\\x"]}""", Json.Options)!;
        Assert.Empty(settings.KeepNames);
    }
}

/// <summary>Works on dummy Temp folders under a throwaway root; never touches the real Temp.</summary>
public sealed class TempBreakdownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-breakdown-tests-" + Guid.NewGuid().ToString("N"));
    private string TempDir => Path.Combine(_root, "Temp");
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-5);

    public TempBreakdownTests() => Directory.CreateDirectory(TempDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Write(string relative, int bytes, DateTime? writeUtc = null)
    {
        var path = Path.Combine(TempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, writeUtc ?? Old);
        if (Path.GetDirectoryName(path) != TempDir) Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, writeUtc ?? Old);
        return path;
    }

    private async Task<CacheService> Scanned(IReadOnlyList<string>? keep = null)
    {
        var list = keep ?? [];
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", TempDir, "")], () => list);
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        return service;
    }

    [Fact]
    public async Task Breakdown_GroupsSizesCountsAndRecentWork()
    {
        Write(@"dawsha-e2e-a1b2c3\x.bin", 1000);
        Write(@"dawsha-e2e-k3m4n5\x.bin", 2000);
        Write(@"dawsha-e2e-z9y8x7\x.bin", 500, DateTime.UtcNow);
        Write("build.log", 50);
        Write("other.log", 25);
        var service = await Scanned();

        var report = await service.BreakdownAsync(CancellationToken.None);

        var e2e = Assert.Single(report.Groups, g => g.Pattern == "dawsha-e2e-*");
        Assert.Equal((3, 3500L, 1, 500L), (e2e.Count, e2e.Bytes, e2e.RecentCount, e2e.RecentBytes));
        var logs = Assert.Single(report.Groups, g => g.Pattern == "*.log");
        Assert.Equal((2, 75L), (logs.Count, logs.Bytes));
        Assert.Equal("dawsha-e2e-*", report.Groups[0].Pattern);
    }

    [Fact]
    public async Task Breakdown_ShowsTopTwelveAndSumsTheRest()
    {
        for (var i = 1; i <= 15; i++) Write($@"tool{(char)('a' + i)}-x\f.bin", i * 10);
        var service = await Scanned();

        var report = await service.BreakdownAsync(CancellationToken.None);

        Assert.Equal(12, report.Groups.Count);
        Assert.Equal(3, report.Other!.Groups);
        Assert.Equal(10 + 20 + 30, report.Other.Bytes);
    }

    [Fact]
    public async Task Breakdown_ListsAiToolsAndKeptNamesAsKeptWithoutClearing()
    {
        Write(@"claude\s.txt", 700);
        Write(@"mine-123456\s.txt", 300);
        Write(@"junk-123456\s.txt", 100);
        var service = await Scanned(["mine-*"]);

        var report = await service.BreakdownAsync(CancellationToken.None);

        Assert.Equal(700, Assert.Single(report.Kept, k => k.Kind == "ai").Bytes);
        Assert.Equal(300, Assert.Single(report.Kept, k => k.Kind == "user").Bytes);
        Assert.Equal(["junk-*"], report.Groups.Select(g => g.Pattern));
        Assert.Equal(["mine-*"], report.KeepList);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearTempGroupAsync(CacheService.AiToolsId, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task ClearTempGroup_RemovesOnlyThatGroupAndKeepsRecentWork()
    {
        Write(@"dawsha-e2e-a1b2c3\x.bin", 1000);
        Write(@"dawsha-e2e-k3m4n5\x.bin", 2000);
        Write(@"dawsha-e2e-fresh1\x.bin", 500, DateTime.UtcNow);
        Write(@"orch-111111\x.bin", 40);
        Write("keep.log", 5);
        var service = await Scanned();
        await service.BreakdownAsync(CancellationToken.None);

        var freed = await service.ClearTempGroupAsync("dawsha-e2e-*", OpReporter.None, CancellationToken.None);

        Assert.Equal(3000, freed);
        Assert.False(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-a1b2c3")));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-fresh1")));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "orch-111111")));
        Assert.True(File.Exists(Path.Combine(TempDir, "keep.log")));
    }

    [Fact]
    public async Task ClearTempGroup_NeverTouchesEntriesAddedOrRenamedAfterTheBreakdown()
    {
        Write(@"dawsha-e2e-a1b2c3\x.bin", 10);
        Write(@"dawsha-e2e-renamed1\x.bin", 10);
        var service = await Scanned();
        await service.BreakdownAsync(CancellationToken.None);
        Directory.Move(Path.Combine(TempDir, "dawsha-e2e-renamed1"), Path.Combine(TempDir, "precious"));
        Write(@"dawsha-e2e-late99\x.bin", 10);

        await service.ClearTempGroupAsync("dawsha-e2e-*", OpReporter.None, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-a1b2c3")));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "precious")));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-late99")));
    }

    [Fact]
    public async Task ClearTempGroup_RefusesAnUnknownGroupOrOneFromBeforeABreakdown()
    {
        Write(@"dawsha-e2e-a1b2c3\x.bin", 10);
        var service = await Scanned();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearTempGroupAsync("dawsha-e2e-*", OpReporter.None, CancellationToken.None));
        await service.BreakdownAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearTempGroupAsync(@"..\..\Windows", OpReporter.None, CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-a1b2c3")));
    }

    [Fact]
    public async Task ClearTempGroup_HonoursAKeepPatternAddedAfterTheBreakdown()
    {
        Write(@"dawsha-e2e-a1b2c3\x.bin", 10);
        var keep = new List<string>();
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", TempDir, "")], () => keep);
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        await service.BreakdownAsync(CancellationToken.None);
        keep.Add("dawsha-e2e-*");

        await service.ClearTempGroupAsync("dawsha-e2e-*", OpReporter.None, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(TempDir, "dawsha-e2e-a1b2c3")));
    }

    [Fact]
    public async Task ClearAsync_WholeRowHonoursTheKeepList()
    {
        Write(@"mine-123456\x.bin", 100);
        Write(@"junk-123456\x.bin", 200);
        var service = await Scanned(["mine-*"]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var freed = await service.ClearAsync("temp", OpReporter.None, CancellationToken.None);

        Assert.Equal(200, Assert.Single(scanned).Bytes);
        Assert.Equal(200, freed);
        Assert.True(Directory.Exists(Path.Combine(TempDir, "mine-123456")));
    }

    [Fact]
    public async Task BrokenLinks_AreGroupedAndClearedWithoutFollowingThem()
    {
        var target = Path.Combine(_root, "gone");
        Directory.CreateDirectory(target);
        var link = Path.Combine(TempDir, "files-workspaces-ab12cd-alias");
        TestLinks.Junction(link, target);
        Directory.Delete(target);
        var live = Write(@"liveTarget\keep.txt", 5);
        TestLinks.Junction(Path.Combine(TempDir, "live-link"), Path.GetDirectoryName(live)!);
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "x");
        var service = await Scanned();

        var report = await service.BreakdownAsync(CancellationToken.None);
        var links = Assert.Single(report.Groups, g => g.Kind == "links");
        await service.ClearTempGroupAsync(links.Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(1, links.Count);
        Assert.False(Directory.Exists(link));
        Assert.False(File.Exists(link));
        Assert.True(Directory.Exists(Path.Combine(TempDir, "live-link")));
        Assert.True(File.Exists(live));
    }

    [Fact]
    public async Task ClearAsync_WholeRowAlsoRemovesBrokenLinks()
    {
        var target = Path.Combine(_root, "gone");
        Directory.CreateDirectory(target);
        var link = Path.Combine(TempDir, "dangling");
        TestLinks.Junction(link, target);
        Directory.Delete(target);
        Write("filler.tmp", 10);
        var service = await Scanned();

        await service.ClearAsync("temp", OpReporter.None, CancellationToken.None);

        Assert.False(Directory.Exists(link));
        Assert.False(File.Exists(Path.Combine(TempDir, "filler.tmp")));
    }
}

public sealed class TempGrowthTests
{
    private static HistorySnapshot Snapshot(DateTime at, params HistoryItem[] items) =>
        new(at, [], new HistoryCategory(HistoryGrowth.TempGroupsCategory, items.Sum(i => i.Bytes), items));

    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Compute_NamesTheGroupsThatGrewMost()
    {
        var before = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var report = HistoryGrowth.Compute([
            Snapshot(before, new HistoryItem("dawsha-e2e-*", "dawsha-e2e-*", 45 * Gb, 25000), new HistoryItem("orch-*", "orch-*", Gb, 100)),
            Snapshot(before.AddDays(2), new HistoryItem("dawsha-e2e-*", "dawsha-e2e-*", 80 * Gb, 42000), new HistoryItem("orch-*", "orch-*", Gb, 100), new HistoryItem("new-*", "new-*", 2 * Gb, 5)),
        ], "", 0);

        Assert.Equal(["dawsha-e2e-*", "new-*"], report.TempGroups.Select(g => g.Pattern));
        Assert.Equal((35 * Gb, 17000L, before), (report.TempGroups[0].Bytes, report.TempGroups[0].NewItems, report.TempGroups[0].Since));
        Assert.DoesNotContain(report.Categories, c => c.Category == HistoryGrowth.TempGroupsCategory);
    }

    [Fact]
    public void Compute_ShowsNothingWithoutAPreviousBreakdown() =>
        Assert.Empty(HistoryGrowth.Compute([Snapshot(DateTime.UtcNow, new HistoryItem("a-*", "a-*", Gb, 1))], "", 0).TempGroups);
}
