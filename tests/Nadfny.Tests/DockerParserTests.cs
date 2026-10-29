using Nadfny.Features.Docker;

namespace Nadfny.Tests;

public sealed class DockerParserTests
{
    [Theory]
    [InlineData("0B", 0)]
    [InlineData("121.9kB", 121_900)]
    [InlineData("420MB", 420_000_000)]
    [InlineData("1.304GB", 1_304_000_000)]
    [InlineData("4.899GB (28%)", 4_899_000_000)]
    [InlineData("2TB", 2_000_000_000_000)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("N/A", 0)]
    public void ParseSize_UsesDecimalUnits(string? text, long expected) =>
        Assert.Equal(expected, DockerParser.ParseSize(text));

    [Fact]
    public void ParseSummary_ReadsDockersOwnTotals()
    {
        const string output = """
            {"Active":"4","Reclaimable":"5.477GB (74%)","Size":"7.349GB","TotalCount":"12","Type":"Images"}
            {"Active":"0","Reclaimable":"4.899GB","Size":"17.02GB","TotalCount":"58","Type":"Build Cache"}
            """;

        var rows = DockerParser.ParseSummary(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal(7_349_000_000, rows[0].Bytes);
        Assert.Equal(5_477_000_000, rows[0].ReclaimableBytes);
        Assert.Equal(12, rows[0].Total);
        Assert.Equal("Build Cache", rows[1].Type);
    }

    [Fact]
    public void ParseVerboseDf_MapsImagesContainersVolumesAndCache()
    {
        const string json = """
            {"Images":[{"Containers":"0","ID":"sha256:aa","Repository":"node","Tag":"20","Size":"1.59GB","UniqueSize":"1.592GB","CreatedSince":"6 months ago"}],
             "Containers":[{"ID":"c1","Names":"pg","Image":"postgres:16","State":"running","Status":"Up 2 hours","Size":"20.5kB"}],
             "Volumes":[{"Name":"f1d3","Size":"49.7MB","Links":"0","Labels":"com.docker.volume.anonymous="},{"Name":"data","Size":"1GB","Links":"1","Labels":""}],
             "BuildCache":[{"Size":"117MB","InUse":"false","Shared":"false"},{"Size":"50MB","InUse":"true","Shared":"false"},{"Size":"999MB","InUse":"false","Shared":"true"}]}
            """;

        var u = DockerParser.ParseVerboseDf(json);

        var image = Assert.Single(u.Images);
        Assert.Equal(1_592_000_000, image.UniqueBytes);
        Assert.Equal(0, image.Containers);
        Assert.Equal("pg", Assert.Single(u.Containers).Name);
        Assert.True(u.Volumes[0].Anonymous);
        Assert.False(u.Volumes[1].Anonymous);
        Assert.Equal(1, u.Volumes[1].Links);
        Assert.Equal(167_000_000, u.BuildCache.Bytes);
        Assert.Equal(117_000_000, u.BuildCache.ReclaimableBytes);
        Assert.Equal(3, u.BuildCache.Entries);
    }

    [Fact]
    public void ParseVerboseDf_ToleratesMissingSections()
    {
        var u = DockerParser.ParseVerboseDf("{}");
        Assert.Empty(u.Images);
        Assert.Empty(u.Volumes);
        Assert.Equal(0, u.BuildCache.Bytes);
    }
}
