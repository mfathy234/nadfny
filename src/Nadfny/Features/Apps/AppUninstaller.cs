using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Nadfny.Core;

namespace Nadfny.Features.Apps;

/// <summary>
/// Runs an app's own uninstaller, the same one Apps &amp; Features would, and waits until the app is really gone
/// (its uninstall key or manifest disappears). Many uninstallers copy themselves to Temp and exit at once, so the
/// process exiting proves nothing; the wait continues for a grace period after it.
/// </summary>
public static partial class AppUninstaller
{
    public static readonly TimeSpan GraceAfterExit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LauncherWait = TimeSpan.FromMinutes(10);

    /// <summary>True when the app is gone; false when the uninstaller finished (or was closed) and it is still there.</summary>
    public static async Task<bool> RunAsync(UninstallPlan plan, OpReporter op, CancellationToken ct)
    {
        switch (plan.Kind)
        {
            case UninstallPlan.Folder:
                op.Phase("Moving to the Recycle Bin");
                await Task.Run(() => SafeDeleter.MoveToRecycleBin(plan.Target), ct);
                return true;

            case UninstallPlan.Appx:
                op.Phase("Windows is removing the app");
                await StoreApps.RemoveAsync(plan.Target, ct);
                return !await StoreApps.IsInstalledAsync(plan.Target, ct);

            case UninstallPlan.Url:
                if (!plan.Target.StartsWith("steam://uninstall/", StringComparison.OrdinalIgnoreCase)
                    && !plan.Target.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unknown launcher link, so nothing was opened.");
                Start(new ProcessStartInfo(plan.Target) { UseShellExecute = true });
                if (plan.Probe is null) return false; // nothing to watch; the user finishes in the launcher
                op.Phase("Waiting for the launcher to remove it");
                return await WaitUntilGoneAsync(() => File.Exists(plan.Probe), null, LauncherWait, ct);

            case UninstallPlan.Msi:
                if (!ProductCode().IsMatch(plan.Target)) throw new InvalidOperationException("That product code looks wrong, so nothing was run.");
                return await RunAndWaitAsync(new ProcessStartInfo("msiexec.exe", $"/x {plan.Target}") { UseShellExecute = true }, plan.Probe, op, ct);

            case UninstallPlan.Command:
                if (!File.Exists(plan.Target)) throw new InvalidOperationException($"The app's uninstaller is missing ({plan.Target}). It may already be partly removed.");
                // ShellExecute, so an uninstaller that needs administrator rights gets the normal UAC prompt.
                return await RunAndWaitAsync(new ProcessStartInfo(plan.Target, plan.Arguments ?? "")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(plan.Target) ?? "",
                }, plan.Probe, op, ct);

            default:
                throw new InvalidOperationException("This app cannot be uninstalled from here.");
        }
    }

    private static async Task<bool> RunAndWaitAsync(ProcessStartInfo psi, string? registryKey, OpReporter op, CancellationToken ct)
    {
        using var proc = Start(psi);
        op.Phase("Waiting for the uninstaller to finish");
        if (registryKey is null)
        {
            if (proc is not null) await proc.WaitForExitAsync(ct);
            return true;
        }
        return await WaitUntilGoneAsync(() => UninstallRegistry.Exists(registryKey), proc, GraceAfterExit, ct);
    }

    internal static async Task<bool> WaitUntilGoneAsync(Func<bool> stillThere, Process? proc, TimeSpan grace, CancellationToken ct, TimeSpan? poll = null)
    {
        DateTime? deadline = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!stillThere()) return true;
            if (proc is null || proc.HasExited)
            {
                deadline ??= DateTime.UtcNow + grace;
                if (DateTime.UtcNow > deadline) return false;
            }
            await Task.Delay(poll ?? TimeSpan.FromSeconds(1), ct);
        }
    }

    private static Process? Start(ProcessStartInfo psi)
    {
        try { return Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("You declined the administrator prompt, so nothing was removed.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"The uninstaller could not be started: {ex.Message}");
        }
    }

    /// <summary>True when a running program's file lives inside the app's folder.</summary>
    public static bool IsRunning(string? installLocation)
    {
        if (string.IsNullOrEmpty(installLocation)) return false;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is { } file && SafeDeleter.IsUnder(file, installLocation)) return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return false;
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCode();
}
