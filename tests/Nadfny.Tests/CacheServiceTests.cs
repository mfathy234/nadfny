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
        var freed = await service.ClearAsync("npm", OpReporter.None, CancellationToken.None);

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

        var freed = await service.ClearAsync("temp", OpReporter.None, CancellationToken.None);

        Assert.Equal(150, freed);
        Assert.False(Directory.Exists(Path.Combine(_root, "Temp", "stale-folder")));
        Assert.False(File.Exists(Path.Combine(_root, "Temp", "stale-file.log")));
        Assert.True(File.Exists(Path.Combine(_root, "Temp", "busy-tool", "deep", "nested", "fresh.lock")));
    }

    [Fact]
    public async Task ClearAsync_RefusesATempPathThatIsNotATempFolder()
    {
        Write(@"Documents\keep.txt", 10);
        var service = new CacheService(() => [new CacheDefinition("temp", "Temporary files", Path.Combine(_root, "Documents"), "")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync("temp", OpReporter.None, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, "Documents", "keep.txt")));
    }

    [Fact]
    public async Task ClearAsync_UnknownIdThrows()
    {
        var service = new CacheService(() => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearAsync("nope", OpReporter.None, CancellationToken.None));
    }
}
