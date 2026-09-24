using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record DatabaseResourceDescriptor(
    int Index,
    string Name,
    string Kind,
    long Offset,
    long Length,
    bool IsSupported);

public enum DatabaseSupportState
{
    Editable,
    ReadOnly,
    Unavailable,
}

public sealed class DatabaseGroup
{
    internal DatabaseGroup(
        string key,
        string title,
        string resourceName,
        IReadOnlyList<DatabaseTable> tables,
        DatabaseSupportState supportState)
    {
        Key = key;
        Title = title;
        ResourceName = resourceName;
        Tables = tables;
        SupportState = supportState;
    }

    public string Key { get; }

    public string Title { get; }

    public string ResourceName { get; }

    public IReadOnlyList<DatabaseTable> Tables { get; }

    public DatabaseSupportState SupportState { get; }

    public bool IsEditable => SupportState == DatabaseSupportState.Editable;

    public bool IsAvailable => SupportState != DatabaseSupportState.Unavailable;

    public int RecordCount => Tables.Sum(table => table.RecordCount);
}

public sealed class DatabaseTable
{
    internal DatabaseTable(
        string key,
        string title,
        string resourceName,
        byte sectionId,
        IReadOnlyList<DatabaseRecord> records,
        DatabaseSupportState supportState,
        bool canAppendRecords)
    {
        Key = key;
        Title = title;
        ResourceName = resourceName;
        SectionId = sectionId;
        Records = records;
        SupportState = supportState;
        CanAppendRecords = canAppendRecords;
    }

    public string Key { get; }

    public string Title { get; }

    public string ResourceName { get; }

    public byte SectionId { get; }

    public IReadOnlyList<DatabaseRecord> Records { get; }

    public DatabaseSupportState SupportState { get; }

    public bool CanAppendRecords { get; }

    public bool IsEditable => SupportState == DatabaseSupportState.Editable;

    public bool IsAvailable => SupportState != DatabaseSupportState.Unavailable;

    public int RecordCount => Records.Count;
}

public sealed class UnitsDatabase
{
    internal UnitsDatabase(
        IReadOnlyList<DatabaseRecord> hitLocationTypes,
        IReadOnlyList<DatabaseRecord> races,
        IReadOnlyList<DatabaseRecord> prototypes,
        IReadOnlyList<DatabaseRecord> npcs)
    {
        HitLocationTypes = hitLocationTypes;
        Races = races;
        Prototypes = prototypes;
        Npcs = npcs;
    }

    public IReadOnlyList<DatabaseRecord> HitLocationTypes { get; }

    public IReadOnlyList<DatabaseRecord> Races { get; }

    public IReadOnlyList<DatabaseRecord> Prototypes { get; }

    public IReadOnlyList<DatabaseRecord> Npcs { get; }
}

public sealed class SpellsDatabase
{
    internal SpellsDatabase(
        IReadOnlyList<DatabaseRecord> spells,
        IReadOnlyList<DatabaseRecord> runes,
        IReadOnlyList<DatabaseRecord> complexSpells,
        IReadOnlyList<DatabaseRecord> complexArmors,
        IReadOnlyList<DatabaseRecord> complexWeapons)
    {
        Spells = spells;
        Runes = runes;
        ComplexSpells = complexSpells;
        ComplexArmors = complexArmors;
        ComplexWeapons = complexWeapons;
    }

    public IReadOnlyList<DatabaseRecord> Spells { get; }

    public IReadOnlyList<DatabaseRecord> Runes { get; }

    public IReadOnlyList<DatabaseRecord> ComplexSpells { get; }

    public IReadOnlyList<DatabaseRecord> ComplexArmors { get; }

    public IReadOnlyList<DatabaseRecord> ComplexWeapons { get; }
}

public sealed class DatabaseRecord
{
    // Detached display record. It is never inserted into a DatabaseDocument.
    public static DatabaseRecord CreateBriefingTextReference(string name, int referenceIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(referenceIndex);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] data = Encoding.GetEncoding(1251).GetBytes(name);
        var chunk = new ChunkSlice(0, 0, 0, 0, data.Length);
        var field = new DatabaseField(0, new FieldSchema(DatabaseValueKind.String, "Имя", Editable: false),
            $"texts.res/BRIEFING {name}", 0, chunk, data, new object());
        return new DatabaseRecord("texts.res", 2, "Брифинги", -1 - referenceIndex,
            name, 0, 0, [field], chunk, data);
    }

    private readonly string _fallbackName;
    private readonly byte _nameFieldId;

    internal DatabaseRecord(
        string resourceName,
        byte sectionId,
        string sectionName,
        int recordIndex,
        string name,
        byte nameFieldId,
        byte identityFieldId,
        IReadOnlyList<DatabaseField> fields,
        ChunkSlice chunk,
        byte[] backingData,
        bool isNew = false)
    {
        ResourceName = resourceName;
        SectionId = sectionId;
        SectionName = sectionName;
        RecordIndex = recordIndex;
        _fallbackName = string.IsNullOrWhiteSpace(name) ? $"Запись {recordIndex + 1}" : name;
        _nameFieldId = nameFieldId;
        IdentityFieldId = identityFieldId;
        Fields = fields;
        Chunk = chunk;
        BackingData = backingData;
        IsNew = isNew;
        Path = $"{resourceName}/{sectionName}[{recordIndex}]";
    }

    public string ResourceName { get; }

    public byte SectionId { get; }

    public string SectionName { get; }

    public int RecordIndex { get; }

    public int NameFieldId => _nameFieldId;

    public int IdentityFieldId { get; }

    public string Name => Fields.FirstOrDefault(field => field.Id == _nameFieldId)?.Value.ToString()
        ?? _fallbackName;

    public string DisplayName => Name;

    public string IdentityValue => Fields.FirstOrDefault(field => field.Id == IdentityFieldId)?.Value.ToString()
        ?? string.Empty;

    public string Path { get; }

    public IReadOnlyList<DatabaseField> Fields { get; }

    public bool IsNew { get; }

    public bool IsModified => IsNew || Fields.Any(field => field.IsModified);

    public string? GetString(string key) => Fields.FirstOrDefault(field =>
        string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(field.DisplayName, key, StringComparison.OrdinalIgnoreCase))?.Value.ToString();

    internal ChunkSlice Chunk { get; }

    internal byte[] BackingData { get; }
}

public sealed class DatabaseFieldState
{
    private readonly DatabaseField _owner;
    private readonly byte[] _payload;

    internal DatabaseFieldState(DatabaseField owner, ReadOnlySpan<byte> payload)
    {
        _owner = owner;
        _payload = payload.ToArray();
    }

    public bool IsEquivalentTo(DatabaseFieldState? other) =>
        other is not null &&
        ReferenceEquals(_owner, other._owner) &&
        _payload.AsSpan().SequenceEqual(other._payload);

    internal void EnsureOwner(DatabaseField field)
    {
        if (!ReferenceEquals(_owner, field))
        {
            throw new ArgumentException("Снимок принадлежит другому полю.", nameof(field));
        }
    }

    internal ReadOnlySpan<byte> Payload => _payload;
    public DatabaseFieldState Rebind(DatabaseField owner) => new(owner, _payload);
}

public sealed class DatabaseField
{
    private readonly FieldSchema _schema;
    private readonly object _editSync;
    private readonly byte[] _resourceData;
    private readonly int _payloadOffset;
    private readonly int _payloadLength;
    private byte[]? _editedPayload;
    private object? _originalValue;
    private bool _originalValueDecoded;

    internal DatabaseField(
        byte id,
        FieldSchema schema,
        string path,
        long absoluteOffset,
        ChunkSlice chunk,
        byte[] resourceData,
        object editSync)
    {
        Id = id;
        _schema = schema;
        Path = path;
        Offset = absoluteOffset;
        Length = chunk.DataLength;
        Chunk = chunk;
        _resourceData = resourceData;
        _editSync = editSync;
        _payloadOffset = chunk.DataOffset;
        _payloadLength = chunk.DataLength;
    }

    public int Id { get; }

    public string Key => _schema.Key ?? $"field-{Id}";

    public string DisplayName => _schema.DisplayName;

