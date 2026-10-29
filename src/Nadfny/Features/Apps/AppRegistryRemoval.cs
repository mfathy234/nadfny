using System.Diagnostics;
using Microsoft.Win32;
using Nadfny.Core;

namespace Nadfny.Features.Apps;

/// <summary>Backs up a scanned uninstall entry before removing its Windows listing.</summary>
public static class AppRegistryRemoval
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private static readonly string RegExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

    internal static IReadOnlyList<string> Arguments(string key, IReadOnlyCollection<string> scanned, string action, string? backup = null)
    {
        var (hive, view, sub) = Validate(key, scanned);
        var physical = view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine
            ? @"SOFTWARE\WOW6432Node\" + sub["SOFTWARE\\".Length..] : sub;
        var target = (hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + physical;
        return action switch
        {
            "export" when backup is not null && Path.IsPathFullyQualified(backup) && backup.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)
                => ["export", target, backup],
            "delete" => ["delete", target, "/f"],
            _ => throw new InvalidOperationException("Invalid registry operation."),
        };
    }

    private static (RegistryHive Hive, RegistryView View, string Sub) Validate(string key, IReadOnlyCollection<string> scanned)
    {
        var parts = key.Split('|');
        if (!scanned.Contains(key, StringComparer.Ordinal) || parts.Length != 3
            || !Enum.TryParse<RegistryHive>(parts[0], out var hive) || hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine)
            || !Enum.TryParse<RegistryView>(parts[1], out var view)
            || !(hive == RegistryHive.CurrentUser && view == RegistryView.Default
                || hive == RegistryHive.LocalMachine && view is RegistryView.Registry32 or RegistryView.Registry64)
            || !parts[2].StartsWith(UninstallPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a scanned uninstall entry can be removed.");
        var name = parts[2][UninstallPath.Length..];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['\\', '/', '\0', '\r', '\n', '"']) >= 0)
            throw new InvalidOperationException("Only a direct uninstall entry can be removed.");
        return (hive, view, parts[2]);
    }

    public static string BackupPath(string name)
    {
        var safe = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(80).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "App";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nadfny", "backups",
            $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.reg");
    }

    public static async Task RemoveAsync(string key, string backup, IReadOnlyCollection<string> scanned, CancellationToken ct)
    {
        var export = Arguments(key, scanned, "export", backup);
        var delete = Arguments(key, scanned, "delete");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var result = await ProcessRunner.RunAsync(RegExe, export, ct: ct);
        if (!result.Ok || !File.Exists(backup) || new FileInfo(backup).Length == 0)
            throw new InvalidOperationException("The registry backup could not be saved. The entry was kept.");
        var (hive, view, sub) = Validate(key, scanned);
        if (hive == RegistryHive.CurrentUser)
        {
            ct.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(hive, view);
            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
        else
        {
            var psi = new ProcessStartInfo(RegExe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in delete) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows could not remove the entry.");
            // Once elevated, wait for completion so another action cannot race the registry change.
            await process.WaitForExitAsync(CancellationToken.None);
            if (process.ExitCode != 0) throw new InvalidOperationException($"The entry could not be removed. Its backup is at {backup}.");
        }
    }
}
