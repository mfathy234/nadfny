namespace Nadfny.Features.GitHealth;

/// <summary>Loose objects git gc can pack, and garbage files it can remove. Byte sizes are on disk.</summary>
public sealed record GcFinding(long LooseCount, long LooseBytes, long GarbageCount, long GarbageBytes, long Packs);

/// <summary>Downloaded Git LFS objects under .git\lfs. ToolInstalled is false when `git lfs version` does not work.</summary>
public sealed record LfsFinding(long Bytes, bool ToolInstalled);

/// <summary>Stashes are shown as information only.</summary>
public sealed record StashFinding(int Count, DateTime? OldestUtc);

public sealed record RepoHealth(
    string Path,
    string Name,
    long GitBytes,
    GcFinding? Gc,
    LfsFinding? Lfs,
    IReadOnlyList<string> GoneBranches,
    StashFinding? Stashes);

/// <summary>Result of an action on one repository: the size before the action and the health read again afterwards.</summary>
public sealed record CleanupResult(long BeforeBytes, RepoHealth Health);

public sealed record KeptBranch(string Name, string Reason);

public sealed record BranchCleanupResult(IReadOnlyList<string> Deleted, IReadOnlyList<KeptBranch> Kept, RepoHealth Health);
