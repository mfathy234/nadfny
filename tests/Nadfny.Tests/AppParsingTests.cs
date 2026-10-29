using Nadfny.Features.Apps;

namespace Nadfny.Tests;

public sealed class AppParsingTests
{
    private static UninstallEntry Entry(string name, string? uninstall = @"""C:\Program Files\Foo\uninst.exe"" /S", string? location = null,
        bool system = false, string? parent = null, string? release = null, string? version = "1.0", bool msi = false, string keyName = "Foo") =>
        new($@"LocalMachine|Registry64|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}", keyName, name, "Foo Inc.", version, "20260115",
            location, uninstall, null, 2048, system, msi, parent, release);

    [Fact]
    public void ToCandidates_HidesSystemComponentsUpdatesAndDuplicates()
    {
        var list = UninstallRegistry.ToCandidates(
        [
            Entry("Foo"),
            Entry("Foo"), // the same app seen again through the 32-bit view
            Entry("Hidden", system: true),
            Entry("KB123 for Foo", parent: "Foo"),
            Entry("GDR 1050 for SQL Server", release: "Security"),
            Entry("Hotfix", release: "Hotfix"),
            Entry("No uninstaller", uninstall: null),
        ]);

        var app = Assert.Single(list);
        Assert.Equal("Foo", app.Name);
        Assert.Equal("2026-01-15", app.InstalledOn);
        Assert.Equal(2048 * 1024L, app.EstimatedBytes);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Git\unins000.exe""", @"C:\Program Files\Git\unins000.exe", "")]
    [InlineData(@"""C:\Program Files\Docker\Docker\Docker Desktop Installer.exe"" ""uninstall""", @"C:\Program Files\Docker\Docker\Docker Desktop Installer.exe", @"""uninstall""")]
    [InlineData(@"C:\Program Files\GIGABYTE\Control Center\uninst.exe", @"C:\Program Files\GIGABYTE\Control Center\uninst.exe", "")]
    [InlineData(@"C:\Program Files\Microsoft OneDrive\OneDriveSetup.exe  /uninstall  /allusers", @"C:\Program Files\Microsoft OneDrive\OneDriveSetup.exe", "/uninstall  /allusers")]
    public void SplitCommand_HandlesQuotedAndUnquotedPaths(string cmd, string file, string args)
    {
        var (f, a) = UninstallRegistry.SplitCommand(cmd);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }

    [Fact]
    public void PlanFor_UsesMsiexecForWindowsInstallerProducts()
    {
        var plan = UninstallRegistry.PlanFor(Entry("Bar", uninstall: "MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", msi: true));

        Assert.Equal(UninstallPlan.Msi, plan!.Kind);
        Assert.Equal("{12345678-1234-1234-1234-123456789ABC}", plan.Target);
        Assert.NotNull(plan.Probe);
    }

    [Fact]
    public void PlanFor_RunsTheRegisteredCommandAndWatchesItsKey()
    {
        var e = Entry("Foo");
        var plan = UninstallRegistry.PlanFor(e);

        Assert.Equal(UninstallPlan.Command, plan!.Kind);
        Assert.Equal(@"C:\Program Files\Foo\uninst.exe", plan.Target);
        Assert.Equal("/S", plan.Arguments);
        Assert.Equal(e.Key, plan.Probe);
    }

    [Theory]
    [InlineData(@"G:\SteamLibrary\steamapps\common\Battlefield 6", "Steam")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite", "Epic Games")]
    [InlineData(@"D:\XboxGames\Forza Horizon 5\Content", "Xbox")]
    [InlineData(@"C:\Program Files\Notepad++", null)]
    public void GameLauncher_RecognizesLauncherFolders(string location, string? launcher) =>
        Assert.Equal(launcher, UninstallRegistry.GameLauncher(location, null));

    [Fact]
    public void InstallFolder_NeverUsesProgramFilesItself()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Null(UninstallRegistry.InstallFolder(Entry("X", location: pf + "\\")));
        Assert.Equal(pf + @"\Foo", UninstallRegistry.InstallFolder(Entry("X", location: pf + @"\Foo\")));
    }

    [Fact]
    public void Vdf_ReadsSteamLibraryFolders()
    {
        const string text = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "228980" "25674515" } }
                "1" { "path" "G:\\SteamLibrary" }
            }
            """;
        var paths = Vdf.Parse(text).Block("libraryfolders")!.Blocks.Select(b => b["path"]).ToList();

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"G:\SteamLibrary"], paths);
    }

    [Fact]
    public void SteamGame_ReadsTheManifestAndSkipsRedistributables()
    {
        const string acf = """
            "AppState" { "appid" "2807960" "name" "Battlefield 6" "installdir" "Battlefield 6" "SizeOnDisk" "173834745308" "LastUpdated" "1790000000" }
            """;
        var game = GameLibraries.SteamGame(@"G:\SteamLibrary", @"G:\SteamLibrary\steamapps\appmanifest_2807960.acf", acf)!;

        Assert.Equal("steam:2807960", game.Id);
        Assert.True(game.IsGame);
        Assert.Equal(@"G:\SteamLibrary\steamapps\common\Battlefield 6", game.InstallLocation);
        Assert.Equal(173834745308, game.EstimatedBytes);
        Assert.Equal("steam://uninstall/2807960", game.Uninstall!.Target);
        Assert.Equal([@"G:\SteamLibrary\steamapps\shadercache\2807960"], game.KnownCaches);

        Assert.Null(GameLibraries.SteamGame("G:", "x", """ "AppState" { "appid" "228980" "name" "Steamworks Common Redistributables" "installdir" "Steamworks Shared" } """));
    }

    [Fact]
    public void EpicGame_ReadsTheItemManifest()
    {
        var game = GameLibraries.EpicGame("""{"DisplayName":"Fortnite","AppName":"Fortnite","InstallLocation":"C:/Program Files/Epic Games/Fortnite","InstallSize":1000,"bIsApplication":false}""")!;

        Assert.True(game.IsGame);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", game.InstallLocation);
        Assert.Null(GameLibraries.EpicGame("""{"DisplayName":"X","AppName":"X","InstallLocation":"C:/X","bIsIncompleteInstall":true}"""));
    }

    [Fact]
    public void StoreApps_ParsesPackagesAndKnowsTheirDataFolder()
    {
        const string json = """[{"name":"Calculator","full":"Microsoft.WindowsCalculator_11.0_x64__8wekyb3d8bbwe","family":"Microsoft.WindowsCalculator_8wekyb3d8bbwe","publisher":"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond","version":"11.0","location":"C:\\Program Files\\WindowsApps\\Microsoft.WindowsCalculator"}]""";
        var app = Assert.Single(StoreApps.Parse(json, @"C:\Users\me\AppData\Local\Packages"));

        Assert.Equal("Calculator", app.Name);
        Assert.Equal("Microsoft Corporation", app.Publisher);
        Assert.Equal([@"C:\Users\me\AppData\Local\Packages\Microsoft.WindowsCalculator_8wekyb3d8bbwe"], app.KnownData);
        Assert.Equal(UninstallPlan.Appx, app.Uninstall!.Kind);
        Assert.DoesNotContain(app.KnownCaches, p => p.EndsWith("LocalCache"));
    }

    [Theory]
    [InlineData("Microsoft.App_1.0_x64__abc")]
    [InlineData("A-b.C~d_9")]
    public void EnsurePackageName_AcceptsPackageNames(string name) => StoreApps.EnsurePackageName(name);

    [Theory]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("a b")]
    public void EnsurePackageName_RejectsAnythingElse(string name) =>
        Assert.Throws<InvalidOperationException>(() => StoreApps.EnsurePackageName(name));
}
