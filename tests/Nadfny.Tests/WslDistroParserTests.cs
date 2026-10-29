using Nadfny.Core;
using Nadfny.Features.Wsl;

namespace Nadfny.Tests;

/// <summary>Uses dummy folders under the temp path only; no real WSL distribution is read or changed.</summary>
public sealed class WslDistroParserTests : IDisposable
{
    private const string UbuntuId = "{4f1b2c3d-0000-4000-8000-000000000001}";
    private const string OtherId = "{4f1b2c3d-0000-4000-8000-000000000002}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-wsl-parser-" + Guid.NewGuid().ToString("N"));

    public WslDistroParserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Folder(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(dir, file), new byte[1000]);
        return dir;
    }

    [Fact]
    public void Parse_ReadsNameVersionAndDiskSizeOfTheExt4Disk()
    {
        var dir = Folder("Ubuntu", "ext4.vhdx");

        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Ubuntu", dir, 2)]));

        Assert.Equal(UbuntuId, distro.Id);
        Assert.Equal("Ubuntu", distro.Name);
        Assert.Equal(2, distro.Version);
        Assert.Equal(Path.Combine(dir, "ext4.vhdx"), distro.VhdxPath);
        Assert.Equal(1000, distro.Bytes);
        Assert.False(distro.IsSparse);
        Assert.True(distro.Compactable);
    }

    [Fact]
    public void Parse_RemovesTheExtendedLengthPrefixFromBasePath()
    {
        var dir = Folder("Debian", "ext4.vhdx");

        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Debian", @"\\?\" + dir, 2)]));

        Assert.Equal(Path.Combine(dir, "ext4.vhdx"), distro.VhdxPath);
    }

    [Fact]
    public void Parse_SkipsDockerDesktopDistributions()
    {
        var dir = Folder("docker", "ext4.vhdx");

        var distros = WslDistroParser.Parse(
        [
            new LxssEntry(UbuntuId, "docker-desktop", dir, 2),
            new LxssEntry(OtherId, "Docker-Desktop-Data", dir, 2),
        ]);

        Assert.Empty(distros);
    }

    [Fact]
    public void Parse_KeepsAWsl1DistributionWithoutADiskFile()
    {
        var dir = Folder("Alpine", "rootfs.txt");

        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Alpine", dir, 1)]));

        Assert.Equal(1, distro.Version);
        Assert.Null(distro.VhdxPath);
        Assert.False(distro.Compactable);
        Assert.Equal(0, distro.Bytes);
    }

    [Fact]
    public void Parse_UsesTheSingleVhdxWhenTheDiskHasAnotherName()
    {
        var dir = Folder("Arch", "rootfs.vhdx");

        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Arch", dir, 2)]));

        Assert.Equal(Path.Combine(dir, "rootfs.vhdx"), distro.VhdxPath);
    }

    [Fact]
    public void Parse_DoesNotGuessBetweenSeveralDiskFiles()
    {
        var dir = Folder("Mixed", "a.vhdx", "b.vhdx");

        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Mixed", dir, 2)]));

        Assert.Null(distro.VhdxPath);
        Assert.Equal(0, distro.Bytes);
    }

    [Fact]
    public void Parse_LeavesOutEntriesWithAMalformedIdNoNameOrNoVersion()
    {
        var dir = Folder("Odd", "ext4.vhdx");

        var distros = WslDistroParser.Parse(
        [
            new LxssEntry("not-a-guid", "Ubuntu", dir, 2),
            new LxssEntry(UbuntuId, null, dir, 2),
            new LxssEntry(OtherId, "NoVersion", dir, null),
        ]);

        Assert.Empty(distros);
    }

    [Fact]
    public void Parse_HasNoDiskFileWhenTheFolderIsMissing()
    {
        var distro = Assert.Single(WslDistroParser.Parse([new LxssEntry(UbuntuId, "Gone", Path.Combine(_root, "missing"), 2)]));

        Assert.Null(distro.VhdxPath);
    }
}
