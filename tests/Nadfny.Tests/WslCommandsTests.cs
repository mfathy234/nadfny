using Nadfny.Features.Wsl;

namespace Nadfny.Tests;

public sealed class WslCommandsTests
{
    private static WslDistro Wsl2(string name) =>
        new("{4f1b2c3d-0000-4000-8000-000000000001}", name, 2, @"C:\Users\Jane\AppData\Local\wsl\{x}\ext4.vhdx", 1, false);

    [Fact]
    public void SetSparse_BuildsTheManageArgumentsForAScannedDistro() =>
        Assert.Equal(new[] { "--manage", "Ubuntu-22.04", "--set-sparse", "true" }, WslCommands.SetSparse(Wsl2("Ubuntu-22.04")));

    [Theory]
    [InlineData("")]
    [InlineData("Ubuntu 22")]
    [InlineData("-rf")]
    [InlineData("Ubuntu;calc")]
    [InlineData("Ubuntu\"x")]
    [InlineData("Ubuntu\n--unregister")]
    [InlineData("..\\evil")]
    public void SetSparse_RefusesAnyNameOutsideTheAllowList(string name) =>
        Assert.Throws<InvalidOperationException>(() => WslCommands.SetSparse(Wsl2(name)));

    [Fact]
    public void SetSparse_RefusesANameLongerThan64Characters() =>
        Assert.Throws<InvalidOperationException>(() => WslCommands.SetSparse(Wsl2(new string('a', 65))));

    [Fact]
    public void SetSparse_RefusesAWsl1Distribution() =>
        Assert.Throws<InvalidOperationException>(() => WslCommands.SetSparse(Wsl2("Ubuntu") with { Version = 1 }));

    [Fact]
    public void Shutdown_IsASingleFixedArgument() =>
        Assert.Equal(new[] { "--shutdown" }, WslCommands.Shutdown);

    [Theory]
    [InlineData(@"C:\Users\Jane\AppData\Local\wsl\{x}\ext4.vhdx")]
    [InlineData(@"D:\WSL Disks\Jane Doe\ext4.VHDX")]
    public void VhdxPath_AcceptsAFullyQualifiedDiskFile(string path) =>
        Assert.Equal(path, WslCommands.VhdxPath(path));

    [Theory]
    [InlineData(@"C:\disks\ext4.vhd")]
    [InlineData(@"C:\disks\ext4.vhdx.exe")]
    [InlineData("ext4.vhdx")]
    [InlineData(@"C:\disks\x'.vhdx")]
    [InlineData("C:\\disks\\x\".vhdx")]
    [InlineData("C:\\disks\\x\n.vhdx")]
    [InlineData(@"C:\disks\`calc.vhdx")]
    public void VhdxPath_RefusesAnythingThatIsNotASafeDiskFile(string path) =>
        Assert.Throws<InvalidOperationException>(() => WslCommands.VhdxPath(path));

    [Fact]
    public void CompactScript_CompactsOnlyTheGivenDiskAndShutsWslDown()
    {
        var script = WslCommands.CompactScript(@"C:\Users\Jane Doe\ext4.vhdx");

        Assert.Contains(@"select vdisk file=""C:\Users\Jane Doe\ext4.vhdx""", script);
        Assert.Contains("compact vdisk", script);
        Assert.Contains("wsl.exe --shutdown", script);
    }

    [Fact]
    public void CompactScript_RefusesAPathThatWouldBreakTheScript() =>
        Assert.Throws<InvalidOperationException>(() => WslCommands.CompactScript(@"C:\x'; Remove-Item C:\ -Recurse; '.vhdx"));
}
