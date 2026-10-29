using System.Text.RegularExpressions;

namespace Nadfny.Features.Wsl;

/// <summary>
/// The only wsl.exe and diskpart input this feature may send. Callers pass distributions and paths from the last scan;
/// these builders check the values again so nothing else can reach a process.
/// </summary>
public static class WslCommands
{
    private static readonly Regex DistroName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static string[] Shutdown { get; } = ["--shutdown"];

    public static string[] SetSparse(WslDistro distro)
    {
        if (distro.Version != 2) throw new InvalidOperationException($"{distro.Name} is not a WSL 2 distribution.");
        return ["--manage", Name(distro.Name), "--set-sparse", "true"];
    }

    public static string Name(string name) =>
        DistroName.IsMatch(name) ? name : throw new InvalidOperationException("That distribution name cannot be used with WSL.");

    public static string VhdxPath(string path)
    {
        var unsafeChar = path.Any(c => char.IsControl(c) || c is '"' or '\'' or '`');
        if (unsafeChar || !Path.IsPathFullyQualified(path) || !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException("Unexpected disk path for a WSL distribution.");
        return path;
    }

    /// <summary>Stops Docker Desktop, shuts WSL down, then compacts the one disk. Runs elevated.</summary>
    public static string CompactScript(string vhdxPath)
    {
        var disk = VhdxPath(vhdxPath);
        return $$"""
            $ErrorActionPreference = 'Stop'
            Get-Process 'Docker Desktop','com.docker.backend','com.docker.build' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Seconds 3
            wsl.exe --shutdown
            Start-Sleep -Seconds 3
            $f = Join-Path $env:windir 'Temp\nadfny-wsl-diskpart.txt'
            Set-Content -Path $f -Encoding ascii -Value @('select vdisk file="{{disk}}"', 'attach vdisk readonly', 'compact vdisk', 'detach vdisk', 'exit')
            diskpart.exe /s $f | Out-Null
            $code = $LASTEXITCODE
            Remove-Item $f -ErrorAction SilentlyContinue
            exit $code
            """;
    }
}
