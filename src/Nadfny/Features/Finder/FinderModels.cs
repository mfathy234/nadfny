namespace Nadfny.Features.Finder;

public sealed record FinderFile(string Path, string Name, string Folder, long Bytes, long Length,
    DateTime LastWriteUtc, string Type, bool IsDirectory,
    [property: System.Text.Json.Serialization.JsonIgnore] string? Fingerprint = null);
public sealed record DuplicateGroup(string Id, IReadOnlyList<FinderFile> Files, long WastedBytes);
public sealed record FinderReport(IReadOnlyList<FinderFile> Files, IReadOnlyList<DuplicateGroup> Groups);
public sealed record FinderItem(string Mode, FinderFile? File, DuplicateGroup? Group);
public sealed record FinderRemoval(IReadOnlyList<string> Removed, IReadOnlyList<string> Failed, long Bytes);
