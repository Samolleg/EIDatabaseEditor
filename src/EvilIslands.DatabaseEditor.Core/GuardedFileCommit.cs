using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>
/// Owns the actual file object until commit. No path-based overwrite is permitted.
/// The old file is renamed by its locked handle to backup, then the verified new
/// file is published with replace=false. If publication fails, rollback also uses
/// replace=false: an intervening third-party file is never overwritten.
/// A crash between the two renames leaves the original in the named backup.
/// </summary>
internal sealed class GuardedFileCommit : IDisposable
{
    private readonly string _sourcePath;
    private readonly FileStream _source;

    public GuardedFileCommit(string sourcePath, string expectedSha256)
    {
        _sourcePath = Path.GetFullPath(sourcePath);
        _source = OpenLocked(_sourcePath);
        try
        {
            VerifyHash(_source, expectedSha256);
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    public void Commit(string preparedPath, string backupPath, string expectedPreparedSha256,
        Action? beforePublish = null)
    {
        preparedPath = Path.GetFullPath(preparedPath);
        backupPath = Path.GetFullPath(backupPath);
        if (!string.Equals(Path.GetDirectoryName(_sourcePath), Path.GetDirectoryName(preparedPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(_sourcePath), Path.GetDirectoryName(backupPath), StringComparison.OrdinalIgnoreCase) ||
            DatabaseSafetyPolicy.ContainsReparsePoint(_sourcePath) ||
            DatabaseSafetyPolicy.ContainsReparsePoint(preparedPath) ||
            DatabaseSafetyPolicy.ContainsReparsePoint(backupPath))
        {
            throw new IOException("Сохранение требует обычных соседних файлов без символических ссылок.");
        }

        using FileStream prepared = OpenLocked(preparedPath);
        VerifyHash(prepared, expectedPreparedSha256);
        Rename(_source.SafeFileHandle, backupPath);
        try
        {
            // Deterministic fault/interleaving injection for the storage regressions.
            beforePublish?.Invoke();
            Rename(prepared.SafeFileHandle, _sourcePath);
        }
        catch (IOException publicationError)
        {
            try
            {
                Rename(_source.SafeFileHandle, _sourcePath);
            }
            catch (IOException rollbackError)
            {
                throw new IOException(
                    $"Сохранение остановлено. Файл по исходному пути не перезаписан; прежняя версия сохранена в {backupPath}.",
                    new AggregateException(publicationError, rollbackError));
            }

            throw;
        }
    }

    public void Dispose() => _source.Dispose();

    private static FileStream OpenLocked(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Защищённое сохранение текущего RES поддерживается в Windows.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new IOException("Путь сохранения проходит через символическую ссылку.");
        }

        // GENERIC_READ | DELETE; FILE_SHARE_READ only. The same handle may rename
        // its file, but other writers/deleters cannot acquire a conflicting handle.
        SafeFileHandle handle = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Не удалось получить исключительное право сохранения {path}.", new Win32Exception(error));
        }

        try { return new FileStream(handle, FileAccess.Read, 128 * 1024); }
        catch { handle.Dispose(); throw; }
    }

    private static void VerifyHash(FileStream stream, string expected)
    {
        stream.Position = 0;
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Файл изменился на диске после открытия. Сохранение отменено до записи.");
        }
    }

    private static void Rename(SafeFileHandle handle, string target)
    {
        byte[] name = Encoding.Unicode.GetBytes(target);
        // FILE_RENAME_INFO: DWORD flags, aligned HANDLE, DWORD name byte count,
        // WCHAR name[]. Zero flags means never replace an existing target.
        int rootOffset = IntPtr.Size;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(int);
        int size = nameOffset + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size))
            {
                throw new IOException($"Не удалось безопасно переименовать файл в {target}.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        IntPtr information, uint size);
}
