namespace Nadfny.Features.Caches;

public sealed partial class CacheService
{
    private static void AddDeveloperCaches(List<CacheDefinition> items, string home, string local, string roaming,
        Func<string, string?> environment)
    {
        items.AddRange([
            new("go-mod", "Go modules", environment("GOMODCACHE") ?? Path.Combine(home, "go", "pkg", "mod"), "Modules are downloaded again on the next Go build."),
            new("go-build", "Go build cache", Path.Combine(local, "go-build"), "Compiled packages are rebuilt on the next Go build."),
            new("uv", "uv cache", Path.Combine(local, "uv", "cache"), "Packages and wheels are downloaded or built again on the next install."),
            new("poetry", "Poetry cache", Path.Combine(local, "pypoetry", "Cache"), "Packages are downloaded again on the next Poetry install."),
            new("bun", "Bun cache", Path.Combine(home, ".bun", "install", "cache"), "Packages are downloaded again on the next Bun install."),
            new("deno", "Deno cache", Path.Combine(local, "deno"), "Modules are downloaded and compiled again on the next Deno run."),
            new("composer-local", "Composer cache (local)", Path.Combine(local, "Composer", "cache"), "Packages are downloaded again on the next Composer install."),
            new("composer-roaming", "Composer cache (roaming)", Path.Combine(roaming, "Composer", "cache"), "Packages are downloaded again on the next Composer install."),
            new("cargo-git", "Cargo git checkouts", Path.Combine(home, ".cargo", "git"), "Git dependencies are cloned again on the next Cargo build. Local edits here are lost."),
            new("vscode-vsix", "VS Code extension downloads", Path.Combine(roaming, "Code", "CachedExtensionVSIXs"), "Extension packages are downloaded again when installing or updating extensions."),
        ]);
        foreach (var folder in new[] { ".conda", "miniconda3", "anaconda3" })
            items.Add(new("conda-" + folder.TrimStart('.'), "Conda packages (" + folder + ")", Path.Combine(home, folder, "pkgs"),
                "Downloaded archives and index files are fetched again on the next Conda install. Extracted packages are kept because environments may reference them."));

        foreach (var product in ProductDirectories(Path.Combine(local, "JetBrains")))
        foreach (var kind in new[] { "caches", "log" })
            items.Add(new("jetbrains-" + Path.GetFileName(product) + "-" + kind,
                Path.GetFileName(product) + " " + kind, Path.Combine(product, kind),
                kind == "log" ? "Logs are written again when the IDE runs. Existing diagnostic history is lost." : "Indexes and caches are rebuilt when the IDE opens. Close the IDE first."));

        foreach (var version in ProductDirectories(Path.Combine(local, "Microsoft", "VisualStudio")))
            items.Add(new("vs-" + Path.GetFileName(version), "Visual Studio " + Path.GetFileName(version) + " component cache",
                Path.Combine(version, "ComponentModelCache"), "The component cache is rebuilt when Visual Studio starts. Close Visual Studio first."));
    }

    private static void AddCrashDumpCaches(List<CacheDefinition> items, string local)
    {
        items.AddRange([
            new("crash-dumps", "App crash dumps", Path.Combine(local, "CrashDumps"),
                "Memory dumps written when a program crashes. Developers sometimes need them to find out why it crashed. Dumps from the last 24 hours are kept."),
            new("wer-archive", "Error reports (archive)", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                "Reports Windows saved about programs that crashed or hung for this user. Reports from the last 24 hours are kept."),
            new("wer-queue", "Error reports (queue)", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
                "Reports waiting to be sent to Microsoft. Clearing them only drops reports that were not sent yet. Reports from the last 24 hours are kept."),
        ]);
    }

    private static IReadOnlyList<string> ProductDirectories(string path)
    {
        try { return Directory.GetDirectories(path).Where(p => !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
