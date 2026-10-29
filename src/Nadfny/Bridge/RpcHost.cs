using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Nadfny.Core;
using Nadfny.Features.Apps;
using Nadfny.Features.Artifacts;
using Nadfny.Features.Caches;
using Nadfny.Features.Docker;
using Nadfny.Features.Explore;
using Nadfny.Features.RecycleBin;
using Nadfny.Features.SystemInfo;
using Nadfny.Features.Toolchains;
using Nadfny.Features.Updates;
using Nadfny.Features.Windows;
using Nadfny.Features.Worktrees;

namespace Nadfny.Bridge;

/// <summary>
/// JSON request/response bridge between the WebView UI and the native services.
/// UI sends {id, method, params}; host answers {type:"result", id, ok, result|error} and pushes {type:"event", name, data}.
/// </summary>
public sealed class RpcHost
{
    private readonly Action<string> _post;
    private readonly Func<string?> _pickFolder;
    private readonly Func<string, bool> _setClipboard;
    private readonly WorktreeService _worktrees = new();
    private readonly DockerService _docker = new();
    private readonly ArtifactService _artifacts = new();
    private readonly CacheService _caches = new();
    private readonly ToolchainService _toolchains = new();
    private readonly ExploreService _explore = new();
    private readonly WindowsCleanupService _windows = new();
    private readonly SystemInfoService _system = new();
    private readonly UpdateService _updates = new();
    private readonly AppService _apps = new();
    private readonly Action _exitApp;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _scans = new();
    private AppSettings _settings = AppSettings.Load();

    public RpcHost(Action<string> post, Func<string?> pickFolder, Func<string, bool> setClipboard, Action exitApp)
    {
        _exitApp = exitApp;
        _post = post;
        _pickFolder = pickFolder;
        _setClipboard = setClipboard;
        _ = Task.Run(PendingDeletes.ResumeAsync); // finish deletes interrupted by a previous run
    }

    public void Handle(string json)
    {
        RpcRequest? req;
        try { req = JsonSerializer.Deserialize<RpcRequest>(json, Json.Options); }
        catch (JsonException) { return; }
        if (req is null || string.IsNullOrEmpty(req.Method)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DispatchAsync(req.Method, req.Params);
                Send(new { type = "result", id = req.Id, ok = true, result });
            }
            catch (OperationCanceledException)
            {
                Send(new { type = "result", id = req.Id, ok = false, error = "Cancelled.", cancelled = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Send(new { type = "result", id = req.Id, ok = false, error = ex.Message });
            }
        });
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p) => method switch
    {
        "app.info" => new { version = Application.ProductVersion, roots = _settings.Roots, drives = ExploreService.Drives(), allDrives = AllDrivesSelected() },
        "settings.scanAllDrives" => SetAllDrives(Bool(p, "enabled")),
        "settings.addRoot" => AddRoot(),
        "settings.removeRoot" => RemoveRoot(Str(p, "path")),
        "drives.list" => ExploreService.Drives(),

        "worktrees.scan" => await Scoped("worktrees", ct => _worktrees.ScanAsync(_settings.Roots, w => Emit("worktrees.item", w), Progress("worktrees"), ct)),
        "worktrees.remove" => await _worktrees.RemoveAsync(Str(p, "path"), Bool(p, "deleteBranch"), Bool(p, "allowUnsaved"), Op(p), CancellationToken.None),

        "docker.scan" => await Scoped("docker", ct => _docker.ScanAsync(ct)),
        "docker.prune" => await _docker.PruneAsync(Str(p, "kind"), Op(p), CancellationToken.None),
        "docker.remove" => await _docker.RemoveAsync(Str(p, "kind"), Str(p, "id"), Op(p), CancellationToken.None),
        "docker.compact" => await _docker.CompactDiskAsync(Op(p), CancellationToken.None),

        "artifacts.scan" => await Scoped("artifacts", ct => _artifacts.ScanAsync(_settings.Roots, a => Emit("artifacts.item", a), Progress("artifacts"), ct)),
        "artifacts.remove" => await RemoveArtifacts(p),
        "artifacts.check" => await _artifacts.CheckAsync(Paths(p), CancellationToken.None),

        "caches.scan" => await Scoped("caches", ct => _caches.ScanAsync(c => Emit("caches.item", c), Progress("caches"), ct)),
        "caches.clear" => await _caches.ClearAsync(Str(p, "id"), Op(p), CancellationToken.None),

        "toolchains.scan" => await Scoped("toolchains", ct => _toolchains.ScanAsync(_settings.Roots, t => Emit("toolchains.item", t), Progress("toolchains"), ct)),
        "toolchains.remove" => await _toolchains.RemoveAsync(Str(p, "id"), Op(p), CancellationToken.None),
        "toolchains.openApps" => OpenAppsFeatures(),

        "windows.scan" => await Scoped("windows", ct => _windows.ScanAsync(ct)),
        "windows.clean" => await _windows.CleanAsync(Ids(p), Op(p), CancellationToken.None),

        "apps.scan" => await Scoped("apps", ct => _apps.ScanAsync(a => Emit("apps.item", a), Progress("apps"), ct)),
        "apps.clearCache" => await _apps.ClearCacheAsync(Str(p, "id"), Op(p), CancellationToken.None),
        "apps.uninstall" => await Scoped("uninstall", ct => _apps.UninstallAsync(Str(p, "id"), Op(p), ct)),
        "apps.removeLeftovers" => await RemoveLeftovers(p),

        "recycle.query" => RecycleBinService.Query(),
        "recycle.empty" => await RecycleBinService.EmptyAsync(Str(p, "drive") is { Length: > 0 } d ? d : null, CancellationToken.None),

        "explore.list" => await Scoped("explore", ct => _explore.ListAsync(Str(p, "path"), e => Emit("explore.item", e), ct)),
        "explore.delete" => await DeleteExplored(Str(p, "path"), Bool(p, "permanent"), Op(p)),

        "system.info" => await _system.GetAsync(Bool(p, "refresh"), CancellationToken.None),
        "clipboard.set" => _setClipboard(Str(p, "text")),
        "update.check" => await _updates.CheckAsync(CancellationToken.None),
        "update.download" => await _updates.DownloadAsync(Op(p), CancellationToken.None),
        "update.install" => InstallUpdate(),
        "update.openPage" => OpenReleasePage(Str(p, "url")),
        "shell.reveal" => Reveal(Str(p, "path")),
        "scan.cancel" => Cancel(Str(p, "scope")),
        _ => throw new InvalidOperationException($"Unknown method: {method}"),
    };

