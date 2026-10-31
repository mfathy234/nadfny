using Nadfny.Core;
using Nadfny.Features.Apps;

namespace Nadfny.Tests;

/// <summary>Data matching, game folders and the clear/leftover flows, against dummy folders in Temp only.</summary>
public sealed class AppDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-app-tests-" + Guid.NewGuid().ToString("N"));

    public AppDataTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Write(string relative, int bytes = 100)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static AppCandidate App(string id, string name, string? publisher = null, string? location = null, string source = AppSource.Win32) =>
        new(id, name, publisher, null, null, source, false, null, location, null, new UninstallPlan(UninstallPlan.Command, "x"), [], []);

    [Theory]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Docker Desktop 4.40.0", "dockerdesktop")]
    [InlineData("Battlefield™ 6", "battlefield6")]
    public void Normalize_DropsVersionsArchitectureAndSymbols(string name, string key) =>
        Assert.Equal(key, AppDataLocator.Normalize(name));

    [Fact]
    public void Match_FindsExactNamesAndVendorSubfoldersOnly()
    {
        var roaming = Path.Combine(_root, "Roaming");
        var local = Path.Combine(_root, "Local");
        Directory.CreateDirectory(Path.Combine(roaming, "Notepad++"));
        Directory.CreateDirectory(Path.Combine(local, "Google", "Chrome"));
        Directory.CreateDirectory(Path.Combine(local, "Other", "Chrome"));   // not Google's: must not match
        Directory.CreateDirectory(Path.Combine(local, "Notepad Helper"));    // partial name: must not match
        Directory.CreateDirectory(Path.Combine(local, "Microsoft", "Teams"));

        var apps = new[]
        {
            App("np", "Notepad++ (64-bit x64)", "Notepad++ Team"),
            App("chrome", "Google Chrome", "Google LLC"),
            App("teams", "Microsoft Teams", "Microsoft Corporation"),
        };
        var map = AppDataLocator.Match(apps, AppDataLocator.Index([roaming, local], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        Assert.Equal([Path.Combine(roaming, "Notepad++")], map["np"]);
        Assert.Equal([Path.Combine(local, "Google", "Chrome")], map["chrome"]);
        Assert.Equal([Path.Combine(local, "Microsoft", "Teams")], map["teams"]);
    }

    [Fact]
    public void FindCaches_FindsChromiumStyleCachesDeepInside()
    {
        Write(@"App\User Data\Default\Cache\Cache_Data\f_000001");
        Write(@"App\User Data\Default\Code Cache\js\index");
        Write(@"App\User Data\Default\Preferences");
        var caches = AppDataLocator.FindCaches(Path.Combine(_root, "App")).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["Cache", "Code Cache"], caches);
    }

    [Fact]
    public void GameFolderScanner_FindsDownloadedGamesInCategoryFolders()
    {
        Write(@"Games\Racing\Speed.Game.v1.2\SpeedGame.exe");
        Write(@"Games\Racing\Speed.Game.v1.2\UnityPlayer.dll");
        Write(@"Games\RPG\Quest\Quest.exe");
        Directory.CreateDirectory(Path.Combine(_root, @"Games\RPG\Quest\Quest_Data"));
        Write(@"Games\Indie\Tiny\Tiny.exe");
        Write(@"Games\Indie\Tiny\Tiny.pck");
        Write(@"Tools\SomeTool\tool.exe");                 // an exe alone is not a game
        Write(@"Tools\SomeTool\steam_api64.dll", 10);      // ...but a folder already known is skipped
        var known = new HashSet<string>([Path.Combine(_root, @"Tools\SomeTool")], StringComparer.OrdinalIgnoreCase);

        var found = GameFolderScanner.Find([_root], known, CancellationToken.None).Select(p => Path.GetRelativePath(_root, p)).Order().ToList();

        Assert.Equal([@"Games\Indie\Tiny", @"Games\Racing\Speed.Game.v1.2", @"Games\RPG\Quest"], found);
        Assert.Equal("Speed Game", GameFolderScanner.DisplayName(Path.Combine(_root, @"Games\Racing\Speed.Game.v1.2")));
        Assert.Equal("Racing", GameFolderScanner.Category(Path.Combine(_root, @"Games\Racing\Speed.Game.v1.2")));
    }

    [Fact]
    public async Task ClearCache_EmptiesCachesAndKeepsSettings()
    {
        var data = Path.Combine(_root, "Roaming", "Chatty");
        Write(@"Roaming\Chatty\Cache\a.bin", 1000);
        Write(@"Roaming\Chatty\GPUCache\b.bin", 500);
        Write(@"Roaming\Chatty\settings.json", 10);
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App("chatty", "Chatty")]),
            () => [Path.Combine(_root, "Roaming")], () => []);

        var app = Assert.Single(await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None));
        var result = await service.ClearCacheAsync("chatty", OpReporter.None, CancellationToken.None);

        Assert.Equal(1510, app.DataBytes);
        Assert.Equal(1500, app.CacheBytes);
        Assert.Equal(1500, result.Freed);
        Assert.True(File.Exists(Path.Combine(data, "settings.json")));
        Assert.True(Directory.Exists(Path.Combine(data, "Cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "Cache")));
    }

    [Fact]
    public async Task Scan_NamesTheDriveTheAppIsInstalledOn()
    {
        var folder = Path.Combine(_root, "Games", "Spider-Man 2");
        Write(@"Games\Spider-Man 2\game.exe", 100);
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App("spidey", "Spider-Man 2", location: folder), App("nowhere", "No folder")]),
            () => [], () => []);

        var apps = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        Assert.EndsWith($"({folder[..2].ToUpperInvariant()})", apps.Single(a => a.Id == "spidey").Drive);
        Assert.Null(apps.Single(a => a.Id == "nowhere").Drive);
    }

    [Fact]
    public async Task Leftovers_SkipFoldersAnotherAppUses()
    {
        Write(@"Roaming\Gone\state.db");
        Write(@"Roaming\Stays\state.db");
        var gone = App("gone", "Gone");
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([gone, App("stays", "Stays", location: Path.Combine(_root, "Roaming", "Gone"))]),
            () => [Path.Combine(_root, "Roaming")], () => []);
        var apps = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        // "Stays" is installed inside Gone's data folder, so that folder is not a leftover.
        Assert.Empty(service.Leftovers(gone, apps.Single(a => a.Id == "gone")));
    }

    [Fact]
    public async Task RemoveLeftovers_RefusesPathsItDidNotOffer()
    {
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([]), () => [], () => []);
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveLeftoversAsync("never-uninstalled", [_root], OpReporter.None, CancellationToken.None));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task WaitUntilGone_GivesUpAfterTheGracePeriodOnceTheUninstallerExited()
    {
        var gone = await AppUninstaller.WaitUntilGoneAsync(() => true, null, TimeSpan.FromMilliseconds(50), CancellationToken.None, TimeSpan.FromMilliseconds(10));
        Assert.False(gone);

        var checks = 0;
        Assert.True(await AppUninstaller.WaitUntilGoneAsync(() => ++checks < 3, null, TimeSpan.FromSeconds(5), CancellationToken.None, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Match_GivesAFolderTwoAppsTieOnToBoth()
    {
        var roaming = Path.Combine(_root, "Roaming");
        Directory.CreateDirectory(Path.Combine(roaming, "Python"));
        var map = AppDataLocator.Match([App("py311", "Python 3.11.9"), App("py312", "Python 3.12.4")],
            AppDataLocator.Index([roaming], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        Assert.Equal([Path.Combine(roaming, "Python")], map["py311"]);
        Assert.Equal([Path.Combine(roaming, "Python")], map["py312"]);
    }

    [Fact]
    public async Task Leftovers_NeverOfferAFolderSharedWithAnotherInstalledApp()
    {
        Write(@"Roaming\Python\Python311\site-packages\x.py");
        var py312 = App("py312", "Python 3.12.4");
        var service = new AppService(_ => Task.FromResult<IReadOnlyList<AppCandidate>>([App("py311", "Python 3.11.9"), py312]),
            () => [Path.Combine(_root, "Roaming")], () => []);
        var apps = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        Assert.Empty(service.Leftovers(py312, apps.Single(a => a.Id == "py312")));
    }

    [Fact]
    public void ShareFolders_SizesASharedFolderOnce()
    {
        var shared = Path.Combine(_root, "NVIDIA");
        var list = AppService.ShareFolders([App("a", "NVIDIA Graphics Driver", location: shared), App("b", "NVIDIA PhysX", location: shared), App("c", "Other", location: _root)]);

        Assert.Equal(shared, list[0].InstallLocation);
        Assert.Null(list[1].InstallLocation);
        Assert.Contains("NVIDIA Graphics Driver", list[1].Note);
        Assert.Equal(_root, list[2].InstallLocation);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\Thesis", "Game")]
    [InlineData("123", @"..\..\Windows")]
    public void SteamGame_RejectsManifestsThatPointOutsideTheLibrary(string appId, string installDir) =>
        Assert.Null(GameLibraries.SteamGame(@"G:\SteamLibrary", "m.acf", $$"""
            "AppState" { "appid" "{{appId.Replace(@"\", @"\\")}}" "name" "Evil" "installdir" "{{installDir.Replace(@"\", @"\\")}}" }
            """));

    [Fact]
    public void GameFolderScanner_SkipsBuildsInsideGameProjects()
    {
        Directory.CreateDirectory(Path.Combine(_root, @"Work\MyGame\Assets"));
        Directory.CreateDirectory(Path.Combine(_root, @"Work\MyGame\ProjectSettings"));
        Write(@"Work\MyGame\Build\MyGame.exe");
        Write(@"Work\MyGame\Build\UnityPlayer.dll");

        Assert.Empty(GameFolderScanner.Find([_root], new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None));
    }

    [Fact]
    public void EnsureDeletable_SeesThroughShortNames()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var shortName = ShortPath(pf);
        if (shortName is null || shortName.Equals(pf, StringComparison.OrdinalIgnoreCase)) return; // 8.3 names are off on this volume

        Assert.Equal(pf, SafeDeleter.Canonical(shortName), StringComparer.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => SafeDeleter.EnsureDeletable(shortName));
    }

    private static string? ShortPath(string path)
    {
        var buffer = new char[512];
        var n = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return n == 0 ? null : new string(buffer, 0, (int)n);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, char[] buffer, uint size);

    [Fact]
    public void RecycleBin_QueryReportsEveryDriveItCounts()
    {
        var info = Nadfny.Features.RecycleBin.RecycleBinService.Query();

        Assert.True(info.Bytes >= 0 && info.Items >= 0);
        Assert.Equal(info.Bytes, info.Drives.Sum(d => d.Bytes));
    }
}
