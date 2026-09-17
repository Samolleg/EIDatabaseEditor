using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EvilIslands.DatabaseEditor.Core;

internal static class ResourceFileStamp
{
    // Legacy archives may carry FILETIME outside DateTime's range. Compare raw
    // metadata for change detection; do not normalize the user's file dates.
    public static long Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.GetLastWriteTimeUtc(path).Ticks;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileTime(handle, IntPtr.Zero, IntPtr.Zero, out long written))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return written;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle handle, IntPtr creation, IntPtr access, out long write);
}
