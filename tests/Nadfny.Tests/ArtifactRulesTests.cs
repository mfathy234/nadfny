using Nadfny.Features.Artifacts;

namespace Nadfny.Tests;

public sealed class ArtifactRulesTests
{
    [Theory]
    [InlineData("Library", "Assets", "ProjectSettings")]
    [InlineData("Temp", "Assets", "ProjectSettings")]
    [InlineData("Obj", "Assets", "ProjectSettings")]
    [InlineData("Logs", "Assets", "ProjectSettings")]
    [InlineData(".godot", "project.godot", "")]
    [InlineData(".turbo", "package.json", "")]
    [InlineData(".parcel-cache", "package.json", "")]
    [InlineData(".svelte-kit", "package.json", "")]
    [InlineData(".nuxt", "package.json", "")]
    [InlineData(".angular", "package.json", "")]
    [InlineData(".expo", "package.json", "")]
    [InlineData(".dart_tool", "pubspec.yaml", "")]
    [InlineData("build", "pubspec.yaml", "")]
    [InlineData("_build", "mix.exs", "")]
    [InlineData("deps", "mix.exs", "")]
    [InlineData("Pods", "Podfile", "")]
    [InlineData(".gradle", "build.gradle", "")]
    [InlineData("build", "build.gradle", "")]
    [InlineData("build", "build.gradle.kts", "")]
    public void Match_NewTypesRequireTheirMarkers(string folder, string marker, string second)
    {
        Assert.NotNull(ArtifactRules.Match(folder, Siblings(marker, second)));
        Assert.Null(ArtifactRules.Match(folder, Siblings("README.md")));
        if (second.Length > 0)
        {
            Assert.Null(ArtifactRules.Match(folder, Siblings(marker)));
            Assert.Null(ArtifactRules.Match(folder, Siblings(second)));
        }
    }

    [Theory]
    [InlineData("__pycache__")]
    [InlineData(".pytest_cache")]
    [InlineData(".mypy_cache")]
    [InlineData(".ruff_cache")]
    [InlineData(".tox")]
    public void Match_PythonCachesNeedNoMarker(string folder) =>
        Assert.NotNull(ArtifactRules.Match(folder, Siblings()));

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
