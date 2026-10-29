using System.Diagnostics;
using Nadfny.Core;

namespace Nadfny.Features.Docker;

public sealed record DockerDisk(string Path, long Bytes);

public sealed record DockerReport(
    bool Installed,
    bool Running,
    string? Message,
    long ImagesBytes,
    long ImagesReclaimable,
    long ContainersBytes,
    long ContainersReclaimable,
    long VolumesBytes,
    long VolumesReclaimable,
    long BuildCacheBytes,
    long BuildCacheReclaimable,
    IReadOnlyList<DockerImage> Images,
    IReadOnlyList<DockerContainer> Containers,
    IReadOnlyList<DockerVolume> Volumes,
    DockerDisk? Disk);

public sealed class DockerService
{
    private static readonly TimeSpan DfTimeout = TimeSpan.FromSeconds(90);

    public static readonly IReadOnlyDictionary<string, string[]> PruneCommands = new Dictionary<string, string[]>
    {
        ["buildCache"] = ["builder", "prune", "--all", "--force"],
        ["danglingImages"] = ["image", "prune", "--force"],
        ["unusedImages"] = ["image", "prune", "--all", "--force"],
        ["stoppedContainers"] = ["container", "prune", "--force"],
        ["unusedVolumes"] = ["volume", "prune", "--all", "--force"],
    };

    public async Task<DockerReport> ScanAsync(CancellationToken ct)
    {
        var disk = FindDisk();
        var version = await ProcessRunner.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], null, TimeSpan.FromSeconds(15), ct);
        if (version.ExitCode == -1)
            return Empty(false, false, "Docker is not installed.", disk);
        if (!version.Ok)
            return Empty(true, false, "Docker Desktop is not running. Start it to see images, containers and build cache.", disk);

        var summaryTask = ProcessRunner.RunAsync("docker", ["system", "df", "--format", "{{json .}}"], null, DfTimeout, ct);
        var verboseTask = ProcessRunner.RunAsync("docker", ["system", "df", "-v", "--format", "{{json .}}"], null, DfTimeout, ct);
        await Task.WhenAll(summaryTask, verboseTask);

        if (!summaryTask.Result.Ok || !verboseTask.Result.Ok)
            return Empty(true, true, $"Docker did not answer: {(summaryTask.Result.Ok ? verboseTask.Result : summaryTask.Result).Error}", disk);

        var rows = DockerParser.ParseSummary(summaryTask.Result.StdOut).ToDictionary(r => r.Type, StringComparer.OrdinalIgnoreCase);
        var usage = DockerParser.ParseVerboseDf(verboseTask.Result.StdOut);
        DockerParser.SummaryRow Row(string type) => rows.TryGetValue(type, out var r) ? r : new(type, 0, 0, 0, 0);

        var images = Row("Images");
        var containers = Row("Containers");
        var volumes = Row("Local Volumes");
        var cache = Row("Build Cache");

        return new DockerReport(true, true, null,
            images.Bytes, images.ReclaimableBytes,
            containers.Bytes, containers.ReclaimableBytes,
            volumes.Bytes, volumes.ReclaimableBytes,
            cache.Bytes, Math.Max(cache.ReclaimableBytes, usage.BuildCache.ReclaimableBytes),
            usage.Images.OrderByDescending(i => i.UniqueBytes).ToList(),
            usage.Containers,
            usage.Volumes.OrderByDescending(v => v.Bytes).ToList(),
            disk);
    }

    public async Task<string> PruneAsync(string kind, OpReporter op, CancellationToken ct)
    {
        if (!PruneCommands.TryGetValue(kind, out var args))
            throw new InvalidOperationException($"Unknown Docker cleanup: {kind}");
        op.Phase("Docker is cleaning up");
        var res = await ProcessRunner.RunAsync("docker", args, null, TimeSpan.FromMinutes(15), ct);
        if (!res.Ok) throw new InvalidOperationException(res.Error);
        var reclaimed = res.StdOut.Split('\n').LastOrDefault(l => l.Contains("reclaimed", StringComparison.OrdinalIgnoreCase))?.Trim();
        return reclaimed ?? "Done.";
    }

    public async Task<string> RemoveAsync(string kind, string id, OpReporter op, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith('-'))
            throw new InvalidOperationException("Invalid Docker object id.");
        string[] args = kind switch
        {
            "image" => ["image", "rm", id],
            "container" => ["container", "rm", "--volumes", id],
            "volume" => ["volume", "rm", id],
            _ => throw new InvalidOperationException($"Unknown Docker object: {kind}"),
        };
        op.Phase($"Removing {kind}");
        var res = await ProcessRunner.RunAsync("docker", args, null, TimeSpan.FromMinutes(5), ct);
        if (!res.Ok) throw new InvalidOperationException(res.Error);
        return "Removed.";
    }

    /// <summary>
    /// Docker's WSL disk (docker_data.vhdx) never shrinks on its own after a prune.
    /// Compaction needs admin rights and Docker/WSL stopped, so it runs as an elevated PowerShell script.
    /// </summary>
    public async Task<string> CompactDiskAsync(OpReporter op, CancellationToken ct)
    {
        var disk = FindDisk() ?? throw new InvalidOperationException("Docker's WSL disk was not found.");
        var before = disk.Bytes;
        if (disk.Path.Contains('"') || disk.Path.Contains('\''))
            throw new InvalidOperationException("Unexpected characters in the Docker disk path.");

        // Passed inline (-EncodedCommand), never via a file in the user-writable temp folder, so nothing can swap
        // the script between UAC approval and execution. The diskpart script lives in the admin-only Windows\Temp.
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            Get-Process 'Docker Desktop','com.docker.backend','com.docker.build' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Seconds 3
            wsl.exe --shutdown
            Start-Sleep -Seconds 3
            $f = Join-Path $env:windir 'Temp\nadfny-diskpart.txt'
            Set-Content -Path $f -Encoding ascii -Value @('select vdisk file="{{disk.Path}}"', 'attach vdisk readonly', 'compact vdisk', 'detach vdisk', 'exit')
            diskpart.exe /s $f | Out-Null
            $code = $LASTEXITCODE
            Remove-Item $f -ErrorAction SilentlyContinue
            exit $code
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));

        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        op.Phase("Waiting for administrator permission");
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Administrator permission was declined."); }
        if (proc is null) throw new InvalidOperationException("Could not start the compaction.");
        op.Phase("Stopping Docker and compacting its disk");

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Compaction did not finish (diskpart exit code {proc.ExitCode}). Docker Desktop may still be running.");
        var after = new FileInfo(disk.Path).Length;
        var saved = Math.Max(0, before - after);
        return $"Docker disk compacted. Saved {Format.Bytes(saved)}. Start Docker Desktop again when you need it.";
    }

    public static DockerDisk? FindDisk()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Docker", "wsl", "disk", "docker_data.vhdx"),
            Path.Combine(local, "Docker", "wsl", "data", "ext4.vhdx"),
            Path.Combine(local, "Docker", "wsl", "main", "ext4.vhdx"),
        ];
        var found = candidates.Where(File.Exists).Select(p => new FileInfo(p)).OrderByDescending(f => f.Length).FirstOrDefault();
        return found is null ? null : new DockerDisk(found.FullName, found.Length);
    }

    private static DockerReport Empty(bool installed, bool running, string message, DockerDisk? disk) =>
        new(installed, running, message, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], disk);
}
