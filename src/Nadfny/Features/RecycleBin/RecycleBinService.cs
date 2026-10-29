using System.Runtime.InteropServices;
using Nadfny.Features.Explore;

namespace Nadfny.Features.RecycleBin;

public sealed record RecycleBinDrive(string Drive, long Bytes, long Items);
public sealed record RecycleBinInfo(long Bytes, long Items, IReadOnlyList<RecycleBinDrive> Drives);

/// <summary>
/// The Recycle Bin, through the shell's own API: what it holds on each local drive, and emptying it. Everything Nadfny
/// moves to the Bin (Explore, app leftovers, downloaded games) only frees space once the Bin is emptied.
/// </summary>
public static class RecycleBinService
{
    public static RecycleBinInfo Query()
    {
        var drives = ExploreService.Drives()
            .Select(d => Query(d.Name) is { } r ? new RecycleBinDrive(d.Name, r.Bytes, r.Items) : null)
            .OfType<RecycleBinDrive>()
            .Where(d => d.Items > 0)
            .ToList();
        return new RecycleBinInfo(drives.Sum(d => d.Bytes), drives.Sum(d => d.Items), drives);
    }

    private static (long Bytes, long Items)? Query(string root)
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBinW(root, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
    }

    /// <summary>Empties the Bin on every drive (or one drive). The app already confirmed, so Windows does not ask again.</summary>
    public static async Task<long> EmptyAsync(string? drive, CancellationToken ct)
    {
        if (drive is not null && !ExploreService.Drives().Any(d => d.Name.Equals(drive, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Only the Recycle Bin of a local drive can be emptied.");
        var before = Query().Bytes;
        var hr = await Task.Run(() => SHEmptyRecycleBinW(IntPtr.Zero, drive, SherbNoConfirmation | SherbNoSound), ct);
        // E_UNEXPECTED (0x8000FFFF) is what Windows returns when the Bin was already empty.
        if (hr != 0 && hr != unchecked((int)0x8000FFFF))
            throw new InvalidOperationException($"Windows could not empty the Recycle Bin (error 0x{hr:X}). A file in it may be in use.");
        return Math.Max(0, before - Query().Bytes);
    }

    private const uint SherbNoConfirmation = 0x1, SherbNoSound = 0x4; // keep the progress window for big bins

    [StructLayout(LayoutKind.Sequential)] // natural 8-byte packing on x64 (shellapi.h packs to 1 only on 32-bit)
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}
