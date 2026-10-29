using Nadfny.Core;

namespace Nadfny.Features.Artifacts;

/// <summary>A folder a tool regenerates on the next install/build, so deleting it loses nothing.</summary>
public sealed record ArtifactRule(string FolderName, string Kind, string Tool, Func<IReadOnlySet<string>, bool> SiblingMatch, string? RequiredFile = null);

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
    private static bool Unity(IReadOnlySet<string> s) => s.Contains("Assets") && s.Contains("ProjectSettings");
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
        new("build", "build", "Gradle", Gradle),
        new(".gradle", ".gradle", "Gradle", Gradle),
        new("Library", "Library", "Unity", Unity),
        new("Temp", "Temp", "Unity", Unity),
        new("Obj", "Obj", "Unity", Unity),
        new("Logs", "Logs", "Unity", Unity),
        new(".godot", ".godot", "Godot", s => s.Contains("project.godot")),
        new(".dart_tool", ".dart_tool", "Flutter", s => s.Contains("pubspec.yaml")),
        new("build", "build", "Flutter", s => s.Contains("pubspec.yaml")),
        new("_build", "_build", "Elixir", s => s.Contains("mix.exs")),
        new("deps", "deps", "Elixir", s => s.Contains("mix.exs")),
        new("Pods", "Pods", "CocoaPods", s => s.Contains("Podfile")),
        new("build", "build", "CMake", Any, "CMakeCache.txt"),
        new("out", "out", "CMake", Any, "CMakeCache.txt"),
        new(".next", ".next", "Next.js", Node),
        new(".nuxt", ".nuxt", "Nuxt", Node),
        new(".svelte-kit", ".svelte-kit", "SvelteKit", Node),
        new(".turbo", ".turbo", "Turborepo", Node),
        new(".angular", ".angular", "Angular CLI", Node),
        new(".parcel-cache", ".parcel-cache", "Parcel", Node),
        new(".expo", ".expo", "Expo", Node),
        new("dist", "dist", "JS build", Node),
        new("coverage", "coverage", "test coverage", Node),
        new("test-results", "test-results", "Playwright", Node),
        new("playwright-report", "playwright-report", "Playwright", Node),
        new(".venv", ".venv", "Python venv", Python),
        new("venv", "venv", "Python venv", Python),
        new("__pycache__", "__pycache__", "Python", Any),
        new(".pytest_cache", ".pytest_cache", "pytest", Any),
        new(".mypy_cache", ".mypy_cache", "mypy", Any),
        new(".ruff_cache", ".ruff_cache", "Ruff", Any),
        new(".tox", ".tox", "tox", Any),
    ];

    private static readonly ILookup<string, ArtifactRule> ByName = All.ToLookup(r => r.FolderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the matching rule for a child folder given the names of its siblings, or null.</summary>
    public static ArtifactRule? Match(string folderName, IReadOnlySet<string> siblingNames, IReadOnlySet<string>? childFiles = null) =>
        ByName[folderName].FirstOrDefault(r => r.SiblingMatch(siblingNames)
            && (r.RequiredFile is null || childFiles?.Contains(r.RequiredFile) == true));

    internal static ArtifactRule? MatchFolder(string path, IReadOnlySet<string> siblingNames)
    {
        var name = Path.GetFileName(path);
        var files = name.Equals("build", StringComparison.OrdinalIgnoreCase) || name.Equals("out", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(DirectorySizer.Shallow(path).Where(e => !e.IsDir).Select(e => Path.GetFileName(e.Path)), StringComparer.OrdinalIgnoreCase)
            : null;
        return Match(name, siblingNames, files);
    }
}
