namespace EvilIslands.DatabaseEditor.Core;

internal sealed class ParsedDatabaseResource
{
    private readonly byte[] _original;
    private readonly IReadOnlyList<ChunkSlice> _topChunks;
    private readonly ChunkSlice _root;
    private readonly IReadOnlyList<ChunkSlice> _rootChildren;
    private readonly Dictionary<byte, ParsedSection> _sections;

    private ParsedDatabaseResource(
        string resourceName,
        byte[] original,
        IReadOnlyList<ChunkSlice> topChunks,
        ChunkSlice root,
        IReadOnlyList<ChunkSlice> rootChildren,
        Dictionary<byte, ParsedSection> sections)
    {
        ResourceName = resourceName;
        _original = original;
        _topChunks = topChunks;
        _root = root;
        _rootChildren = rootChildren;
        _sections = sections;
    }

    public string ResourceName { get; }

    public bool IsModified => _sections.Values.Any(section => section.IsModified);

    public static ParsedDatabaseResource Parse(
        string resourceName,
        ReadOnlyMemory<byte> source,
        long absoluteResourceOffset,
        IReadOnlyList<SectionSchema> schemas,
        object editSync,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = source.ToArray();
        var span = original.AsSpan();
        var topChunks = ChunkCodec.ReadList(
            span,
            0,
            span.Length,
            resourceName,
            checked((int)absoluteResourceOffset),
            cancellationToken);
        var rootCandidates = topChunks.Where(chunk => chunk.Id == 1).ToArray();
        if (rootCandidates.Length != 1)
        {
            throw new DatabaseFormatException(
                $"Resource '{resourceName}' must contain exactly one root chunk.",
                resourceName,
                absoluteResourceOffset);
        }

        var root = rootCandidates[0];
        var rootChildren = ChunkCodec.ReadList(
            span,
            root.DataOffset,
            root.EndOffset,
            resourceName,
            checked((int)absoluteResourceOffset),
            cancellationToken);
        var schemaById = schemas.ToDictionary(schema => schema.Id);
        var sections = new Dictionary<byte, ParsedSection>();

        foreach (var sectionChunk in rootChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!schemaById.TryGetValue(sectionChunk.Id, out var schema))
            {
                continue;
            }

            if (sections.ContainsKey(sectionChunk.Id))
            {
                throw new DatabaseFormatException(
                    $"Resource '{resourceName}' contains duplicate section {sectionChunk.Id}.",
                    resourceName,
                    absoluteResourceOffset + sectionChunk.HeaderOffset);
            }

            var sectionChildren = ChunkCodec.ReadList(
                span,
                sectionChunk.DataOffset,
                sectionChunk.EndOffset,
                resourceName,
                checked((int)absoluteResourceOffset),
                cancellationToken);
            var records = new List<DatabaseRecord>();
            var byHeaderOffset = new Dictionary<int, DatabaseRecord>();
            foreach (var recordChunk in sectionChildren.Where(chunk => chunk.Id == 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordIndex = records.Count;
                var fieldChunks = ChunkCodec.ReadList(
                    span,
                    recordChunk.DataOffset,
                    recordChunk.EndOffset,
                    resourceName,
                    checked((int)absoluteResourceOffset),
                    cancellationToken);
                var fields = new List<DatabaseField>(fieldChunks.Count);
                foreach (var fieldChunk in fieldChunks)
                {
                    if ((fields.Count & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var fieldSchema = schema.Fields.TryGetValue(fieldChunk.Id, out var known)
                        ? known
                        : new FieldSchema(DatabaseValueKind.Hex, $"Поле {fieldChunk.Id}", false);
                    var fieldPath = $"{resourceName}/{schema.Name}[{recordIndex}]/field-{fieldChunk.Id}";
                    fields.Add(new DatabaseField(
                        fieldChunk.Id,
                        fieldSchema,
                        fieldPath,
                        absoluteResourceOffset + fieldChunk.DataOffset,
                        fieldChunk,
                        original,
                        editSync));
                }

                AddOptionalMaterialGraphics(resourceName, schema, recordIndex, fields, original, editSync);
                var displayName = fields.FirstOrDefault(field => field.Id == schema.NameFieldId)?.Value.ToString()
                    ?? $"Запись {recordIndex + 1}";
                var record = new DatabaseRecord(
                    resourceName,
                    schema.Id,
                    schema.Name,
                    recordIndex,
                    displayName,
                    schema.NameFieldId,
                    schema.IdentityFieldId,
                    fields,
                    recordChunk,
                    original);
                records.Add(record);
                byHeaderOffset.Add(recordChunk.HeaderOffset, record);
            }

            sections.Add(sectionChunk.Id, new ParsedSection(
                schema,
                sectionChunk,
                sectionChildren,
                records,
                byHeaderOffset));
        }

        return new ParsedDatabaseResource(resourceName, original, topChunks, root, rootChildren, sections);
    }

    private static void AddOptionalMaterialGraphics(string resourceName, SectionSchema schema, int recordIndex,
        List<DatabaseField> fields, byte[] backingData, object editSync)
    {
        if (!resourceName.Equals("items.idb", StringComparison.OrdinalIgnoreCase) || schema.Id != 4 ||
            fields.Any(f => f.Id == 28) || !schema.Fields.TryGetValue(28, out var fieldSchema)) return;
        // Expose the new optional setting in old databases without adding bytes
        // until the user supplies a value. HeaderOffset -1 marks an absent field.
        fields.Add(new DatabaseField(28, fieldSchema,
            $"{resourceName}/{schema.Name}[{recordIndex}]/field-28", -1,
            new ChunkSlice(28, -1, 0, 0, 0), backingData, editSync));
    }

    public IReadOnlyList<DatabaseRecord> GetRecords(byte sectionId) =>
        _sections.TryGetValue(sectionId, out var section) ? section.Records : [];

    public bool HasSection(byte sectionId) => _sections.ContainsKey(sectionId);

    public DatabaseRecord AppendClone(byte sectionId, DatabaseRecord template, object editSync, bool detachedHistory = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!_sections.TryGetValue(sectionId, out var section) ||
            (detachedHistory ? template.ResourceName != ResourceName || template.SectionId != sectionId
                : !section.Records.Any(record => ReferenceEquals(record, template))))
        {
            throw new ArgumentException("Строка-образец не принадлежит выбранной таблице.", nameof(template));
        }

        if (!section.Schema.CanAppendRecords)
        {
            throw new InvalidOperationException("Для этой таблицы добавление строк пока не прошло безопасную проверку.");
        }

        byte[] templatePayload = BuildRecordPayload(template);
        byte[] cloneBacking = ChunkCodec.Encode(template.Chunk.Id, templatePayload, template.Chunk.HeaderSize);
        ChunkSlice cloneChunk = ChunkCodec.ReadList(
                cloneBacking,
                0,
                cloneBacking.Length,
                $"{ResourceName}/{section.Schema.Name}/new-record")
            .Single(chunk => chunk.Id == template.Chunk.Id);
        IReadOnlyList<ChunkSlice> fieldChunks = ChunkCodec.ReadList(
            cloneBacking,
            cloneChunk.DataOffset,
            cloneChunk.EndOffset,
            $"{ResourceName}/{section.Schema.Name}/new-record");

        // RecordIndex is a physical zero-based index used by the game (and by the
        // material ID field), so an append after Undo must reuse the vacated tail
        // index instead of leaving a gap.
        int recordIndex = detachedHistory ? template.RecordIndex : section.Records.Count;
        var fields = new List<DatabaseField>(fieldChunks.Count);
        foreach (ChunkSlice fieldChunk in fieldChunks)
        {
            FieldSchema fieldSchema = section.Schema.Fields.TryGetValue(fieldChunk.Id, out FieldSchema? known)
                ? known
                : new FieldSchema(DatabaseValueKind.Hex, $"Поле {fieldChunk.Id}", false);
            fields.Add(new DatabaseField(
                fieldChunk.Id,
                fieldSchema,
                $"{ResourceName}/{section.Schema.Name}[{recordIndex}]/field-{fieldChunk.Id}",
                -1,
                fieldChunk,
                cloneBacking,
                editSync));
        }

        AddOptionalMaterialGraphics(ResourceName, section.Schema, recordIndex, fields, cloneBacking, editSync);
        string displayName = fields.FirstOrDefault(field => field.Id == section.Schema.NameFieldId)?.Value.ToString()
            ?? $"Запись {recordIndex + 1}";
        var clone = new DatabaseRecord(
            ResourceName,
            section.Schema.Id,
            section.Schema.Name,
            recordIndex,
            displayName,
            section.Schema.NameFieldId,
            section.Schema.IdentityFieldId,
            fields,
            cloneChunk,
            cloneBacking,
            isNew: true);

        if (!detachedHistory && section.Schema.Key.Equals("items.materials", StringComparison.Ordinal) &&
            fields.FirstOrDefault(field => field.Id == 3) is { IsEditable: true } materialId)
        {
            if (!materialId.TrySetValue(recordIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), out string? error))
            {
                throw new InvalidOperationException(error ?? "Не удалось назначить индекс новому материалу.");
            }
        }

        if (!detachedHistory)
        {
            section.Records.Add(clone);
            section.AddedRecords.Add(clone);
        }
        section.HistoryRecords.Add(clone);
        return clone;
    }

