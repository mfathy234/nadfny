namespace Nadfny.Features.Artifacts;

/// <summary>A folder a tool regenerates on the next install/build, so deleting it loses nothing.</summary>
public sealed record ArtifactRule(string FolderName, string Kind, string Tool, Func<IReadOnlySet<string>, bool> SiblingMatch);

public static class ArtifactRules
{
    private static bool Any(IReadOnlySet<string> s) => true;
    private static bool Node(IReadOnlySet<string> s) => s.Contains("package.json");
    private static bool DotNet(IReadOnlySet<string> s) =>
        s.Any(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase));
    private static bool Rust(IReadOnlySet<string> s) => s.Contains("Cargo.toml");
    private static bool Maven(IReadOnlySet<string> s) => s.Contains("pom.xml");
    private static bool Gradle(IReadOnlySet<string> s) => s.Contains("build.gradle") || s.Contains("build.gradle.kts");
    private static bool Python(IReadOnlySet<string> s) =>
        s.Contains("pyproject.toml") || s.Contains("requirements.txt") || s.Contains("setup.py") || s.Contains("Pipfile");

    public static readonly IReadOnlyList<ArtifactRule> All =
    [
        // Only next to a package.json: a bare node_modules is often a tool install (VS Code extensions, Node itself).
        new("node_modules", "node_modules", "npm install", Node),
        new("bin", "bin", ".NET build", DotNet),
        new("obj", "obj", ".NET build", DotNet),
        new(".vs", ".vs", "Visual Studio", Any),
        new("target", "target", "cargo build", Rust),
        new("target", "target", "mvn package", Maven),
        new("build", "build", "gradle build", Gradle),
        new(".gradle", ".gradle", "gradle", Gradle),
        new(".next", ".next", "Next.js", Node),
        new(".nuxt", ".nuxt", "Nuxt", Node),
        new(".svelte-kit", ".svelte-kit", "SvelteKit", Node),
        new(".turbo", ".turbo", "Turborepo", Node),
        new(".angular", ".angular", "Angular CLI", Node),
        new(".parcel-cache", ".parcel-cache", "Parcel", Node),
        new("dist", "dist", "JS build", Node),
        new("coverage", "coverage", "test coverage", Node),
        new("test-results", "test-results", "Playwright", Node),
        new("playwright-report", "playwright-report", "Playwright", Node),
        new(".venv", ".venv", "Python venv", Python),
        new("venv", "venv", "Python venv", Python),
        new("__pycache__", "__pycache__", "Python", Any),
        new(".pytest_cache", ".pytest_cache", "pytest", Any),
        new(".mypy_cache", ".mypy_cache", "mypy", Any),
    ];

    private static readonly ILookup<string, ArtifactRule> ByName = All.ToLookup(r => r.FolderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the matching rule for a child folder given the names of its siblings, or null.</summary>
    public static ArtifactRule? Match(string folderName, IReadOnlySet<string> siblingNames) =>
        ByName[folderName].FirstOrDefault(r => r.SiblingMatch(siblingNames));
}
