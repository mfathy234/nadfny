using System.Collections.Concurrent;
using Nadfny.Core;

namespace Nadfny.Features.Apps;

public sealed record AppQueueResult(string Id, bool Removed, IReadOnlyList<AppFolder> Leftovers, string? Error, bool NeedsReview = false);
public sealed record ForcedRemovalPreview(string Id, string BackupPath, IReadOnlyList<AppFolder> Folders, bool NeedsAdmin);
public sealed record ForcedRemovalResult(string BackupPath, int Removed, IReadOnlyList<string> Failed);

public sealed partial class AppService
{
    private readonly SemaphoreSlim _uninstallGate = new(1, 1);
    private readonly Func<UninstallPlan, OpReporter, CancellationToken, Task<bool>> _runner;
    private readonly Func<string, string, IReadOnlyCollection<string>, CancellationToken, Task> _registryRemoval;
    private readonly ConcurrentDictionary<string, ForcedRemovalPreview> _forcedPreviews = new();

    internal static bool IsBroken(AppCandidate c) => c.Source == AppSource.Win32
        && ((c.InstallLocation ?? c.SharedInstallLocation) is { } folder && !Directory.Exists(folder)
            || c.Uninstall is { Kind: UninstallPlan.Command } p && !File.Exists(p.Target));

    public async Task<IReadOnlyList<AppQueueResult>> UninstallQueueAsync(IReadOnlyList<string> ids, bool quiet, OpReporter op, CancellationToken ct)
    {
        if (ids.Count > 1000) throw new InvalidOperationException("Select up to 1000 apps at a time.");
        var queue = ids.Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in queue) Get(id);
        await _uninstallGate.WaitAsync(ct);
        try
        {
            var results = new List<AppQueueResult>();
            foreach (var id in queue)
            {
                try
                {
                    var result = await UninstallCoreAsync(id, quiet, op, ct.IsCancellationRequested ? CancellationToken.None : ct);
                    results.Add(new(id, result.Removed, result.Leftovers, result.Removed ? null : "The app is still installed."));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
                {
                    results.Add(new(id, false, [], ex is OperationCanceledException ? "Cancelled or stopped waiting. Finish any open uninstaller before continuing." : ex.Message,
                        NeedsReview: ex is UninstallPendingException));
                    if (ex is UninstallPendingException) break;
                }
            }
            return results;
        }
        finally { _uninstallGate.Release(); }
    }

    public async Task<ForcedRemovalPreview> PreviewForcedRemovalAsync(string id, CancellationToken ct)
    {
        await _uninstallGate.WaitAsync(ct);
        try { return PreviewForcedRemoval(id); }
        finally { _uninstallGate.Release(); }
    }

    private ForcedRemovalPreview PreviewForcedRemoval(string id)
    {
        var (c, info) = Get(id);
        if (!info.Broken || !info.CanUninstall) throw new InvalidOperationException("Only a broken scanned entry can be removed this way.");
        AppRegistryRemoval.Arguments(c.Id, [c.Id], "delete");
        var preview = new ForcedRemovalPreview(id, AppRegistryRemoval.BackupPath(info.Name), Leftovers(c, info), c.Id.StartsWith("LocalMachine|", StringComparison.Ordinal));
        _forcedPreviews[id] = preview;
        return preview;
    }

    public async Task<ForcedRemovalResult> ForceRemoveAsync(string id, OpReporter op, CancellationToken ct)
    {
        await _uninstallGate.WaitAsync(ct);
        try
        {
            var (c, info) = Get(id);
            if (!_forcedPreviews.TryRemove(id, out var preview) || !info.Broken || !info.CanUninstall || !IsBroken(c))
                throw new InvalidOperationException("Review this broken entry again before removing it.");
            var current = Leftovers(c, info).Where(f => f.Deletable).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            op.Phase("Backing up the uninstall entry");
            await _registryRemoval(id, preview.BackupPath, _apps.Keys.ToList(), ct);
            _apps.TryRemove(id, out _);
            _leftovers[id] = preview.Folders.Where(f => f.Deletable && current.Contains(f.Path)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await RemoveLeftoversCoreAsync(id, preview.Folders.Where(f => f.Deletable).Select(f => f.Path).ToList(), op, CancellationToken.None);
            return new(preview.BackupPath, result.Removed, result.Failed);
        }
        finally { _uninstallGate.Release(); }
    }
}
