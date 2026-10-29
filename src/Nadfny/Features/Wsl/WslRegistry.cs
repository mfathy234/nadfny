using Microsoft.Win32;

namespace Nadfny.Features.Wsl;

/// <summary>Reads the per-distribution entries WSL keeps under HKCU, the same data wsl --list is built from.</summary>
public static class WslRegistry
{
    private const string LxssPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public static IReadOnlyList<LxssEntry> Read()
    {
        using var root = Registry.CurrentUser.OpenSubKey(LxssPath);
        if (root is null) return [];

        var entries = new List<LxssEntry>();
        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key is null) continue;
            entries.Add(new LxssEntry(
                id,
                key.GetValue("DistributionName") as string,
                key.GetValue("BasePath") as string,
                key.GetValue("Version") as int?));
        }
        return entries;
    }
}
