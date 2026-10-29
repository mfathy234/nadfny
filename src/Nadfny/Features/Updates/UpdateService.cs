using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Nadfny.Core;

namespace Nadfny.Features.Updates;

public sealed record UpdateInfo(
    string CurrentVersion,
    string Mode, // "installed" (MSI, can self-update) or "portable" (notify and link only)
    bool Available,
    string? LatestVersion,
    string? Notes,
    string? ReleaseUrl,
    long? DownloadBytes);

/// <summary>
/// Checks GitHub Releases for a newer Nadfny. Installed copies download the new MSI, verify it against the
/// release's SHA256SUMS.txt, and upgrade in place (per-user, no admin). Portable copies only get a link.
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "mfathy234/nadfny";

    // Declared before Http: static fields initialize in order, and the HTTP client puts the version in its User-Agent.
    public static string CurrentVersion { get; } = NormalizeVersion(
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private static readonly string DefaultFeed = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    private Release? _latest;
    private string? _downloaded;

    /// <summary>Installed by the MSI = lives in %LocalAppData%\Programs\Nadfny.</summary>
    public static bool IsInstalled { get; } = SafeDeleter.IsUnder(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nadfny"));

    private static string Feed => Environment.GetEnvironmentVariable("NADFNY_UPDATE_FEED") is { Length: > 0 } f ? f : DefaultFeed;

    public async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        using var res = await Http.GetAsync(Feed, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Could not reach GitHub ({(int)res.StatusCode}).");
        var release = ParseRelease(await res.Content.ReadAsStringAsync(ct));
        _latest = release;
        var available = release.Version is not null && IsNewer(release.Version, CurrentVersion);
        return new UpdateInfo(CurrentVersion, IsInstalled ? "installed" : "portable", available,
            release.Version, release.Notes, release.HtmlUrl, release.Msi?.Size);
    }

    /// <summary>Downloads the new installer with progress and verifies its SHA-256 before it can be run.</summary>
    public async Task<string> DownloadAsync(OpReporter op, CancellationToken ct)
    {
        if (!IsInstalled) throw new InvalidOperationException("This is the portable version. Download the new zip from the release page.");
        var release = _latest ?? throw new InvalidOperationException("Check for updates first.");
        var msi = release.Msi ?? throw new InvalidOperationException("This release has no installer.");
        var dir = Path.Combine(Path.GetTempPath(), "Nadfny-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(msi.Name));

        op.Phase($"Downloading Nadfny {release.Version}", msi.Size);
        using (var res = await Http.GetAsync(msi.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                op.Report(done);
            }
        }

        op.Phase("Verifying the download");
        if (release.Sums is { } sums)
        {
            var expected = await ExpectedHashAsync(sums.Url, msi.Name, ct)
                ?? throw new InvalidOperationException("The release checksums do not list the installer, so it was not installed.");
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, ct)));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidOperationException("The download did not match the release checksum, so it was deleted.");
            }
        }
        op.Flush();
        _downloaded = target;
        return release.Version!;
    }

    /// <summary>
    /// Runs the MSI upgrade after Nadfny exits, then starts the new version. The MSI's MajorUpgrade replaces the
    /// old one in place. Returns once the helper is launched; the caller then closes the app.
    /// </summary>
    public void InstallAndRestart()
    {
        var msi = _downloaded ?? throw new InvalidOperationException("Download the update first.");
        var exe = Path.Combine(AppContext.BaseDirectory, "Nadfny.exe");
        var pid = Environment.ProcessId;
        // Wait for this process to exit, upgrade with a small progress window, then relaunch.
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{pid}} -Timeout 30
            $p = Start-Process msiexec.exe -ArgumentList '/i', '"{{msi}}"', '/passive', '/norestart' -Wait -PassThru
            Start-Process '{{exe}}'
            if ($p.ExitCode -eq 0) { Remove-Item '{{msi}}' }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Opens the release page; only GitHub release URLs of this repo are allowed.</summary>
    public static void OpenReleasePage(string url)
    {
        if (!url.StartsWith($"https://github.com/{Repo}/releases", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Nadfny release pages can be opened.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    internal sealed record Asset(string Name, string Url, long Size);
    internal sealed record Release(string? Version, string? Notes, string? HtmlUrl, Asset? Msi, Asset? Sums);

    internal static Release ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => new Asset(Str(x, "name") ?? "", Str(x, "browser_download_url") ?? "",
                x.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0)).ToList()
            : [];
        var tag = Str(r, "tag_name");
        return new Release(
            tag is null ? null : NormalizeVersion(tag),
            Str(r, "body"),
            Str(r, "html_url"),
            assets.FirstOrDefault(x => x.Name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(x => x.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"v0.1" -> "0.1.0", "1.2.3-beta" -> "1.2.3".</summary>
    internal static string NormalizeVersion(string v)
    {
        var core = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat([0, 0, 0]).Take(3);
        return string.Join('.', parts);
    }

    internal static bool IsNewer(string candidate, string current) =>
        Version.TryParse(NormalizeVersion(candidate), out var a) && Version.TryParse(NormalizeVersion(current), out var b) && a > b;

    internal static string? ParseSums(string text, string fileName) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].TrimStart('*').Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[0])
            .FirstOrDefault();

    private static async Task<string?> ExpectedHashAsync(string sumsUrl, string fileName, CancellationToken ct) =>
        ParseSums(await Http.GetStringAsync(sumsUrl, ct), fileName);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nadfny", CurrentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
