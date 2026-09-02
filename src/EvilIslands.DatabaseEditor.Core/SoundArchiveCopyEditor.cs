using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record SoundImportAsset(
    string SourcePath,
    string FileName,
    string Format,
    ReadOnlyMemory<byte> Data,
    string Sha256);

public sealed record BriefingSoundImportTarget(
    SoundArchiveInfo Archive,
    string BriefingKey,
    int PhraseNumber,
    string ResourceName,
    bool IsReplacement,
    SoundResourceInfo? ExistingResource);

public sealed record SoundArchiveCopyResult(
    string TargetPath,
    long FileLength,
    string Sha256,
    string ResourceName,
    bool IsReplacement,
    int ResourceCount,
    bool ReopenedSuccessfully,
    string? BackupPath = null);

public static class SoundArchiveCopyEditor
{
    private const int CopyBufferSize = 1024 * 1024;

    public static async Task<SoundImportAsset> LoadImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Выбранный звуковой файл не найден.", fullPath);
        }

        string format = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
        if (format is not ("MP3" or "WAV"))
        {
            throw new InvalidOperationException("Для Брифингов можно импортировать только MP3 или WAV.");
        }

        byte[] data = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Звуковой файл пуст.");
        }

        ValidateAudioSignature(data, format);
        return new SoundImportAsset(
            fullPath,
            Path.GetFileName(fullPath),
            format,
            data,
            Convert.ToHexString(SHA256.HashData(data)));
    }

    public static BriefingSoundImportTarget ResolveBriefingTarget(
        SoundArchiveCatalog catalog,
        string briefingKey,
        int phraseNumber,
        string format,
        SoundResourceInfo? existingResource = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(briefingKey);
        if (phraseNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(phraseNumber), "Номер реплики не может быть отрицательным.");
        }

        string normalizedFormat = format.Trim().TrimStart('.').ToUpperInvariant();
        if (normalizedFormat is not ("MP3" or "WAV"))
        {
            throw new InvalidOperationException("Для Брифингов можно импортировать только MP3 или WAV.");
        }

        string normalizedKey = NormalizeKey(briefingKey);
        if (existingResource is not null)
        {
            if (!catalog.Resources.Contains(existingResource))
            {
                throw new InvalidOperationException("Выбранный аудиоресурс не принадлежит подключённому каталогу.");
            }

            if (!existingResource.Format.Equals(normalizedFormat, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Существующий файл имеет формат {existingResource.Format}. Для безопасной замены выберите {existingResource.Format}, а не {normalizedFormat}.");
            }

            return new BriefingSoundImportTarget(
                existingResource.Archive,
                normalizedKey,
                phraseNumber,
                existingResource.ResourceName,
                true,
                existingResource);
        }

        SoundResourceInfo[] sceneResources = catalog.FindBriefing(normalizedKey)
            .Where(resource => resource.ResourceName.Contains("\\Briefing\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        SoundArchiveInfo? archive = sceneResources.FirstOrDefault()?.Archive;
        string? directory = sceneResources
            .Select(resource => GetResourceDirectory(resource.ResourceName))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (archive is null || directory is null)
        {
            SoundResourceInfo? briefingAnchor = catalog.Resources
                .Where(resource => resource.ResourceName.Contains("\\Briefing\\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(resource => resource.Archive.FileName.Equals("speech.res", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(resource => resource.Archive.Priority)
                .FirstOrDefault();
            archive = briefingAnchor?.Archive ?? catalog.Archives
                .OrderBy(candidate => candidate.FileName.Equals("speech.res", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(candidate => candidate.Priority)
                .FirstOrDefault();
            if (archive is null)
            {
                throw new InvalidOperationException("Не подключён ни один звуковой RES для записи копии.");
            }

            string prefix = briefingAnchor is null
                ? "s\\"
                : GetBriefingRootPrefix(briefingAnchor.ResourceName);
            directory = $"{prefix}Briefing\\{normalizedKey}";
        }

        string resourceName = $"{directory.TrimEnd('\\')}\\{phraseNumber}.{normalizedFormat.ToLowerInvariant()}";
        if (catalog.Resources.Any(resource =>
                resource.Archive.SourcePath.Equals(archive.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                resource.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"В {archive.FileName} уже существует ресурс {resourceName}. Обновите каталог звуков или выберите найденный файл для замены.");
        }

        ValidateResourceName(resourceName);
        return new BriefingSoundImportTarget(
            archive,
            normalizedKey,
            phraseNumber,
            resourceName,
            false,
            null);
    }

    public static Task<SoundArchiveCopyResult> SaveCopyAsync(
        BriefingSoundImportTarget target,
        SoundImportAsset asset,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCopy(target, asset, outputPath),
            CancellationToken.None);
    }

    public static Task<SoundArchiveCopyResult> SaveInPlaceAsync(
        BriefingSoundImportTarget target,
        SoundImportAsset asset,
        bool allowGameInstallationPath = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveInPlace(target, asset, allowGameInstallationPath),
            CancellationToken.None);
    }

    private static SoundArchiveCopyResult SaveCopy(
        BriefingSoundImportTarget target,
        SoundImportAsset asset,
        string outputPath)
    {
        string sourcePath = Path.GetFullPath(target.Archive.SourcePath);
        string destinationPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Исходный звуковой RES заблокирован от перезаписи. Сохраните новую копию.");
        }

        EnsureSafeDestination(destinationPath);
        ValidateResourceName(target.ResourceName);
        ValidateAudioSignature(asset.Data.Span, asset.Format);

        string directory = Path.GetDirectoryName(destinationPath)!;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.partial-{Guid.NewGuid():N}");
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
            FileInfo sourceInfo = new(sourcePath);
            sourceInfo.Refresh();
            if (sourceInfo.Length != target.Archive.FileLength ||
                sourceInfo.LastWriteTimeUtc != target.Archive.LastWriteTimeUtc)
            {
                throw new IOException(
                    "Исходный звуковой RES изменился после подключения. Переподключите звуки и повторите импорт.");
            }

            ArchiveIndex sourceIndex = ReadArchiveIndex(sourcePath, source);
            BuildResource? existing = sourceIndex.Entries
                .Where(entry => entry.Name.Equals(target.ResourceName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new BuildResource(entry.Name, entry, null))
                .FirstOrDefault();
            if (target.IsReplacement && existing is null)
            {
                throw new IOException("Заменяемый звуковой ресурс исчез из исходного RES после подключения.");
            }

            if (!target.IsReplacement && existing is not null)
            {
                throw new IOException("Добавляемый звуковой ресурс уже появился в исходном RES. Переподключите каталог.");
            }

            var resources = sourceIndex.Entries
                .OrderBy(entry => entry.Offset)
                .Select(entry => entry.Name.Equals(target.ResourceName, StringComparison.OrdinalIgnoreCase)
                    ? new BuildResource(entry.Name, null, asset.Data)
                    : new BuildResource(entry.Name, entry, null))
                .ToList();
            if (!target.IsReplacement)
            {
                resources.Add(new BuildResource(target.ResourceName, null, asset.Data));
            }

            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                WriteArchive(source, output, resources);
                output.Flush(true);
            }

            ArchiveIndex verified = OpenArchiveIndex(tempPath);
            VerifyResult(tempPath, verified, resources, target, asset);
            VerifyUnchangedResources(source, tempPath, verified, resources);
            string outputSha256;
            using (var resultStream = File.OpenRead(tempPath))
            {
                outputSha256 = Convert.ToHexString(SHA256.HashData(resultStream));
            }

            EnsureSafeDestination(destinationPath);
            File.Move(tempPath, destinationPath);
            long resultLength = new FileInfo(destinationPath).Length;
            return new SoundArchiveCopyResult(
                destinationPath,
                resultLength,
                outputSha256,
                target.ResourceName,
                target.IsReplacement,
                verified.Entries.Count,
                true);
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

    private static SoundArchiveCopyResult SaveInPlace(
        BriefingSoundImportTarget target,
        SoundImportAsset asset,
        bool allowGameInstallationPath)
    {
        string sourcePath = Path.GetFullPath(target.Archive.SourcePath);
        EnsureSafeInPlaceTarget(sourcePath, allowGameInstallationPath);
        ValidateResourceName(target.ResourceName);
        ValidateAudioSignature(asset.Data.Span, asset.Format);

        string directory = Path.GetDirectoryName(sourcePath)!;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(sourcePath)}.partial-{Guid.NewGuid():N}");
        string? backupPath = null;
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);
            EnsureSourceMetadataUnchanged(sourcePath, target.Archive);
            string sourceSha256 = ComputeSha256(source);
            ArchiveIndex sourceIndex = ReadArchiveIndex(sourcePath, source);
            List<BuildResource> resources = BuildResources(sourceIndex, target, asset);

            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                WriteArchive(source, output, resources);
                output.Flush(true);
            }

            ArchiveIndex verified = OpenArchiveIndex(tempPath);
            VerifyResult(tempPath, verified, resources, target, asset);
            VerifyUnchangedResources(source, tempPath, verified, resources);
            string outputSha256 = ComputeSha256(tempPath);

            EnsureSafeInPlaceTarget(sourcePath, allowGameInstallationPath);
            EnsureSourceMetadataUnchanged(sourcePath, target.Archive);
            if (!ComputeSha256(sourcePath).Equals(sourceSha256, StringComparison.Ordinal))
            {
                throw new IOException(
                    "Исходный звуковой RES изменился после подключения. Сохранение отменено до замены файла.");
            }

            RetainNewestManagedBackupBeforeSave(sourcePath);
            backupPath = CreateAvailableBackupPath(sourcePath);
            CreateVerifiedBackup(source, sourceSha256, backupPath);
            if (!ComputeSha256(sourcePath).Equals(sourceSha256, StringComparison.Ordinal))
            {
                throw new IOException(
                    "Исходный звуковой RES изменился перед заменой. Сохранение отменено; проверенная backup-копия сохранена.");
            }

            File.Replace(tempPath, sourcePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            EnsureFileHash(backupPath, sourceSha256, "Резервная копия исходного звукового RES");
            EnsureFileHash(sourcePath, outputSha256, "Сохранённый звуковой RES");

            return new SoundArchiveCopyResult(
                sourcePath,
                new FileInfo(sourcePath).Length,
                outputSha256,
                target.ResourceName,
                target.IsReplacement,
                verified.Entries.Count,
                true,
                backupPath);
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

    private static List<BuildResource> BuildResources(
        ArchiveIndex sourceIndex,
        BriefingSoundImportTarget target,
        SoundImportAsset asset)
    {
        BuildResource? existing = sourceIndex.Entries
            .Where(entry => entry.Name.Equals(target.ResourceName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new BuildResource(entry.Name, entry, null))
            .FirstOrDefault();
        if (target.IsReplacement && existing is null)
        {
            throw new IOException("Заменяемый звуковой ресурс исчез из исходного RES после подключения.");
        }

        if (!target.IsReplacement && existing is not null)
        {
            throw new IOException("Добавляемый звуковой ресурс уже появился в исходном RES. Переподключите каталог.");
        }

        var resources = sourceIndex.Entries
            .OrderBy(entry => entry.Offset)
            .Select(entry => entry.Name.Equals(target.ResourceName, StringComparison.OrdinalIgnoreCase)
                ? new BuildResource(entry.Name, null, asset.Data)
                : new BuildResource(entry.Name, entry, null))
            .ToList();
        if (!target.IsReplacement)
        {
            resources.Add(new BuildResource(target.ResourceName, null, asset.Data));
        }

        return resources;
    }

    private static void WriteArchive(
        FileStream source,
        FileStream output,
        IReadOnlyList<BuildResource> resources)
    {
        if (resources.Count == 0)
        {
            throw new DatabaseFormatException("Звуковой RES не может быть пустым.");
        }

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
            resource.NewOffset = checked((int)output.Position);
            if (resource.ImportedData is ReadOnlyMemory<byte> imported)
            {
                output.Write(imported.Span);
                resource.NewSize = imported.Length;
            }
            else if (resource.SourceEntry is ResEntry entry)
            {
                source.Position = entry.Offset;
                CopyExactly(source, output, entry.Size);
                resource.NewSize = entry.Size;
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
        int namePoolSize = checked((int)(end - directoryOffset - resources.Count * ResArchiveDocument.EntrySize));
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)namePoolSize));
        output.Write(header);
        output.Position = end;
    }

    private static void WriteDirectory(FileStream output, IReadOnlyList<BuildResource> resources)
    {
        int count = resources.Count;
        var table = new BuildResource?[count];
        IGrouping<int, BuildResource>[] groups = resources
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

        var namePool = new MemoryStream();
        byte[] entryBytes = new byte[ResArchiveDocument.EntrySize];
        for (var index = 0; index < table.Length; index++)
        {
            BuildResource resource = table[index]
                ?? throw new DatabaseFormatException("Hash-таблица RES содержит пустую запись.");
            byte[] nameBytes = ResResourceNameCodec.Encode(resource.Name);
            int nameOffset = checked((int)namePool.Position);
            namePool.Write(nameBytes);

            Span<byte> entry = entryBytes;
            entry.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(entry, nextIndices[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint)resource.NewSize));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)resource.NewOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], checked((ushort)nameBytes.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[18..], checked((uint)nameOffset));
            output.Write(entry);
        }

        namePool.Position = 0;
        namePool.CopyTo(output);
    }

    private static void VerifyResult(
        string path,
        ArchiveIndex verified,
        IReadOnlyList<BuildResource> expected,
        BriefingSoundImportTarget target,
        SoundImportAsset asset)
    {
        if (verified.Entries.Count != expected.Count)
        {
            throw new IOException("Повторная проверка звукового RES обнаружила неверное число ресурсов.");
        }

        string[] expectedNames = expected.Select(resource => resource.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actualNames = verified.Entries.Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!expectedNames.SequenceEqual(actualNames, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("Повторная проверка звукового RES обнаружила изменение каталога ресурсов.");
        }

        ResEntry importedEntry = verified.Entries.Single(entry =>
            entry.Name.Equals(target.ResourceName, StringComparison.OrdinalIgnoreCase));
        if (importedEntry.Size != asset.Data.Length)
        {
            throw new IOException("Размер импортированного звука в проверенной копии не совпал с исходным файлом.");
        }

        using var stream = File.OpenRead(path);
        stream.Position = importedEntry.Offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Math.Min(CopyBufferSize, Math.Max(1, importedEntry.Size))];
        int remaining = importedEntry.Size;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Импортированный звуковой ресурс обрезан.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Импортированный звук не прошёл проверку SHA-256 после записи в RES.");
        }
    }

    private static void VerifyUnchangedResources(
        FileStream source,
        string rebuiltPath,
        ArchiveIndex rebuiltIndex,
        IReadOnlyList<BuildResource> expected)
    {
        Dictionary<string, ResEntry> rebuiltByName = rebuiltIndex.Entries.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        using var rebuilt = new FileStream(
            rebuiltPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.SequentialScan);
        byte[] sourceBuffer = new byte[CopyBufferSize];
        byte[] rebuiltBuffer = new byte[CopyBufferSize];
        foreach (BuildResource resource in expected)
        {
            if (resource.SourceEntry is not ResEntry sourceEntry || resource.ImportedData is not null)
            {
                continue;
            }

            ResEntry rebuiltEntry = rebuiltByName[resource.Name];
            if (sourceEntry.Size != rebuiltEntry.Size)
            {
                throw new IOException($"Неизменённый ресурс '{resource.Name}' изменил размер после сборки RES.");
            }

            source.Position = sourceEntry.Offset;
            rebuilt.Position = rebuiltEntry.Offset;
            int remaining = sourceEntry.Size;
            while (remaining > 0)
            {
                int blockSize = Math.Min(remaining, sourceBuffer.Length);
                source.ReadExactly(sourceBuffer.AsSpan(0, blockSize));
                rebuilt.ReadExactly(rebuiltBuffer.AsSpan(0, blockSize));
                if (!sourceBuffer.AsSpan(0, blockSize).SequenceEqual(rebuiltBuffer.AsSpan(0, blockSize)))
                {
                    throw new IOException($"Неизменённый ресурс '{resource.Name}' отличается после сборки RES.");
                }

                remaining -= blockSize;
            }
        }
    }

    private static ArchiveIndex OpenArchiveIndex(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.RandomAccess);
        return ReadArchiveIndex(path, stream);
    }

    private static ArchiveIndex ReadArchiveIndex(string path, FileStream stream)
    {
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
        if (countValue == 0 || countValue > 1_000_000 || directoryValue > int.MaxValue || namePoolValue > int.MaxValue)
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
            int entryOffset = index * ResArchiveDocument.EntrySize;
            ReadOnlySpan<byte> entry = catalog.AsSpan(entryOffset, ResArchiveDocument.EntrySize);
            uint sizeValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint dataOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[16..]);
            uint nameOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[18..]);
            if (dataOffsetValue == 0 || nameLength == 0 ||
                sizeValue > int.MaxValue || dataOffsetValue > int.MaxValue)
            {
                throw new DatabaseFormatException("RES catalog contains an empty or unsupported entry.", path, directoryOffset + entryOffset);
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
            string name = ResResourceNameCodec.Decode(catalog.AsSpan(nameStart, nameLength)).TrimEnd('\0');
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

        return new ArchiveIndex(path, stream.Length, directoryOffset, entries);
    }

    private static void CopyExactly(Stream source, Stream destination, int count)
    {
        byte[] buffer = new byte[Math.Min(CopyBufferSize, Math.Max(1, count))];
        int remaining = count;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Исходный звуковой ресурс обрезан.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void EnsureSafeDestination(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для копии звукового RES не существует.");
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Целевой файл уже существует. Безопасное сохранение не перезаписывает файлы.");
        }

        if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(path))
        {
            throw new InvalidOperationException(
                "Первая версия записи звука не пишет в каталог игры. Сохраните новый RES в staging-папку.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new InvalidOperationException(
                "Путь копии звукового RES проходит через символическую ссылку или junction.");
        }
    }

    private static void EnsureSafeInPlaceTarget(string path, bool allowGameInstallationPath)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("Подключённый звуковой RES больше не существует.", fullPath);
        }

        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            DatabaseSafetyPolicy.ContainsReparsePoint(fullPath))
        {
            throw new InvalidOperationException(
                "Путь звукового RES проходит через символическую ссылку или junction.");
        }

        if (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(fullPath))
        {
            throw new InvalidOperationException(
                "Прямая запись звукового RES в каталог игры требует отдельного подтверждения пользователя.");
        }
    }

    private static void EnsureSourceMetadataUnchanged(string sourcePath, SoundArchiveInfo archive)
    {
        var sourceInfo = new FileInfo(sourcePath);
        sourceInfo.Refresh();
        if (sourceInfo.Length != archive.FileLength || sourceInfo.LastWriteTimeUtc != archive.LastWriteTimeUtc)
        {
            throw new IOException(
                "Исходный звуковой RES изменился после подключения. Переподключите звуки и повторите импорт.");
        }
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        string result = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return result;
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

    private static void CreateVerifiedBackup(FileStream source, string sourceSha256, string backupPath)
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
        EnsureFileHash(backupPath, sourceSha256, "Резервная копия исходного звукового RES");
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

    private static void RetainNewestManagedBackupBeforeSave(string sourcePath)
    {
        IReadOnlyList<FileInfo> backups = EnumerateManagedBackups(sourcePath);
        foreach (FileInfo obsolete in backups.Skip(1))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(obsolete.FullName);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException($"Старая резервная копия имеет небезопасный тип: {obsolete.FullName}");
                }

                File.Delete(obsolete.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Не удалось удалить старую резервную копию '{obsolete.FullName}'. " +
                    "Сохранение отменено до изменения звукового RES.",
                    exception);
            }
        }

        if (EnumerateManagedBackups(sourcePath).Count > 1)
        {
            throw new IOException(
                "Не удалось сократить число резервных копий. Сохранение отменено до изменения звукового RES.");
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateManagedBackups(string sourcePath)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string directoryPath = Path.GetDirectoryName(fullSourcePath)
            ?? throw new DirectoryNotFoundException("Не найдена папка подключённого звукового RES.");
        string sourceFileName = Path.GetFileName(fullSourcePath);
        var backupNamePattern = new Regex(
            $"^{Regex.Escape(sourceFileName)}\\.bak(?:\\.\\d{{8}}-\\d{{6}}(?:-\\d{{3}})?|\\.[0-9a-f]{{32}})?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var backups = new List<FileInfo>();
        foreach (string candidatePath in Directory.EnumerateFileSystemEntries(directoryPath, sourceFileName + ".bak*"))
        {
            string candidateName = Path.GetFileName(candidatePath);
            if (!backupNamePattern.IsMatch(candidateName))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(candidatePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException($"Резервная копия имеет небезопасный тип: {candidatePath}");
            }

            backups.Add(new FileInfo(candidatePath));
        }

        return backups
            .OrderByDescending(backup => backup.LastWriteTimeUtc)
            .ThenByDescending(backup => backup.CreationTimeUtc)
            .ThenByDescending(backup => backup.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateAudioSignature(ReadOnlySpan<byte> data, string format)
    {
        if (format.Equals("WAV", StringComparison.OrdinalIgnoreCase))
        {
            if (data.Length < 12 ||
                !data[..4].SequenceEqual("RIFF"u8) ||
                !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidOperationException("Выбранный WAV не содержит заголовок RIFF/WAVE.");
            }

            return;
        }

        if (!format.Equals("MP3", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Поддерживается импорт только MP3 или WAV.");
        }

        bool hasId3 = data.Length >= 3 && data[..3].SequenceEqual("ID3"u8);
        bool hasFrame = false;
        int probeLength = Math.Min(data.Length - 1, 64 * 1024);
        for (var index = 0; index < probeLength; index++)
        {
            if (data[index] == 0xFF && (data[index + 1] & 0xE0) == 0xE0)
            {
                hasFrame = true;
                break;
            }
        }

        if (!hasId3 && !hasFrame)
        {
            throw new InvalidOperationException("Выбранный MP3 не содержит заголовок ID3 или MPEG-аудиокадр.");
        }
    }

    private static void ValidateResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(character => character <= 0x7F && !char.IsControl(character)))
        {
            throw new DatabaseFormatException($"Имя ресурса '{name}' не является допустимым ASCII-путём.", name);
        }

        if (ResResourceNameCodec.Encode(name).Length > ushort.MaxValue)
        {
            throw new DatabaseFormatException("Имя ресурса слишком длинное.", name);
        }
    }

    private static string NormalizeKey(string value)
    {
        string normalized = value.Trim().Replace(' ', '_').Replace('/', '\\').Trim('\\');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ключ Брифинга не подходит для имени звукового ресурса.");
        }

        return normalized;
    }

    private static string? GetResourceDirectory(string resourceName)
    {
        int separator = resourceName.LastIndexOf('\\');
        return separator > 0 ? resourceName[..separator] : null;
    }

    private static string GetBriefingRootPrefix(string resourceName)
    {
        int marker = resourceName.IndexOf("Briefing\\", StringComparison.OrdinalIgnoreCase);
        return marker <= 0 ? string.Empty : resourceName[..marker];
    }

    private static int ComputeHash(string name, int count)
    {
        return ResResourceNameCodec.ComputeHash(name, count);
    }

    private sealed class BuildResource(
        string name,
        ResEntry? sourceEntry,
        ReadOnlyMemory<byte>? importedData)
    {
        public string Name { get; } = name;

        public ResEntry? SourceEntry { get; } = sourceEntry;

        public ReadOnlyMemory<byte>? ImportedData { get; } = importedData;

        public int NewOffset { get; set; }

        public int NewSize { get; set; }
    }

    private sealed record ArchiveIndex(
        string SourcePath,
        long FileLength,
        int DirectoryOffset,
        IReadOnlyList<ResEntry> Entries);
}
