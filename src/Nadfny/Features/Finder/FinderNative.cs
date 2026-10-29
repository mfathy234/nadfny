using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nadfny.Features.Finder;

internal static class FinderNative
{
    internal static string Downloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out pointer);
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Downloads could not be located.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
            if (initialized) CoUninitialize();
        }
    }

    internal static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("File identity could not be read.");
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    internal static string Identity(string path)
    {
        // Metadata access does not read or hydrate cloud files. Open links themselves, never their targets.
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        return Identity(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