    private static bool OpenAppsFeatures()
    {
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        return true;
    }

    /// <summary>One running scan per scope; starting a new one cancels the previous.</summary>
    private async Task<T> Scoped<T>(string scope, Func<CancellationToken, Task<T>> run)
    {
        var cts = new CancellationTokenSource();
        if (_scans.TryGetValue(scope, out var previous)) previous.Cancel();
        _scans[scope] = cts;
        try { return await run(cts.Token); }
        finally { _scans.TryRemove(new KeyValuePair<string, CancellationTokenSource>(scope, cts)); cts.Dispose(); }
    }

    private bool Cancel(string scope)
    {
        if (_scans.TryGetValue(scope, out var cts)) cts.Cancel();
        return true;
    }

    private async Task<object> RemoveArtifacts(JsonElement p)
    {
        var (removed, bytes, failed) = await _artifacts.RemoveAsync(Paths(p), Op(p), CancellationToken.None);
        return new { removed, bytes, failed };
    }

    private async Task<object> RemoveLeftovers(JsonElement p)
    {
        var (removed, failed) = await _apps.RemoveLeftoversAsync(Str(p, "id"), Paths(p), Op(p), CancellationToken.None);
        return new { removed, failed };
    }

    /// <summary>Explore may delete anywhere on a fixed drive except system and profile folders (SafeDeleter.EnsureDeletable).</summary>
    private async Task<object> DeleteExplored(string path, bool permanent, OpReporter op)
    {
        if (!ExploreService.Drives().Any(d => SafeDeleter.IsUnder(path, d.Name)))
            throw new InvalidOperationException("Only files on local drives can be deleted.");
        await _explore.DeleteAsync(path, permanent, op, CancellationToken.None);
        return new { path };
    }

    private bool AllDrivesSelected()
    {
        var drives = ExploreService.Drives().Select(d => d.Name).ToList();
        return drives.Count > 0 && drives.All(d => _settings.Roots.Contains(d, StringComparer.OrdinalIgnoreCase));
    }

    private object SetAllDrives(bool enabled)
    {
        _settings = enabled
            ? _settings with { Roots = ExploreService.Drives().Select(d => d.Name).ToList() }
            : AppSettings.Defaults();
        _settings.Save();
        return new { roots = _settings.Roots, allDrives = AllDrivesSelected() };
    }

    private bool InstallUpdate()
    {
        _updates.InstallAndRestart();
        _ = Task.Delay(400).ContinueWith(_ => _exitApp()); // let the reply reach the UI, then close so the MSI can replace files
        return true;
    }

    private static bool OpenReleasePage(string url)
    {
        UpdateService.OpenReleasePage(url);
        return true;
    }

    /// <summary>Progress channel for one user action; the UI passes its own opId so it can match the events.</summary>
    private OpReporter Op(JsonElement p) => Str(p, "opId") is { Length: > 0 } id ? new OpReporter(id, snap => Emit("op.progress", snap)) : OpReporter.None;

    private ScanProgress Progress(string scope) => new(scope, snap => Emit("scan.progress", snap));

    private object AddRoot()
    {
        var picked = _pickFolder();
        if (picked is null) return new { roots = _settings.Roots, added = false };
        if (!_settings.Roots.Contains(picked, StringComparer.OrdinalIgnoreCase))
        {
            _settings = _settings with { Roots = [.. _settings.Roots, picked] };
            _settings.Save();
        }
        return new { roots = _settings.Roots, added = true };
    }

    private object RemoveRoot(string path)
    {
        _settings = _settings with { Roots = _settings.Roots.Where(r => !r.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList() };
        _settings.Save();
        return new { roots = _settings.Roots };
    }

    private static bool Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("That item no longer exists.");
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
        return true;
    }

    private void Emit(string name, object data) => Send(new { type = "event", name, data });

    private void Send(object message) => _post(JsonSerializer.Serialize(message, Json.Options));

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static List<string> Paths(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").Where(s => s.Length > 0).ToList()
            : [];

    private static List<string> Ids(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").Where(s => s.Length > 0).ToList()
            : [];

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private sealed record RpcRequest(string Id, string Method, JsonElement Params);
}
