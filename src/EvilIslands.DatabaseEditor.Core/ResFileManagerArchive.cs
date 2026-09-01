using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public enum ResFileEditKind
{
    AddOrReplace,
    Delete,
}

public sealed record ResImportedFile(
    string SourcePath,
    long FileLength,
    DateTime LastWriteTimeUtc,
    string Sha256);

public sealed record ResFileEdit(
    ResFileEditKind Kind,
    string ResourceName,
    ResImportedFile? ImportedFile = null);

public sealed record ResFileManagerSaveResult(
    string TargetPath,
    long FileLength,
    string Sha256,
    int ResourceCount,
    int AddedCount,
    int ReplacedCount,
    int DeletedCount,
    string? BackupPath = null);

public sealed class ResFileManagerArchive
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly Dictionary<string, ResEntry> _entriesByName;

    private ResFileManagerArchive(
        string sourcePath,
        long fileLength,
        DateTime lastWriteTimeUtc,
        string sha256,
        IReadOnlyList<ResEntry> entries)
    {
        SourcePath = sourcePath;
        FileLength = fileLength;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Sha256 = sha256;
        Entries = entries;
        _entriesByName = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string SourcePath { get; }

    public long FileLength { get; }

    public DateTime LastWriteTimeUtc { get; }

    public string Sha256 { get; }

    public IReadOnlyList<ResEntry> Entries { get; }

    public static Task<ResFileManagerArchive> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Open(fullPath, cancellationToken), CancellationToken.None);
    }

    public static Task<ResImportedFile> InspectImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("Добавляемый файл не найден.", fullPath);
                }

                EnsureRegularFile(fullPath, "Добавляемый файл");
                var info = new FileInfo(fullPath);
                long initialLength = info.Length;
                DateTime initialLastWriteTimeUtc = info.LastWriteTimeUtc;
                if (initialLength > int.MaxValue)
                {
                    throw new DatabaseFormatException(
                        "Один файл внутри RES не может быть больше 2 ГиБ.",
                        fullPath);
                }
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.SequentialScan);
                string hash = ComputeSha256(stream, cancellationToken);
                info.Refresh();
                if (info.Length != initialLength ||
                    stream.Length != initialLength ||
                    info.LastWriteTimeUtc != initialLastWriteTimeUtc)
                {
                    throw new IOException("Добавляемый файл изменился во время чтения.");
                }

                return new ResImportedFile(fullPath, info.Length, info.LastWriteTimeUtc, hash);
            },
            CancellationToken.None);
    }

    public bool Contains(string resourceName) => _entriesByName.ContainsKey(resourceName);

    public Task ExtractAsync(
        string resourceName,
        string targetPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!_entriesByName.TryGetValue(resourceName, out ResEntry? entry))
        {
            throw new KeyNotFoundException($"Ресурс '{resourceName}' не найден в архиве.");
        }

        string destinationPath = Path.GetFullPath(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => Extract(entry, destinationPath, overwrite, cancellationToken),
            CancellationToken.None);
    }

    public static Task ExtractImportedAsync(
        ResImportedFile importedFile,
        string targetPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importedFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string destinationPath = Path.GetFullPath(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ExtractImported(importedFile, destinationPath, overwrite, cancellationToken),
            CancellationToken.None);
    }

    public Task<ResFileManagerSaveResult> SaveCopyAsync(
        IReadOnlyList<ResFileEdit> edits,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string destinationPath = Path.GetFullPath(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCopy(edits, destinationPath, cancellationToken),
            CancellationToken.None);
    }

    public Task<ResFileManagerSaveResult> SaveInPlaceAsync(
        IReadOnlyList<ResFileEdit> edits,
        bool allowGameInstallationPath = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveInPlace(edits, allowGameInstallationPath, cancellationToken),
            CancellationToken.None);
    }

    public static string NormalizeResourceName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        string normalized = resourceName.Trim().Replace('/', '\\').Trim('\\');
        ValidateResourceName(normalized);
        string[] segments = normalized.Split('\\');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new DatabaseFormatException(
                $"Имя ресурса '{resourceName}' содержит небезопасный участок пути.",
                resourceName);
        }

        return string.Join('\\', segments);
    }

    private static ResFileManagerArchive Open(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("RES-файл не найден.", path);
        }

        EnsureRegularFile(path, "RES-файл");
        var info = new FileInfo(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.RandomAccess);
        ArchiveIndex index = ReadArchiveIndex(path, stream, cancellationToken);
        string sha256 = ComputeSha256(stream, cancellationToken);
        info.Refresh();
        if (info.Length != index.FileLength || info.LastWriteTimeUtc != index.LastWriteTimeUtc)
        {
            throw new IOException("RES изменился во время открытия. Откройте его повторно.");
        }

        return new ResFileManagerArchive(
            path,
            index.FileLength,
            index.LastWriteTimeUtc,
            sha256,
            index.Entries);
    }

    private void Extract(
        ResEntry entry,
        string targetPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для извлечения файла не существует.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(targetPath))
        {
            throw new InvalidOperationException("Путь извлечения проходит через символическую ссылку или junction.");
        }

        if (!overwrite && (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            throw new IOException("Целевой файл уже существует.");
        }

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.partial-{Guid.NewGuid():N}");
        try
        {
            using (var source = OpenVerifiedSource(cancellationToken))
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                source.Position = entry.Offset;
                CopyExactly(source, output, entry.Size, cancellationToken);
                output.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath, true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
    }

    private static void ExtractImported(
        ResImportedFile importedFile,
        string targetPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        EnsureImportedIdentity(importedFile, cancellationToken);
        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для извлечения файла не существует.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(targetPath))
        {
            throw new InvalidOperationException("Путь извлечения проходит через символическую ссылку или junction.");
        }

        if (!overwrite && (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            throw new IOException("Целевой файл уже существует.");
        }

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.partial-{Guid.NewGuid():N}");
        try
        {
            using (var source = new FileStream(
                       importedFile.SourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       CopyBufferSize,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                CopyExactly(source, output, checked((int)importedFile.FileLength), cancellationToken);
                output.Flush(true);
            }

            EnsureFileHash(tempPath, importedFile.Sha256, "Извлечённый импортированный файл", cancellationToken);
            if (overwrite && File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath, true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
    }

    private ResFileManagerSaveResult SaveCopy(
        IReadOnlyList<ResFileEdit> edits,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(SourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Для исходного RES используйте «Сохранить в текущий RES».");
        }

        EnsureSafeCopyDestination(destinationPath);
        MutationPlan plan = CreatePlan(edits);
        string directory = Path.GetDirectoryName(destinationPath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.partial-{Guid.NewGuid():N}");
        try
        {
            string resultSha256;
            using (FileStream source = OpenVerifiedSource(cancellationToken))
            {
                WriteAndVerify(source, tempPath, plan, cancellationToken);
                resultSha256 = ComputeSha256(tempPath, cancellationToken);
            }

            EnsureSafeCopyDestination(destinationPath);
            File.Move(tempPath, destinationPath);
            return CreateResult(destinationPath, resultSha256, plan, backupPath: null);
        }
        catch
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
    }

    private ResFileManagerSaveResult SaveInPlace(
        IReadOnlyList<ResFileEdit> edits,
        bool allowGameInstallationPath,
        CancellationToken cancellationToken)
    {
        EnsureSafeInPlaceTarget(SourcePath, allowGameInstallationPath);
        MutationPlan plan = CreatePlan(edits);
        string directory = Path.GetDirectoryName(SourcePath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(SourcePath)}.partial-{Guid.NewGuid():N}");
        string? backupPath = null;
        try
        {
            string resultSha256;
            using (FileStream source = OpenVerifiedSource(cancellationToken))
            {
                WriteAndVerify(source, tempPath, plan, cancellationToken);
                resultSha256 = ComputeSha256(tempPath, cancellationToken);
                EnsureSafeInPlaceTarget(SourcePath, allowGameInstallationPath);
                EnsureSourceIdentity(source, cancellationToken);

                RetainNewestManagedBackupBeforeSave(SourcePath);
                backupPath = CreateAvailableBackupPath(SourcePath);
                CreateVerifiedBackup(source, backupPath, cancellationToken);
                EnsureSourceIdentity(source, cancellationToken);

                File.Replace(tempPath, SourcePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }

            EnsureFileHash(backupPath, Sha256, "Резервная копия исходного RES", cancellationToken);
            EnsureFileHash(SourcePath, resultSha256, "Сохранённый RES", cancellationToken);
            ValidateKnownArchive(SourcePath, Path.GetFileName(SourcePath), cancellationToken);
            return CreateResult(SourcePath, resultSha256, plan, backupPath);
        }
        catch
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
    }

    private FileStream OpenVerifiedSource(CancellationToken cancellationToken)
    {
        EnsureRegularFile(SourcePath, "Исходный RES");
        var source = new FileStream(
            SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.RandomAccess);
        try
        {
            EnsureSourceIdentity(source, cancellationToken);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void EnsureSourceIdentity(FileStream source, CancellationToken cancellationToken)
    {
        var info = new FileInfo(SourcePath);
        info.Refresh();
        if (source.Length != FileLength || info.Length != FileLength || info.LastWriteTimeUtc != LastWriteTimeUtc)
        {
            throw new IOException("Исходный RES изменился после открытия. Откройте его повторно.");
        }

        string currentSha256 = ComputeSha256(source, cancellationToken);
        if (!currentSha256.Equals(Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Исходный RES изменился после открытия. Откройте его повторно.");
        }
    }

    private MutationPlan CreatePlan(IReadOnlyList<ResFileEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw new InvalidOperationException("В RES нет подготовленных изменений.");
        }

        var byName = new Dictionary<string, ResFileEdit>(StringComparer.OrdinalIgnoreCase);
        foreach (ResFileEdit edit in edits)
        {
            string normalizedName = NormalizeResourceName(edit.ResourceName);
            if (!byName.TryAdd(normalizedName, edit with { ResourceName = normalizedName }))
            {
                throw new InvalidOperationException($"Для ресурса '{normalizedName}' задано больше одного изменения.");
            }

            if (edit.Kind == ResFileEditKind.AddOrReplace && edit.ImportedFile is null)
            {
                throw new InvalidOperationException($"Для ресурса '{normalizedName}' не выбран добавляемый файл.");
            }

            if (edit.Kind == ResFileEditKind.Delete && edit.ImportedFile is not null)
            {
                throw new InvalidOperationException($"Удаление ресурса '{normalizedName}' содержит лишние данные.");
            }
        }

        var resources = new List<BuildResource>(Entries.Count + byName.Count);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var replaced = 0;
        var deleted = 0;
        foreach (ResEntry entry in Entries.OrderBy(candidate => candidate.Offset))
        {
            if (!byName.TryGetValue(entry.Name, out ResFileEdit? edit))
            {
                resources.Add(new BuildResource(entry.Name, entry, null));
                continue;
            }

            handled.Add(entry.Name);
            if (edit.Kind == ResFileEditKind.Delete)
            {
                deleted++;
                continue;
            }

            resources.Add(new BuildResource(entry.Name, null, edit.ImportedFile));
            replaced++;
        }

        foreach ((string name, ResFileEdit edit) in byName)
        {
            if (handled.Contains(name))
            {
                continue;
            }

            if (edit.Kind == ResFileEditKind.Delete)
            {
                throw new InvalidOperationException($"Удаляемый ресурс '{name}' отсутствует в исходном RES.");
            }

            resources.Add(new BuildResource(name, null, edit.ImportedFile));
            added++;
        }

        if (resources.Count == 0)
        {
            throw new InvalidOperationException("Нельзя удалить последний файл из RES.");
        }

        return new MutationPlan(resources, added, replaced, deleted);
    }

    private void WriteAndVerify(
        FileStream source,
        string tempPath,
        MutationPlan plan,
        CancellationToken cancellationToken)
    {
        using (var output = new FileStream(
                   tempPath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None,
                   CopyBufferSize,
                   FileOptions.WriteThrough | FileOptions.SequentialScan))
        {
            WriteArchive(source, output, plan.Resources, cancellationToken);
            output.Flush(true);
        }

        ArchiveIndex rebuilt = OpenArchiveIndex(tempPath, cancellationToken);
        VerifyResult(source, tempPath, rebuilt, plan.Resources, cancellationToken);
        ValidateKnownArchive(tempPath, Path.GetFileName(SourcePath), cancellationToken);
    }

    private static void WriteArchive(
        FileStream source,
        FileStream output,
        IReadOnlyList<BuildResource> resources,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BuildResource resource in resources)
        {
            ValidateResourceName(resource.Name);
            if (!names.Add(resource.Name))
            {
                throw new DatabaseFormatException($"Имя ресурса '{resource.Name}' встречается больше одного раза.");
            }
        }

        output.SetLength(ResArchiveDocument.HeaderSize);
        output.Position = ResArchiveDocument.HeaderSize;
        foreach (BuildResource resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resource.NewOffset = checked((int)output.Position);
            if (resource.ImportedFile is ResImportedFile imported)
            {
                resource.NewSize = CopyImported(imported, output, cancellationToken);
            }
            else if (resource.SourceEntry is ResEntry sourceEntry)
            {
                source.Position = sourceEntry.Offset;
                CopyExactly(source, output, sourceEntry.Size, cancellationToken);
                resource.NewSize = sourceEntry.Size;
            }
            else
            {
                throw new DatabaseFormatException($"У ресурса '{resource.Name}' отсутствуют данные.");
            }
        }

        int directoryOffset = checked((int)output.Position);
        WriteDirectory(output, resources);
        long end = output.Position;
        output.Position = 0;
        Span<byte> header = stackalloc byte[ResArchiveDocument.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)resources.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)directoryOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..],
            checked((uint)(end - directoryOffset - (long)resources.Count * ResArchiveDocument.EntrySize)));
        output.Write(header);
        output.Position = end;
    }

    private static int CopyImported(
        ResImportedFile imported,
        FileStream output,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(imported.SourcePath, "Добавляемый файл");
        var info = new FileInfo(imported.SourcePath);
        info.Refresh();
        if (info.Length != imported.FileLength || info.LastWriteTimeUtc != imported.LastWriteTimeUtc ||
            imported.FileLength > int.MaxValue)
        {
            throw new IOException($"Добавляемый файл '{info.Name}' изменился после выбора.");
        }

        using var input = new FileStream(
            imported.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copied += read;
        }

        if (copied != imported.FileLength ||
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(imported.Sha256, StringComparison.Ordinal))
        {
            throw new IOException($"Добавляемый файл '{info.Name}' изменился во время записи.");
        }

        return checked((int)copied);
    }

    private static void EnsureImportedIdentity(
        ResImportedFile imported,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(imported.SourcePath, "Добавляемый файл");
        var info = new FileInfo(imported.SourcePath);
        info.Refresh();
        if (info.Length != imported.FileLength ||
            info.LastWriteTimeUtc != imported.LastWriteTimeUtc ||
            imported.FileLength > int.MaxValue)
        {
            throw new IOException($"Добавляемый файл '{info.Name}' изменился после выбора.");
        }

        EnsureFileHash(
            imported.SourcePath,
            imported.Sha256,
            $"Добавляемый файл '{info.Name}'",
            cancellationToken);
    }

    private static void WriteDirectory(FileStream output, IReadOnlyList<BuildResource> resources)
    {
        int count = resources.Count;
        var table = new BuildResource?[count];
        var groups = resources
            .GroupBy(resource => ComputeHash(resource.Name, count))
            .OrderBy(group => group.Key)
            .ToArray();
        foreach (IGrouping<int, BuildResource> group in groups)
        {
            table[group.Key] = group.First();
        }

        using IEnumerator<int> freeSlots = Enumerable.Range(0, count)
            .Where(index => table[index] is null)
            .GetEnumerator();
        var chains = new Dictionary<int, List<int>>();
        foreach (IGrouping<int, BuildResource> group in groups)
        {
            var slots = new List<int> { group.Key };
            foreach (BuildResource resource in group.Skip(1))
            {
                if (!freeSlots.MoveNext())
                {
                    throw new DatabaseFormatException("В hash-таблице RES не осталось места для коллизии.");
                }

                table[freeSlots.Current] = resource;
                slots.Add(freeSlots.Current);
            }

            chains.Add(group.Key, slots);
        }

        var nextIndices = Enumerable.Repeat(uint.MaxValue, count).ToArray();
        foreach (IReadOnlyList<int> slots in chains.Values)
        {
            for (var index = 0; index + 1 < slots.Count; index++)
            {
                nextIndices[slots[index]] = checked((uint)slots[index + 1]);
            }
        }

        using var names = new MemoryStream();
        var entryBuffer = new byte[ResArchiveDocument.EntrySize];
        for (var index = 0; index < count; index++)
        {
            BuildResource resource = table[index]
                ?? throw new DatabaseFormatException("Hash-таблица RES содержит пустую запись.");
            byte[] nameBytes = Encoding.ASCII.GetBytes(resource.Name);
            int nameOffset = checked((int)names.Position);
            names.Write(nameBytes);

            Span<byte> entry = entryBuffer;
            entry.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(entry, nextIndices[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint)resource.NewSize));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)resource.NewOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], checked((ushort)nameBytes.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[18..], checked((uint)nameOffset));
            output.Write(entry);
        }

        names.Position = 0;
        names.CopyTo(output);
    }

    private static void VerifyResult(
        FileStream source,
        string rebuiltPath,
        ArchiveIndex rebuilt,
        IReadOnlyList<BuildResource> resources,
        CancellationToken cancellationToken)
    {
        if (rebuilt.Entries.Count != resources.Count)
        {
            throw new IOException("Повторная проверка RES обнаружила неверное число файлов.");
        }

        Dictionary<string, ResEntry> rebuiltByName = rebuilt.Entries.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        using var rebuiltStream = new FileStream(
            rebuiltPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.RandomAccess);
        byte[] sourceBuffer = new byte[CopyBufferSize];
        byte[] rebuiltBuffer = new byte[CopyBufferSize];
        foreach (BuildResource expected in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rebuiltByName.TryGetValue(expected.Name, out ResEntry? actual) || actual.Size != expected.NewSize)
            {
                throw new IOException($"Ресурс '{expected.Name}' отсутствует или имеет неверный размер после сборки.");
            }

            if (expected.SourceEntry is ResEntry original)
            {
                CompareExactly(
                    source,
                    original.Offset,
                    rebuiltStream,
                    actual.Offset,
                    original.Size,
                    sourceBuffer,
                    rebuiltBuffer,
                    expected.Name,
                    cancellationToken);
                continue;
            }

            ResImportedFile imported = expected.ImportedFile
                ?? throw new IOException($"Для ресурса '{expected.Name}' потеряны импортированные данные.");
            rebuiltStream.Position = actual.Offset;
            string hash = ComputeSha256(rebuiltStream, actual.Size, cancellationToken);
            if (!hash.Equals(imported.Sha256, StringComparison.Ordinal))
            {
                throw new IOException($"Импортированный ресурс '{expected.Name}' не прошёл SHA-256 после сборки.");
            }
        }
    }

    private static void CompareExactly(
        Stream left,
        int leftOffset,
        Stream right,
        int rightOffset,
        int length,
        byte[] leftBuffer,
        byte[] rightBuffer,
        string resourceName,
        CancellationToken cancellationToken)
    {
        left.Position = leftOffset;
        right.Position = rightOffset;
        int remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int block = Math.Min(remaining, leftBuffer.Length);
            left.ReadExactly(leftBuffer.AsSpan(0, block));
            right.ReadExactly(rightBuffer.AsSpan(0, block));
            if (!leftBuffer.AsSpan(0, block).SequenceEqual(rightBuffer.AsSpan(0, block)))
            {
                throw new IOException($"Неизменённый ресурс '{resourceName}' отличается после сборки RES.");
            }

            remaining -= block;
        }
    }

    private static ArchiveIndex OpenArchiveIndex(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.RandomAccess);
        return ReadArchiveIndex(path, stream, cancellationToken);
    }

    private static ArchiveIndex ReadArchiveIndex(
        string path,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length < ResArchiveDocument.HeaderSize)
        {
            throw new DatabaseFormatException("RES header is truncated.", path, 0);
        }

        Span<byte> header = stackalloc byte[ResArchiveDocument.HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (signature != ResArchiveDocument.Signature)
        {
            throw new DatabaseFormatException($"Unexpected RES signature 0x{signature:X8}.", path, 0);
        }

        uint countValue = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint directoryValue = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint namePoolValue = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (countValue == 0 || countValue > 1_000_000 ||
            directoryValue > int.MaxValue || namePoolValue > int.MaxValue)
        {
            throw new DatabaseFormatException("RES header contains unsupported sizes.", path, 4);
        }

        int count = (int)countValue;
        int directoryOffset = (int)directoryValue;
        int namePoolSize = (int)namePoolValue;
        long directoryLength = checked((long)count * ResArchiveDocument.EntrySize);
        long catalogLength = checked(directoryLength + namePoolSize);
        if (directoryOffset < ResArchiveDocument.HeaderSize ||
            (long)directoryOffset + catalogLength > stream.Length ||
            catalogLength > int.MaxValue)
        {
            throw new DatabaseFormatException("RES directory or name pool is truncated.", path, directoryOffset);
        }

        byte[] catalog = new byte[(int)catalogLength];
        stream.Position = directoryOffset;
        stream.ReadExactly(catalog);
        var entries = new List<ResEntry>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entryOffset = index * ResArchiveDocument.EntrySize;
            ReadOnlySpan<byte> entry = catalog.AsSpan(entryOffset, ResArchiveDocument.EntrySize);
            uint sizeValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint dataOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[16..]);
            uint nameOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[18..]);
            if (dataOffsetValue == 0 || nameLength == 0)
            {
                continue;
            }

            if (sizeValue > int.MaxValue || dataOffsetValue > int.MaxValue)
            {
                throw new DatabaseFormatException("RES catalog contains an unsupported entry.", path, directoryOffset + entryOffset);
            }

            int resourceOffset = (int)dataOffsetValue;
            int resourceSize = (int)sizeValue;
            if (resourceOffset < ResArchiveDocument.HeaderSize ||
                (long)resourceOffset + resourceSize > directoryOffset)
            {
                throw new DatabaseFormatException("RES resource range is invalid.", path, directoryOffset + entryOffset);
            }

            long nameEnd = checked((long)nameOffsetValue + nameLength);
            if (nameOffsetValue > namePoolSize || nameEnd > namePoolSize)
            {
                throw new DatabaseFormatException("RES resource name exceeds the name pool.", path, directoryOffset + entryOffset);
            }

            int nameStart = checked((int)directoryLength + (int)nameOffsetValue);
            string name = Encoding.ASCII.GetString(catalog, nameStart, nameLength).TrimEnd('\0');
            ValidateResourceName(name);
            if (!names.Add(name))
            {
                throw new DatabaseFormatException($"Resource name '{name}' occurs more than once.", path);
            }

            entries.Add(new ResEntry(
                index,
                name,
                resourceOffset,
                resourceSize,
                directoryOffset + entryOffset,
                nameLength,
                nameOffsetValue));
        }

        if (entries.Count == 0)
        {
            throw new DatabaseFormatException("RES не содержит файлов.", path);
        }

        var ordered = entries.OrderBy(entry => entry.Offset).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            ResEntry previous = ordered[index - 1];
            ResEntry current = ordered[index];
            if (previous.Size > 0 && current.Size > 0 &&
                (long)previous.Offset + previous.Size > current.Offset)
            {
                throw new DatabaseFormatException(
                    $"Resources '{previous.Name}' and '{current.Name}' overlap.",
                    current.Name,
                    current.Offset);
            }
        }

        var info = new FileInfo(path);
        info.Refresh();
        return new ArchiveIndex(path, stream.Length, info.LastWriteTimeUtc, directoryOffset, entries);
    }

    private static ResFileManagerSaveResult CreateResult(
        string targetPath,
        string sha256,
        MutationPlan plan,
        string? backupPath) =>
        new(
            targetPath,
            new FileInfo(targetPath).Length,
            sha256,
            plan.Resources.Count,
            plan.AddedCount,
            plan.ReplacedCount,
            plan.DeletedCount,
            backupPath);

    private static void ValidateKnownArchive(
        string path,
        string logicalFileName,
        CancellationToken cancellationToken)
    {
        _ = OpenArchiveIndex(path, cancellationToken);
        if (logicalFileName.Equals("database.res", StringComparison.OrdinalIgnoreCase))
        {
            _ = DatabaseDocument.Open(path);
        }
        else if (logicalFileName.Equals("texts.res", StringComparison.OrdinalIgnoreCase))
        {
            _ = TextsDocument.Open(path);
        }
    }

    private static void ValidateResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(character => character <= 0x7F && !char.IsControl(character)) ||
            name.Contains(':', StringComparison.Ordinal) ||
            name.StartsWith('\\'))
        {
            throw new DatabaseFormatException($"Имя ресурса '{name}' не является допустимым ASCII-путём.", name);
        }

        if (Encoding.ASCII.GetByteCount(name) > ushort.MaxValue)
        {
            throw new DatabaseFormatException("Имя ресурса слишком длинное.", name);
        }
    }

    private static int ComputeHash(string name, int count)
    {
        int sum = 0;
        foreach (char character in name)
        {
            sum += character is >= 'A' and <= 'Z' ? character + 32 : character;
        }

        return sum % count;
    }

    private static void CopyExactly(
        Stream source,
        Stream destination,
        int count,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(CopyBufferSize, Math.Max(1, count))];
        int remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Исходный ресурс обрезан.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        stream.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeSha256(
        Stream stream,
        int count,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Math.Min(CopyBufferSize, Math.Max(1, count))];
        int remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Проверяемый ресурс обрезан.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.SequentialScan);
        return ComputeSha256(stream, cancellationToken);
    }

    private static void EnsureFileHash(
        string path,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken)
    {
        if (!ComputeSha256(path, cancellationToken).Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new IOException($"{description} не прошёл проверку SHA-256.");
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new InvalidOperationException($"{description} проходит через символическую ссылку или junction.");
        }
    }

    private static void EnsureSafeCopyDestination(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для копии RES не существует.");
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Целевой файл уже существует. Безопасное сохранение не перезаписывает файлы.");
        }

        if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(path))
        {
            throw new InvalidOperationException("Сохранение копии не пишет в каталог игры. Выберите staging-папку.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new InvalidOperationException("Путь копии RES проходит через символическую ссылку или junction.");
        }
    }

    private static void EnsureSafeInPlaceTarget(string path, bool allowGameInstallationPath)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Открытый RES больше не существует.", path);
        }

        EnsureRegularFile(path, "Открытый RES");
        if (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(path))
        {
            throw new InvalidOperationException("Прямая запись RES в каталог игры требует отдельного подтверждения.");
        }
    }

    private static void RetainNewestManagedBackupBeforeSave(string sourcePath)
    {
        foreach (FileInfo obsolete in EnumerateManagedBackups(sourcePath).Skip(1))
        {
            try
            {
                EnsureRegularFile(obsolete.FullName, "Резервная копия");
                File.Delete(obsolete.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Не удалось удалить старую резервную копию '{obsolete.FullName}'. Сохранение отменено.",
                    exception);
            }
        }

        if (EnumerateManagedBackups(sourcePath).Count > 1)
        {
            throw new IOException("Не удалось безопасно сократить число резервных копий.");
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
        foreach (string candidate in Directory.EnumerateFileSystemEntries(directory, fileName + ".bak*"))
        {
            if (!pattern.IsMatch(Path.GetFileName(candidate)))
            {
                continue;
            }

            EnsureRegularFile(candidate, "Резервная копия");
            backups.Add(new FileInfo(candidate));
        }

        return backups
            .OrderByDescending(backup => backup.LastWriteTimeUtc)
            .ThenByDescending(backup => backup.CreationTimeUtc)
            .ThenByDescending(backup => backup.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private void CreateVerifiedBackup(
        FileStream source,
        string backupPath,
        CancellationToken cancellationToken)
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
            byte[] buffer = new byte[CopyBufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                backup.Write(buffer, 0, read);
            }

            backup.Flush(true);
        }

        source.Position = 0;
        EnsureFileHash(backupPath, Sha256, "Резервная копия исходного RES", cancellationToken);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class BuildResource(
        string name,
        ResEntry? sourceEntry,
        ResImportedFile? importedFile)
    {
        public string Name { get; } = name;

        public ResEntry? SourceEntry { get; } = sourceEntry;

        public ResImportedFile? ImportedFile { get; } = importedFile;

        public int NewOffset { get; set; }

        public int NewSize { get; set; }
    }

    private sealed record MutationPlan(
        IReadOnlyList<BuildResource> Resources,
        int AddedCount,
        int ReplacedCount,
        int DeletedCount);

    private sealed record ArchiveIndex(
        string SourcePath,
        long FileLength,
        DateTime LastWriteTimeUtc,
        int DirectoryOffset,
        IReadOnlyList<ResEntry> Entries);
}
