using Nadfny.Core;
using Nadfny.Features.Artifacts;

namespace Nadfny.Tests;

public sealed class ArtifactServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string Write(string relative, DateTime? date = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, date ?? _old);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private Task<IReadOnlyList<ArtifactInfo>> Scan(ArtifactService? service = null) =>
        (service ?? new()).ScanAsync([_root], _ => { }, ScanProgress.None, CancellationToken.None);

    [Theory]
    [InlineData("build")]
    [InlineData("out")]
    public async Task Scan_CMakeRequiresCacheInsideOutput(string folder)
    {
        Write($@"cmake\{folder}\CMakeCache.txt");
        Write($@"scripts\{folder}\keep.txt");
        Write(@"scripts\CMakeCache.txt"); // A sibling cache is not proof.
        var item = Assert.Single(await Scan());
        Assert.Equal("CMake", item.Tool);
        Assert.Equal(Path.Combine(_root, "cmake", folder), item.Path);
    }

    [Fact]
    public async Task Scan_LastUsedComesFromSourceAndIsSharedByProjectOutputs()
    {
        var used = _old.AddDays(10);
        Write(@"project\App.csproj");
        Write(@"project\src\feature\Program.cs", used);
        Write(@"project\bin\app.dll", DateTime.UtcNow);
        Write(@"project\obj\app.cache", DateTime.UtcNow);
        Write(@"project\.git\index", DateTime.UtcNow);
        Write(@"project\src\node_modules\lib\index.js", DateTime.UtcNow);
        Write(@"project\src\a\b\too-deep\ignore.cs", DateTime.UtcNow);
        var items = await Scan();
        Assert.Equal(2, items.Count);
        foreach (var item in items)
            Assert.Equal(used, item.ProjectLastUsedUtc);
    }

    [Fact]
    public async Task Scan_LastUsedIncludesTopLevelFiles()
    {
        var used = _old.AddDays(20);
        Write(@"project\package.json", used);
        Write(@"project\node_modules\lib\index.js", DateTime.UtcNow);
        var item = Assert.Single(await Scan());
        Assert.Equal(used, item.ProjectLastUsedUtc);
    }

    [Theory]
    [InlineData("Library", "code.cs")]
    [InlineData(".godot", "code.gd")]
    [InlineData("build", "code.dart")]
    [InlineData("_build", "code.ex")]
    [InlineData("Pods", "code.swift")]
    [InlineData("out", "code.cpp")]
    [InlineData("out", "code.java")]
    [InlineData("Obj", "code.java")]
    [InlineData("build", "code.java")]
    [InlineData(".nuxt", "code.ts")]
    [InlineData(".tox", "code.py")]
    [InlineData("deps", "code.ex")]
    [InlineData(".gradle", "code.java")]
    [InlineData(".expo", "code.js")]
    public async Task Remove_NewTypesKeepHandWrittenSource(string folder, string file)
    {
        Write(@"project\project.godot");
        Write(@"project\pubspec.yaml");
        Write(@"project\mix.exs");
        Write(@"project\Podfile");
        Write(@"project\package.json");
        Write(@"project\build.gradle");
        Directory.CreateDirectory(Path.Combine(_root, "project", "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "project", "ProjectSettings"));
        var output = Path.Combine(_root, "project", folder);
        Write($@"project\{folder}\CMakeCache.txt");
        var service = new ArtifactService();
        Assert.Contains(await Scan(service), a => a.Path == output);
        var source = Write($@"project\{folder}\{file}");
        var result = await service.RemoveAsync([output], OpReporter.None, CancellationToken.None);
        Assert.Equal(0, result.Removed);
        Assert.Single(result.Failed);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Remove_NewOutputDeletesOnlyScannedGeneratedFolder()
    {
        var source = Write(@"project\pubspec.yaml");
        Write(@"project\build\app.dll");
        var service = new ArtifactService();
        var item = Assert.Single(await Scan(service));
        var result = await service.RemoveAsync([item.Path], OpReporter.None, CancellationToken.None);
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(item.Path));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("Library")]
    [InlineData("build")]
    [InlineData("target")]
    public async Task Scan_LastUsedIncludesSourceFoldersWithoutOutputMarkers(string folder)
    {
        var used = _old.AddDays(30);
        Write(@"project\package.json");
        Write(@"project\node_modules\index.js", DateTime.UtcNow);
        Write($@"project\src\{folder}\code.ts", used);
        var item = Assert.Single(await Scan());
        Assert.Equal(used, item.ProjectLastUsedUtc);
    }

    [Theory]
    [InlineData(".nuxt", "package.json", "app.js")]
    [InlineData("deps", "mix.exs", "lib.ex")]
    [InlineData("Pods", "Podfile", "lib.h")]
    [InlineData(".tox", "pyproject.toml", "lib.py")]
    public async Task Remove_NewTypesAllowGeneratorMarkedSource(string folder, string marker, string generated)
    {
        var source = Write($@"project\{marker}");
        var output = Write($@"project\{folder}\{generated}");
        File.WriteAllText(output, "// <auto-generated>\ncontent");
        var service = new ArtifactService();
        var item = Assert.Single(await Scan(service));
        var result = await service.RemoveAsync([item.Path], OpReporter.None, CancellationToken.None);
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(item.Path));
        Assert.True(File.Exists(source));
    }
}
