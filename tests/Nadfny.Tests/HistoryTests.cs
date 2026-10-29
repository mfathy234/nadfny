using System.Globalization;
using Nadfny.Core;
using Nadfny.Features.History;
using Nadfny.Features.Caches;

namespace Nadfny.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-history-tests-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private const long Mb = 1024 * 1024;
    private HistoryService Service(long cap = 5 * 1024 * 1024) => new(_root, () => _now, () => [new("C:\\", 1000, 400)], cap);
    private static HistoryCategory Category(params HistoryItem[] items) => new("caches", items.Sum(i => i.Bytes), items);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    [Fact]
    public async Task WritesAndReadsAcrossInstancesWithTop200AndFullTotal()
    {
        var items = Enumerable.Range(1, 220).Select(i => new HistoryItem($"id{i}", $"Item {i}", i)).ToArray();
        await Service().RecordAsync(Category(items));
        var snapshot = Assert.Single(await Service().ReadAsync());
        Assert.Equal(items.Sum(i => i.Bytes), snapshot.Category!.TotalBytes);
        Assert.Equal(200, snapshot.Category.Items.Count);
        Assert.Equal(220, snapshot.Category.Items[0].Bytes);
        Assert.Equal(400, Assert.Single(snapshot.Drives).FreeBytes);
    }

    [Fact]
    public async Task GrowthUsesPreviousScanOfSameCategoryAndIgnoresRemovedItems()
    {
        var service = Service();
        await service.RecordAsync(Category(new HistoryItem("grown", "Grown", 1000 * Mb), new("removed", "Removed", 500 * Mb)));
        _now = _now.AddHours(1);
        await service.RecordAsync(null);
        await service.RecordAsync(Category(new HistoryItem("grown", "Grown", 1200 * Mb), new("new", "New", 300 * Mb)));
        var report = await service.ReportAsync();
        Assert.Equal(2, report.Growth.Count);
        Assert.Equal(200 * Mb, report.Growth.Single(g => g.Id == "grown").Bytes);
        Assert.True(report.Growth.Single(g => g.Id == "new").IsNew);
        Assert.Equal(0, Assert.Single(report.Categories).Bytes);
        Assert.Single(report.Hints);
    }

    [Theory]
    [InlineData(1000, 1100, false)]
    [InlineData(2000, 2150, false)]
    [InlineData(1000, 1101, true)]
    public async Task HintRequiresMoreThan100MbAnd10Percent(long before, long after, bool expected)
    {
        var service = Service();
        await service.RecordAsync(Category(new HistoryItem("id", "Item", before * Mb)));
        await service.RecordAsync(Category(new HistoryItem("id", "Item", after * Mb)));
        Assert.Equal(expected, (await service.ReportAsync()).Hints.Count > 0);
    }

    [Fact]
    public async Task PrunesOlderThan90DaysAndHonorsByteCap()
    {
        var service = Service(1800);
        await service.RecordAsync(Category(new HistoryItem("old", "Old", 1)));
        _now = _now.AddDays(91);
        await service.RecordAsync(Category(new HistoryItem("current", "Current", 2)));
        Assert.Single(await service.ReadAsync());
        for (var i = 0; i < 15; i++)
        {
            _now = _now.AddMinutes(1);
            await service.RecordAsync(Category(new HistoryItem("id", new string('x', 100), i)));
        }
        Assert.InRange(Directory.GetFiles(_root).Sum(p => new FileInfo(p).Length), 1, 1800);
        Assert.Equal(14, (await service.ReadAsync()).Last().Category!.TotalBytes);
    }

    [Fact]
    public async Task ClearDeletesOnlyHistoryAndCanRecordAgain()
    {
        var service = Service();
        await service.RecordAsync(Category(new HistoryItem("id", "Item", 1)));
        await service.ClearAsync();
        Assert.False(Directory.Exists(_root));
        Assert.Empty(await service.ReadAsync());
        await service.RecordAsync(null);
        Assert.Single(await service.ReadAsync());
    }

    [Fact]
    public async Task UnwritableHistoryDoesNotFailRecording()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "keep");
        var service = new HistoryService(file);
        await service.RecordAsync(null);
        Assert.Empty(await service.ReadAsync());
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public async Task EqualClockReadingsPreserveScanOrder()
    {
        var service = Service();
        for (var i = 0; i < 20; i++)
            await service.RecordAsync(Category(new HistoryItem("id", "Item", i)));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (long)i),
            (await service.ReadAsync()).Select(s => s.Category!.TotalBytes));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task DuplicateIdsAreStoredAndComparedOnce()
    {
        var service = Service();
        await service.RecordAsync(Category(new("id", "First tag", 1000 * Mb), new("ID", "Second tag", 1000 * Mb)));
        await service.RecordAsync(Category(new("id", "First tag", 1300 * Mb), new("ID", "Second tag", 1300 * Mb)));
        var snapshots = await service.ReadAsync();
        Assert.All(snapshots, s => Assert.Single(s.Category!.Items));
        Assert.Single((await service.ReportAsync()).Hints);
    }

    [Fact]
    public async Task ScanCaptureKeepsResultAndOnlyRecordsCompletedScans()
    {
        var service = Service();
        var capture = new ScanHistory(() => { }, service);
        IReadOnlyList<CacheInfo> rows = [new("npm", "npm", _root, "", 300)];
        Assert.Same(rows, await capture.CaptureAsync(Task.FromResult(rows), CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            capture.CaptureAsync(Task.FromResult(rows), new CancellationToken(true)));
        await Assert.ThrowsAsync<IOException>(() => capture.CaptureAsync(
            Task.FromException<IReadOnlyList<CacheInfo>>(new IOException("Scan failed")), CancellationToken.None));
        var snapshots = await service.ReadAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("caches", snapshots.Last().Category!.Id);
        Assert.Equal(300, snapshots.Last().Category!.TotalBytes);
        await capture.ClearAsync();
        Assert.Empty((await capture.ReportAsync()).Series);
    }

    [Fact]
    public async Task InterruptedLineDoesNotHideLaterSnapshots()
    {
        var service = Service();
        await service.RecordAsync(Category(new HistoryItem("first", "First", 1)));
        File.AppendAllText(Assert.Single(Directory.GetFiles(_root)), "{interrupted");
        await service.RecordAsync(Category(new HistoryItem("second", "Second", 2)));
        Assert.Equal(2, (await service.ReadAsync()).Count);
    }

    [Fact]
    public async Task PrunesIndividualRecordsOnThe90DayBoundary()
    {
        var service = Service();
        await service.RecordAsync(null);
        _now = _now.AddDays(90).AddMinutes(1);
        await service.RecordAsync(null);
        Assert.Single(await service.ReadAsync());
    }

    [Fact]
    public async Task SlowHistoryStorageDoesNotBlockScanResult()
    {
        using var release = new ManualResetEventSlim();
        var service = new HistoryService(_root, () => _now, () =>
        {
            release.Wait();
            return [];
        });
        try
        {
            var capture = new ScanHistory(() => { }, service);
            IReadOnlyList<CacheInfo> rows = [new("npm", "npm", _root, "", 300)];
            Assert.Same(rows, await capture.CaptureAsync(Task.FromResult(rows), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
        Assert.Equal(2, (await service.ReadAsync()).Count);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public async Task DailyFilesUseGregorianDatesRegardlessOfWindowsCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await Service().RecordAsync(null);
            Assert.Equal("2026-10-29.jsonl", Path.GetFileName(Assert.Single(Directory.GetFiles(_root))));
            Assert.Single(await Service().ReadAsync());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
