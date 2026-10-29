using Nadfny.Core;

namespace Nadfny.Features.Wsl;

/// <summary>
/// Lists WSL distributions and compacts or shrinks their disks. Actions only touch distributions from the latest scan,
/// looked up by id, so the UI can never name a path or distribution the host did not find itself.
/// </summary>
public sealed class WslService
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SparseTimeout = TimeSpan.FromSeconds(60);
    private readonly Func<IReadOnlyList<LxssEntry>> _readRegistry;
    private readonly Func<CancellationToken, Task<Version?>> _readVersion;
    private volatile Snapshot _last = new(new Dictionary<string, WslDistro>(), null);

    public WslService() : this(WslRegistry.Read, ReadVersionAsync) { }

    public WslService(Func<IReadOnlyList<LxssEntry>> readRegistry, Func<CancellationToken, Task<Version?>> readVersion)
    {
        _readRegistry = readRegistry;
        _readVersion = readVersion;
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, WslDistro> Distros, Version? Version);

    public async Task<WslReport> ScanAsync(CancellationToken ct)
    {
        var distros = WslDistroParser.Parse(_readRegistry());
        var version = await _readVersion(ct);
        _last = new Snapshot(distros.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase), version);
        return new WslReport(distros, version?.ToString(), WslOutput.SupportsSparse(version));
    }

    public async Task<WslCompactResult> CompactAsync(string id, OpReporter op, CancellationToken ct)
    {
        var distro = Lookup(id);
        var path = distro.VhdxPath ?? throw new InvalidOperationException($"{distro.Name} keeps its files in a folder, so there is no disk file to compact.");
        if (!File.Exists(path)) throw new InvalidOperationException($"{distro.Name}’s disk file is gone. Rescan and try again.");
        var script = WslCommands.CompactScript(path);
        var before = DirectorySizer.OnDisk(new FileInfo(path));

        var exitCode = await ElevatedPowerShell.RunAsync(script, op, "Shutting down WSL and compacting its disk", ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"Compaction did not finish (diskpart exit code {exitCode}). Close anything still using WSL and try again.");

        var after = File.Exists(path) ? DirectorySizer.OnDisk(new FileInfo(path)) : 0;
        var freed = Math.Max(0, before - after);
        return new WslCompactResult($"{distro.Name}’s disk compacted. Freed {Format.Bytes(freed)}.", freed);
    }

    public async Task<string> EnableSparseAsync(string id, CancellationToken ct)
    {
        var distro = Lookup(id);
        if (!WslOutput.SupportsSparse(_last.Version)) throw new InvalidOperationException("Automatic shrinking needs WSL 2.0 or newer.");
        var result = await ProcessRunner.RunAsync("wsl.exe", WslCommands.SetSparse(distro), null, SparseTimeout, ct);
        if (!result.Ok) throw new InvalidOperationException(WslOutput.Clean(result.Error));
        return $"Automatic shrinking is on for {distro.Name}.";
    }

    private WslDistro Lookup(string id) =>
        _last.Distros.TryGetValue(id, out var distro)
            ? distro
            : throw new InvalidOperationException("That WSL distribution is not in the latest scan. Rescan and try again.");

    private static async Task<Version?> ReadVersionAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("wsl.exe", ["--version"], null, VersionTimeout, ct);
        return result.Ok ? WslOutput.ParseVersion(result.StdOut) : null;
    }
}