    public string Kind => _schema.Kind.ToString();

    public IReadOnlyList<DatabaseFieldProjection> Projections => _schema.Projections;

    public int DisplayOrder => _schema.DisplayOrder;

    public bool ShowInReadableView => _schema.ShowInReadableView;

    public object Value
    {
        get
        {
            lock (_editSync)
            {
                return _editedPayload is null
                    ? OriginalValue
                    : DatabaseValueCodec.DecodeValue(_schema, _editedPayload);
            }
        }
    }

    public object OriginalValue
    {
        get
        {
            lock (_editSync)
            {
                if (!_originalValueDecoded)
                {
                    _originalValue = DatabaseValueCodec.DecodeValue(_schema, OriginalPayload);
                    _originalValueDecoded = true;
                }

                return _originalValue!;
            }
        }
    }

    public string EditorText
    {
        get
        {
            lock (_editSync)
            {
                return DatabaseValueCodec.GetEditorText(_schema, Payload);
            }
        }
    }

    public string OriginalEditorText
    {
        get
        {
            lock (_editSync)
            {
                return DatabaseValueCodec.GetEditorText(_schema, OriginalPayload);
            }
        }
    }

    public bool IsEditable => _schema.Editable && DatabaseValueCodec.CanEdit(_schema, OriginalPayload);

    public bool IsModified
    {
        get
        {
            lock (_editSync)
            {
                return _editedPayload is not null;
            }
        }
    }

    public string Path { get; }

    public long Offset { get; }

    public long Length { get; }

    public object? GetProjectedValue(DatabaseFieldProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_editSync)
        {
            return DatabaseValueCodec.DecodeProjection(_schema, Payload, projection);
        }
    }

    public bool CanEditProjection(DatabaseFieldProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_editSync)
        {
            if (!IsEditable || !_schema.Projections.Contains(projection))
            {
                return false;
            }

            return projection.Kind switch
            {
                DatabaseFieldProjectionKind.Bit =>
                    projection.Index is >= 0 and < 32 &&
                    _schema.Kind == DatabaseValueKind.UInt32 &&
                    Payload.Length == sizeof(uint),
                DatabaseFieldProjectionKind.ListElement =>
                    projection.Index >= 0 &&
                    DatabaseValueCodec.TryGetPrimitiveListCount(_schema, Payload, out int count) &&
                    projection.Index < count,
                DatabaseFieldProjectionKind.NestedField =>
                    DatabaseValueCodec.CanEditNestedField(_schema, Payload, projection.Index),
                DatabaseFieldProjectionKind.PackedField =>
                    DatabaseValueCodec.CanEditPackedField(_schema, Payload, projection),
                DatabaseFieldProjectionKind.EnumValue =>
                    DatabaseValueCodec.CanEditEnumValue(_schema, Payload, projection),
                _ => false,
            };
        }
    }

    public bool TrySetProjectedValue(
        DatabaseFieldProjection projection,
        string value,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_editSync)
        {
            error = null;
            if (!CanEditProjection(projection))
            {
                error = "Эта расшифрованная ячейка доступна только для чтения.";
                return false;
            }

            if (projection.Kind == DatabaseFieldProjectionKind.ListElement)
            {
                if (!DatabaseValueCodec.TryEncodePrimitiveListElement(
                        _schema,
                        Payload,
                        projection.Index,
                        value,
                        out byte[] encodedList,
                        out error))
                {
                    return false;
                }

                _editedPayload = encodedList.AsSpan().SequenceEqual(OriginalPayload) ? null : encodedList;
                return true;
            }

            if (projection.Kind == DatabaseFieldProjectionKind.NestedField)
            {
                if (!DatabaseValueCodec.TryEncodeNestedField(
                        _schema,
                        Payload,
                        projection.Index,
                        value,
                        out byte[] encodedStructure,
                        out error))
                {
                    return false;
                }

                _editedPayload = encodedStructure.AsSpan().SequenceEqual(OriginalPayload)
                    ? null
                    : encodedStructure;
                return true;
            }

            if (projection.Kind == DatabaseFieldProjectionKind.PackedField)
            {
                if (!DatabaseValueCodec.TryEncodePackedField(
                        _schema,
                        Payload,
                        projection,
                        value,
                        out byte[] encodedStructure,
                        out error))
                {
                    return false;
                }

                _editedPayload = encodedStructure.AsSpan().SequenceEqual(OriginalPayload)
                    ? null
                    : encodedStructure;
                return true;
            }

            if (projection.Kind == DatabaseFieldProjectionKind.EnumValue)
            {
                if (!DatabaseValueCodec.TryEncodeEnumValue(
                        _schema,
                        Payload,
                        projection,
                        value,
                        out byte[] encodedValue,
                        out error))
                {
                    return false;
                }

                _editedPayload = encodedValue.AsSpan().SequenceEqual(OriginalPayload)
                    ? null
                    : encodedValue;
                return true;
            }

            string normalized = value.Trim();
            bool enabled;
            if (normalized is "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("да", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }
            else if (normalized is "0" || normalized.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Equals("нет", StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
            }
            else
            {
                error = "Для битовой ячейки введите 0 или 1.";
                return false;
            }

            byte[] encoded = Payload.ToArray();
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
            uint bit = 1u << projection.Index;
            mask = enabled ? mask | bit : mask & ~bit;
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, mask);
            _editedPayload = encoded.AsSpan().SequenceEqual(OriginalPayload) ? null : encoded;
            return true;
        }
    }

    public bool TryGetStringListValues(out IReadOnlyList<string> values)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.TryDecodeStringListValues(_schema, Payload, out values);
        }
    }

    public bool CanEditStructuredList
    {
        get
        {
            lock (_editSync)
            {
                return IsEditable &&
                       DatabaseValueCodec.CanEditStructuredListEntries(_schema, Payload);
            }
        }
    }

    public bool TryGetStructuredListEntries(
        out IReadOnlyList<StructuredListFieldDescriptor> fields,
        out IReadOnlyList<StructuredListEntryValue> entries)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.TryDecodeStructuredListEntries(
                _schema,
                Payload,
                out fields,
                out entries);
        }
    }

    public bool TrySetStructuredListEntries(
        IReadOnlyList<StructuredListEntryValue> entries,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_editSync)
        {
            error = null;
            if (!CanEditStructuredList)
            {
                error = "Этот составной список пока доступен только для чтения.";
                return false;
            }

            if (!DatabaseValueCodec.TryEncodeStructuredListEntries(
                    _schema,
                    Payload,
                    entries,
                    out byte[] encoded,
                    out error))
            {
                return false;
            }

            _editedPayload = encoded.AsSpan().SequenceEqual(OriginalPayload) ? null : encoded;
            return true;
        }
    }

    internal IReadOnlyList<StructuredListStringValue> GetStructuredListStringValues(int nestedFieldId)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.DecodeStructuredListStringValues(
                _schema,
                Payload,
                nestedFieldId);
        }
    }

    public bool TrySetStringListValues(
        IReadOnlyList<string> values,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_editSync)
        {
            error = null;
            if (!IsEditable)
            {
                error = "Это поле пока доступно только для чтения.";
                return false;
            }

            if (!DatabaseValueCodec.TryEncodeStringListValues(
                    _schema,
                    values,
                    OriginalPayload,
                    out byte[] encoded,
                    out error))
            {
                return false;
            }

            _editedPayload = encoded.AsSpan().SequenceEqual(OriginalPayload) ? null : encoded;
            return true;
        }
    }

    internal bool TryGetPrimitiveListCount(out int count)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.TryGetPrimitiveListCount(_schema, Payload, out count);
        }
    }

    public bool TryPrepareValue(string value, out DatabaseFieldState? state, out string? error)
    {
        lock (_editSync)
        {
            state = null; error = null;
            if (!IsEditable) { error = "Поле доступно только для чтения."; return false; }
            if (!DatabaseValueCodec.TryEncode(_schema, value, OriginalPayload, out var encoded, out error)) return false;
            state = new(this, encoded); return true;
        }
    }

    public bool TrySetValue(string value, out string? error)
    {
        lock (_editSync)
        {
            error = null;
            if (!IsEditable)
            {
                error = "Это поле пока доступно только для чтения.";
                return false;
            }

            if (!DatabaseValueCodec.TryEncode(_schema, value, OriginalPayload, out var encoded, out error))
            {
                return false;
            }

            _editedPayload = encoded.AsSpan().SequenceEqual(OriginalPayload) ? null : encoded;
            return true;
        }
    }

    public DatabaseFieldState CaptureState()
    {
        lock (_editSync)
        {
            return new(this, Payload);
        }
    }

    public bool TryApplyTransferredPayload(
        ReadOnlySpan<byte> expectedCurrentPayload,
        ReadOnlySpan<byte> transferredPayload,
        out string? error)
    {
        lock (_editSync)
        {
            error = null;
            if (!IsEditable)
            {
                error = "Поле недоступно для переноса.";
                return false;
            }

            if (!Payload.SequenceEqual(expectedCurrentPayload))
            {
                error = "Значение изменилось после сравнения. Выполните сравнение баз заново.";
                return false;
            }

            try
            {
                _ = DatabaseValueCodec.DecodeValue(_schema, transferredPayload);
                _ = DatabaseValueCodec.GetEditorText(_schema, transferredPayload);
            }
            catch (Exception exception) when (exception is DatabaseFormatException or
                                               ArgumentException or
                                               DecoderFallbackException)
            {
                error = $"Значение второй базы несовместимо со схемой поля: {exception.Message}";
                return false;
            }

            _editedPayload = transferredPayload.SequenceEqual(OriginalPayload)
                ? null
                : transferredPayload.ToArray();
            return true;
        }
    }

    public void RestoreState(DatabaseFieldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_editSync)
        {
            state.EnsureOwner(this);
            _editedPayload = state.Payload.SequenceEqual(OriginalPayload)
                ? null
                : state.Payload.ToArray();
        }
    }

    internal ChunkSlice Chunk { get; }

    internal ReadOnlySpan<byte> Payload => _editedPayload is null ? OriginalPayload : _editedPayload;

    public void ResetToOriginal()
    {
        lock (_editSync)
        {
            _editedPayload = null;
        }
    }

    private ReadOnlySpan<byte> OriginalPayload =>
        _resourceData.AsSpan(_payloadOffset, _payloadLength);
}

