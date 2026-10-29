namespace Nadfny.Features.Worktrees;

public sealed record WorktreeEntry(string Path, string? Head, string? Branch, bool Detached, bool Bare, bool Locked, bool Prunable);

public sealed record GitStatusSummary(int ChangedFiles, int Ahead, int Behind, bool HasUpstream);

public static class WorktreeParser
{
    /// <summary>Parses <c>git worktree list --porcelain</c>. The first entry is always the main worktree.</summary>
    public static IReadOnlyList<WorktreeEntry> ParsePorcelain(string output)
    {
        var result = new List<WorktreeEntry>();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? path = null, head = null, branch = null;
            bool detached = false, bare = false, locked = false, prunable = false;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var (key, value) = SplitOnce(line);
                switch (key)
                {
                    case "worktree": path = NormalizePath(value); break;
                    case "HEAD": head = value; break;
                    case "branch": branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value; break;
                    case "detached": detached = true; break;
                    case "bare": bare = true; break;
                    case "locked": locked = true; break;
                    case "prunable": prunable = true; break;
                }
            }
            if (path is not null) result.Add(new WorktreeEntry(path, head, branch, detached, bare, locked, prunable));
        }
        return result;
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch</c>.</summary>
    public static GitStatusSummary ParseStatusV2(string output)
    {
        int changed = 0, ahead = 0, behind = 0;
        bool upstream = false, abSeen = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) { upstream = true; continue; }
            if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                abSeen = true;
                var parts = line["# branch.ab ".Length..].Split(' ');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0].TrimStart('+'), out ahead);
                    int.TryParse(parts[1].TrimStart('-'), out behind);
                }
                continue;
            }
            if (line[0] == '#') continue;
            changed++;
        }
        // git omits branch.ab when the configured upstream branch was deleted: treat that as "not pushed anywhere".
        return new GitStatusSummary(changed, ahead, behind, upstream && abSeen);
    }

    private static (string Key, string Value) SplitOnce(string line)
    {
        var i = line.IndexOf(' ');
        return i < 0 ? (line, "") : (line[..i], line[(i + 1)..]);
    }

    public static string NormalizePath(string p) => p.Replace('/', '\\').TrimEnd('\\');
}
