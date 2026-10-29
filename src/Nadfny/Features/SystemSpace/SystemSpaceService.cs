using System.ComponentModel;
using System.Diagnostics;
using Nadfny.Core;

namespace Nadfny.Features.SystemSpace;

public sealed class SystemSpaceService
{
    private readonly Func<CancellationToken, Task<SystemSpaceSnapshot>> _read;
    private readonly Func<IReadOnlyList<string>, Task<int>> _power;
    private readonly SemaphoreSlim _gate = new(1);
    private SystemSpaceSnapshot? _last;

    public SystemSpaceService() : this(SystemSpaceReader.ReadAsync, RunPowerAsync) { }

    internal SystemSpaceService(Func<CancellationToken, Task<SystemSpaceSnapshot>> read, Func<IReadOnlyList<string>, Task<int>> power)
    {
        _read = read;
        _power = power;
    }

    public async Task<SystemSpaceSnapshot> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _last = await _read(ct); }
        finally { _gate.Release(); }
    }

    internal static string[] PowerArguments(string action) => action switch
    {
        "off" => ["/hibernate", "off"],
        "reduce" => ["/hibernate", "/type", "reduced"],
        _ => throw new ArgumentException("Unknown hibernation action."),
    };

    public async Task<HibernationResult> ChangeHibernationAsync(string action, OpReporter op)
    {
        var args = PowerArguments(action);
        await _gate.WaitAsync();
        try
        {
            var before = _last ?? throw new InvalidOperationException("Scan Windows space first.");
            if (before.Hibernation.Enabled != true || before.Hibernation.Bytes is not > 0)
                throw new InvalidOperationException("Hibernation is off or its file could not be measured. Rescan Windows space.");
            op.Phase("Waiting for administrator permission");
            int exit;
            try { exit = await _power(args); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(true, 0, before); }
            op.Phase("Measuring Windows space");
            _last = await _read(CancellationToken.None);
            if (exit != 0) throw new InvalidOperationException("Windows could not change hibernation. If the file has a custom size, use Windows power settings to reset it first.");
            return new(false, _last.Hibernation.Bytes is { } after ? Math.Max(0, before.Hibernation.Bytes.Value - after) : 0, _last);
        }
        finally { _gate.Release(); }
    }

    private static async Task<int> RunPowerAsync(IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows power settings.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static bool OpenSettings(string kind)
    {
        var file = kind switch
        {
            "pagefile" => "SystemPropertiesPerformance.exe",
            "protection" => "SystemPropertiesProtection.exe",
            _ => throw new ArgumentException("Unknown Windows settings page."),
        };
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file)) { UseShellExecute = true });
        return process is not null;
    }

    internal static long? FileBytes(string drive, string name)
    {
        try
        {
            // Directory enumeration supplies the length without opening locked paging/hibernation files.
            return new DirectoryInfo(drive).EnumerateFiles(name, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
                .FirstOrDefault()?.Length ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}