internal sealed record StructuredListStringValue(int ItemIndex, string Value);

public sealed record StructuredListFieldDescriptor(
    int Id,
    string DisplayName,
    string Kind,
    bool IsEditable);

public sealed record StructuredListEntryValue(
    int Index,
    IReadOnlyDictionary<int, string> Fields)
{
    public int? SourceIndex { get; init; }
}

public enum DatabaseFieldProjectionKind
{
    ListElement,
    Bit,
    NestedField,
    PackedField,
    EnumValue,
    DecodedValue,
}

public sealed record DatabaseFieldProjection(
    DatabaseFieldProjectionKind Kind,
    int Index,
    string DisplayName,
    string? Description = null,
    string? ValueKind = null,
    int DisplayOrder = int.MaxValue,
    IReadOnlyDictionary<int, string>? ValueLabels = null);

internal enum DatabaseValueKind
{
    Hex,
    String,
    FixedString,
    Int32,
    UInt32,
    Float32,
    Byte,
    Boolean,
    StringList,
    UInt32List,
    Float32List,
    ByteList,
    FixedStringList,
    Nested,
    StructuredList,
    PackedStructure,
}

internal sealed record FieldSchema(
    DatabaseValueKind Kind,
    string DisplayName,
    bool Editable = true,
    string? Key = null,
    IReadOnlyList<DatabaseFieldProjection>? ProjectionMetadata = null,
    int DisplayOrder = int.MaxValue,
    int? FixedElementSize = null,
    IReadOnlyDictionary<byte, FieldSchema>? NestedFields = null,
    DatabaseComplexEncoding ComplexEncoding = DatabaseComplexEncoding.None,
    bool ShowInReadableView = true)
{
    public IReadOnlyList<DatabaseFieldProjection> Projections =>
        ProjectionMetadata ?? Array.Empty<DatabaseFieldProjection>();
}

internal sealed record SectionSchema(
    byte Id,
    string Key,
    string Name,
    byte NameFieldId,
    byte IdentityFieldId,
    bool CanAppendRecords,
    IReadOnlyDictionary<byte, FieldSchema> Fields)
{
    public bool IsEditable => Fields.Values.Any(field => field.Editable);
}

internal static class DatabaseValueCodec
{
    private static readonly Encoding Windows1251 = CreateWindows1251();

    public static bool CanEdit(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        try
        {
            return schema.Kind switch
            {
                DatabaseValueKind.String or DatabaseValueKind.Hex => true,
                DatabaseValueKind.FixedString => payload.Length > 0,
                DatabaseValueKind.Int32 or DatabaseValueKind.UInt32 or DatabaseValueKind.Float32 => payload.Length == 4,
                DatabaseValueKind.Byte or DatabaseValueKind.Boolean => payload.Length == 1,
                DatabaseValueKind.Float32List => DecodeFloatValues(payload) is not null,
                DatabaseValueKind.UInt32List => DecodeUInt32Values(payload) is not null,
                DatabaseValueKind.ByteList => DecodeByteValues(payload) is not null,
                DatabaseValueKind.StringList => DecodeStringListValues(payload) is not null,
                DatabaseValueKind.FixedStringList => DecodeFixedStringListValues(payload, schema.FixedElementSize) is not null,
                DatabaseValueKind.Nested => CanDecodeNested(schema, payload),
                DatabaseValueKind.StructuredList => CanDecodeStructuredList(schema, payload),
                DatabaseValueKind.PackedStructure => payload.Length == 36,
                _ => false,
            };
        }
        catch (Exception exception) when (exception is DatabaseFormatException or DecoderFallbackException or ArgumentException)
        {
            return false;
        }
    }

    public static string GetEditorText(FieldSchema schema, ReadOnlySpan<byte> payload) => schema.Kind switch
    {
        DatabaseValueKind.Hex or DatabaseValueKind.Nested or DatabaseValueKind.StructuredList or
            DatabaseValueKind.PackedStructure => Convert.ToHexString(payload),
        DatabaseValueKind.StringList => string.Join("; ", DecodeStringListValues(payload)),
        DatabaseValueKind.FixedStringList => string.Join(
            "; ",
            DecodeFixedStringListValues(payload, schema.FixedElementSize)),
        _ => FormatInvariant(DecodeValue(schema, payload)),
    };

    public static object DecodeValue(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        try
        {
            return schema.Kind switch
            {
                DatabaseValueKind.String or DatabaseValueKind.FixedString => DecodeString(payload),
                DatabaseValueKind.Int32 when payload.Length == 4 =>
                    BinaryPrimitives.ReadInt32LittleEndian(payload),
                DatabaseValueKind.UInt32 when payload.Length == 4 =>
                    BinaryPrimitives.ReadUInt32LittleEndian(payload),
                DatabaseValueKind.Float32 when payload.Length == 4 =>
                    BinaryPrimitives.ReadSingleLittleEndian(payload),
                DatabaseValueKind.Byte when payload.Length == 1 => payload[0],
                DatabaseValueKind.Boolean when payload.Length == 1 => payload[0] != 0,
                DatabaseValueKind.Float32List => DecodeFloatList(payload),
                DatabaseValueKind.UInt32List => DecodeUInt32List(payload),
                DatabaseValueKind.ByteList => DecodeByteList(payload),
                DatabaseValueKind.StringList => DecodeStringList(payload),
                DatabaseValueKind.FixedStringList => DecodeFixedStringList(payload, schema.FixedElementSize),
                DatabaseValueKind.Nested => DecodeStructure(schema, payload),
                DatabaseValueKind.StructuredList => DecodeStructureList(schema, payload),
                DatabaseValueKind.PackedStructure => DecodePackedStructure(payload),
                _ => ToCompactHex(payload),
            };
        }
        catch (Exception exception) when (exception is DecoderFallbackException or DatabaseFormatException or ArgumentException)
        {
            return ToCompactHex(payload);
        }
    }

