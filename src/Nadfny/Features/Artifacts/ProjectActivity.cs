using Nadfny.Core;

namespace Nadfny.Features.Artifacts;

internal static class ProjectActivity
{
    private const int MaxDepth = 3;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj",
    };

    public static DateTime? LastUsedUtc(string project, CancellationToken ct)
    {
        long newest = 0;
        Visit(project, 0);
        return newest == 0 ? null : new DateTime(newest, DateTimeKind.Utc);

        void Visit(string folder, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var entries = DirectorySizer.Shallow(folder).ToList();
            var names = new HashSet<string>(entries.Select(e => Path.GetFileName(e.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!entry.IsDir) newest = Math.Max(newest, entry.WriteTicks);
                else if (depth < MaxDepth && !Excluded.Contains(Path.GetFileName(entry.Path))
                    && ArtifactRules.MatchFolder(entry.Path, names) is null)
                    Visit(entry.Path, depth + 1);
            }
        }
    }
}
