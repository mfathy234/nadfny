using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Nadfny.Core;

namespace Nadfny.Features.SystemSpace;

internal static class SystemSpaceReader
{
    // Fixed read-only CIM query. References are resolved by DeviceID, never by localized display text.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $automatic = $null; $storage = @(); $shadowReadable = $false
        $restorePoints = $null; $restoreReadable = $false
        try { $automatic = (Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile } catch {}
        try {
          $volumes = @(Get-CimInstance Win32_Volume)
          function Label($id) {
            $v = $volumes | Where-Object { $_.DeviceID -eq $id } | Select-Object -First 1
            if ($v.DriveLetter) { $v.DriveLetter } else { $id }
          }
          $storage = @(Get-CimInstance Win32_ShadowStorage | ForEach-Object {
            [ordered]@{ drive = (Label $_.Volume.DeviceID); storageDrive = (Label $_.DiffVolume.DeviceID)
              used = [string]$_.UsedSpace; maximum = [string]$_.MaxSpace }
          })
          $shadowReadable = $true
        } catch {}
        try {
          $restorePoints = @(Get-CimInstance -Namespace root\default -ClassName SystemRestore).Count
          $restoreReadable = $true
        } catch {}
        [ordered]@{ automatic = $automatic; storage = @($storage); shadowReadable = $shadowReadable
          restorePoints = $restorePoints; restoreReadable = $restoreReadable } | ConvertTo-Json -Depth 5 -Compress
        """;

    internal static async Task<SystemSpaceSnapshot> ReadAsync(CancellationToken ct)
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var path = Path.Combine(systemDrive, "hiberfil.sys");
        object? enabled = null, type = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            // Many PCs never write HibernateEnabled; Windows then uses HibernateEnabledDefault.
            enabled = key?.GetValue("HibernateEnabled") ?? key?.GetValue("HibernateEnabledDefault");
            type = key?.GetValue("HiberFileType");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        var paging = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new PagingDrive(d.Name, SystemSpaceService.FileBytes(d.Name, "pagefile.sys"), SystemSpaceService.FileBytes(d.Name, "swapfile.sys"))).ToArray();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        var result = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], timeout: TimeSpan.FromSeconds(45), ct: ct);
        CimSpace cim = new(null, [], false, null);
        if (result.Ok)
        {
            try
            {
                using var json = JsonDocument.Parse(result.StdOut.Trim());
                cim = SystemSpaceMapper.Cim(json.RootElement);
            }
            catch (JsonException) { }
        }
        return new(SystemSpaceMapper.Hibernation(path, SystemSpaceService.FileBytes(systemDrive, "hiberfil.sys"), enabled, type),
            paging, cim.AutomaticManaged, cim.Storage, cim.ShadowReadable, cim.RestorePoints,
            SystemSpaceMapper.InstalledMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
    }
}
