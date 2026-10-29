using System.Text;
using Nadfny.Features.Wsl;

namespace Nadfny.Tests;

public sealed class WslOutputTests
{
    private const string English = "WSL version: 2.1.5.0\r\nKernel version: 5.15.146.1-1\r\nWSLg version: 1.0.61\r\nMSRDC version: 1.2.5326.0\r\n";

    [Fact]
    public void ParseVersion_ReadsTheWslLineNotTheKernelOrWslgLine() =>
        Assert.Equal(new Version(2, 1, 5, 0), WslOutput.ParseVersion(English));

    [Fact]
    public void ParseVersion_ReadsUtf16OutputThatArrivesAsUtf8WithNulBetweenCharacters()
    {
        // wsl.exe writes UTF-16 when its output is redirected, and ProcessRunner decodes it as UTF-8.
        var captured = Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(English));

        Assert.Contains('\0', captured);
        Assert.Equal(new Version(2, 1, 5, 0), WslOutput.ParseVersion(captured));
    }

    [Fact]
    public void ParseVersion_SkipsAByteOrderMark() =>
        Assert.Equal(new Version(2, 0, 9, 0), WslOutput.ParseVersion("﻿WSL version: 2.0.9.0\r\n"));

    [Fact]
    public void ParseVersion_ReadsLocalizedLabels() =>
        Assert.Equal(new Version(2, 1, 5, 0), WslOutput.ParseVersion("WSL-Version: 2.1.5.0\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("Usage: wsl.exe [Argument] [Options...]\r\n")]
    [InlineData("WSLg version: 1.0.61\r\n")]
    public void ParseVersion_ReturnsNullWhenThereIsNoWslVersion(string output) =>
        Assert.Null(WslOutput.ParseVersion(output));

    [Theory]
    [InlineData(null, false)]
    [InlineData("1.2.5.0", false)]
    [InlineData("2.0.0.0", true)]
    [InlineData("2.1.5.0", true)]
    public void SupportsSparse_NeedsWsl2OrNewer(string? version, bool expected) =>
        Assert.Equal(expected, WslOutput.SupportsSparse(version is null ? null : Version.Parse(version)));

    [Fact]
    public void Clean_RemovesNulsAndSurroundingWhitespace() =>
        Assert.Equal("WSL", WslOutput.Clean("W\0S\0L\0\n"));
}
