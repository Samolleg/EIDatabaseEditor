using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EvilIslands.DatabaseEditor.Core;

internal sealed class NativePakReader : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Library = "EvilIslands.PakReader.dll";
    internal sealed record Row(uint NativeIndex, string Name, ushort NameLength, uint Method, long Size);
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, Row> Rows => _rows;
    internal IReadOnlyList<ResEntry> Entries { get; private set; } = [];
    internal IReadOnlyList<string> Deleted => _rows.Values.Where(r => r.Method == 2).Select(r => r.Name).ToArray();

    private NativePakReader() : base(true) { }

    internal static NativePakReader Open(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Check(OpenNative(path, out var handle));
        var result = new NativePakReader();
        result.SetHandle(handle);
        try {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            uint count = result.Count;
            for (uint index = 0; index < count; index++) {
                token.ThrowIfCancellationRequested();
                var entry = result.Entry(index);
                string name = encoding.GetString(entry.Name);
                result._rows.Add(PakArchive.LookupKey(name), new(index, name, checked((ushort)entry.Name.Length), entry.Method, entry.Size));
            }
            result.Entries = result._rows.Values.Where(r => r.Method != 2).Select((r, i) =>
                new ResEntry(i, r.Name, 0, checked((int)r.Size), 0, r.NameLength, 0)).ToArray();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal byte[] ReadPrefix(string name, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_rows.TryGetValue(PakArchive.LookupKey(name), out var row) || row.Method == 2) throw new KeyNotFoundException(name);
        byte[] result = Read(row.NativeIndex, checked((int)Math.Min(row.Size, maximumBytes)));
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal uint Count { get { Check(CountNative(this, out uint count)); return count; } }
    internal (byte[] Name, uint Method, long Size) Entry(uint index)
    {
        bool held = false;
        try {
            DangerousAddRef(ref held);
            Check(EntryNative(this, index, out var entry));
            byte[] name = new byte[checked((int)entry.NameLength)];
            Marshal.Copy(entry.Name, name, 0, name.Length);
            return (name, entry.Method, checked((long)entry.Size));
        }
        finally { if (held) DangerousRelease(); }
    }
    internal byte[] Read(uint index, int length)
    {
        byte[] result = new byte[length];
        Check(ReadNative(this, index, result, checked((uint)length)));
        return result;
    }
    protected override bool ReleaseHandle() { CloseNative(handle); return true; }
    private static void Check(int result)
    {
        if (result != 0) throw new IOException(Marshal.PtrToStringUTF8(ErrorNative()) ?? "PAK read failed.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EntryInfo { internal IntPtr Name; internal uint NameLength; internal uint Method; internal ulong Size; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_open", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int OpenNative(string path, out IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CloseNative(IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CountNative(NativePakReader handle, out uint count);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_entry", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EntryNative(NativePakReader handle, uint index, out EntryInfo entry);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_read", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadNative(NativePakReader handle, uint index, [Out] byte[] destination, uint capacity);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    [DllImport(Library, EntryPoint = "ei_pak_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ErrorNative();
}
