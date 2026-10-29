using System.Diagnostics;
using System.Text;

namespace Nadfny.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Error => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

/// <summary>Runs CLI tools (git, docker) without a console window and with a hard timeout.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, string? workingDir = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return new ProcessResult(-1, "", $"Could not start {file}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", $"{file} is not installed or not on PATH.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return new ProcessResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-2, "", $"{file} timed out.");
        }
    }
}
