using System.Collections.Concurrent;

namespace Nadfny.Core;

/// <summary>
/// Finds main git repositories (folders with a .git directory) under the scan locations. Linked worktrees have a
/// .git file and are not returned here; they are listed by git itself with `git worktree list`.
/// </summary>
public static class GitRepoFinder
{
    private const int SearchDepth = 10;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "target", "packages", "vendor", "Library", "SteamLibrary", "steamapps",
    };

    public static async Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, ScanProgress progress, CancellationToken ct)
    {
        progress.SetPhase("Finding repositories");
        var repos = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in TreeWalker.EffectiveRoots(roots).Where(r => Directory.Exists(Path.Combine(r, ".git"))))
            repos.TryAdd(root, 0);
        await TreeWalker.RunAsync(roots, SearchDepth, (dir, children) => VisitForRepos(children, repos), progress, ct);
        return repos.Keys.ToList();
    }

    private static IEnumerable<string> VisitForRepos(IReadOnlyList<TreeWalker.Entry> children, ConcurrentDictionary<string, byte> repos)
    {
        var descend = new List<string>();
        foreach (var c in children)
        {
            if (!c.IsDir) continue;
            var name = Path.GetFileName(c.Path);
            if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
            if (Directory.Exists(Path.Combine(c.Path, ".git"))) { repos.TryAdd(c.Path, 0); continue; }
            descend.Add(c.Path);
        }
        return descend;
    }
}