    public static object? DecodeProjection(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        DatabaseFieldProjection projection)
    {
        try
        {
            if (projection.Index < 0)
            {
                return null;
            }

            return projection.Kind switch
            {
                DatabaseFieldProjectionKind.Bit => DecodeBit(payload, projection.Index),
                DatabaseFieldProjectionKind.ListElement => DecodeListElement(schema.Kind, payload, projection.Index),
                DatabaseFieldProjectionKind.NestedField => DecodeStructureField(schema, payload, projection.Index),
                DatabaseFieldProjectionKind.PackedField => DecodePackedField(payload, projection),
                DatabaseFieldProjectionKind.EnumValue => DecodeEnumValue(schema, payload, projection),
                DatabaseFieldProjectionKind.DecodedValue => DecodeValue(schema, payload),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is DecoderFallbackException or DatabaseFormatException or ArgumentException)
        {
            return null;
        }
    }

    public static bool TryDecodeStringListValues(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        try
        {
            values = schema.Kind switch
            {
                DatabaseValueKind.StringList => DecodeStringListValues(payload),
                DatabaseValueKind.FixedStringList => DecodeFixedStringListValues(payload, schema.FixedElementSize),
                _ => Array.Empty<string>(),
            };
            return schema.Kind is DatabaseValueKind.StringList or DatabaseValueKind.FixedStringList;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or DatabaseFormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool CanEditStructuredListEntries(
        FieldSchema schema,
        ReadOnlySpan<byte> payload) =>
        TryDecodeStructuredListEntries(schema, payload, out _, out _);

    public static bool TryDecodeStructuredListEntries(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        out IReadOnlyList<StructuredListFieldDescriptor> fields,
        out IReadOnlyList<StructuredListEntryValue> entries)
    {
        fields = [];
        entries = [];
        if (schema.Kind != DatabaseValueKind.StructuredList ||
            schema.ComplexEncoding != DatabaseComplexEncoding.StructureList ||
            schema.NestedFields is null)
        {
            return false;
        }

        try
        {
            StructuredListFieldDescriptor[] descriptors = schema.NestedFields
                .OrderBy(pair => pair.Value.DisplayOrder)
                .ThenBy(pair => pair.Key)
                .Select(pair => new StructuredListFieldDescriptor(
                    pair.Key,
                    pair.Value.DisplayName,
                    pair.Value.Kind.ToString(),
                    pair.Value.Editable))
                .ToArray();
            IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(
                payload,
                0,
                payload.Length,
                "editable-structured-list");
            var decodedEntries = new List<StructuredListEntryValue>();
            var itemIndex = 0;
            foreach (ChunkSlice item in chunks)
            {
                if (item.Id != 1)
                {
                    continue;
                }

                ReadOnlySpan<byte> itemPayload = payload.Slice(item.DataOffset, item.DataLength);
                IReadOnlyList<ChunkSlice> itemFields = ChunkCodec.ReadList(
                    itemPayload,
                    0,
                    itemPayload.Length,
                    $"editable-structured-list[{itemIndex}]");
                var values = descriptors.ToDictionary(field => field.Id, _ => string.Empty);
                foreach (ChunkSlice itemField in itemFields)
                {
                    if (schema.NestedFields.TryGetValue(itemField.Id, out FieldSchema? fieldSchema))
                    {
                        values[itemField.Id] = FormatInvariant(DecodeValue(
                            fieldSchema,
                            itemPayload.Slice(itemField.DataOffset, itemField.DataLength)));
                    }
                }

                decodedEntries.Add(new StructuredListEntryValue(itemIndex, values)
                {
                    SourceIndex = itemIndex,
                });
                itemIndex++;
            }

            fields = descriptors;
            entries = decodedEntries;
            return true;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or DatabaseFormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryEncodeStructuredListEntries(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        IReadOnlyList<StructuredListEntryValue> entries,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (schema.Kind != DatabaseValueKind.StructuredList ||
            schema.ComplexEncoding != DatabaseComplexEncoding.StructureList ||
            schema.NestedFields is null)
        {
            error = "Поле не является поддерживаемым составным списком.";
            return false;
        }

        IReadOnlyList<ChunkSlice> outerChunks;
        try
        {
            outerChunks = ChunkCodec.ReadList(original, 0, original.Length, "editable-structured-list");
        }
        catch (DatabaseFormatException exception)
        {
            error = $"Нарушена структура списка: {exception.Message}";
            return false;
        }

        ChunkSlice[] originalItems = outerChunks.Where(chunk => chunk.Id == 1).ToArray();
        bool hasExplicitSources = entries.Any(entry => entry.SourceIndex.HasValue);
        bool structureChanged = entries.Count != originalItems.Length ||
            entries.Select((entry, index) => ResolveSourceIndex(entry, index, originalItems.Length, hasExplicitSources))
                .Where(sourceIndex => sourceIndex.HasValue)
                .Select(sourceIndex => sourceIndex!.Value)
                .SequenceEqual(Enumerable.Range(0, originalItems.Length)) is false;
        if (structureChanged && outerChunks.Any(chunk => chunk.Id != 1))
        {
            error = "В списке обнаружены неизвестные служебные блоки: менять количество или порядок вариантов небезопасно.";
            return false;
        }

        using var output = new MemoryStream(Math.Max(original.Length, entries.Count * 64));
        if (!structureChanged)
        {
            var itemIndex = 0;
            foreach (ChunkSlice outerChunk in outerChunks)
            {
                if (outerChunk.Id != 1)
                {
                    output.Write(original.Slice(outerChunk.HeaderOffset, outerChunk.TotalLength));
                    continue;
                }

                if (!TryEncodeStructuredListEntry(
                        schema,
                        original,
                        originalItems,
                        entries[itemIndex],
                        itemIndex,
                        hasExplicitSources,
                        out byte[] encodedItem,
                        out error))
                {
                    return false;
                }

                output.Write(encodedItem);
                itemIndex++;
            }
        }
        else
        {
            for (int itemIndex = 0; itemIndex < entries.Count; itemIndex++)
            {
                if (!TryEncodeStructuredListEntry(
                        schema,
                        original,
                        originalItems,
                        entries[itemIndex],
                        itemIndex,
                        hasExplicitSources,
                        out byte[] encodedItem,
                        out error))
                {
                    return false;
                }

                output.Write(encodedItem);
            }
        }

        encoded = output.ToArray();
        return true;
    }

    private static bool TryEncodeStructuredListEntry(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        IReadOnlyList<ChunkSlice> originalItems,
        StructuredListEntryValue entry,
        int itemIndex,
        bool hasExplicitSources,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (schema.NestedFields is null)
        {
            error = "Для составного списка отсутствует схема вложенных полей.";
            return false;
        }

        if (entry.Index != itemIndex)
        {
            error = $"Нарушен порядок вариантов: ожидался номер {itemIndex + 1}.";
            return false;
        }

        if (entry.Fields.Keys.Any(fieldId =>
                fieldId is < 0 or > byte.MaxValue ||
                !schema.NestedFields.ContainsKey((byte)fieldId)))
        {
            error = $"Вариант {itemIndex + 1} содержит неизвестное поле.";
            return false;
        }

        int? sourceIndex = ResolveSourceIndex(entry, itemIndex, originalItems.Count, hasExplicitSources);
        if (sourceIndex is int invalidSource && (uint)invalidSource >= (uint)originalItems.Count)
        {
            error = $"Вариант {itemIndex + 1} ссылается на отсутствующий исходный вариант.";
            return false;
        }

        if (sourceIndex is not int resolvedSource)
        {
            return TryCreateStructuredListEntry(schema, entry, itemIndex, out encoded, out error);
        }

        ChunkSlice sourceChunk = originalItems[resolvedSource];
        ReadOnlySpan<byte> itemPayload = original.Slice(sourceChunk.DataOffset, sourceChunk.DataLength);
        IReadOnlyList<ChunkSlice> itemFields;
        try
        {
            itemFields = ChunkCodec.ReadList(
                itemPayload,
                0,
                itemPayload.Length,
                $"editable-structured-list[{resolvedSource}]");
        }
        catch (DatabaseFormatException exception)
        {
            error = $"Вариант {itemIndex + 1}: {exception.Message}";
            return false;
        }

        var existingFieldIds = new HashSet<int>();
        using var itemOutput = new MemoryStream(itemPayload.Length);
        foreach (ChunkSlice itemField in itemFields)
        {
            if (!schema.NestedFields.TryGetValue(itemField.Id, out FieldSchema? fieldSchema) ||
                !entry.Fields.TryGetValue(itemField.Id, out string? newValue))
            {
                itemOutput.Write(itemPayload.Slice(itemField.HeaderOffset, itemField.TotalLength));
                continue;
            }

            existingFieldIds.Add(itemField.Id);
            if (!fieldSchema.Editable ||
                !TryEncode(
                    fieldSchema,
                    newValue,
                    itemPayload.Slice(itemField.DataOffset, itemField.DataLength),
                    out byte[] nestedPayload,
                    out error))
            {
                error = fieldSchema.Editable
                    ? $"Вариант {itemIndex + 1}, «{fieldSchema.DisplayName}»: {error}"
                    : $"Вариант {itemIndex + 1}, «{fieldSchema.DisplayName}» доступен только для чтения.";
                return false;
            }

            itemOutput.Write(ChunkCodec.Encode(itemField.Id, nestedPayload, itemField.HeaderSize));
        }

        foreach ((int fieldId, string value) in entry.Fields.OrderBy(pair => schema.NestedFields[(byte)pair.Key].DisplayOrder).ThenBy(pair => pair.Key))
        {
            if (existingFieldIds.Contains(fieldId) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            FieldSchema fieldSchema = schema.NestedFields[(byte)fieldId];
            if (!fieldSchema.Editable ||
                !TryEncode(fieldSchema, value, [], out byte[] nestedPayload, out error))
            {
                error = fieldSchema.Editable
                    ? $"Вариант {itemIndex + 1}, «{fieldSchema.DisplayName}»: {error}"
                    : $"Вариант {itemIndex + 1}, «{fieldSchema.DisplayName}» доступен только для чтения.";
                return false;
            }

            itemOutput.Write(ChunkCodec.Encode((byte)fieldId, nestedPayload, 2));
        }

        byte[] updatedItem = itemOutput.ToArray();
        encoded = updatedItem.AsSpan().SequenceEqual(itemPayload)
            ? original.Slice(sourceChunk.HeaderOffset, sourceChunk.TotalLength).ToArray()
            : ChunkCodec.Encode(1, updatedItem, sourceChunk.HeaderSize);
        return true;
    }

    private static bool TryCreateStructuredListEntry(
        FieldSchema schema,
        StructuredListEntryValue entry,
        int itemIndex,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (schema.NestedFields is null)
        {
            error = "Для составного списка отсутствует схема вложенных полей.";
            return false;
        }

        using var itemOutput = new MemoryStream();
        foreach ((byte fieldId, FieldSchema fieldSchema) in schema.NestedFields
                     .OrderBy(pair => pair.Value.DisplayOrder)
                     .ThenBy(pair => pair.Key))
        {
            string value = entry.Fields.GetValueOrDefault(fieldId, GetDefaultStructuredFieldValue(fieldSchema));
            if (!TryEncode(fieldSchema, value, [], out byte[] nestedPayload, out error))
            {
                error = $"Вариант {itemIndex + 1}, «{fieldSchema.DisplayName}»: {error}";
                return false;
            }

            itemOutput.Write(ChunkCodec.Encode(fieldId, nestedPayload, 2));
        }

        encoded = ChunkCodec.Encode(1, itemOutput.ToArray(), 2);
        return true;
    }

    private static int? ResolveSourceIndex(
        StructuredListEntryValue entry,
        int itemIndex,
        int originalItemCount,
        bool hasExplicitSources)
    {
        if (entry.SourceIndex.HasValue)
        {
            return entry.SourceIndex;
        }

        return !hasExplicitSources && itemIndex < originalItemCount ? entry.Index : null;
    }

    private static string GetDefaultStructuredFieldValue(FieldSchema schema) => schema.Kind switch
    {
        DatabaseValueKind.Int32 or DatabaseValueKind.UInt32 or DatabaseValueKind.Float32 or
            DatabaseValueKind.Byte => "0",
        DatabaseValueKind.Boolean => "false",
        _ => string.Empty,
    };

    internal static IReadOnlyList<StructuredListStringValue> DecodeStructuredListStringValues(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        int nestedFieldId)
    {
        if (schema.Kind != DatabaseValueKind.StructuredList || nestedFieldId is < 0 or > byte.MaxValue)
        {
            return [];
        }

        try
        {
            IReadOnlyList<ChunkSlice> items = ChunkCodec.ReadList(payload, 0, payload.Length, "structured-list");
            var values = new List<StructuredListStringValue>();
            var itemIndex = 0;
            foreach (ChunkSlice item in items)
            {
                if (item.Id != 1)
                {
                    continue;
                }

                ReadOnlySpan<byte> itemPayload = payload.Slice(item.DataOffset, item.DataLength);
                IReadOnlyList<ChunkSlice> fields = ChunkCodec.ReadList(
                    itemPayload,
                    0,
                    itemPayload.Length,
                    "structured-list-item");
                int matchIndex = -1;
                for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                {
                    if (fields[fieldIndex].Id == (byte)nestedFieldId)
                    {
                        matchIndex = fieldIndex;
                        break;
                    }
                }

                if (matchIndex >= 0)
                {
                    ChunkSlice match = fields[matchIndex];
                    FieldSchema fieldSchema = schema.NestedFields is not null &&
                                              schema.NestedFields.TryGetValue((byte)nestedFieldId, out FieldSchema? known)
                        ? known
                        : new FieldSchema(DatabaseValueKind.Hex, $"Field {nestedFieldId}", false);
                    object value = DecodeValue(
                        fieldSchema,
                        itemPayload.Slice(match.DataOffset, match.DataLength));
                    if (value is string text)
                    {
                        values.Add(new(itemIndex, text));
                    }
                }

                itemIndex++;
            }

            return values;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or DatabaseFormatException or ArgumentException)
        {
            return [];
        }
    }

    public static bool TryGetPrimitiveListCount(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        out int count)
    {
        count = 0;
        try
        {
            string[]? values = schema.Kind switch
            {
                DatabaseValueKind.Float32List => DecodeFloatValues(payload),
                DatabaseValueKind.UInt32List => DecodeUInt32Values(payload),
                DatabaseValueKind.ByteList => DecodeByteValues(payload),
                _ => null,
            };
            if (values is null)
            {
                return false;
            }

            count = values.Length;
            return true;
        }
        catch (Exception exception) when (exception is DatabaseFormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool CanEditNestedField(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        int fieldId)
    {
        if (schema.Kind != DatabaseValueKind.Nested ||
            schema.ComplexEncoding != DatabaseComplexEncoding.Structure ||
            fieldId is < 0 or > byte.MaxValue ||
            schema.NestedFields is null ||
            !schema.NestedFields.TryGetValue((byte)fieldId, out FieldSchema? nestedSchema) ||
            !nestedSchema.Editable)
        {
            return false;
        }

        try
        {
            foreach (ChunkSlice chunk in ChunkCodec.ReadList(
                         payload,
                         0,
                         payload.Length,
                         "editable-nested-value"))
            {
                if (chunk.Id == (byte)fieldId &&
                    CanEdit(nestedSchema, payload.Slice(chunk.DataOffset, chunk.DataLength)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (DatabaseFormatException)
        {
            return false;
        }
    }

    public static bool CanEditPackedField(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        DatabaseFieldProjection projection) =>
        schema.Kind == DatabaseValueKind.PackedStructure &&
        payload.Length == 36 &&
        projection.Index is >= 0 and <= 8 &&
        projection.ValueKind is nameof(DatabaseValueKind.Float32) or nameof(DatabaseValueKind.UInt32);

    public static bool CanEditEnumValue(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        DatabaseFieldProjection projection) =>
        projection.ValueLabels is not null &&
        schema.Kind switch
        {
            DatabaseValueKind.Int32 or DatabaseValueKind.UInt32 => payload.Length == sizeof(uint),
            DatabaseValueKind.Byte => payload.Length == sizeof(byte),
            _ => false,
        };

    public static bool TryEncodeNestedField(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        int fieldId,
        string text,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (!CanEditNestedField(schema, original, fieldId) || schema.NestedFields is null)
        {
            error = $"Вложенное поле {fieldId} отсутствует или имеет неподдерживаемый тип.";
            return false;
        }

        FieldSchema nestedSchema = schema.NestedFields[(byte)fieldId];
        IReadOnlyList<ChunkSlice> chunks;
        try
        {
            chunks = ChunkCodec.ReadList(original, 0, original.Length, "editable-nested-value");
        }
        catch (DatabaseFormatException exception)
        {
            error = $"Нарушена внутренняя структура поля: {exception.Message}";
            return false;
        }

        var replaced = false;
        using var output = new MemoryStream(original.Length);
        foreach (ChunkSlice chunk in chunks)
        {
            if (!replaced && chunk.Id == (byte)fieldId)
            {
                if (!TryEncode(
                        nestedSchema,
                        text,
                        original.Slice(chunk.DataOffset, chunk.DataLength),
                        out byte[] nestedPayload,
                        out error))
                {
                    return false;
                }

                output.Write(ChunkCodec.Encode(chunk.Id, nestedPayload, chunk.HeaderSize));
                replaced = true;
            }
            else
            {
                output.Write(original.Slice(chunk.HeaderOffset, chunk.TotalLength));
            }
        }

        if (!replaced)
        {
            error = $"Вложенное поле {fieldId} отсутствует.";
            return false;
        }

        encoded = output.ToArray();
        return true;
    }

    public static bool TryEncodePackedField(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        DatabaseFieldProjection projection,
        string text,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (!CanEditPackedField(schema, original, projection))
        {
            error = "Упакованное поле имеет неподдерживаемый размер или тип.";
            return false;
        }

        DatabaseValueKind valueKind = string.Equals(
            projection.ValueKind,
            DatabaseValueKind.UInt32.ToString(),
            StringComparison.Ordinal)
            ? DatabaseValueKind.UInt32
            : DatabaseValueKind.Float32;
        int offset = projection.Index * sizeof(uint);
        if (!TryEncode(
                new FieldSchema(valueKind, projection.DisplayName),
                text,
                original.Slice(offset, sizeof(uint)),
                out byte[] member,
                out error))
        {
            return false;
        }

        encoded = original.ToArray();
        member.CopyTo(encoded, offset);
        return true;
    }

    public static bool TryEncodeEnumValue(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        DatabaseFieldProjection projection,
        string text,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (!CanEditEnumValue(schema, original, projection) || projection.ValueLabels is null)
        {
            error = "Поле не является поддерживаемым перечислением.";
            return false;
        }

        string normalized = text.Trim();
        KeyValuePair<int, string> labelMatch = projection.ValueLabels
            .FirstOrDefault(pair => string.Equals(pair.Value, normalized, StringComparison.OrdinalIgnoreCase));
        if (labelMatch.Value is not null &&
            string.Equals(labelMatch.Value, normalized, StringComparison.OrdinalIgnoreCase))
        {
            normalized = labelMatch.Key.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            int separator = normalized.IndexOf('—');
            if (separator > 0)
            {
                normalized = normalized[..separator].Trim();
            }
        }

        return TryEncode(schema, normalized, original, out encoded, out error);
    }

    public static bool TryEncodePrimitiveListElement(
        FieldSchema schema,
        ReadOnlySpan<byte> original,
        int index,
        string text,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        string[]? values = schema.Kind switch
        {
            DatabaseValueKind.Float32List => DecodeFloatValues(original),
            DatabaseValueKind.UInt32List => DecodeUInt32Values(original),
            DatabaseValueKind.ByteList => DecodeByteValues(original),
            _ => null,
        };
        if (values is null)
        {
            error = "Поле не является числовым списком.";
            return false;
        }

        if (index < 0 || index >= values.Length)
        {
            error = $"Элемент списка {index + 1} отсутствует.";
            return false;
        }

        string normalized = text.Trim();
        switch (schema.Kind)
        {
            case DatabaseValueKind.Float32List:
                normalized = normalized.Replace(',', '.');
                if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue) ||
                    !float.IsFinite(floatValue))
                {
                    error = "Ожидается конечное 32-битное число.";
                    return false;
                }

                normalized = floatValue.ToString("R", CultureInfo.InvariantCulture);
                break;
            case DatabaseValueKind.UInt32List:
                if (!uint.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintValue))
                {
                    error = "Ожидается целое число от 0 до 4294967295.";
                    return false;
                }

                normalized = uintValue.ToString(CultureInfo.InvariantCulture);
                break;
            case DatabaseValueKind.ByteList:
                if (!byte.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte byteValue))
                {
                    error = "Ожидается целое число от 0 до 255.";
                    return false;
                }

                normalized = byteValue.ToString(CultureInfo.InvariantCulture);
                break;
        }

        values[index] = normalized;
        return TryEncode(schema, string.Join("; ", values), original, out encoded, out error);
    }

    public static bool TryEncode(
        FieldSchema schema,
        string text,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        if (string.Equals(text, GetEditorText(schema, original), StringComparison.Ordinal))
        {
            encoded = original.ToArray();
            return true;
        }

        try
        {
            switch (schema.Kind)
            {
                case DatabaseValueKind.String:
                    {
                        if (text.Contains('\0'))
                        {
                            error = "Строка не может содержать NUL.";
                            return false;
                        }

                        var bytes = Windows1251.GetBytes(text);
                        var terminated = original.Length > 0 && original[^1] == 0;
                        encoded = terminated ? [.. bytes, 0] : bytes;
                        return true;
                    }
                case DatabaseValueKind.FixedString:
                    {
                        var bytes = Windows1251.GetBytes(text);
                        if (bytes.Length > original.Length)
                        {
                            error = $"Длина не должна превышать {original.Length} байт.";
                            return false;
                        }

                        encoded = new byte[original.Length];
                        bytes.CopyTo(encoded, 0);
                        return true;
                    }
                case DatabaseValueKind.Int32:
                    {
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        {
                            error = "Ожидается целое число от -2147483648 до 2147483647.";
                            return false;
                        }

                        encoded = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
                        return true;
                    }
                case DatabaseValueKind.UInt32:
                    {
                        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        {
                            error = "Ожидается целое число от 0 до 4294967295.";
                            return false;
                        }

                        encoded = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
                        return true;
                    }
                case DatabaseValueKind.Float32:
                    {
                        var normalized = text.Trim().Replace(',', '.');
                        if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                            !float.IsFinite(value))
                        {
                            error = "Ожидается конечное 32-битное число.";
                            return false;
                        }

                        encoded = new byte[4];
                        BinaryPrimitives.WriteSingleLittleEndian(encoded, value);
                        return true;
                    }
                case DatabaseValueKind.Byte:
                    {
                        if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        {
                            error = "Ожидается целое число от 0 до 255.";
                            return false;
                        }

                        encoded = [value];
                        return true;
                    }
                case DatabaseValueKind.Boolean:
                    {
                        var normalized = text.Trim().ToLowerInvariant();
                        if (normalized is "true" or "1" or "да")
                        {
                            encoded = [1];
                            return true;
                        }

                        if (normalized is "false" or "0" or "нет")
                        {
                            encoded = [0];
                            return true;
                        }

                        error = "Ожидается true/false, 1/0 или да/нет.";
                        return false;
                    }
                case DatabaseValueKind.Float32List:
                    return TryEncodeFloatList(text, original, out encoded, out error);
                case DatabaseValueKind.UInt32List:
                    return TryEncodeUInt32List(text, original, out encoded, out error);
                case DatabaseValueKind.ByteList:
                    return TryEncodeByteList(text, original, out encoded, out error);
                case DatabaseValueKind.StringList:
                    return TryEncodeStringList(text, original, fixedElementSize: null, out encoded, out error);
                case DatabaseValueKind.FixedStringList:
                    return TryEncodeStringList(text, original, schema.FixedElementSize, out encoded, out error);
                case DatabaseValueKind.Hex:
                    return TryEncodeHex(text, out encoded, out error);
                case DatabaseValueKind.Nested:
                    if (!TryEncodeHex(text, out encoded, out error))
                    {
                        return false;
                    }

                    _ = DecodeStructure(schema, encoded);
                    return true;
                case DatabaseValueKind.StructuredList:
                    if (!TryEncodeHex(text, out encoded, out error))
                    {
                        return false;
                    }

                    _ = DecodeStructureList(schema, encoded);
                    return true;
                case DatabaseValueKind.PackedStructure:
                    if (!TryEncodeHex(text, out encoded, out error))
                    {
                        return false;
                    }

                    if (encoded.Length != 36)
                    {
                        error = "Упакованная структура должна содержать ровно 36 байт (72 hex-символа).";
                        return false;
                    }

                    return true;
                default:
                    error = "Этот тип поля не поддерживает запись.";
                    return false;
            }
        }
        catch (EncoderFallbackException)
        {
            error = "Строка содержит символы, которые нельзя сохранить в Windows-1251.";
            return false;
        }
        catch (DatabaseFormatException exception)
        {
            error = $"Нарушена внутренняя структура поля: {exception.Message}";
            return false;
        }
    }

    private static bool TryEncodeFloatList(
        string text,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        string[] tokens = SplitListText(text);
        var values = new byte[tokens.Length][];
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                !float.IsFinite(value))
            {
                encoded = [];
                error = $"Элемент {index + 1}: ожидается конечное 32-битное число.";
                return false;
            }

            values[index] = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(values[index], value);
        }

        encoded = EncodePrimitiveList(original, values, 4);
        error = null;
        return true;
    }

    private static bool TryEncodeUInt32List(
        string text,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        string[] tokens = SplitListText(text);
        var values = new byte[tokens.Length][];
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!uint.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
            {
                encoded = [];
                error = $"Элемент {index + 1}: ожидается целое число от 0 до 4294967295.";
                return false;
            }

            values[index] = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(values[index], value);
        }

        encoded = EncodePrimitiveList(original, values, 4);
        error = null;
        return true;
    }

    private static bool TryEncodeByteList(
        string text,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        string[] tokens = SplitListText(text);
        var values = new byte[tokens.Length][];
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!byte.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value))
            {
                encoded = [];
                error = $"Элемент {index + 1}: ожидается целое число от 0 до 255.";
                return false;
            }

            values[index] = [value];
        }

        encoded = EncodePrimitiveList(original, values, 1);
        error = null;
        return true;
    }

