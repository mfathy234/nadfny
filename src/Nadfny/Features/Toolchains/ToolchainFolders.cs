using System.Text.RegularExpressions;

namespace Nadfny.Features.Toolchains;

public sealed record ToolchainLocations(string Home, string Local, string Roaming, string AndroidSdk)
{
    public static ToolchainLocations Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new(home, local, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Path.Combine(local, "Android", "Sdk"));
    }
}

internal sealed record ToolchainFolder(string Kind, string Name, string Path, string Note, bool Recycle);

internal static class ToolchainFolders
{
    private static readonly Regex ProductVersion = new(@"^([A-Za-z][A-Za-z-]*)(\d{4}\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<ToolchainFolder> Discover(ToolchainLocations locations)
    {
        var items = new List<ToolchainFolder>();
        foreach (var path in Children(Path.Combine(locations.Home, ".android", "avd")).Where(p => p.EndsWith(".avd", StringComparison.OrdinalIgnoreCase)))
            items.Add(new("avd", "Android emulator " + Path.GetFileNameWithoutExtension(path), path,
                "Contains emulator apps, snapshots and user data. Recreate the device in Android Studio; its saved data is not recreated.", true));

        foreach (var api in Children(Path.Combine(locations.AndroidSdk, "system-images")))
        foreach (var tag in Children(api))
        foreach (var architecture in Children(tag))
            items.Add(new("android-image", "Android image " + Path.GetFileName(api) + " / " + Path.GetFileName(tag) + " / " + Path.GetFileName(architecture), architecture,
                "Emulators using this image cannot start until it is downloaded again in Android Studio's SDK Manager. Close Android Studio and emulators first.", false));

        var products = new[] { locations.Local, locations.Roaming }
            .SelectMany(root => Children(Path.Combine(root, "JetBrains")))
            .Select(path => (Path: path, Match: ProductVersion.Match(Path.GetFileName(path))))
            .Where(x => x.Match.Success)
            .Select(x => (x.Path, Product: x.Match.Groups[1].Value, Version: Version.Parse(x.Match.Groups[2].Value))).ToList();
        foreach (var product in products.GroupBy(x => x.Product, StringComparer.OrdinalIgnoreCase))
        {
            var newest = product.Max(x => x.Version)!;
            foreach (var old in product.Where(x => x.Version < newest))
                items.Add(new("jetbrains", Path.GetFileName(old.Path) + (IsUnder(old.Path, locations.Roaming) ? " settings" : " caches and data"), old.Path,
                    "An older product version exists beside " + product.Key + newest + ". May contain settings, plugins and Local History. These are not recreated. Close all JetBrains IDEs first.", true));
        }
        return items;
    }

    internal static IReadOnlyList<string> Children(string path)
    {
        try { return Directory.GetDirectories(path).Where(p => !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool CanRemove(string path, string home)
    {
        if (!IsUnder(path, home)) return false;
        // Check each ancestor too: an SDK root can itself be a junction to another drive.
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }
}
