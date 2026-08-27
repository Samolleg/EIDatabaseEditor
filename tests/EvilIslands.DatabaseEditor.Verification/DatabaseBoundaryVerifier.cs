using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record DatabaseBoundarySummary(
    string ResourceName,
    int TopLevelChunks,
    int Sections,
    int Records,
    int Fields);

internal sealed record DatabaseRawField(
    byte Id,
    int HeaderSize,
    byte[] Payload);

internal sealed record DatabaseRawRecord(
    string ResourceName,
    byte SectionId,
    int RecordIndex,
    int HeaderSize,
    IReadOnlyList<DatabaseRawField> Fields);

internal static class DatabaseBoundaryVerifier
{
    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db",
        ".idb",
        ".ldb",
        ".pdb",
        ".qdb",
        ".sdb",
        ".udb",
    };

    public static IReadOnlyList<DatabaseBoundarySummary> Verify(ResArchiveDocument archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var summaries = new List<DatabaseBoundarySummary>();
        foreach (ResEntry entry in archive.Entries.Where(IsDatabaseResource))
        {
            ReadOnlySpan<byte> data = archive.GetResourceBytes(entry).Span;
            IReadOnlyList<ChunkBoundary> topLevel = ReadLevel(data, 0, data.Length, entry.Name);
            ChunkBoundary? mainData = null;
            foreach (ChunkBoundary chunk in topLevel)
            {
                if (chunk.Id == 1)
                {
                    mainData = chunk;
                    break;
                }
            }

            if (mainData is null)
            {
                throw new VerificationException($"{entry.Name}: отсутствует корневой чанк 1.");
            }

            IReadOnlyList<ChunkBoundary> sections = ReadLevel(
                data,
                mainData.Value.DataOffset,
                mainData.Value.EndOffset,
                $"{entry.Name}/data");

            var recordCount = 0;
            var fieldCount = 0;
            foreach (ChunkBoundary section in sections)
            {
                IReadOnlyList<ChunkBoundary> records = ReadLevel(
                    data,
                    section.DataOffset,
                    section.EndOffset,
                    $"{entry.Name}/section:{section.Id}");
                recordCount += records.Count;

                foreach (ChunkBoundary record in records)
                {
                    IReadOnlyList<ChunkBoundary> fields = ReadLevel(
                        data,
                        record.DataOffset,
                        record.EndOffset,
                        $"{entry.Name}/section:{section.Id}/record:{recordCount}");
                    fieldCount += fields.Count;
                }
            }

            summaries.Add(new(
                entry.Name,
                topLevel.Count,
                sections.Count,
                recordCount,
                fieldCount));
        }

        if (summaries.Count == 0)
        {
            throw new VerificationException("В RES не найдено ни одной внутренней базы.");
        }

        return summaries;
    }

    public static DatabaseRawRecord CaptureRecord(
        ResArchiveDocument archive,
        string resourceName,
        byte sectionId,
        int recordIndex)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentOutOfRangeException.ThrowIfNegative(recordIndex);

        if (!archive.TryGetEntry(resourceName, out ResEntry? entry) || entry is null)
        {
            throw new VerificationException($"В RES отсутствует ресурс {resourceName}.");
        }

        ReadOnlySpan<byte> data = archive.GetResourceBytes(entry).Span;
        ChunkBoundary[] roots = ReadLevel(data, 0, data.Length, resourceName)
            .Where(chunk => chunk.Id == 1)
            .ToArray();
        if (roots.Length != 1)
        {
            throw new VerificationException(
                $"{resourceName}: ожидался один корневой чанк 1, найдено {roots.Length}.");
        }

        ChunkBoundary[] sections = ReadLevel(
                data,
                roots[0].DataOffset,
                roots[0].EndOffset,
                $"{resourceName}/data")
            .Where(chunk => chunk.Id == sectionId)
            .ToArray();
        if (sections.Length != 1)
        {
            throw new VerificationException(
                $"{resourceName}: ожидалась одна секция {sectionId}, найдено {sections.Length}.");
        }

        ChunkBoundary[] records = ReadLevel(
                data,
                sections[0].DataOffset,
                sections[0].EndOffset,
                $"{resourceName}/section:{sectionId}")
            .Where(chunk => chunk.Id == 1)
            .ToArray();
        if (recordIndex >= records.Length)
        {
            throw new VerificationException(
                $"{resourceName}/section:{sectionId}: индекс записи {recordIndex} вне диапазона 0..{records.Length - 1}.");
        }

        ChunkBoundary record = records[recordIndex];
        IReadOnlyList<ChunkBoundary> fieldChunks = ReadLevel(
            data,
            record.DataOffset,
            record.EndOffset,
            $"{resourceName}/section:{sectionId}/record:{recordIndex}");
        var fields = new DatabaseRawField[fieldChunks.Count];
        for (var index = 0; index < fieldChunks.Count; index++)
        {
            ChunkBoundary field = fieldChunks[index];
            fields[index] = new DatabaseRawField(
                field.Id,
                field.DataOffset - field.HeaderOffset,
                data.Slice(field.DataOffset, field.DataLength).ToArray());
        }

        return new DatabaseRawRecord(
            resourceName,
            sectionId,
            recordIndex,
            record.DataOffset - record.HeaderOffset,
            fields);
    }

    private static bool IsDatabaseResource(ResEntry entry) =>
        DatabaseExtensions.Contains(Path.GetExtension(entry.Name));

    private static IReadOnlyList<ChunkBoundary> ReadLevel(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        string path)
    {
        if ((uint)start > (uint)data.Length || end < start || end > data.Length)
        {
            throw new VerificationException($"{path}: недопустимый диапазон чанков {start}..{end}.");
        }

        var chunks = new List<ChunkBoundary>();
        var position = start;
        while (position < end)
        {
            int headerOffset = position;
            if (end - position < 2)
            {
                throw new VerificationException($"{path}: оборван заголовок чанка по адресу {position}.");
            }

            byte id = data[position++];
            uint encodedLength = data[position++];
            if ((encodedLength & 1) != 0)
            {
                if (end - position < 3)
                {
                    throw new VerificationException(
                        $"{path}: оборвана расширенная длина чанка по адресу {headerOffset}.");
                }

                encodedLength |= (uint)data[position++] << 8;
                encodedLength |= (uint)data[position++] << 16;
                encodedLength |= (uint)data[position++] << 24;
            }

            uint lengthValue = encodedLength >> 1;
            if (lengthValue > int.MaxValue || (long)position + lengthValue > end)
            {
                throw new VerificationException(
                    $"{path}: чанк {id} по адресу {headerOffset} выходит за границу родителя.");
            }

            int length = (int)lengthValue;
            chunks.Add(new(id, headerOffset, position, length));
            position += length;
        }

        if (position != end)
        {
            throw new VerificationException($"{path}: список чанков завершился не на границе родителя.");
        }

        return chunks;
    }

    private readonly record struct ChunkBoundary(
        byte Id,
        int HeaderOffset,
        int DataOffset,
        int DataLength)
    {
        public int EndOffset => checked(DataOffset + DataLength);
    }
}
