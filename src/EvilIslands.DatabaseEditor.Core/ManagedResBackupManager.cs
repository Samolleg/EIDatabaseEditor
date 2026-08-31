using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public enum ManagedResKind
{
    Database,
    Texts,
    Sound,
}

public sealed record ManagedResBackupInfo(
    ManagedResKind Kind,
    string TargetPath,
    string BackupPath,
    long FileLength,
    DateTime LastWriteTimeUtc,
    int BackupCount);

public sealed record ManagedResRestoreResult(
    ManagedResKind Kind,
    string TargetPath,
    string RestoredFromPath,
    string CurrentStateBackupPath,
    long FileLength,
    string Sha256);

public static class ManagedResBackupManager
{
    private const int CopyBufferSize = 1024 * 1024;

    public static ManagedResBackupInfo? FindLatest(string sourcePath, ManagedResKind kind)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        IReadOnlyList<FileInfo> backups = EnumerateManagedBackups(fullSourcePath);
        FileInfo? latest = backups.FirstOrDefault();
        return latest is null
            ? null
            : new ManagedResBackupInfo(
                kind,
                fullSourcePath,
                latest.FullName,
                latest.Length,
                latest.LastWriteTimeUtc,
                backups.Count);
    }

    public static Task<ManagedResRestoreResult> RestoreLatestAsync(
        ManagedResBackupInfo backup,
        string? expectedCurrentSha256 = null,
        long? expectedCurrentLength = null,
        DateTime? expectedCurrentLastWriteTimeUtc = null,
        bool allowGameInstallationPath = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => RestoreLatest(
                backup,
                expectedCurrentSha256,
                expectedCurrentLength,
                expectedCurrentLastWriteTimeUtc,
                allowGameInstallationPath),
            CancellationToken.None);
    }

    private static ManagedResRestoreResult RestoreLatest(
        ManagedResBackupInfo requestedBackup,
        string? expectedCurrentSha256,
        long? expectedCurrentLength,
        DateTime? expectedCurrentLastWriteTimeUtc,
        bool allowGameInstallationPath)
    {
        string sourcePath = Path.GetFullPath(requestedBackup.TargetPath);
        EnsureSafeTarget(sourcePath, allowGameInstallationPath);
        ManagedResBackupInfo latest = FindLatest(sourcePath, requestedBackup.Kind)
            ?? throw new FileNotFoundException("Для выбранного RES больше нет резервной копии.", requestedBackup.BackupPath);
        if (!Path.GetFullPath(latest.BackupPath).Equals(
                Path.GetFullPath(requestedBackup.BackupPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Список резервных копий изменился. Обновите обзор базы и повторите восстановление.");
        }

        EnsureSafeBackup(sourcePath, latest.BackupPath);
        string directory = Path.GetDirectoryName(sourcePath)!;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(sourcePath)}.restore-{Guid.NewGuid():N}");
        string? currentStateBackupPath = null;
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);
            FileInfo sourceInfo = new(sourcePath);
            sourceInfo.Refresh();
            EnsureExpectedSource(
                sourceInfo,
                source,
                expectedCurrentSha256,
                expectedCurrentLength,
                expectedCurrentLastWriteTimeUtc);
            string sourceSha256 = ComputeSha256(source);

            string restoreSha256;
            using (var backup = new FileStream(
                       latest.BackupPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       CopyBufferSize,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                restoreSha256 = ComputeSha256(backup);
                backup.CopyTo(output, CopyBufferSize);
                output.Flush(true);
            }

            EnsureFileHash(tempPath, restoreSha256, "Временная копия восстанавливаемого RES");
            ValidateArchive(tempPath, requestedBackup.Kind);
            EnsureSafeTarget(sourcePath, allowGameInstallationPath);
            EnsureFileHash(sourcePath, sourceSha256, "Текущий RES");

            RetainNewestManagedBackupBeforeSave(sourcePath);
            currentStateBackupPath = CreateAvailableBackupPath(sourcePath);
            CreateVerifiedBackup(source, sourceSha256, currentStateBackupPath);
            EnsureFileHash(sourcePath, sourceSha256, "Текущий RES перед восстановлением");

            File.Replace(tempPath, sourcePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            EnsureFileHash(currentStateBackupPath, sourceSha256, "Резервная копия текущего состояния");
            EnsureFileHash(sourcePath, restoreSha256, "Восстановленный RES");
            ValidateArchive(sourcePath, requestedBackup.Kind);

            return new ManagedResRestoreResult(
                requestedBackup.Kind,
                sourcePath,
                latest.BackupPath,
                currentStateBackupPath,
                new FileInfo(sourcePath).Length,
                restoreSha256);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static void ValidateArchive(string path, ManagedResKind kind)
    {
        switch (kind)
        {
            case ManagedResKind.Database:
                _ = DatabaseDocument.Open(path);
                break;
            case ManagedResKind.Texts:
                _ = TextsDocument.Open(path);
                break;
            case ManagedResKind.Sound:
                SoundArchiveCatalog catalog = SoundArchiveCatalog.Open([path]);
                if (catalog.Resources.Count == 0)
                {
                    throw new DatabaseFormatException(
                        "Резервная копия звукового RES не содержит WAV, MP3 или OGG.",
                        path);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void EnsureExpectedSource(
        FileInfo sourceInfo,
        FileStream source,
        string? expectedSha256,
        long? expectedLength,
        DateTime? expectedLastWriteTimeUtc)
    {
        if (expectedLength is long length && sourceInfo.Length != length ||
            expectedLastWriteTimeUtc is DateTime lastWrite && sourceInfo.LastWriteTimeUtc != lastWrite)
        {
            throw new IOException("Текущий RES изменился после открытия. Восстановление отменено.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !ComputeSha256(source).Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new IOException("Текущий RES изменился после открытия. Восстановление отменено.");
        }
    }

    private static void EnsureSafeTarget(string path, bool allowGameInstallationPath)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Восстанавливаемый RES больше не существует.", path);
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new InvalidOperationException("Путь RES проходит через символическую ссылку или junction.");
        }

        if (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(path))
        {
            throw new InvalidOperationException(
                "Восстановление RES в каталоге игры требует отдельного подтверждения пользователя.");
        }
    }

    private static void EnsureSafeBackup(string sourcePath, string backupPath)
    {
        string fullBackupPath = Path.GetFullPath(backupPath);
        if (!Path.GetDirectoryName(fullBackupPath)!.Equals(
                Path.GetDirectoryName(sourcePath),
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullBackupPath))
        {
            throw new IOException("Резервная копия находится вне папки текущего RES или больше не существует.");
        }

        FileAttributes attributes = File.GetAttributes(fullBackupPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("Резервная копия имеет небезопасный тип.");
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateManagedBackups(string sourcePath)
    {
        string directory = Path.GetDirectoryName(sourcePath)
            ?? throw new DirectoryNotFoundException("Не найдена папка открытого RES.");
        string fileName = Path.GetFileName(sourcePath);
        var pattern = new Regex(
            $"^{Regex.Escape(fileName)}\\.bak(?:\\.\\d{{8}}-\\d{{6}}(?:-\\d{{3}})?|\\.[0-9a-f]{{32}})?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var backups = new List<FileInfo>();
        foreach (string candidate in Directory.EnumerateFileSystemEntries(
                     directory,
                     fileName + ".bak*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!pattern.IsMatch(Path.GetFileName(candidate)))
            {
                continue;
            }

            string fullCandidate = Path.GetFullPath(candidate);
            FileAttributes attributes = File.GetAttributes(fullCandidate);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            backups.Add(new FileInfo(fullCandidate));
        }

        return backups
            .OrderByDescending(backup => backup.LastWriteTimeUtc)
            .ThenByDescending(backup => backup.CreationTimeUtc)
            .ThenByDescending(backup => backup.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void RetainNewestManagedBackupBeforeSave(string sourcePath)
    {
        foreach (FileInfo obsolete in EnumerateManagedBackups(sourcePath).Skip(1))
        {
            try
            {
                File.Delete(obsolete.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Не удалось удалить старую резервную копию '{obsolete.FullName}'. " +
                    "Восстановление отменено до изменения RES.",
                    exception);
            }
        }

        if (EnumerateManagedBackups(sourcePath).Count > 1)
        {
            throw new IOException("Не удалось безопасно сократить число резервных копий.");
        }
    }

    private static string CreateAvailableBackupPath(string sourcePath)
    {
        string basePath = sourcePath + ".bak";
        if (!File.Exists(basePath) && !Directory.Exists(basePath))
        {
            return basePath;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            string candidate = suffix == 0
                ? $"{basePath}.{timestamp}"
                : $"{basePath}.{timestamp}-{suffix:D3}";
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{basePath}.{Guid.NewGuid():N}";
    }

    private static void CreateVerifiedBackup(FileStream source, string expectedSha256, string backupPath)
    {
        source.Position = 0;
        using (var backup = new FileStream(
                   backupPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   CopyBufferSize,
                   FileOptions.WriteThrough | FileOptions.SequentialScan))
        {
            source.CopyTo(backup, CopyBufferSize);
            backup.Flush(true);
        }

        source.Position = 0;
        EnsureFileHash(backupPath, expectedSha256, "Резервная копия текущего состояния");
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureFileHash(string path, string expectedSha256, string description)
    {
        if (!ComputeSha256(path).Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new IOException($"{description} не прошёл проверку SHA-256.");
        }
    }
}
