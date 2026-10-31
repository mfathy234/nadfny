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
/// Checks GitHub Releases for a newer Nadfny. Installed copies download the new setup.exe (or the MSI for older releases), verify it against the
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
            release.Version, release.Notes, release.HtmlUrl, release.Installer?.Size);
    }

    /// <summary>
    /// Downloads the new installer with progress, resuming a partial download from an earlier try, and verifies its
    /// SHA-256 before it can be run. An installer already downloaded and verified is reused.
    /// </summary>
    public async Task<string> DownloadAsync(OpReporter op, CancellationToken ct)
    {
        if (!IsInstalled) throw new InvalidOperationException("This is the portable version. Download the new zip from the release page.");
        var release = _latest ?? throw new InvalidOperationException("Check for updates first.");
        var installer = release.Installer ?? throw new InvalidOperationException("This release has no installer.");
        var dir = Path.Combine(Path.GetTempPath(), "Nadfny-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(installer.Name));
        RemoveOtherDownloads(dir, target);

        var expected = release.Sums is { } sums
            ? await ExpectedHashAsync(sums.Url, installer.Name, ct)
                ?? throw new InvalidOperationException("The release checksums do not list the installer, so it was not installed.")
            : null;

        if (!File.Exists(target) || !await MatchesAsync(target, expected, ct))
        {
            op.Phase($"Downloading Nadfny {release.Version}", installer.Size);
            var part = await ResumableDownload.RunAsync(Downloads, installer.Url, target, installer.Size, op, ct);
            op.Phase("Verifying the download");
            if (!await MatchesAsync(part, expected, ct))
            {
                File.Delete(part);
                throw new InvalidOperationException("The download did not match the release checksum, so it was deleted. Try again.");
            }
            File.Move(part, target, overwrite: true);
        }
        op.Flush();
        _downloaded = target;
        return release.Version!;
    }

    private static async Task<bool> MatchesAsync(string path, string? expectedSha256, CancellationToken ct)
    {
        if (expectedSha256 is null) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installers and partial downloads of other versions are never used again.</summary>
    internal static void RemoveOtherDownloads(string dir, string target)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (file.Equals(target, StringComparison.OrdinalIgnoreCase) || file.Equals(ResumableDownload.PartPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs the installer after Nadfny exits, then starts the new version. The MSI's MajorUpgrade replaces the
    /// old one in place, whether it is run directly or through the setup.exe bundle. Returns once the helper is launched; the caller then closes the app.
    /// </summary>
    public void InstallAndRestart()
    {
        var installer = _downloaded ?? throw new InvalidOperationException("Download the update first.");
        if (!File.Exists(installer))
        {
            _downloaded = null;
            throw new UpdateMissingException("The downloaded update was removed. Nadfny will download it again.");
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "Nadfny.exe");
        var pid = Environment.ProcessId;
        // Wait for this process to exit, upgrade with a small progress window, then relaunch.
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{pid}} -Timeout 30
            $p = {{StartInstaller(installer)}}
            Start-Process '{{exe}}'
            if ($p.ExitCode -eq 0) { Remove-Item '{{installer}}' }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    internal static string StartInstaller(string path) => path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
        ? $"Start-Process msiexec.exe -ArgumentList '/i', '\"{path}\"', '/passive', '/norestart' -Wait -PassThru"
        : $"Start-Process '{path}' -ArgumentList '/passive', '/norestart' -Wait -PassThru";

    /// <summary>Opens the release page; only GitHub release URLs of this repo are allowed.</summary>
    public static void OpenReleasePage(string url)
    {
        if (!url.StartsWith($"https://github.com/{Repo}/releases", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Nadfny release pages can be opened.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    internal sealed record Asset(string Name, string Url, long Size);
    internal sealed record Release(string? Version, string? Notes, string? HtmlUrl, Asset? Installer, Asset? Sums);

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
            assets.FirstOrDefault(x => x.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                ?? assets.FirstOrDefault(x => x.Name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase)),
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

    // Installer downloads: no total time limit (ResumableDownload fails only on a stall) and no GitHub API Accept header.
    private static readonly HttpClient Downloads = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nadfny", CurrentVersion));
        return http;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nadfny", CurrentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}

/// <summary>The downloaded installer is gone (for example Temp was cleared); the page downloads it again.</summary>
public sealed class UpdateMissingException(string message) : InvalidOperationException(message);
