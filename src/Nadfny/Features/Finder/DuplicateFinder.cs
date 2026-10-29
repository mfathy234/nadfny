using System.Security.Cryptography;
using Nadfny.Core;

namespace Nadfny.Features.Finder;

internal static class DuplicateFinder
{
    internal static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<FinderFile> files,
        Action<DuplicateGroup> emit, ScanProgress progress, CancellationToken ct)
    {
        var partial = new List<(FinderFile File, string Hash)>();
        var identities = new HashSet<string>();
        var candidates = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        progress.SetPhase("Comparing file samples", candidates.Count);
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = Open(file.Path);
                if (identities.Add(FinderNative.Identity(stream.SafeFileHandle)))
                {
                    var hash = await HashAsync(stream, true, ct);
                    if (Unchanged(file)) partial.Add((file, hash));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            progress.CompleteOne(file.Path);
        }

        var full = new List<(FinderFile File, string Hash)>();
        identities.Clear();
        var remaining = partial.GroupBy(x => (x.File.Length, x.Hash)).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        progress.SetPhase("Verifying matching files", remaining.Count);
        foreach (var (file, _) in remaining)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = Open(file.Path);
                if (!identities.Add(FinderNative.Identity(stream.SafeFileHandle))) continue;
                var hash = await HashAsync(stream, false, ct);
                if (Unchanged(file)) full.Add((file, hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            progress.CompleteOne(file.Path);
        }

        var groups = full.GroupBy(x => (x.File.Length, x.Hash)).Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Key.Hash, g.Select(x => x.File).OrderByDescending(f => f.LastWriteUtc).ToList(),
                g.Sum(x => x.File.Bytes) - g.Max(x => x.File.Bytes)))
            .OrderByDescending(g => g.WastedBytes).ToList();
        foreach (var group in groups) { ct.ThrowIfCancellationRequested(); emit(group); }
        progress.Flush();
        return groups;
    }

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static bool Unchanged(FinderFile file)
    {
        var info = new FileInfo(file.Path);
        return info.Exists && info.Length == file.Length && info.LastWriteTimeUtc == file.LastWriteUtc &&
            !FinderRules.SkipFile(info.Name, info.Attributes) && FinderRules.SafeScanPath(file.Folder);
    }

    internal static async Task<string> HashAsync(FileStream stream, bool partial, CancellationToken ct)
    {
        if (!partial) return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        var buffer = new byte[65536 * 2];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, 65536), ct);
        stream.Seek(-65536, SeekOrigin.End);
        await stream.ReadExactlyAsync(buffer.AsMemory(65536, 65536), ct);
        return Convert.ToHexString(SHA256.HashData(buffer));
    }
}
