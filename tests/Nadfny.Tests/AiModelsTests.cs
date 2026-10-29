using Nadfny.Core;
using Nadfny.Features.AiModels;

namespace Nadfny.Tests;

public sealed class AiModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nadfny-ai-tests-" + Guid.NewGuid().ToString("N"));

    public AiModelsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeDeleter.DeletePermanently(_root);
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private AiModelLocations Locations() => new(
        Path.Combine(_root, "ollama", "models"),
        Path.Combine(_root, "hf", "hub"),
        [Path.Combine(_root, "lm", "models")]);

    private static ProcessResult OllamaOutput(string stdout) => new(0, stdout, "");

    private const string OllamaListOutput = """
        NAME                         ID              SIZE      MODIFIED
          llama3.2:latest        a80c4f17acd5    2.0 GB    2 weeks ago
        qwen2.5-coder:7b-instruct    dae161e27b0e   4.7 GB   3 months ago
        not a model row
        """;

    [Fact]
    public void OllamaList_ParsesTaggedNamesHeaderAndOddSpacing()
    {
        var models = OllamaList.Parse(OllamaListOutput);

        Assert.Equal(2, models.Count);
        Assert.Equal("llama3.2:latest", models[0].Name);
        Assert.Equal("a80c4f17acd5", models[0].Id);
        Assert.Equal((long)(2.0 * 1024 * 1024 * 1024), models[0].Bytes);
        Assert.Equal("2 weeks ago", models[0].Modified);
        Assert.Equal("qwen2.5-coder:7b-instruct", models[1].Name);
        Assert.Equal("3 months ago", models[1].Modified);
    }

    [Theory]
    [InlineData("llama3.2:latest", true)]
    [InlineData("library/qwen2.5-coder:7b", true)]
    [InlineData("model_1.0-x.v2", true)]
    [InlineData("x; rm -rf", false)]
    [InlineData("-rf", false)]
    [InlineData("a b", false)]
    [InlineData("$(calc)", false)]
    [InlineData("", false)]
    public void OllamaList_NameAllowListRejectsShellAndFlags(string name, bool expected) =>
        Assert.Equal(expected, OllamaList.IsSafeName(name));

    [Theory]
    [InlineData("models--meta-llama--Llama-3.1-8B", "model", "meta-llama/Llama-3.1-8B")]
    [InlineData("datasets--HuggingFaceH4--ultrachat", "dataset", "HuggingFaceH4/ultrachat")]
    [InlineData("models--gpt2", "model", "gpt2")]
    public void HuggingFaceFolderName_MapsToOrgAndName(string folder, string kind, string name)
    {
        Assert.True(AiModelFolders.TryParseHuggingFaceName(folder, out var k, out var n));
        Assert.Equal(kind, k);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("spaces--someone--app")]
    [InlineData("models--")]
    public void HuggingFaceFolderName_IgnoresOtherFolders(string folder) =>
        Assert.False(AiModelFolders.TryParseHuggingFaceName(folder, out _, out _));

    [Fact]
    public void Locations_HonorAbsoluteEnvironmentOverridesAndFallBackToHome()
    {
        var env = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = _root,
            ["OLLAMA_MODELS"] = Path.Combine(_root, "custom-ollama"),
            ["HF_HOME"] = Path.Combine(_root, "hfhome"),
        };
        var loc = AiModelLocations.Resolve(name => env.GetValueOrDefault(name));

        Assert.Equal(Path.Combine(_root, "custom-ollama"), loc.OllamaModels);
        Assert.Equal(Path.Combine(_root, "hfhome", "hub"), loc.HuggingFaceHub);
        Assert.Equal([Path.Combine(_root, ".lmstudio", "models"), Path.Combine(_root, ".cache", "lm-studio", "models")], loc.LmStudioRoots);

        env["HF_HUB_CACHE"] = Path.Combine(_root, "exact-hub");
        env["OLLAMA_MODELS"] = "relative\\models";
        var exact = AiModelLocations.Resolve(name => env.GetValueOrDefault(name));
        Assert.Equal(Path.Combine(_root, "exact-hub"), exact.HuggingFaceHub);
        Assert.Equal(Path.Combine(_root, ".ollama", "models"), exact.OllamaModels);
    }

    [Fact]
    public void LmStudio_ListsPublisherModelFoldersFromEveryRoot()
    {
        Write(@"lm\models\bartowski\Qwen2.5-7B\model.gguf", 10);
        Write(@"lmcache\models\ggml-org\tiny\model.gguf", 5);
        var items = AiModelFolders.LmStudio([Path.Combine(_root, "lm", "models"), Path.Combine(_root, "lmcache", "models")]);

        Assert.Equal(["bartowski/Qwen2.5-7B", "ggml-org/tiny"], items.Select(i => i.Name).Order().ToArray());
        Assert.All(items, i => Assert.True(i.Recycle));
    }

    [Fact]
    public async Task Scan_CombinesOllamaHuggingFaceAndLmStudioSortedBySize()
    {
        Write(@"hf\hub\models--org--small\blobs\a", 100);
        Write(@"hf\hub\datasets--org--big\blobs\b", 5000);
        Write(@"lm\models\pub\llm\weights.bin", 1000);
        var service = new AiModelService(Locations(),
            _ => Task.FromResult(OllamaOutput(OllamaListOutput)),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var items = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        Assert.Equal(["Ollama", "Ollama", "Hugging Face", "LM Studio", "Hugging Face"], items.Select(i => i.Source).ToArray());
        Assert.Equal("org/big", items[2].Name);
        Assert.Equal("dataset", items[2].Kind);
        Assert.Equal(items.OrderByDescending(i => i.Bytes).Select(i => i.Bytes), items.Select(i => i.Bytes));
    }

    [Fact]
    public async Task Scan_WithoutOllamaCli_ShowsOneFolderRowWithoutRemove()
    {
        Write(@"ollama\models\blobs\sha256-abc", 2048);
        var service = new AiModelService(Locations(), _ => Task.FromResult(new ProcessResult(-1, "", "missing")));

        var items = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);

        var row = Assert.Single(items);
        Assert.Equal("Ollama models (folder)", row.Name);
        Assert.Equal(2048, row.Bytes);
        Assert.False(row.CanRemove);
        Assert.Contains("Install the Ollama app", row.Note);
    }

    [Fact]
    public async Task Remove_RefusesIdNotFromLastScan()
    {
        var service = new AiModelService(Locations(), _ => Task.FromResult(OllamaOutput(OllamaListOutput)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("anything", OpReporter.None, CancellationToken.None));

        await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("not-in-scan", OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_OllamaRunsRmWithScannedName()
    {
        var removed = new List<string>();
        var service = new AiModelService(Locations(), _ => Task.FromResult(OllamaOutput(OllamaListOutput)),
            (name, _) => { removed.Add(name); return Task.FromResult(new ProcessResult(0, "deleted", "")); });
        var items = await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None);
        var first = items.Single(i => i.Name == "llama3.2:latest");

        await service.RemoveAsync(first.Id, OpReporter.None, CancellationToken.None);

        Assert.Equal(["llama3.2:latest"], removed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(first.Id, OpReporter.None, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_HuggingFaceFolderIsDeletedPermanently()
    {
        var file = Write(@"hf\hub\models--org--tiny\blobs\a", 300);
        var service = new AiModelService(Locations(), _ => Task.FromResult(new ProcessResult(-1, "", "missing")));
        var item = (await service.ScanAsync(_ => { }, ScanProgress.None, CancellationToken.None)).Single(i => i.Name == "org/tiny");

        var freed = await service.RemoveAsync(item.Id, OpReporter.None, CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(item.Path));
        Assert.Equal(300, freed);
    }
}
