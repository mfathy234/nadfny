namespace Nadfny.Core;

/// <summary>
/// Journal of folders that were moved aside for deletion. If Nadfny is closed mid-delete, the next start
/// finishes the job instead of leaving a half-deleted ".nadfny-deleting-*" folder behind.
/// </summary>
public static class PendingDeletes
{
    private const string Marker = ".nadfny-deleting-";
    private static readonly string Journal = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nadfny", "pending-deletes.txt");
    private static readonly Lock Sync = new();

    /// <summary>Instantly moves a folder aside (same volume rename) and records it. Returns the new path.</summary>
    public static string MoveAside(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        SafeDeleter.EnsureDeletable(full);
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("Cannot move a drive root.");
        var aside = Path.Combine(parent, $"{Marker}{Path.GetFileName(full)}-{Guid.NewGuid().ToString("N")[..8]}");
        Record(aside);
        try
        {
            Directory.Move(full, aside);
        }
        catch
        {
            Forget(aside);
            throw;
        }
        return aside;
    }

    public static bool IsAside(string path) => Path.GetFileName(path).StartsWith(Marker, StringComparison.Ordinal);

    public static void Complete(string aside) => Forget(aside);

    /// <summary>Finishes deletes interrupted by a previous run. Only touches paths it recorded itself.</summary>
    public static async Task ResumeAsync()
    {
        string[] pending;
        lock (Sync) pending = File.Exists(Journal) ? File.ReadAllLines(Journal) : [];
        foreach (var path in pending.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsAside(path)) { Forget(path); continue; } // never delete anything that is not our own marker folder
            try
            {
                await Task.Run(() => SafeDeleter.DeletePermanently(path));
                Forget(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still locked; try again next start.
            }
        }
    }

    private static void Record(string path)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
            File.AppendAllLines(Journal, [path]);
        }
    }

    private static void Forget(string path)
    {
        lock (Sync)
        {
            if (!File.Exists(Journal)) return;
            var rest = File.ReadAllLines(Journal).Where(l => l.Length > 0 && !l.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            File.WriteAllLines(Journal, rest);
        }
    }
}