    public DatabaseRecord RebindHistoryRecord(DatabaseRecord previous, bool present, object editSync)
    {
        if (!present) return AppendClone(previous.SectionId, previous, editSync, detachedHistory: true);
        var section = _sections[previous.SectionId];
        var record = section.Records.Single(r => r.RecordIndex == previous.RecordIndex);
        section.HistoryRecords.Add(record);
        return record;
    }

    public void RemoveAddedRecord(DatabaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_sections.TryGetValue(record.SectionId, out var section) ||
            section.Records.Count == 0 || !ReferenceEquals(section.Records[^1], record) ||
            (!section.HistoryRecords.Contains(record) &&
             (!section.Schema.CanAppendRecords ||
              !section.OriginalRecordsByHeaderOffset.Values.Contains(record))))
        {
            throw new InvalidOperationException("Удалить можно только последнюю строку поддерживаемой таблицы: номера остальных записей должны сохраниться.");
        }

        // Original tail rows can be deleted too; register them for the same
        // presence history used by append Undo/Redo.
        section.HistoryRecords.Add(record);
        if (!section.AddedRecords.Remove(record)) section.RemovedOriginalRecords.Add(record);
        section.Records.RemoveAt(section.Records.Count - 1);
    }

    public void RestoreAddedRecord(DatabaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_sections.TryGetValue(record.SectionId, out var section) || !section.HistoryRecords.Contains(record))
        {
            throw new ArgumentException("Строка не является добавленной записью этой таблицы.", nameof(record));
        }

        if (section.Records.Any(candidate => ReferenceEquals(candidate, record)))
        {
            throw new InvalidOperationException("Строка уже присутствует в таблице.");
        }

        if (record.RecordIndex != section.Records.Count)
        {
            throw new InvalidOperationException("Добавленную строку можно восстановить только на её исходную позицию в конце таблицы.");
        }

        section.Records.Add(record);
        if (!section.RemovedOriginalRecords.Remove(record)) section.AddedRecords.Add(record);
    }

    public byte[] Rebuild()
    {
        if (!IsModified)
        {
            return _original;
        }

        var rootPayload = BuildRootPayload();
        var rebuiltRoot = ChunkCodec.Encode(_root.Id, rootPayload, _root.HeaderSize);
        using var output = new MemoryStream(checked(_original.Length + Math.Max(0, rebuiltRoot.Length - _root.TotalLength)));
        foreach (var chunk in _topChunks)
        {
            if (chunk.HeaderOffset == _root.HeaderOffset)
            {
                output.Write(rebuiltRoot);
            }
            else
            {
                WriteOriginalChunk(output, chunk);
            }
        }

        return output.ToArray();
    }

    private byte[] BuildRootPayload()
    {
        using var output = new MemoryStream(_root.DataLength);
        foreach (var child in _rootChildren)
        {
            if (_sections.TryGetValue(child.Id, out var section) && section.IsModified)
            {
                var payload = BuildSectionPayload(section);
                output.Write(ChunkCodec.Encode(child.Id, payload, child.HeaderSize));
            }
            else
            {
                WriteOriginalChunk(output, child);
            }
        }

        return output.ToArray();
    }

    private byte[] BuildSectionPayload(ParsedSection section)
    {
        using var output = new MemoryStream(section.Chunk.DataLength);
        int lastRecordHeaderOffset = section.OriginalRecordsByHeaderOffset.Count == 0
            ? -1
            : section.OriginalRecordsByHeaderOffset.Keys.Max();
        foreach (var child in section.Children)
        {
            section.OriginalRecordsByHeaderOffset.TryGetValue(child.HeaderOffset, out var record);
            if (record is not null && section.RemovedOriginalRecords.Contains(record))
            {
                // Keep the insertion boundary below even when the last original row is undone.
            }
            else if (record is not null && record.IsModified)
            {
                var payload = BuildRecordPayload(record);
                output.Write(ChunkCodec.Encode(child.Id, payload, child.HeaderSize));
            }
            else
            {
                WriteOriginalChunk(output, child);
            }

            if (child.HeaderOffset == lastRecordHeaderOffset)
            {
                WriteAddedRecords(output, section);
            }
        }

        return output.ToArray();
    }

    private void WriteAddedRecords(Stream output, ParsedSection section)
    {
        foreach (DatabaseRecord record in section.AddedRecords)
        {
            byte[] payload = BuildRecordPayload(record);
            output.Write(ChunkCodec.Encode(record.Chunk.Id, payload, record.Chunk.HeaderSize));
        }
    }

    private byte[] BuildRecordPayload(DatabaseRecord record)
    {
        var fieldsByOffset = record.Fields.Where(field => field.Chunk.HeaderOffset >= 0).ToDictionary(field => field.Chunk.HeaderOffset);
        byte[] backingData = record.BackingData;
        var chunks = ChunkCodec.ReadList(
            backingData,
            record.Chunk.DataOffset,
            record.Chunk.EndOffset,
            ResourceName);
        using var output = new MemoryStream(record.Chunk.DataLength);
        foreach (var chunk in chunks)
        {
            if (fieldsByOffset.TryGetValue(chunk.HeaderOffset, out var field) && field.IsModified)
            {
                output.Write(ChunkCodec.Encode(chunk.Id, field.Payload, chunk.HeaderSize));
            }
            else
            {
                output.Write(backingData.AsSpan(chunk.HeaderOffset, chunk.TotalLength));
            }
        }

        foreach (var field in record.Fields.Where(f => f.Chunk.HeaderOffset < 0 && f.IsModified))
            output.Write(ChunkCodec.Encode((byte)field.Id, field.Payload, 2));

        return output.ToArray();
    }

    private void WriteOriginalChunk(Stream destination, ChunkSlice chunk) =>
        destination.Write(_original.AsSpan(chunk.HeaderOffset, chunk.TotalLength));

    private sealed class ParsedSection(
        SectionSchema schema,
        ChunkSlice chunk,
        IReadOnlyList<ChunkSlice> children,
        List<DatabaseRecord> records,
        IReadOnlyDictionary<int, DatabaseRecord> originalRecordsByHeaderOffset)
    {
        public SectionSchema Schema { get; } = schema;

        public ChunkSlice Chunk { get; } = chunk;

        public IReadOnlyList<ChunkSlice> Children { get; } = children;

        public List<DatabaseRecord> Records { get; } = records;

        public List<DatabaseRecord> AddedRecords { get; } = [];
        public HashSet<DatabaseRecord> HistoryRecords { get; } = [];
        public HashSet<DatabaseRecord> RemovedOriginalRecords { get; } = [];

        public IReadOnlyDictionary<int, DatabaseRecord> OriginalRecordsByHeaderOffset { get; } =
            originalRecordsByHeaderOffset;

        public bool IsModified => AddedRecords.Count > 0 || RemovedOriginalRecords.Count > 0 ||
                                  OriginalRecordsByHeaderOffset.Values.Any(record => record.IsModified);
    }
}
