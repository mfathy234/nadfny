using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace Nadfny.Features.Finder;

internal static class FolderSnapshot
{
    internal static string Capture(string path, CancellationToken ct)
    {
        var entries = new FileSystemEnumerable<string>(path, (ref FileSystemEntry e) =>
        {
            ct.ThrowIfCancellationRequested();
            var full = e.ToFullPath();
            return $"{Path.GetRelativePath(path, full)}|{FinderNative.Identity(full)}|{e.Length}|{e.LastWriteTimeUtc.UtcTicks}|{(int)e.Attributes}";
        }, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(entry + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