    private static bool TryEncodeStringList(
        string text,
        ReadOnlySpan<byte> original,
        int? fixedElementSize,
        out byte[] encoded,
        out string? error)
    {
        if (!StringListEditorCodec.TryParseEditorText(text, out IReadOnlyList<string> tokens, out error))
        {
            encoded = [];
            return false;
        }

        return TryEncodeStringListValues(
            tokens,
            original,
            fixedElementSize,
            out encoded,
            out error);
    }

    public static bool TryEncodeStringListValues(
        FieldSchema schema,
        IReadOnlyList<string> values,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        if (schema.Kind is not (DatabaseValueKind.StringList or DatabaseValueKind.FixedStringList))
        {
            encoded = [];
            error = "Поле не является строковым списком.";
            return false;
        }

        return TryEncodeStringListValues(
            values,
            original,
            schema.Kind == DatabaseValueKind.FixedStringList ? schema.FixedElementSize : null,
            out encoded,
            out error);
    }

    private static bool TryEncodeStringListValues(
        IReadOnlyList<string> values,
        ReadOnlySpan<byte> original,
        int? fixedElementSize,
        out byte[] encoded,
        out string? error)
    {
        IReadOnlyList<ChunkSlice> originalChunks = ChunkCodec.ReadList(
            original,
            0,
            original.Length,
            "editable-string-list");
        if (originalChunks.Any(chunk => chunk.Id != 1))
        {
            encoded = [];
            error = "Список содержит неподдерживаемый тип вложенного элемента.";
            return false;
        }

        using var output = new MemoryStream();
        for (int index = 0; index < values.Count; index++)
        {
            string value = values[index] ?? string.Empty;
            if (value.Length == 0)
            {
                encoded = [];
                error = $"Элемент {index + 1}: пустые строки в списке не поддерживаются.";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Windows1251.GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                encoded = [];
                error = $"Элемент {index + 1}: текст содержит символы вне Windows-1251.";
                return false;
            }
            ChunkSlice? template = originalChunks.Count == 0
                ? null
                : originalChunks[Math.Min(index, originalChunks.Count - 1)];
            byte[] payload;
            if (fixedElementSize is int fixedSize)
            {
                if (bytes.Length > fixedSize)
                {
                    encoded = [];
                    error = $"Элемент {index + 1}: длина не должна превышать {fixedSize} байт.";
                    return false;
                }

                payload = new byte[fixedSize];
                bytes.CopyTo(payload, 0);
            }
            else
            {
                bool terminated = template is ChunkSlice source
                    ? source.DataLength > 0 && original[source.DataOffset + source.DataLength - 1] == 0
                    : true;
                payload = terminated ? [.. bytes, 0] : bytes;
            }

            int headerSize = template?.HeaderSize ?? 2;
            output.Write(ChunkCodec.Encode(1, payload, headerSize));
        }

        encoded = output.ToArray();
        error = null;
        return true;
    }

