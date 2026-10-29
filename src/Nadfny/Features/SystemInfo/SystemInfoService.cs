using System.Text.Json;
using Nadfny.Core;

namespace Nadfny.Features.SystemInfo;

/// <summary>
/// Reads this PC's hardware and OS details with one read-only PowerShell/CIM query. The script is a constant
/// (no user input), so nothing can be injected into it. Results are cached until the user refreshes.
/// </summary>
public sealed class SystemInfoService
{
    private JsonElement? _cached;

    // Win32_VideoController.AdapterRAM is a 32-bit value (caps at 4 GB), so real VRAM is read from the
    // display adapter's registry key. nvidia-smi, when present, adds clocks and live temperature.
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        function Iso($d) { if ($d) { ([datetime]$d).ToUniversalTime().ToString('o') } }
        $os   = Get-CimInstance Win32_OperatingSystem
        $nt   = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $cs   = Get-CimInstance Win32_ComputerSystem
        $prod = Get-CimInstance Win32_ComputerSystemProduct
        $bb   = Get-CimInstance Win32_BaseBoard
        $bios = Get-CimInstance Win32_BIOS

        $vram = @{}
        Get-ItemProperty 'HKLM:\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*' |
          ForEach-Object { $q = $_.'HardwareInformation.qwMemorySize'; if ($q) { $vram[$_.DriverDesc] = [int64]$q } }
        $smi = @{}
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
          nvidia-smi --query-gpu=name,memory.total,clocks.max.graphics,clocks.max.memory,temperature.gpu,power.limit,pcie.link.gen.max,pcie.link.width.max --format=csv,noheader,nounits |
            ForEach-Object { $p = $_.Split(',') | ForEach-Object { $_.Trim() }
              $smi[$p[0]] = [ordered]@{ memoryMiB = $p[1]; coreClockMHz = $p[2]; memoryClockMHz = $p[3]; temperatureC = $p[4]; powerLimitW = $p[5]; pcieGen = $p[6]; pcieWidth = $p[7] } }
        }

        $letters = @{}
        Get-Partition | Where-Object DriveLetter | ForEach-Object { $letters[[string]$_.DiskNumber] += @("$($_.DriveLetter):") }
        $volumes = @{}
        Get-Volume | Where-Object DriveLetter | ForEach-Object { $volumes["$($_.DriveLetter):"] = [ordered]@{ label = $_.FileSystemLabel; fileSystem = $_.FileSystem; size = $_.Size; free = $_.SizeRemaining } }

        $monitors = @(Get-CimInstance -Namespace root\wmi WmiMonitorID | ForEach-Object {
          $dec = { param($a) (($a | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join '') }
          [ordered]@{ name = (& $dec $_.UserFriendlyName); maker = (& $dec $_.ManufacturerName); year = $_.YearOfManufacture } })
        $basic = @(Get-CimInstance -Namespace root\wmi WmiMonitorBasicDisplayParams | ForEach-Object {
          [ordered]@{ widthCm = $_.MaxHorizontalImageSize; heightCm = $_.MaxVerticalImageSize } })
        for ($i = 0; $i -lt $monitors.Count; $i++) { if ($basic[$i]) { $monitors[$i].widthCm = $basic[$i].widthCm; $monitors[$i].heightCm = $basic[$i].heightCm } }

        [ordered]@{
          os = [ordered]@{
            name = $os.Caption; displayVersion = $nt.DisplayVersion; build = "$($nt.CurrentBuild).$($nt.UBR)"
            architecture = $os.OSArchitecture; installed = Iso $os.InstallDate; lastBoot = Iso $os.LastBootUpTime
            computerName = $env:COMPUTERNAME; user = $env:USERNAME }
          system = [ordered]@{
            manufacturer = $cs.Manufacturer; model = $cs.Model; family = $cs.SystemFamily; sku = $prod.Version
            totalMemory = [int64]$cs.TotalPhysicalMemory; hypervisorPresent = [bool]$cs.HypervisorPresent }
          board = [ordered]@{
            manufacturer = $bb.Manufacturer; product = $bb.Product; version = $bb.Version
            biosVendor = $bios.Manufacturer; biosVersion = $bios.SMBIOSBIOSVersion; biosDate = Iso $bios.ReleaseDate }
          cpus = @(Get-CimInstance Win32_Processor | ForEach-Object { [ordered]@{
            name = $_.Name.Trim(); manufacturer = $_.Manufacturer; cores = $_.NumberOfCores; threads = $_.NumberOfLogicalProcessors
            maxClockMHz = $_.MaxClockSpeed; l2KB = $_.L2CacheSize; l3KB = $_.L3CacheSize; socket = $_.SocketDesignation
            virtualization = $_.VirtualizationFirmwareEnabled } })
          gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{
            name = $_.Name; vendor = $_.AdapterCompatibility; vramBytes = $(if ($vram[$_.Name]) { $vram[$_.Name] } else { [int64]$_.AdapterRAM })
            driverVersion = $_.DriverVersion; driverDate = Iso $_.DriverDate
            resolution = $(if ($_.CurrentHorizontalResolution) { "$($_.CurrentHorizontalResolution) x $($_.CurrentVerticalResolution)" })
            refreshHz = $_.CurrentRefreshRate; nvidia = $smi[$_.Name] } })
          memory = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [ordered]@{
            slot = $_.DeviceLocator; bank = $_.BankLabel; manufacturer = $_.Manufacturer; partNumber = "$($_.PartNumber)".Trim()
            capacity = [int64]$_.Capacity; speedMTs = $_.Speed; configuredMTs = $_.ConfiguredClockSpeed
            type = $_.SMBIOSMemoryType; formFactor = $_.FormFactor; voltageMv = $_.ConfiguredVoltage } })
          disks = @(Get-PhysicalDisk | Sort-Object DeviceId | ForEach-Object { [ordered]@{
            number = $_.DeviceId; name = $_.FriendlyName; mediaType = "$($_.MediaType)"; busType = "$($_.BusType)"
            size = [int64]$_.Size; health = "$($_.HealthStatus)"; firmware = $_.FirmwareVersion; serial = "$($_.SerialNumber)".Trim()
            spindleRpm = $_.SpindleSpeed; letters = @($letters[[string]$_.DeviceId]) } })
          volumes = $volumes
          displays = $monitors
        } | ConvertTo-Json -Depth 6 -Compress
        """;

    public async Task<JsonElement> GetAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _cached is { } hit) return hit;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script));
        var res = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], null, TimeSpan.FromSeconds(60), ct);
        var json = res.StdOut.Trim();
        if (!json.StartsWith('{')) throw new InvalidOperationException($"Could not read this PC's details. {res.Error}".Trim());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        _cached = root;
        return root;
    }
}
