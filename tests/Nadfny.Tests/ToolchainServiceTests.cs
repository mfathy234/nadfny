using Nadfny.Core;
using Nadfny.Features.Toolchains;

namespace Nadfny.Tests;

public sealed class ToolchainServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-tools-tests-" + Guid.NewGuid().ToString("N"));
    public ToolchainServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => SafeDeleter.DeletePermanently(_root);

    private string Write(string relative, string text = "data")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private ToolchainService Service(string sdks = "", string? android = null) => new(
        new ToolchainLocations(_root, Path.Combine(_root, "Local"), Path.Combine(_root, "Roaming"), android ?? Path.Combine(_root, "Local", "Android", "Sdk")),
        _ => Task.FromResult(new ProcessResult(0, sdks, "")));

    [Fact]
    public void ParseSdks_ValidatesVersionAndPreservesInstallPath()
    {
        var sdks = DotnetSdks.Parse("8.0.100 [C:\\Program Files\\dotnet\\sdk]\n8.0.102 [D:\\dotnet\\sdk]\n10.0.100-preview.7 [C:\\sdk]\ninvalid\n8.0.999;evil [C:\\sdk]");
        Assert.Equal(3, sdks.Count);
        Assert.Equal("C:\\Program Files\\dotnet\\sdk", sdks[0].BasePath);
        Assert.Equal("10.0.100-preview.7", sdks[2].Version);
    }

    [Fact]
    public void OldSdks_ComparePatchesWithinFeatureBandsAndRespectPins()
    {
        var sdks = DotnetSdks.Parse("8.0.100 [C:\\sdk]\n8.0.101 [C:\\sdk]\n8.0.102 [C:\\sdk]\n8.0.400 [C:\\sdk]\n9.0.100 [C:\\sdk]\n10.0.100-preview.1 [C:\\sdk]\n10.0.100-preview.2 [C:\\sdk]");
        var old = DotnetSdks.OldVersions(sdks, new HashSet<string> { "8.0.100" });
        Assert.Equal(["8.0.101"], old.Order().ToArray());
    }

    [Fact]
    public void FindPins_ReadsCommentsAndSkipsDependenciesAndDeepTrees()
    {
        Write(@"repos\app\global.json", "{ // sdk\n\"sdk\": { \"version\": \"8.0.100\", }, }");
        Write(@"repos\node_modules\dep\global.json", "{\"sdk\":{\"version\":\"9.0.100\"}}");
        Write(@"repos\.git\global.json", "broken");
        Write(@"repos\a\b\c\global.json", "{\"sdk\":{\"version\":\"8.0.400\"}}");
        var pins = DotnetSdks.FindPins([Path.Combine(_root, "repos")], CancellationToken.None, maxDepth: 2);
        Assert.Equal(["8.0.100"], pins.Versions.Order().ToArray());
        Assert.False(pins.Complete);
    }

    [Fact]
    public async Task Scan_ListsAvdsAndImagesWithSizesAndOnlyOldJetBrainsVersions()
    {
        Write(@".android\avd\Pixel.avd\userdata.img");
        Write(@"Local\Android\Sdk\system-images\android-35\google_apis\x86_64\system.img");
        Write(@"Local\JetBrains\Rider2024.3\caches\index");
        Write(@"Local\JetBrains\Rider2025.1\caches\index");
        Write(@"Roaming\JetBrains\Rider2024.3\options\settings.xml");
        Write(@"Local\JetBrains\PyCharm2023.1\caches\index");
        var report = await Service().ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        Assert.Contains(report.Items, x => x.Kind == "avd" && x.Name.Contains("Pixel") && x.Bytes == 4 && x.Recycle);
        Assert.Contains(report.Items, x => x.Kind == "android-image" && x.Bytes == 4 && x.CanRemove);
        Assert.Equal(2, report.Items.Count(x => x.Kind == "jetbrains"));
        Assert.DoesNotContain(report.Items, x => x.Name.Contains("PyCharm") || x.Name.Contains("2025.1"));
    }

    [Fact]
    public async Task Scan_UsesPinsAndNeverAllowsSdkDirectoryDeletion()
    {
        Write(@"repo\global.json", "{\"sdk\":{\"version\":\"8.0.100\"}}");
        var report = await Service("8.0.100 [C:\\Program Files\\dotnet\\sdk]\n8.0.101 [C:\\Program Files\\dotnet\\sdk]\n8.0.102 [C:\\Program Files\\dotnet\\sdk]")
            .ScanAsync([Path.Combine(_root, "repo")], _ => { }, ScanProgress.None, CancellationToken.None);
        Assert.True(report.Items.Single(x => x.Name == ".NET SDK 8.0.100").Pinned);
        var old = Assert.Single(report.Items, x => x.Old);
        Assert.Equal("dotnet-core-uninstall remove 8.0.101 --sdk", old.Command);
        Assert.All(report.Items, x => Assert.False(x.CanRemove));
    }

    [Fact]
    public async Task Scan_MalformedPinDisablesOldSdkRecommendations()
    {
        Write(@"repo\global.json", "broken");
        var report = await Service("8.0.100 [C:\\sdk]\n8.0.101 [C:\\sdk]")
            .ScanAsync([Path.Combine(_root, "repo")], _ => { }, ScanProgress.None, CancellationToken.None);
        Assert.NotEmpty(report.Warnings);
        Assert.DoesNotContain(report.Items, x => x.Old || x.Command is not null);
    }

    [Fact]
    public async Task Remove_RejectsUnknownAndSdkItemsAndDeletesOnlyScannedTempImage()
    {
        var image = Write(@"Local\Android\Sdk\system-images\android-35\google_apis\x86_64\system.img");
        var service = Service("8.0.100 [C:\\sdk]");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(image, OpReporter.None, CancellationToken.None));
        var report = await service.ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(report.Items.Single(x => x.Kind == "dotnet").Id, OpReporter.None, CancellationToken.None));
        var item = report.Items.Single(x => x.Kind == "android-image");
        Assert.Equal(4, await service.RemoveAsync(item.Id, OpReporter.None, CancellationToken.None));
        Assert.False(File.Exists(image));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(item.Id, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_ExternalAndroidSdkIsReadOnly()
    {
        Write(@"external\system-images\android-35\google_apis\x86_64\system.img");
        var service = new ToolchainService(new ToolchainLocations(Path.Combine(_root, "profile"), Path.Combine(_root, "profile", "Local"), Path.Combine(_root, "profile", "Roaming"), Path.Combine(_root, "external")),
            _ => Task.FromResult(new ProcessResult(0, "", "")));
        var report = await service.ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        Assert.False(Assert.Single(report.Items).CanRemove);
    }

    [Fact]
    public async Task Scan_LinkedSdkAncestorCannotBeRemoved()
    {
        var keep = Write(@"outside\system-images\android-35\google_apis\x86_64\keep.img");
        var link = Path.Combine(_root, "profile", "Sdk");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var mklink = await ProcessRunner.RunAsync("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(_root, "outside")]);
        Assert.True(mklink.Ok, "Could not create temp junction.");
        var service = new ToolchainService(new ToolchainLocations(Path.Combine(_root, "profile"), Path.Combine(_root, "profile", "Local"), Path.Combine(_root, "profile", "Roaming"), link),
            _ => Task.FromResult(new ProcessResult(0, "", "")));
        var report = await service.ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.False(item.CanRemove);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(item.Id, OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void FindPins_InvalidVersionCannotAuthorizeOldSdkRecommendations()
    {
        Write(@"repo\global.json", "{\"sdk\":{\"version\":\"not-a-version\"}}");
        Assert.False(DotnetSdks.FindPins([Path.Combine(_root, "repo")], CancellationToken.None).Complete);
    }

    [Fact]
    public async Task Scan_CancelledRescanRevokesPreviousItems()
    {
        Write(@"Local\Android\Sdk\system-images\android-35\google_apis\x86_64\system.img");
        var service = Service();
        var report = await service.ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync([], _ => cts.Cancel(), ScanProgress.None, cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(Assert.Single(report.Items).Id, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_DotnetUnavailableStillListsPerUserTools()
    {
        Write(@".android\avd\Pixel.avd\userdata.img");
        var service = new ToolchainService(new ToolchainLocations(_root, Path.Combine(_root, "Local"), Path.Combine(_root, "Roaming"), Path.Combine(_root, "Sdk")),
            _ => Task.FromResult(new ProcessResult(-1, "", "missing")));
        var report = await service.ScanAsync([], _ => { }, ScanProgress.None, CancellationToken.None);
        Assert.Equal("avd", Assert.Single(report.Items).Kind);
        Assert.Contains(report.Warnings, x => x.Contains("unavailable"));
    }
}