    private static byte[] EncodePrimitiveList(
        ReadOnlySpan<byte> original,
        IReadOnlyList<byte[]> values,
        int elementSize)
    {
        IReadOnlyList<ChunkSlice>? chunks = TryReadPrimitiveChunks(original, elementSize);
        using var output = new MemoryStream();
        if (chunks is not null && chunks.Count > 0)
        {
            for (int index = 0; index < values.Count; index++)
            {
                int headerSize = chunks[Math.Min(index, chunks.Count - 1)].HeaderSize;
                output.Write(ChunkCodec.Encode(1, values[index], headerSize));
            }
        }
        else
        {
            foreach (byte[] value in values)
            {
                output.Write(value);
            }
        }

        return output.ToArray();
    }

    private static IReadOnlyList<ChunkSlice>? TryReadPrimitiveChunks(
        ReadOnlySpan<byte> payload,
        int elementSize)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "editable-primitive-list");
            return chunks.Count > 0 && chunks.All(chunk => chunk.Id == 1 && chunk.DataLength == elementSize)
                ? chunks
                : null;
        }
        catch (DatabaseFormatException)
        {
            return null;
        }
    }

    private static string[] SplitListText(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryEncodeHex(string text, out byte[] encoded, out string? error)
    {
        string normalized = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if ((normalized.Length & 1) != 0)
        {
            encoded = [];
            error = "Hex-строка должна содержать чётное число символов.";
            return false;
        }

        try
        {
            encoded = Convert.FromHexString(normalized);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            encoded = [];
            error = "Ожидаются только шестнадцатеричные символы 0-9 и A-F.";
            return false;
        }
    }

    private static bool CanDecodeNested(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        _ = DecodeStructure(schema, payload);
        return true;
    }

    private static bool CanDecodeStructuredList(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        _ = DecodeStructureList(schema, payload);
        return true;
    }

    private static Encoding CreateWindows1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static string DecodeString(ReadOnlySpan<byte> payload)
    {
        var end = payload.IndexOf((byte)0);
        if (end < 0)
        {
            end = payload.Length;
        }

        return Windows1251.GetString(payload[..end]);
    }

    private static string DecodeFloatList(ReadOnlySpan<byte> payload)
    {
        string[]? values = DecodeFloatValues(payload);
        return values is null ? ToCompactHex(payload) : string.Join(", ", values);
    }

    private static string DecodeUInt32List(ReadOnlySpan<byte> payload)
    {
        string[]? values = DecodeUInt32Values(payload);
        return values is null ? ToCompactHex(payload) : string.Join(", ", values);
    }

    private static string DecodeByteList(ReadOnlySpan<byte> payload) =>
        string.Join(", ", DecodeByteValues(payload));

    private static object? DecodeListElement(
        DatabaseValueKind kind,
        ReadOnlySpan<byte> payload,
        int index)
    {
        string[]? values = kind switch
        {
            DatabaseValueKind.Float32List => DecodeFloatValues(payload),
            DatabaseValueKind.UInt32List => DecodeUInt32Values(payload),
            DatabaseValueKind.ByteList => DecodeByteValues(payload),
            _ => null,
        };
        return values is not null && index < values.Length ? values[index] : null;
    }

    private static string[]? DecodeFloatValues(ReadOnlySpan<byte> payload)
    {
        byte[][]? chunks = TryDecodePrimitiveChunkList(payload, 4);
        if (chunks is null && payload.Length % 4 != 0)
        {
            return null;
        }

        int count = chunks?.Length ?? payload.Length / 4;
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> value = chunks is null ? payload.Slice(index * 4, 4) : chunks[index];
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(value).ToString("R", CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static string[]? DecodeUInt32Values(ReadOnlySpan<byte> payload)
    {
        byte[][]? chunks = TryDecodePrimitiveChunkList(payload, 4);
        if (chunks is null && payload.Length % 4 != 0)
        {
            return null;
        }

        int count = chunks?.Length ?? payload.Length / 4;
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> value = chunks is null ? payload.Slice(index * 4, 4) : chunks[index];
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static string[] DecodeByteValues(ReadOnlySpan<byte> payload)
    {
        byte[][]? chunks = TryDecodePrimitiveChunkList(payload, 1);
        if (chunks is not null)
        {
            return chunks.Select(item => item[0].ToString(CultureInfo.InvariantCulture)).ToArray();
        }

        var values = new string[payload.Length];
        for (var index = 0; index < payload.Length; index++)
        {
            values[index] = payload[index].ToString(CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static string DecodeStringList(ReadOnlySpan<byte> payload) =>
        string.Join(", ", DecodeStringListValues(payload));

    private static string[] DecodeStringListValues(ReadOnlySpan<byte> payload)
    {
        var chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "string-list");
        var values = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Id == 1)
            {
                values.Add(DecodeString(payload.Slice(chunk.DataOffset, chunk.DataLength)));
            }
        }

        return values.ToArray();
    }

    private static string DecodeFixedStringList(ReadOnlySpan<byte> payload, int? fixedElementSize) =>
        string.Join(", ", DecodeFixedStringListValues(payload, fixedElementSize));

    private static string[] DecodeFixedStringListValues(ReadOnlySpan<byte> payload, int? fixedElementSize)
    {
        IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "fixed-string-list");
        var values = new List<string>(chunks.Count);
        foreach (ChunkSlice chunk in chunks)
        {
            if (chunk.Id != 1 || (fixedElementSize is int minimum && chunk.DataLength < minimum))
            {
                continue;
            }

            string value = DecodeString(payload.Slice(chunk.DataOffset, chunk.DataLength));
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static string DecodeStructure(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "nested-value");
        return FormatStructure(schema, payload, chunks);
    }

    private static string DecodeStructureList(FieldSchema schema, ReadOnlySpan<byte> payload)
    {
        IReadOnlyList<ChunkSlice> items = ChunkCodec.ReadList(payload, 0, payload.Length, "structured-list");
        var values = new List<string>(items.Count);
        foreach (ChunkSlice item in items)
        {
            if (item.Id != 1)
            {
                continue;
            }

            ReadOnlySpan<byte> itemPayload = payload.Slice(item.DataOffset, item.DataLength);
            IReadOnlyList<ChunkSlice> fields = ChunkCodec.ReadList(
                itemPayload,
                0,
                itemPayload.Length,
                "structured-list-item");
            values.Add(FormatStructure(schema, itemPayload, fields));
        }

        return string.Join(" | ", values);
    }

    private static object? DecodeStructureField(FieldSchema schema, ReadOnlySpan<byte> payload, int fieldId)
    {
        if (fieldId > byte.MaxValue)
        {
            return null;
        }

        IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "nested-value");
        int matchIndex = -1;
        for (var index = 0; index < chunks.Count; index++)
        {
            if (chunks[index].Id == (byte)fieldId)
            {
                matchIndex = index;
                break;
            }
        }

        if (matchIndex < 0)
        {
            return null;
        }

        ChunkSlice match = chunks[matchIndex];

        FieldSchema fieldSchema = schema.NestedFields is not null &&
                                  schema.NestedFields.TryGetValue((byte)fieldId, out FieldSchema? known)
            ? known
            : new FieldSchema(DatabaseValueKind.Hex, $"Field {fieldId}", false);
        return DecodeValue(fieldSchema, payload.Slice(match.DataOffset, match.DataLength));
    }

    private static string FormatStructure(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<ChunkSlice> chunks)
    {
        var values = new List<string>(chunks.Count);
        foreach (ChunkSlice chunk in chunks)
        {
            FieldSchema fieldSchema = schema.NestedFields is not null &&
                                      schema.NestedFields.TryGetValue(chunk.Id, out FieldSchema? known)
                ? known
                : new FieldSchema(DatabaseValueKind.Hex, $"Field {chunk.Id}", false);
            object value = DecodeValue(fieldSchema, payload.Slice(chunk.DataOffset, chunk.DataLength));
            values.Add($"{fieldSchema.DisplayName}: {FormatInvariant(value)}");
        }

        return string.Join("; ", values);
    }

    private static object? DecodeBit(ReadOnlySpan<byte> payload, int index)
    {
        if (index >= 32 || payload.Length != 4)
        {
            return null;
        }

        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return (mask >> index) & 1U;
    }

    private static string DecodePackedStructure(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 36)
        {
            return ToCompactHex(payload);
        }

        var values = new string[9];
        for (var index = 0; index < 8; index++)
        {
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(index * 4, 4))
                .ToString("R", CultureInfo.InvariantCulture);
        }

        values[8] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(32, 4))
            .ToString(CultureInfo.InvariantCulture);
        return string.Join(", ", values);
    }

    private static object? DecodePackedField(
        ReadOnlySpan<byte> payload,
        DatabaseFieldProjection projection)
    {
        if (payload.Length != 36 || projection.Index is < 0 or > 8)
        {
            return null;
        }

        ReadOnlySpan<byte> value = payload.Slice(projection.Index * 4, 4);
        return string.Equals(projection.ValueKind, DatabaseValueKind.UInt32.ToString(), StringComparison.Ordinal)
            ? (object)BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadSingleLittleEndian(value);
    }

    private static string? DecodeEnumValue(
        FieldSchema schema,
        ReadOnlySpan<byte> payload,
        DatabaseFieldProjection projection)
    {
        if (!CanEditEnumValue(schema, payload, projection) || projection.ValueLabels is null)
        {
            return null;
        }

        long value = schema.Kind switch
        {
            DatabaseValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(payload),
            DatabaseValueKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(payload),
            DatabaseValueKind.Byte => payload[0],
            _ => 0,
        };
        string label = value is >= int.MinValue and <= int.MaxValue &&
                       projection.ValueLabels.TryGetValue((int)value, out string? known)
            ? known
            : "неизвестное значение";
        return $"{value.ToString(CultureInfo.InvariantCulture)} — {label}";
    }

    private static byte[][]? TryDecodePrimitiveChunkList(ReadOnlySpan<byte> payload, int elementSize)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        try
        {
            IReadOnlyList<ChunkSlice> chunks = ChunkCodec.ReadList(payload, 0, payload.Length, "primitive-list");
            if (chunks.Count == 0 || chunks.Any(item => item.Id != 1 || item.DataLength != elementSize))
            {
                return null;
            }

            var result = new byte[chunks.Count][];
            for (var index = 0; index < chunks.Count; index++)
            {
                ChunkSlice item = chunks[index];
                result[index] = payload.Slice(item.DataOffset, item.DataLength).ToArray();
            }

            return result;
        }
        catch (DatabaseFormatException)
        {
            return null;
        }
    }

    private static string FormatInvariant(object? value) => value switch
    {
        null => string.Empty,
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static string ToCompactHex(ReadOnlySpan<byte> payload)
    {
        const int visibleBytes = 32;
        var shown = payload[..Math.Min(payload.Length, visibleBytes)];
        var text = Convert.ToHexString(shown);
        return payload.Length > visibleBytes ? $"{text}… ({payload.Length} байт)" : text;
    }
}
