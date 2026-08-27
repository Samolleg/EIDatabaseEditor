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

    public bool IsEditable => _schema.Editable && DatabaseValueCodec.CanEdit(_schema.Kind, OriginalPayload);

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

    internal bool TryGetStringListValues(out IReadOnlyList<string> values)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.TryDecodeStringListValues(_schema, Payload, out values);
        }
    }

    internal bool TryGetPrimitiveListCount(out int count)
    {
        lock (_editSync)
        {
            return DatabaseValueCodec.TryGetPrimitiveListCount(_schema, Payload, out count);
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

            if (!DatabaseValueCodec.TryEncode(_schema.Kind, value, OriginalPayload, out var encoded, out error))
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

public enum DatabaseFieldProjectionKind
{
    ListElement,
    Bit,
    NestedField,
    PackedField,
}

public sealed record DatabaseFieldProjection(
    DatabaseFieldProjectionKind Kind,
    int Index,
    string DisplayName,
    string? Description = null,
    string? ValueKind = null,
    int DisplayOrder = int.MaxValue);

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
    DatabaseComplexEncoding ComplexEncoding = DatabaseComplexEncoding.None)
{
    public IReadOnlyList<DatabaseFieldProjection> Projections { get; } =
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

    public static bool CanEdit(DatabaseValueKind kind, ReadOnlySpan<byte> payload) => kind switch
    {
        DatabaseValueKind.String => true,
        DatabaseValueKind.FixedString => payload.Length > 0,
        DatabaseValueKind.Int32 or DatabaseValueKind.UInt32 or DatabaseValueKind.Float32 => payload.Length == 4,
        DatabaseValueKind.Byte or DatabaseValueKind.Boolean => payload.Length == 1,
        _ => false,
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

    public static bool TryEncode(
        DatabaseValueKind kind,
        string text,
        ReadOnlySpan<byte> original,
        out byte[] encoded,
        out string? error)
    {
        encoded = [];
        error = null;
        try
        {
            switch (kind)
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
                default:
                    error = "Этот тип поля пока доступен только для чтения.";
                    return false;
            }
        }
        catch (EncoderFallbackException)
        {
            error = "Строка содержит символы, которые нельзя сохранить в Windows-1251.";
            return false;
        }
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
