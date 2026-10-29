using System.Diagnostics;
using Nadfny.Core;

namespace Nadfny.Features.Windows;

public sealed record WindowsCleanupItem(string Id, string Name, string Note, bool Present, long? Bytes, string? Warning);

/// <summary>
/// Windows update leftovers, cleaned only through Windows' own Disk Cleanup (cleanmgr /sagerun) so Windows decides
/// what is safe inside each category. Nadfny never deletes system folders itself. Needs one administrator prompt.
/// </summary>
public sealed class WindowsCleanupService
{
    private const int SageSet = 742; // our private Disk Cleanup profile number
    private const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    private sealed record Definition(string Id, string Name, string Note, string[] Handlers, Func<string?> SizePath, string? Warning);

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string SystemDrive = Path.GetPathRoot(WindowsDir) ?? @"C:\";

    // Fixed allowlist of Disk Cleanup handlers. "Device Driver Packages" is deliberately absent: removing old
    // drivers can block rolling a driver back.
    private static readonly Definition[] Definitions =
    [
        new("previous", "Previous Windows installation", "Windows.old, kept after a Windows upgrade so you can go back.",
            ["Previous Installations"], () => Path.Combine(SystemDrive, "Windows.old"),
            "You can no longer go back to the previous version of Windows."),
        new("updates", "Windows Update cleanup", "Older copies of system files that newer updates replaced.",
            ["Update Cleanup"], () => null,
            "Updates that are already installed can no longer be uninstalled."),
        new("delivery", "Delivery Optimization files", "Update downloads Windows keeps to share with other PCs.",
            ["Delivery Optimization Files"],
            () => Path.Combine(WindowsDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), null),
        new("setup", "Upgrade logs and setup files", "Logs and temporary files left by Windows upgrades.",
            ["Windows Upgrade Log Files", "Temporary Setup Files"], () => null, null),
    ];

    public static IReadOnlyCollection<string> AllowedHandlers { get; } = Definitions.SelectMany(d => d.Handlers).ToHashSet();

    public async Task<IReadOnlyList<WindowsCleanupItem>> ScanAsync(CancellationToken ct)
    {
        // The Delivery Optimization folder is admin-only; Windows reports its size through an official cmdlet.
        var deliveryBytes = await DeliveryCacheBytesAsync(ct);
        return await Task.Run<IReadOnlyList<WindowsCleanupItem>>(() =>
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(VolumeCaches);
            var installed = root?.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            return Definitions
                .Where(d => d.Handlers.Any(installed.Contains))
                .Select(d =>
                {
                    var path = d.SizePath();
                    var readable = path is not null && Directory.Exists(path);
                    var present = d.Id != "previous" || readable;
                    long? bytes = d.Id == "delivery" ? deliveryBytes : readable ? DirectorySizer.Measure(path!, ct).Bytes : null;
                    return new WindowsCleanupItem(d.Id, d.Name, d.Note, present, bytes, d.Warning);
                })
                .Where(i => i.Present)
                .ToList();
        }, ct);
    }

    private static async Task<long?> DeliveryCacheBytesAsync(CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "(Get-DeliveryOptimizationPerfSnap).CacheSizeBytes"], null, TimeSpan.FromSeconds(30), ct);
        return res.Ok && long.TryParse(res.StdOut.Trim(), out var n) ? n : null;
    }

    /// <summary>Builds the elevated script: select only our allowlisted handlers, run Disk Cleanup, then unselect.</summary>
    internal static string BuildScript(IReadOnlyList<string> ids)
    {
        var handlers = Definitions.Where(d => ids.Contains(d.Id)).SelectMany(d => d.Handlers).Distinct().ToList();
        if (handlers.Count == 0) throw new InvalidOperationException("Nothing selected to clean.");
        var quoted = string.Join(",", handlers.Select(h => $"'{h}'")); // handlers come from the fixed list above, never from input
        return $$"""
            $ErrorActionPreference = 'Stop'
            $base = 'HKLM:\{{VolumeCaches}}'
            $flag = 'StateFlags{{SageSet:D4}}'
            Get-ChildItem $base | ForEach-Object { Remove-ItemProperty -Path $_.PSPath -Name $flag -ErrorAction SilentlyContinue }
            foreach ($h in @({{quoted}})) { if (Test-Path "$base\$h") { New-ItemProperty -Path "$base\$h" -Name $flag -Value 2 -PropertyType DWord -Force | Out-Null } }
            $p = Start-Process -FilePath "$env:windir\System32\cleanmgr.exe" -ArgumentList '/sagerun:{{SageSet}}' -Wait -PassThru
            Get-ChildItem $base | ForEach-Object { Remove-ItemProperty -Path $_.PSPath -Name $flag -ErrorAction SilentlyContinue }
            exit $p.ExitCode
            """;
    }

    public async Task<string> CleanAsync(IReadOnlyList<string> ids, OpReporter op, CancellationToken ct)
    {
        var unknown = ids.Where(id => Definitions.All(d => d.Id != id)).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"Unknown Windows cleanup: {string.Join(", ", unknown)}");

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(BuildScript(ids)));
        var freeBefore = new DriveInfo(SystemDrive).AvailableFreeSpace;
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        op.Phase("Waiting for administrator permission");
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Administrator permission was declined. Nothing was cleaned."); }
        if (proc is null) throw new InvalidOperationException("Could not start Windows Disk Cleanup.");
        op.Phase("Windows Disk Cleanup is running");

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"Windows Disk Cleanup did not finish (exit code {proc.ExitCode}).");
        var freed = Math.Max(0, new DriveInfo(SystemDrive).AvailableFreeSpace - freeBefore);
        return $"Windows cleanup finished. {Format.Bytes(freed)} freed on {SystemDrive.TrimEnd('\\')}.";
    }
}
