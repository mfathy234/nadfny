using System.Diagnostics;
using Nadfny.Core;
using Xunit.Abstractions;

namespace Nadfny.Tests;

/// <summary>Manual benchmark (set NADFNY_BENCH=1). Compares ways to delete a node_modules-like tree.</summary>
public sealed class DeleteBenchmark(ITestOutputHelper output)
{
    private const int Files = 25_000;

    private static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "nadfny-bench-" + Guid.NewGuid().ToString("N"));
        var blob = new byte[8192];
        for (var i = 0; i < Files; i++)
        {
            var dir = Path.Combine(root, "node_modules", $"pkg{i % 400}", $"lib{i % 7}");
            if (i < 2800) Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i}.js"), blob);
        }
        return root;
    }

    private double Time(string name, Action<string> delete)
    {
        var root = MakeTree();
        var sw = Stopwatch.StartNew();
        delete(root);
        sw.Stop();
        Assert.False(Directory.Exists(root));
        output.WriteLine($"{name,-34} {sw.Elapsed.TotalSeconds,6:F2}s");
        return sw.Elapsed.TotalSeconds;
    }

    [Fact]
    public void CompareDeleteStrategies()
    {
        if (Environment.GetEnvironmentVariable("NADFNY_BENCH") != "1") return;
        Time("Directory.Delete (single thread)", r => Directory.Delete(r, true));
        Time("cmd rd /s /q", r => Process.Start(new ProcessStartInfo("cmd.exe", $"/c rd /s /q \"{r}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit());
        Time("SafeDeleter (parallel)", r => SafeDeleter.DeletePermanently(r));
        Time("SafeDeleter + counter", r => SafeDeleter.DeletePermanently(r, default, new DeleteCounter()));
        Time("3 trees in parallel (SafeDeleter)", r =>
        {
            var others = new[] { MakeTree(), MakeTree() };
            var sw = Stopwatch.StartNew();
            Parallel.ForEach(others.Append(r), t => SafeDeleter.DeletePermanently(t));
            output.WriteLine($"   (3 trees together took {sw.Elapsed.TotalSeconds:F2}s)");
        });
    }
}
