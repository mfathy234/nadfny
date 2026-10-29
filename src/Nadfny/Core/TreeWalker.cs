namespace Nadfny.Core;

/// <summary>
/// Parallel directory walk shared by the worktree and build-output scanners. Handles whole drives:
/// skips OS folders, never follows junctions, and reports progress per top-level branch.
/// </summary>
public static class TreeWalker
{
    public readonly record struct Entry(bool IsDir, string Path, long Length, long WriteTicks);

    /// <summary>Given a folder and its children, returns the sub-folders worth walking into.</summary>
    public delegate IEnumerable<string> Visitor(string dir, IReadOnlyList<Entry> children);

    private static readonly HashSet<string> SkipAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "AppData", "$RECYCLE.BIN", "System Volume Information",
    };

    private static readonly HashSet<string> SkipAtDriveRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs",
        "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "MSOCache", "Intel", "AMD", "Drivers", "OneDriveTemp",
    };

    public static async Task RunAsync(IReadOnlyList<string> roots, int maxDepth, Visitor visit, ScanProgress progress, CancellationToken ct)
    {
        // Seed two levels deep so a single huge folder (C:\Users) still splits into many parallel branches.
        var seeds = new List<(string Dir, int Depth)>();
        foreach (var root in EffectiveRoots(roots))
        {
            foreach (var l1 in Step(root, visit))
            {
                ct.ThrowIfCancellationRequested();
                progress.Visit(l1);
                var l2 = Step(l1, visit).ToList();
                seeds.AddRange(l2.Select(d => (d, 2)));
            }
        }

        progress.AddTotal(seeds.Count);
        await Parallel.ForEachAsync(seeds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (seed, token) =>
            {
                Walk(seed.Dir, seed.Depth, maxDepth, visit, progress, token);
                progress.CompleteOne(seed.Dir);
                return ValueTask.CompletedTask;
            });
    }

    /// <summary>Drops roots that sit inside another root so nothing is scanned twice.</summary>
    public static IReadOnlyList<string> EffectiveRoots(IReadOnlyList<string> roots)
    {
        // TrimEndingDirectorySeparator keeps "C:\" intact but turns "X\" into "X", so duplicates collapse before the nesting check.
        var normalized = roots.Where(Directory.Exists)
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Where(r => !normalized.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) && SafeDeleter.IsUnder(r, o))).ToList();
    }

    public static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(path) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);

    private static void Walk(string dir, int depth, int maxDepth, Visitor visit, ScanProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress.Visit(dir);
        if (depth >= maxDepth) { Step(dir, visit).ToList(); return; } // still let the visitor see the children
        foreach (var sub in Step(dir, visit))
            Walk(sub, depth + 1, maxDepth, visit, progress, ct);
    }

    private static IEnumerable<string> Step(string dir, Visitor visit)
    {
        var children = DirectorySizer.Shallow(dir).Select(c => new Entry(c.IsDir, c.Path, c.Length, c.WriteTicks)).ToList();
        var atDriveRoot = IsDriveRoot(dir);
        return visit(dir, children).Where(sub =>
        {
            var name = Path.GetFileName(sub);
            return !SkipAnywhere.Contains(name) && !(atDriveRoot && SkipAtDriveRoot.Contains(name));
        }).ToList();
    }
}
