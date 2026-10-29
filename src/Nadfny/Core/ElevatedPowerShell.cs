using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Nadfny.Core;

/// <summary>
/// Runs a PowerShell script elevated (UAC). The script goes in -EncodedCommand, never through a file in a user-writable
/// folder, so nothing can swap it between approval and execution.
/// </summary>
public static class ElevatedPowerShell
{
    public static async Task<int> RunAsync(string script, OpReporter op, string runningPhase, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        op.Phase("Waiting for administrator permission");
        Process? started;
        try { started = Process.Start(psi); }
        catch (Win32Exception) { throw new InvalidOperationException("Administrator permission was declined."); }
        using var proc = started ?? throw new InvalidOperationException("Could not start the elevated script.");
        op.Phase(runningPhase);
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
