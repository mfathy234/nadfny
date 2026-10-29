using Nadfny.Core;
using Nadfny.Features.Wsl;

namespace Nadfny.Tests;

/// <summary>
/// Only refusal paths and scans run here, with the registry and version injected. Nothing in these tests
/// runs wsl --shutdown, compacts a disk or changes sparse mode.
/// </summary>
public sealed class WslServiceTests : IDisposable
{
    private const string UbuntuId = "{4f1b2c3d-0000-4000-8000-000000000001}";
    private const string MissingId = "{00000000-0000-0000-0000-000000000000}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-wsl-service-" + Guid.NewGuid().ToString("N"));

    public WslServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Ubuntu"));
        File.WriteAllBytes(Path.Combine(_root, "Ubuntu", "ext4.vhdx"), new byte[512]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private WslService Service(int version, Version? wslVersion) => new(
        () => [new LxssEntry(UbuntuId, "Ubuntu", Path.Combine(_root, "Ubuntu"), version)],
        _ => Task.FromResult(wslVersion));

    [Fact]
    public async Task ScanAsync_ReportsDistrosAndWhetherSparseModeIsAvailable()
    {
        var report = await Service(2, new Version(2, 1, 5, 0)).ScanAsync(CancellationToken.None);

        var distro = Assert.Single(report.Distros);
        Assert.Equal("Ubuntu", distro.Name);
        Assert.Equal(512, distro.Bytes);
        Assert.Equal("2.1.5.0", report.Version);
        Assert.True(report.SparseSupported);
    }

    [Fact]
    public async Task ScanAsync_HidesSparseModeWhenTheVersionCannotBeRead()
    {
        var report = await Service(2, null).ScanAsync(CancellationToken.None);

        Assert.Null(report.Version);
        Assert.False(report.SparseSupported);
    }

    [Fact]
    public async Task CompactAsync_RefusesAnIdThatWasNotInTheLastScan()
    {
        var service = Service(2, new Version(2, 1, 5, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(UbuntuId, OpReporter.None, CancellationToken.None));

        await service.ScanAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(MissingId, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task CompactAsync_RefusesAWsl1DistributionWithNoDiskFile()
    {
        var service = Service(1, new Version(2, 1, 5, 0));
        await service.ScanAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(UbuntuId, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task EnableSparseAsync_RefusesWhenTheVersionIsTooOld()
    {
        var service = Service(2, new Version(1, 2, 5, 0));
        await service.ScanAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnableSparseAsync(UbuntuId, CancellationToken.None));
    }

    [Fact]
    public async Task EnableSparseAsync_RefusesAnIdThatWasNotInTheLastScan()
    {
        var service = Service(2, new Version(2, 1, 5, 0));
        await service.ScanAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnableSparseAsync(MissingId, CancellationToken.None));
    }
}
