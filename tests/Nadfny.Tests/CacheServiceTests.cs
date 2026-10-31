using Nadfny.Core;
using Nadfny.Features.Caches;

namespace Nadfny.Tests;

/// <summary>Clears dummy cache folders only; never touches the machine's real caches.</summary>
public sealed class CacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-cache-tests-" + Guid.NewGuid().ToString("N"));

    public CacheServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Write(string relative, int bytes, DateTime? writeUtc = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (writeUtc is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, t);
        }
        return path;
    }

    [Fact]
    public async Task ClearAsync_EmptiesTheCacheButKeepsTheFolder()
    {
        Write(@"npm\_cacache\a.bin", 1000);
        Write(@"npm\_cacache\sub\b.bin", 500);
        var service = new CacheService(() => [new CacheDefinition("npm", "npm cache", Path.Combine(_root, "npm"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var freed = await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(1500, Assert.Single(scanned).Bytes);
        Assert.Equal(1500, freed);
        Assert.True(Directory.Exists(Path.Combine(_root, "npm")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "npm")));
    }

    [Fact]
    public async Task ClearAsync_TempKeepsAnythingWithARecentFileAtAnyDepth()
    {
        var old = DateTime.UtcNow.AddDays(-5);
        Write(@"Temp\stale-folder\x.tmp", 100, old);
        Write(@"Temp\stale-file.log", 50, old);
        // Folder itself looks old, but a file deep inside was written just now: must be kept.
        Write(@"Temp\busy-tool\deep\nested\fresh.lock", 10);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "Temp", "busy-tool"), old);
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", Path.Combine(_root, "Temp"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        var freed = await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(150, freed);
        Assert.False(Directory.Exists(Path.Combine(_root, "Temp", "stale-folder")));
        Assert.False(File.Exists(Path.Combine(_root, "Temp", "stale-file.log")));
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "busy-tool", "deep", "nested", "fresh.lock")));
    }

    [Fact]
    public async Task Temp_LeavesAiToolFoldersOutOfTheSizeAndTheClear()
    {
        var old = DateTime.UtcNow.AddDays(-5);
        Write(@"Temp\claude\session\scratch.txt", 1000, old);
        Write(@"Temp\codex-trial-tests\out.bin", 400, old);
        Write(@"Temp\ollama123\model.part", 300, old);
        Write(@"Temp\old-installer\setup.tmp", 100, old);
        Write(@"Temp\claudette-notes.log", 20, old); // only the tool name followed by a separator counts
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", Path.Combine(_root, "Temp"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var freed = await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(120, Assert.Single(scanned).Bytes);
        Assert.Equal(120, freed);
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "claude", "session", "scratch.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "codex-trial-tests", "out.bin")));
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "ollama123", "model.part")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Temp", "old-installer")));
        Assert.False(File.Exists(Path.Combine(_root, "Temp", "claudette-notes.log")));
    }

    [Fact]
    public async Task Temp_NeverClearsADownloadedNadfnyUpdate()
    {
        var old = DateTime.UtcNow.AddDays(-5);
        Write(@"Temp\Nadfny-update\Nadfny-0.4.3-setup.exe", 500, old);
        Write(@"Temp\stale\x.tmp", 10, old);
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", Path.Combine(_root, "Temp"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var freed = await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(10, freed);
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "Nadfny-update", "Nadfny-0.4.3-setup.exe")));
    }

    [Fact]
    public void KeepList_ManyWildcardsStayFastOnLongNames()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 45)) + "b";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(TempKeepList.Matches(pattern, new string('a', 200)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Assert.True(TempKeepList.Matches("build-*-run*", "build-17-run-x"));
    }

    [Fact]
    public async Task OtherCaches_DoNotSkipFoldersNamedAfterAiTools()
    {
        Write(@"npm\claude\pkg.tgz", 70);
        var service = new CacheService(() => [new CacheDefinition("npm", "npm cache", Path.Combine(_root, "npm"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        Assert.Equal(70, await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("Claude-Code", true)]
    [InlineData("ClaudeCode", true)]
    [InlineData("claudecode-1234", true)]
    [InlineData("codex_tmp", true)]
    [InlineData("cursor-updater", true)]
    [InlineData("ollama", true)]
    [InlineData("ollama20481", true)]
    [InlineData("lm-studio", true)]
    [InlineData("LMStudio.cache", true)]
    [InlineData("github-copilot", true)]
    [InlineData("chatgpt", true)]
    [InlineData("claudette", false)]
    [InlineData("DockerDesktop", false)]
    [InlineData("jest", false)]
    [InlineData("cursorless", false)]
    public void TempKeepRules_RecognisesAiToolFolders(string name, bool expected) =>
        Assert.Equal(expected, TempKeepRules.IsAiToolFolder(name));

    [Theory]
    [InlineData("crash-dumps")]
    [InlineData("wer-archive")]
    [InlineData("wer-queue")]
    public async Task ClearAsync_CrashFoldersKeepFilesWrittenInTheLast24Hours(string id)
    {
        var old = DateTime.UtcNow.AddDays(-5);
        Write(@"Dumps\old-crash.dmp", 50, old);
        Write(@"Dumps\report-being-written.dmp", 100);
        var service = new CacheService(() => [new CacheDefinition(id, "Crash folder", Path.Combine(_root, "Dumps"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var freed = await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(50, freed);
        Assert.False(File.Exists(Path.Combine(_root, "Dumps", "old-crash.dmp")));
        Assert.True(File.Exists(Path.Combine(_root, "Dumps", "report-being-written.dmp")));
    }

    [Fact]
    public void Definitions_ResolveCrashDumpAndErrorReportFolders()
    {
        var local = Path.Combine(_root, "Local");
        var env = new Dictionary<string, string> { ["USERPROFILE"] = _root, ["LOCALAPPDATA"] = local, ["APPDATA"] = Path.Combine(_root, "Roaming") };
        var definitions = CacheService.Definitions(key => env.GetValueOrDefault(key));
        Assert.Equal(Path.Combine(local, "CrashDumps"), definitions.Single(x => x.Id == "crash-dumps").Path);
        Assert.Equal(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), definitions.Single(x => x.Id == "wer-archive").Path);
        Assert.Equal(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), definitions.Single(x => x.Id == "wer-queue").Path);
        Assert.DoesNotContain(definitions, x => x.Path.Contains("D3DSCache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClearAsync_RefusesATempPathThatIsNotATempFolder()
    {
        Write(@"Documents\keep.txt", 10);
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", Path.Combine(_root, "Documents"), "")]);

        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, "Documents", "keep.txt")));
    }

    [Fact]
    public async Task ClearAsync_UnknownIdThrows()
    {
        var service = new CacheService(() => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync("nope", OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task ClearAsync_RequiresAnItemFromTheLastScan()
    {
        var path = Write(@"go\pkg\mod\keep.txt", 42);
        var service = new CacheService(() => [new("go-mod", "Go modules", Path.GetDirectoryName(path)!, "")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync("go-mod", OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ClearAsync_RemovesReadOnlyGoModules()
    {
        var path = Write(@"go\pkg\mod\example\module.txt", 42);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        File.SetAttributes(Path.GetDirectoryName(path)!, FileAttributes.Directory | FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(_root, "go", "pkg", "mod", "cache", "download")); // what makes it a Go module cache
        var service = new CacheService(() => [new("go-mod", "Go modules", Path.Combine(_root, "go", "pkg", "mod"), "")]);
        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        Assert.Equal(42, await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Definitions_ResolveEnvironmentOverridesAndProductFolders()
    {
        var local = Path.Combine(_root, "Local");
        var roaming = Path.Combine(_root, "Roaming");
        Directory.CreateDirectory(Path.Combine(local, "JetBrains", "Rider2025.1", "caches"));
        Directory.CreateDirectory(Path.Combine(local, "Microsoft", "VisualStudio", "17.0_test", "ComponentModelCache"));
        Directory.CreateDirectory(Path.Combine(_root, "miniconda3", "pkgs"));
        var env = new Dictionary<string, string> { ["USERPROFILE"] = _root, ["LOCALAPPDATA"] = local, ["APPDATA"] = roaming, ["GOMODCACHE"] = Path.Combine(_root, "custom-go") };
        var definitions = CacheService.Definitions(key => env.GetValueOrDefault(key));
        Assert.Equal(env["GOMODCACHE"], definitions.Single(x => x.Id == "go-mod").Path);
        Assert.Equal(Path.Combine(local, "go-build"), definitions.Single(x => x.Id == "go-build").Path);
        Assert.Equal(Path.Combine(roaming, "Code", "CachedExtensionVSIXs"), definitions.Single(x => x.Id == "vscode-vsix").Path);
        var expected = new Dictionary<string, string>
        {
            ["uv"] = Path.Combine(local, "uv", "cache"),
            ["poetry"] = Path.Combine(local, "pypoetry", "Cache"),
            ["bun"] = Path.Combine(_root, ".bun", "install", "cache"),
            ["deno"] = Path.Combine(local, "deno"),
            ["composer-local"] = Path.Combine(local, "Composer", "cache"),
            ["composer-roaming"] = Path.Combine(roaming, "Composer", "cache"),
            ["cargo-git"] = Path.Combine(_root, ".cargo", "git"),
            ["conda-conda"] = Path.Combine(_root, ".conda", "pkgs"),
            ["conda-miniconda3"] = Path.Combine(_root, "miniconda3", "pkgs"),
            ["conda-anaconda3"] = Path.Combine(_root, "anaconda3", "pkgs"),
        };
        Assert.All(expected, item => Assert.Equal(item.Value, definitions.Single(x => x.Id == item.Key).Path));
        env.Remove("GOMODCACHE");
        Assert.Equal(Path.Combine(_root, "go", "pkg", "mod"), CacheService.Definitions(key => env.GetValueOrDefault(key)).Single(x => x.Id == "go-mod").Path);
        Assert.Contains(definitions, x => x.Path == Path.Combine(local, "JetBrains", "Rider2025.1", "caches"));
        Assert.Contains(definitions, x => x.Path == Path.Combine(local, "Microsoft", "VisualStudio", "17.0_test", "ComponentModelCache"));
        Assert.Contains(definitions, x => x.Path == Path.Combine(_root, "miniconda3", "pkgs"));
        Assert.All(definitions, x => Assert.False(string.IsNullOrWhiteSpace(x.Note)));
    }

    [Fact]
    public async Task ClearAsync_StaleIdCannotClearAChangedDefinitionPath()
    {
        Write(@"first\keep.txt", 42);
        var second = Write(@"second\keep.txt", 42);
        var path = Path.Combine(_root, "first");
        var service = new CacheService(() => [new("npm", "npm cache", path, "")]);
        var first = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        path = Path.Combine(_root, "second");
        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var stale = Assert.Single(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync(stale.Id, OpReporter.None, CancellationToken.None, stale.Path));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Scan_IgnoresAGoModCachePathThatIsNotAModuleCache()
    {
        var thesis = Write(@"home\Documents\thesis.docx", 42);
        var service = new CacheService(() => [new("go-mod", "Go modules", Path.Combine(_root, "home"), "")]);

        Assert.Empty(await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync("go-mod", OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(thesis));
    }

    [Fact]
    public async Task ClearAsync_CondaKeepsExtractedPackagesThatEnvironmentsMayReference()
    {
        var package = Write(@"pkgs\python-3\python.exe", 100);
        Write(@"pkgs\python-3.conda", 20);
        Write(@"pkgs\numpy.tar.bz2", 30);
        Write(@"pkgs\cache\repodata.json", 40);
        var service = new CacheService(() => [new("conda-user", "Conda packages", Path.Combine(_root, "pkgs"), "")]);
        var scanned = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        Assert.Equal(90, Assert.Single(scanned).Bytes);
        Assert.Equal(90, await service.ClearAsync(Assert.Single(scanned).Id, OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(package));
    }
}
