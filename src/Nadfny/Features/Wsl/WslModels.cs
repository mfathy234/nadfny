namespace Nadfny.Features.Wsl;

/// <summary>One registry entry under HKCU\...\Lxss, the record Windows keeps for each installed distribution.</summary>
public sealed record LxssEntry(string Id, string? Name, string? BasePath, int? Version);

/// <summary>
/// A WSL distribution. VhdxPath is null for WSL 1 (plain folders) and when no single disk file can be identified,
/// in which case there is nothing to compact.
/// </summary>
public sealed record WslDistro(string Id, string Name, int Version, string? VhdxPath, long Bytes, bool IsSparse)
{
    public bool Compactable => VhdxPath is not null;
}

/// <summary>Version is the "WSL version" reported by wsl --version, or null when that command failed.</summary>
public sealed record WslReport(IReadOnlyList<WslDistro> Distros, string? Version, bool SparseSupported);

public sealed record WslCompactResult(string Message, long Freed);
