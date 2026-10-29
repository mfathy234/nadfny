using System.Collections.Concurrent;
using System.Globalization;
using Nadfny.Core;

namespace Nadfny.Features.AiModels;

public sealed record AiModelInfo(string Id, string Source, string Name, string Kind, string Path, long Bytes, string Modified,
    string Note, bool CanRemove, bool Recycle);

/// <summary>Local AI model files from Ollama, the Hugging Face hub cache and LM Studio. Removal only accepts ids from the last scan.</summary>
public sealed class AiModelService
{
    public const string OllamaSource = "Ollama";
    private const string OllamaNote = "Removed with ollama rm. Ollama downloads it again with ollama pull.";
    private const string OllamaInstallNote = "Install the Ollama app to remove single models. Its files are shared between models, so they are not deleted here.";
    private const string OllamaFailedNote = "Ollama did not list its models. Start Ollama to remove single models.";

    private readonly AiModelLocations _locations;
    private readonly Func<CancellationToken, Task<ProcessResult>> _ollamaList;
    private readonly Func<string, CancellationToken, Task<ProcessResult>> _ollamaRemove;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, AiModelInfo> _scanned = new Dictionary<string, AiModelInfo>();

    /// <param name="locations">Override the folders (tests point them at temp folders).</param>
    /// <param name="ollamaList">Override `ollama list`; an exit code of -1 means the CLI is not installed.</param>
    /// <param name="ollamaRemove">Override `ollama rm &lt;name&gt;`.</param>
    public AiModelService(AiModelLocations? locations = null,
        Func<CancellationToken, Task<ProcessResult>>? ollamaList = null,
        Func<string, CancellationToken, Task<ProcessResult>>? ollamaRemove = null)
    {
        _locations = locations ?? AiModelLocations.Current();
        _ollamaList = ollamaList ?? (ct => ProcessRunner.RunAsync("ollama", ["list"], timeout: TimeSpan.FromSeconds(20), ct: ct));
        _ollamaRemove = ollamaRemove ?? ((name, ct) => ProcessRunner.RunAsync("ollama", ["rm", name], timeout: TimeSpan.FromMinutes(2), ct: ct));
    }

    public async Task<IReadOnlyList<AiModelInfo>> ScanAsync(Action<AiModelInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ScanCoreAsync(onFound, progress, ct); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<AiModelInfo>> ScanCoreAsync(Action<AiModelInfo> onFound, ScanProgress progress, CancellationToken ct)
    {
        _scanned = new Dictionary<string, AiModelInfo>();
        var found = new ConcurrentDictionary<string, AiModelInfo>();
        void Add(AiModelInfo info)
        {
            found[info.Id] = info;
            onFound(info);
        }

        progress.SetPhase("Listing Ollama models");
        foreach (var row in await OllamaRowsAsync(ct)) Add(row);

        var folders = AiModelFolders.HuggingFace(_locations.HuggingFaceHub)
            .Concat(AiModelFolders.LmStudio(_locations.LmStudioRoots)).ToList();
        progress.SetPhase("Measuring AI models", folders.Count);
        await Parallel.ForEachAsync(folders, ct, (folder, token) =>
        {
            var size = DirectorySizer.MeasureSequential(folder.Path, token);
            Add(new(NewId(), folder.Source, folder.Name, folder.Kind, folder.Path, size.Bytes, FormatDate(size.LastWriteUtc),
                folder.Note, true, folder.Recycle));
            progress.CompleteOne(folder.Path);
            return ValueTask.CompletedTask;
        });
        progress.Flush();
        ct.ThrowIfCancellationRequested();

        var list = found.Values.OrderByDescending(x => x.Bytes).ToList();
        _scanned = list.ToDictionary(x => x.Id);
        return list;
    }

    private async Task<IReadOnlyList<AiModelInfo>> OllamaRowsAsync(CancellationToken ct)
    {
        var result = await _ollamaList(ct);
        if (result.ExitCode == -1) return OllamaFolderOnly(OllamaInstallNote);
        if (!result.Ok) return OllamaFolderOnly(OllamaFailedNote);
        return OllamaList.Parse(result.StdOut)
            .Select(m => new AiModelInfo(NewId(), OllamaSource, m.Name, "model", _locations.OllamaModels, m.Bytes, m.Modified,
                OllamaNote, true, false))
            .ToList();
    }

    private IReadOnlyList<AiModelInfo> OllamaFolderOnly(string note)
    {
        var folder = _locations.OllamaModels;
        if (!Directory.Exists(folder)) return [];
        var size = DirectorySizer.MeasureSequential(folder);
        return [new(NewId(), OllamaSource, "Ollama models (folder)", "folder", folder, size.Bytes, FormatDate(size.LastWriteUtc),
            note, false, false)];
    }

    public async Task<long> RemoveAsync(string id, OpReporter op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RemoveCoreAsync(id, op, ct); }
        finally { _gate.Release(); }
    }

    private async Task<long> RemoveCoreAsync(string id, OpReporter op, CancellationToken ct)
    {
        var item = _scanned.GetValueOrDefault(id)
            ?? throw new InvalidOperationException("Scan AI models again before removing this model.");
        if (!item.CanRemove) throw new InvalidOperationException("This folder cannot be removed here. Remove single models through the Ollama app.");
        if (item.Source == OllamaSource) return await RemoveOllamaAsync(item, op, ct);

        SafeDeleter.EnsureDeletable(item.Path);
        if (!Directory.Exists(item.Path))
        {
            Forget(id);
            return 0;
        }
        if (item.Recycle)
        {
            op.Phase("Moving to the Recycle Bin");
            await Task.Run(() => SafeDeleter.MoveToRecycleBin(item.Path), ct);
            Forget(id);
            return 0;
        }
        long freed = 0;
        await op.DeleteWithProgressAsync("Deleting " + item.Name, item.Bytes, counter =>
        {
            SafeDeleter.DeletePermanently(item.Path, ct, counter);
            freed = counter.Bytes;
        }, ct);
        Forget(id);
        return freed;
    }

    private async Task<long> RemoveOllamaAsync(AiModelInfo item, OpReporter op, CancellationToken ct)
    {
        if (!OllamaList.IsSafeName(item.Name)) throw new InvalidOperationException("That model name cannot be passed to Ollama.");
        op.Phase("Removing " + item.Name);
        var result = await _ollamaRemove(item.Name, ct);
        if (!result.Ok) throw new InvalidOperationException($"Ollama could not remove {item.Name}: {result.Error}");
        Forget(item.Id);
        return 0;
    }

    private void Forget(string id) => _scanned = _scanned.Where(x => x.Key != id).ToDictionary(x => x.Key, x => x.Value);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string FormatDate(DateTime utc) =>
        utc == default ? "" : utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
