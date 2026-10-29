using Nadfny.Features.Artifacts;

namespace Nadfny.Tests;

public sealed class ArtifactRulesTests
{
    private static HashSet<string> Siblings(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("bin", "App.csproj")]
    [InlineData("obj", "App.fsproj")]
    [InlineData("target", "Cargo.toml")]
    [InlineData("target", "pom.xml")]
    [InlineData("build", "build.gradle.kts")]
    [InlineData("dist", "package.json")]
    [InlineData(".next", "package.json")]
    [InlineData(".venv", "pyproject.toml")]
    public void Match_RecognizesRegenerableOutput(string folder, string marker) =>
        Assert.NotNull(ArtifactRules.Match(folder, Siblings(marker, "README.md")));

    [Theory]
    [InlineData("bin")]      // e.g. scripts folder in a non-.NET repo
    [InlineData("obj")]
    [InlineData("dist")]     // dist without package.json may be a release artifact someone keeps
    [InlineData("build")]    // build scripts folder
    [InlineData("target")]
    [InlineData("venv")]
    [InlineData("src")]
    public void Match_RefusesFoldersWithoutTheirToolMarker(string folder) =>
        Assert.Null(ArtifactRules.Match(folder, Siblings("README.md", "LICENSE")));

    [Fact]
    public void Match_NodeModulesNeedsPackageJson()
    {
        Assert.NotNull(ArtifactRules.Match("node_modules", Siblings("package.json")));
        Assert.Null(ArtifactRules.Match("node_modules", Siblings("extension.vsixmanifest")));
    }

    [Theory]
    [InlineData("__pycache__")]
    [InlineData(".vs")]
    public void Match_AlwaysRegenerableFoldersNeedNoMarker(string folder) =>
        Assert.NotNull(ArtifactRules.Match(folder, Siblings()));

    [Fact]
    public void Match_IsCaseInsensitiveLikeWindows() =>
        Assert.NotNull(ArtifactRules.Match("Node_Modules", Siblings("PACKAGE.JSON")));
}
