using System.Text.Json;
using Nadfny.Core;

namespace Nadfny.Features.Apps;

/// <summary>
/// Microsoft Store (and Xbox app) packages the user can see in Start. Read through Windows PowerShell, which ships
/// the Appx module. Their data lives in %LocalAppData%\Packages\&lt;family name&gt;.
/// </summary>
public static class StoreApps
{
    // Start menu names are the friendly names ("Calculator", not Microsoft.WindowsCalculator); a package without a
    // Start entry is a helper or extension, not an app anyone looks for.
    private const string Script = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $names = @{}
        Get-StartApps | Where-Object { $_.AppID -like '*!*' } | ForEach-Object {
          $family = $_.AppID.Split('!')[0]
          if (-not $names.ContainsKey($family)) { $names[$family] = $_.Name }
        }
        @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' -and $names.ContainsKey($_.PackageFamilyName) } | ForEach-Object {
          [pscustomobject]@{ name = $names[$_.PackageFamilyName]; full = $_.PackageFullName; family = $_.PackageFamilyName;
            publisher = $_.Publisher; version = [string]$_.Version; location = $_.InstallLocation }
        }) | ConvertTo-Json -Compress
        """;

    public static async Task<IReadOnlyList<AppCandidate>> ReadAsync(CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", Script], timeout: TimeSpan.FromSeconds(60), ct: ct);
        return res.Ok ? Parse(res.StdOut, PackagesFolder) : [];
    }

    public static string PackagesFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    internal static IReadOnlyList<AppCandidate> Parse(string json, string packagesFolder)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            return items.Select(e => ToCandidate(e, packagesFolder)).OfType<AppCandidate>().ToList();
        }
        catch (JsonException) { return []; }
    }

    private static AppCandidate? ToCandidate(JsonElement e, string packagesFolder)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (S("name") is not { } name || S("full") is not { } full || S("family") is not { } family) return null;
        var location = S("location");
        var launcher = UninstallRegistry.GameLauncher(location, null);
        var data = Path.Combine(packagesFolder, family);
        return new AppCandidate(
            $"store:{family}", name, PublisherName(S("publisher")), S("version"), null, AppSource.Store,
            launcher is not null, launcher ?? "Microsoft Store", location, null,
            new UninstallPlan(UninstallPlan.Appx, full),
            [data],
            // Only folders Windows itself treats as disposable. LocalCache is skipped on purpose: some apps
            // (Python from the Store, for one) keep real data there.
            [Path.Combine(data, "AC", "INetCache"), Path.Combine(data, "AC", "Temp"), Path.Combine(data, "TempState")]);
    }

    /// <summary>"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond..." -> "Microsoft Corporation".</summary>
    internal static string? PublisherName(string? distinguished)
    {
        if (distinguished is null) return null;
        foreach (var part in distinguished.Split(','))
        {
            var kv = part.Trim();
            if (kv.StartsWith("O=", StringComparison.OrdinalIgnoreCase)) return kv[2..].Trim('"');
        }
        return distinguished.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? distinguished[3..].Split(',')[0].Trim('"') : distinguished;
    }

    public static async Task RemoveAsync(string packageFullName, CancellationToken ct)
    {
        EnsurePackageName(packageFullName);
        var res = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"Remove-AppxPackage -Package '{packageFullName}' -ErrorAction Stop"],
            timeout: TimeSpan.FromMinutes(10), ct: ct);
        if (!res.Ok) throw new InvalidOperationException(FirstLine(res.Error) ?? "Windows could not remove the app.");
    }

    public static async Task<bool> IsInstalledAsync(string packageFullName, CancellationToken ct)
    {
        EnsurePackageName(packageFullName);
        var res = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"if (Get-AppxPackage | Where-Object PackageFullName -eq '{packageFullName}') {{ 'yes' }}"],
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        return res.StdOut.Contains("yes", StringComparison.Ordinal);
    }

    // The name came from our own scan; still, refuse anything that is not a plain package name before it reaches a shell.
    internal static void EnsurePackageName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9._\-~]+$"))
            throw new InvalidOperationException("That package name looks wrong, so nothing was removed.");
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
