using Nadfny.Features.Worktrees;

namespace Nadfny.Features.GitHealth;

/// <summary>Counts from <c>git count-objects -v</c>. Sizes are KiB, as git prints them.</summary>
public sealed record ObjectCounts(long Count, long SizeKiB, long Packs, long SizePackKiB, long PrunePackable, long Garbage, long SizeGarbageKiB);

public sealed record BranchTrack(string Name, string Track, string? Upstream);

public static class GitHealthParser
{
    public const string Gone = "[gone]";

    /// <summary>Parses <c>git count-objects -v</c>. Unknown or missing keys read as zero.</summary>
    public static ObjectCounts ParseCountObjects(string output)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (long.TryParse(line[(colon + 1)..].Trim(), out var n)) values[line[..colon]] = n;
        }
        long Get(string key) => values.GetValueOrDefault(key);
        return new ObjectCounts(Get("count"), Get("size"), Get("packs"), Get("size-pack"), Get("prune-packable"), Get("garbage"), Get("size-garbage"));
    }

    /// <summary>
    /// Parses <c>git for-each-ref --format=%(refname:lstrip=2)%09%(upstream:track)%09%(upstream:short) refs/heads</c>.
    /// Lines without a track are kept with an empty track.
    /// </summary>
    public static IReadOnlyList<BranchTrack> ParseBranchTracks(string output)
    {
        var result = new List<BranchTrack>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts[0].Length == 0) continue;
            var track = parts.Length > 1 ? parts[1] : "";
            var upstream = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            result.Add(new BranchTrack(parts[0], track, upstream));
        }
        return result;
    }

    /// <summary>Branches whose upstream was deleted on the remote and pruned locally.</summary>
    public static IReadOnlyList<string> GoneBranches(IEnumerable<BranchTrack> branches, IEnumerable<string> excluded)
    {
        var skip = new HashSet<string>(excluded, StringComparer.Ordinal);
        return branches
            .Where(b => b.Track.Trim() == Gone && !skip.Contains(b.Name))
            .Select(b => b.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every branch checked out in any worktree, including the main one. Detached worktrees contribute nothing.</summary>
    public static IReadOnlySet<string> CheckedOutBranches(string worktreePorcelain) =>
        WorktreeParser.ParsePorcelain(worktreePorcelain)
            .Where(e => e.Branch is not null)
            .Select(e => e.Branch!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Parses <c>git stash list --format=%ct</c> (committer time, Unix seconds): the count and the oldest entry.</summary>
    public static (int Count, DateTime? OldestUtc) ParseStashDates(string output)
    {
        var stamps = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => long.TryParse(s, out _))
            .Select(long.Parse)
            .ToList();
        return stamps.Count == 0 ? (0, null) : (stamps.Count, DateTimeOffset.FromUnixTimeSeconds(stamps.Min()).UtcDateTime);
    }
}
