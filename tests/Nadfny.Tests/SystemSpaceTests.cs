using System.ComponentModel;
using System.Text.Json;
using Nadfny.Core;
using Nadfny.Features.SystemSpace;

namespace Nadfny.Tests;

public sealed class SystemSpaceTests
{
    [Theory]
    [InlineData(1, 2, true, "full")]
    [InlineData(1, 1, true, "reduced")]
    [InlineData(0, null, false, "unknown")]
    [InlineData(null, 9, true, "unknown")] // no registry value, but the file exists: on
    public void MapsPowerRegistry(int? enabled, int? type, bool? expected, string expectedType)
    {
        var result = SystemSpaceMapper.Hibernation(@"C:\hiberfil.sys", 4096, enabled, type);
        Assert.Equal(expected, result.Enabled);
        Assert.Equal(expectedType, result.FileType);
        Assert.Equal(4096, result.Bytes);
    }

    [Theory]
    [InlineData(34_359_738_368, 34_359_738_368)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void InstalledMemoryIsKnownOnlyWhenPositive(long total, long? expected) =>
        Assert.Equal(expected, SystemSpaceMapper.InstalledMemory(total));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, null)]
    public void MapsComputerSystemAndShadowStorage(bool? automatic, bool? expected)
    {
        // Normalized CIM fixture: uint64 values may arrive as JSON numbers or decimal strings.
        var json = $$"""
            {"automatic":{{(automatic.HasValue ? automatic.Value.ToString().ToLowerInvariant() : "null")}},
             "shadowReadable":true,"restoreReadable":true,"restorePoints":3,
             "storage":[{"drive":"C:","storageDrive":"D:","used":"4294967296","maximum":8589934592}]}
            """;
        var result = SystemSpaceMapper.Cim(JsonDocument.Parse(json).RootElement);
        Assert.Equal(expected, result.AutomaticManaged);
        Assert.Equal(3, result.RestorePoints);
        var storage = Assert.Single(result.Storage);
        Assert.Equal("D:", storage.StorageDrive);
        Assert.Equal(4294967296UL, storage.Used);
        Assert.Equal(8589934592UL, storage.Maximum);
    }

    [Fact]
    public void MissingOrDeniedCimDoesNotReportZero()
    {
        var result = SystemSpaceMapper.Cim(JsonDocument.Parse("{}").RootElement);
        Assert.Null(result.AutomaticManaged);
        Assert.Null(result.RestorePoints);
        Assert.False(result.ShadowReadable);
        Assert.Empty(result.Storage);
    }

    [Fact]
    public void MapsReadOnlyQueryCapturedWithoutAdministrator()
    {
        // Captured from the fixed CIM query on Windows, 2026-10-29.
        const string fixture = """
            {"automatic":true,"storage":[],"shadowReadable":false,"restoreReadable":false,"restorePoints":null}
            """;
        var result = SystemSpaceMapper.Cim(JsonDocument.Parse(fixture).RootElement);
        Assert.True(result.AutomaticManaged);
        Assert.False(result.ShadowReadable);
        Assert.Null(result.RestorePoints);
        Assert.Empty(result.Storage);
    }

    [Fact]
    public void MapsUnlimitedAndMalformedCimValuesWithoutInventingSizes()
    {
        const string fixture = """
            {"shadowReadable":true,"restoreReadable":true,"restorePoints":-1,
             "storage":[{"used":"bad","maximum":"18446744073709551615"}]}
            """;
        var result = SystemSpaceMapper.Cim(JsonDocument.Parse(fixture).RootElement);
        var storage = Assert.Single(result.Storage);
        Assert.True(storage.MaximumUnlimited);
        Assert.Null(storage.Used);
        Assert.Equal("Unknown volume", storage.Drive);
        Assert.Null(result.RestorePoints);
    }

    [Theory]
    [InlineData("1", "2")]
    [InlineData(-1, 0)]
    public void UnexpectedRegistryShapesStayUnknown(object enabled, object type)
    {
        var result = SystemSpaceMapper.Hibernation("test", null, enabled, type);
        Assert.Null(result.Enabled);
        Assert.Equal("unknown", result.FileType);
    }

    [Fact]
    public void AHibernationFileOnDiskMeansOnWhenTheRegistryIsSilent()
    {
        Assert.True(SystemSpaceMapper.Hibernation(@"C:\hiberfil.sys", 13_600_000_000, null, null).Enabled);
        Assert.Null(SystemSpaceMapper.Hibernation(@"C:\hiberfil.sys", 0, null, null).Enabled);
        Assert.False(SystemSpaceMapper.Hibernation(@"C:\hiberfil.sys", 13_600_000_000, 0, null).Enabled); // explicit value wins
    }

    [Theory]
    [InlineData("off", "/hibernate off")]
    [InlineData("reduce", "/hibernate /type reduced")]
    public void OnlyBuildsFixedPowerCommands(string action, string expected) =>
        Assert.Equal(expected, string.Join(' ', SystemSpaceService.PowerArguments(action)));

    [Theory]
    [InlineData("on")]
    [InlineData("off & whoami")]
    [InlineData("/hibernate off")]
    [InlineData("")]
    public void RejectsOtherPowerCommands(string action) =>
        Assert.Throws<ArgumentException>(() => SystemSpaceService.PowerArguments(action));

    [Fact]
    public async Task RequiresScanAndTreatsUacDeclineAsCancel()
    {
        var calls = 0;
        var service = new SystemSpaceService(_ => Task.FromResult(Snapshot()), _ =>
        {
            calls++;
            throw new Win32Exception(1223);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeHibernationAsync("off", OpReporter.None));
        Assert.Equal(0, calls);
        await service.ScanAsync(CancellationToken.None);
        var result = await service.ChangeHibernationAsync("off", OpReporter.None);
        Assert.True(result.Cancelled);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RereadsAfterChangeAndReportsMeasuredDifference()
    {
        var reads = 0;
        var service = new SystemSpaceService(_ => Task.FromResult(Snapshot(reads++ == 0 ? 8192 : 4096)), _ => Task.FromResult(0));
        await service.ScanAsync(CancellationToken.None);
        var result = await service.ChangeHibernationAsync("reduce", OpReporter.None);
        Assert.Equal(2, reads);
        Assert.Equal(4096, result.Freed);
        Assert.Equal(4096, result.Info.Hibernation.Bytes);
    }

    [Fact]
    public async Task FailedPowerCommandRereadsAndSurfacesFailure()
    {
        var reads = 0;
        var service = new SystemSpaceService(_ => { reads++; return Task.FromResult(Snapshot()); }, _ => Task.FromResult(1));
        await service.ScanAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeHibernationAsync("reduce", OpReporter.None));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task OtherLaunchErrorsAreNotTreatedAsUacCancel()
    {
        var service = new SystemSpaceService(_ => Task.FromResult(Snapshot()), _ => throw new Win32Exception(2));
        await service.ScanAsync(CancellationToken.None);
        await Assert.ThrowsAsync<Win32Exception>(() => service.ChangeHibernationAsync("off", OpReporter.None));
    }

    [Fact]
    public void RejectsUnknownSettingsWithoutLaunchingAnything() =>
        Assert.Throws<ArgumentException>(() => SystemSpaceService.OpenSettings("cmd.exe"));

    [Fact]
    public void MeasuresLockedFileUsingDirectoryMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-space-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "hiberfil.sys");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.SetLength(12345);
            Assert.Equal(12345, SystemSpaceService.FileBytes(root, "hiberfil.sys"));
            Assert.Equal(0, SystemSpaceService.FileBytes(root, "swapfile.sys"));
        }
        finally { SafeDeleter.DeletePermanently(root); }
    }

    private static SystemSpaceSnapshot Snapshot(long bytes = 8192) => new(
        new(@"C:\hiberfil.sys", bytes, true, "full"), [], null, [], false, null);
}
