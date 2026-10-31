using Nadfny.Features.Apps;

namespace Nadfny.Tests;

public sealed class DriveNameTests
{
    [Theory]
    [InlineData(@"D:\", "Games", "Games (D:)")]
    [InlineData(@"C:\", "", "Local Disk (C:)")]
    [InlineData(@"E:\", "   ", "Local Disk (E:)")]
    [InlineData(@"e:\", null, "Local Disk (E:)")]
    public void Format_MatchesHowExplorerNamesDrives(string root, string? label, string expected) =>
        Assert.Equal(expected, DriveName.Format(root, label));

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Battlefield 6", @"D:\")]
    [InlineData(@"c:\Program Files\Foo", @"C:\")]
    [InlineData(@"\\server\share\Games", null)]
    [InlineData("relative\\path", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RootOf_ReturnsTheLocalDriveOnly(string? path, string? expected) =>
        Assert.Equal(expected, DriveName.RootOf(path));

    [Fact]
    public void For_UsesTheLabelLookupOncePerDrive()
    {
        var lookups = 0;
        var names = new DriveName(root => { lookups++; return root == @"D:\" ? "Games" : null; });

        Assert.Equal("Games (D:)", names.For(@"D:\Games\Spider-Man 2"));
        Assert.Equal("Games (D:)", names.For(@"d:\SteamLibrary"));
        Assert.Equal("Local Disk (C:)", names.For(@"C:\Program Files\Steam"));
        Assert.Null(names.For(null));
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task For_ReadsASlowDriveOnlyOnceWhenAskedInParallel()
    {
        var lookups = 0;
        var names = new DriveName(_ => { Interlocked.Increment(ref lookups); Thread.Sleep(50); return "Games"; });

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => names.For(@"D:\Games"))));

        Assert.All(results, r => Assert.Equal("Games (D:)", r));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void For_FallsBackToTheLetterWhenTheDriveCannotBeRead()
    {
        var names = new DriveName(_ => throw new IOException("The device is not ready."));
        Assert.Equal("F:", names.For(@"F:\Games"));
    }
}
