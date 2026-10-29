using Nadfny.Features.Updates;

namespace Nadfny.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.1", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("V2", "2.0.0")]
    public void NormalizeVersion_HandlesTagStyles(string tag, string expected) =>
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));

    [Theory]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("v0.2", "0.1.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    [InlineData("garbage", "0.1.0", false)]
    public void IsNewer_ComparesNumerically(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewer(candidate, current));

    [Fact]
    public void ParseRelease_PrefersSetupExeOverMsi()
    {
        const string json = """
            {"tag_name":"v0.2.0","body":"Notes","html_url":"https://github.com/mfathy234/nadfny/releases/tag/v0.2.0",
             "assets":[
               {"name":"Nadfny-0.2.0-portable-win-x64.zip","browser_download_url":"https://x/zip","size":10},
               {"name":"Nadfny-0.2.0-win-x64.msi","browser_download_url":"https://x/msi","size":20},
               {"name":"Nadfny-0.2.0-setup.exe","browser_download_url":"https://x/setup","size":25},
               {"name":"SHA256SUMS.txt","browser_download_url":"https://x/sums","size":1}]}
            """;

        var r = UpdateService.ParseRelease(json);

        Assert.Equal("0.2.0", r.Version);
        Assert.Equal("https://x/setup", r.Installer!.Url);
        Assert.Equal(25, r.Installer.Size);
        Assert.Equal("https://x/sums", r.Sums!.Url);
    }

    [Fact]
    public void ParseRelease_FallsBackToMsiForOlderReleases()
    {
        const string json = """
            {"tag_name":"v0.1.0",
             "assets":[
               {"name":"Nadfny-0.1.0-portable-win-x64.zip","browser_download_url":"https://x/zip","size":10},
               {"name":"Nadfny-0.1.0-win-x64.msi","browser_download_url":"https://x/msi","size":20}]}
            """;

        Assert.Equal("https://x/msi", UpdateService.ParseRelease(json).Installer!.Url);
    }

    [Fact]
    public void StartInstaller_UsesMsiexecForMsiAndBurnFlagsForSetup()
    {
        Assert.Contains("msiexec.exe", UpdateService.StartInstaller(@"C:\t\Nadfny-0.2.0-win-x64.msi"));
        var setup = UpdateService.StartInstaller(@"C:\t\Nadfny-0.2.0-setup.exe");
        Assert.DoesNotContain("msiexec", setup);
        Assert.Contains("'/passive', '/norestart'", setup);
    }

    [Fact]
    public void ParseSums_ReadsSha256sumFormatIncludingBinaryMarker()
    {
        const string sums = "2c31aa  *Nadfny-0.2.0-win-x64.msi\n4185bb  Nadfny-0.2.0-portable-win-x64.zip\n";
        Assert.Equal("2c31aa", UpdateService.ParseSums(sums, "Nadfny-0.2.0-win-x64.msi"));
        Assert.Equal("4185bb", UpdateService.ParseSums(sums, "Nadfny-0.2.0-portable-win-x64.zip"));
        Assert.Null(UpdateService.ParseSums(sums, "other.msi"));
    }

    [Theory]
    [InlineData("https://github.com/mfathy234/nadfny/releases/tag/v0.2.0", true)]
    [InlineData("https://evil.example/releases", false)]
    [InlineData("https://github.com/someone-else/nadfny/releases", false)]
    public void OpenReleasePage_OnlyAllowsThisRepo(string url, bool allowed)
    {
        if (allowed) return; // opening a browser is not done in tests
        Assert.Throws<InvalidOperationException>(() => UpdateService.OpenReleasePage(url));
    }
}
