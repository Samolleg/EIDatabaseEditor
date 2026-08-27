using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

internal enum DatabaseComplexEncoding
{
    None,
    Structure,
    StructureList,
}

/// <summary>
/// Read-only display metadata reconstructed from the configuration shipped with the legacy
/// DBEditor. The two source files are embedded so a published build never depends on the old
/// editor being installed next to it.
/// </summary>
internal static partial class LegacySchemaCatalog
{
    private static readonly string[] ManualSkillNames = ["Science", "Stealing", "Tame"];

    private static readonly string[] AutoSkillNames =
        ["Melee", "Archery", "Backstab", "Elemental", "Senses", "Astral", "Stealth", "Awareness"];

    private static readonly IReadOnlyDictionary<TableKey, IReadOnlyDictionary<byte, FieldSchema>> Tables = Build();

    public static IReadOnlyDictionary<byte, FieldSchema> Merge(
        string sectionKey,
        byte sectionId,
        IReadOnlyDictionary<byte, FieldSchema> explicitFields)
    {
        string group = ToLegacyGroup(sectionKey);
        if (!Tables.TryGetValue(new TableKey(group, sectionId), out var legacyFields))
        {
            return explicitFields;
        }

        var merged = new Dictionary<byte, FieldSchema>(legacyFields.Count);
        foreach ((byte id, FieldSchema legacy) in legacyFields)
        {
            if (!explicitFields.TryGetValue(id, out FieldSchema? current))
            {
                merged.Add(id, legacy);
                continue;
            }

            DatabaseValueKind kind = legacy.Kind == DatabaseValueKind.Hex ? current.Kind : legacy.Kind;
            IReadOnlyList<DatabaseFieldProjection> projections = MergeProjections(current.Projections, legacy.Projections);
            merged.Add(id, new FieldSchema(
                kind,
                current.DisplayName,
                current.Editable,
                current.Key,
                projections,
                legacy.DisplayOrder,
                legacy.FixedElementSize,
                legacy.NestedFields,
                legacy.ComplexEncoding));
        }

        foreach ((byte id, FieldSchema current) in explicitFields)
        {
            merged.TryAdd(id, current);
        }

        return merged;
    }

    private static IReadOnlyList<DatabaseFieldProjection> MergeProjections(
        IReadOnlyList<DatabaseFieldProjection> current,
        IReadOnlyList<DatabaseFieldProjection> legacy)
    {
        if (current.Count == 0)
        {
            return legacy;
        }

        if (legacy.Count == 0)
        {
            return current;
        }

        var metadata = legacy.ToDictionary(item => (item.Kind, item.Index));
        return current.Select(item =>
        {
            if (!metadata.TryGetValue((item.Kind, item.Index), out DatabaseFieldProjection? source))
            {
                return item;
            }

            return item with
            {
                ValueKind = source.ValueKind,
                DisplayOrder = source.DisplayOrder,
            };
        }).ToArray();
    }

    private static IReadOnlyDictionary<TableKey, IReadOnlyDictionary<byte, FieldSchema>> Build()
    {
        IReadOnlyList<TypeDefinition> types = ReadTypes();
        IReadOnlyList<HeaderDefinition> headers = ReadHeaders();
        var nestedByKey = types
            .Where(item => item.BlockId >= 20)
            .GroupBy(item => new TableKey(item.Group, item.BlockId))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<byte, FieldSchema>)group.ToDictionary(
                    item => item.FieldId,
                    item => CreateNestedField(item.Group, item.FieldId, ParseType(item.LegacyType).Kind)));

        var result = new Dictionary<TableKey, IReadOnlyDictionary<byte, FieldSchema>>();
        foreach (IGrouping<TableKey, TypeDefinition> table in types
                     .Where(item => item.BlockId < 20)
                     .GroupBy(item => new TableKey(item.Group, item.BlockId)))
        {
            var fields = new Dictionary<byte, FieldSchema>();
            foreach (TypeDefinition definition in table.OrderBy(item => item.SourceOrder))
            {
                ParsedType parsedType = ParseType(definition.LegacyType);
                HeaderDefinition[] fieldHeaders = headers
                    .Where(item => string.Equals(item.Group, definition.Group, StringComparison.OrdinalIgnoreCase) &&
                                   item.BlockId == definition.BlockId &&
                                   item.FieldId == definition.FieldId)
                    .OrderBy(item => item.SourceOrder)
                    .ToArray();
                IReadOnlyDictionary<byte, FieldSchema>? nestedFields = null;
                if (parsedType.NestedBlockId is byte nestedBlockId)
                {
                    nestedByKey.TryGetValue(new TableKey(definition.Group, nestedBlockId), out nestedFields);
                }

                IReadOnlyList<DatabaseFieldProjection> projections = BuildProjections(
                    parsedType,
                    fieldHeaders,
                    nestedFields);
                HeaderDefinition? scalarHeader = fieldHeaders.LastOrDefault();
                string displayName = scalarHeader?.Name ?? $"Field {definition.FieldId}";
                int displayOrder = fieldHeaders.Length == 0
                    ? 10_000 + definition.SourceOrder
                    : fieldHeaders.Min(item => item.SourceOrder);
                fields.Add(definition.FieldId, new FieldSchema(
                    parsedType.Kind,
                    displayName,
                    false,
                    null,
                    projections,
                    displayOrder,
                    parsedType.FixedElementSize,
                    nestedFields,
                    parsedType.ComplexEncoding));
            }

            ApplyNativeSchemaCorrections(table.Key, fields);

            result.Add(table.Key, fields);
        }

        return result;
    }

    private static void ApplyNativeSchemaCorrections(TableKey table, IDictionary<byte, FieldSchema> fields)
    {
        if (table == new TableKey("Items", 2))
        {
            ReplaceList(fields, 22, DatabaseValueKind.UInt32List, ["Stat modifier 1", "Stat modifier 2"]);
            ReplaceList(fields, 27, DatabaseValueKind.ByteList, ManualSkillNames);
            ReplaceList(fields, 28, DatabaseValueKind.ByteList, AutoSkillNames);
        }
        else if (table == new TableKey("Items", 3))
        {
            ReplaceArmorAbsorption(fields, 21, "Main");
            ReplaceArmorAbsorption(fields, 22, "Additional");
            ReplaceList(fields, 25, DatabaseValueKind.ByteList, ManualSkillNames);
            ReplaceList(fields, 26, DatabaseValueKind.ByteList, AutoSkillNames);
        }
        else if (table == new TableKey("Items", 4))
        {
            ReplaceKind(fields, 21, DatabaseValueKind.Int32, "Quick item type");
            ReplaceKind(fields, 24, DatabaseValueKind.Int32, "Damage type");
            ReplaceList(fields, 27, DatabaseValueKind.ByteList, AutoSkillNames);
        }
        else if (table == new TableKey("Items", 6))
        {
            ReplaceKind(fields, 21, DatabaseValueKind.Int32, "Loot type");
            ReplaceKind(fields, 22, DatabaseValueKind.Int32, "Instruction type");
        }
        else if (table == new TableKey("Quests", 1))
        {
            ReplaceKind(fields, 2, DatabaseValueKind.Float32, "Total experience");
            ReplaceKind(fields, 5, DatabaseValueKind.Float32, "Money");
            ReplaceKind(fields, 7, DatabaseValueKind.Float32List, "Places (X, Y)", []);
        }
        else if (table == new TableKey("Units", 2))
        {
            ReplaceKind(fields, 47, DatabaseValueKind.Int32);
            ReplaceKind(fields, 48, DatabaseValueKind.Int32);
            ReplaceKind(fields, 49, DatabaseValueKind.Int32);
            ReplaceKind(fields, 50, DatabaseValueKind.Int32);
        }
        else if (table == new TableKey("Units", 3))
        {
            ReplaceKind(fields, 2, DatabaseValueKind.UInt32);
            ReplaceKind(fields, 4, DatabaseValueKind.UInt32);
            ReplaceKind(fields, 41, DatabaseValueKind.Int32, "Base level");
        }
    }

    private static void ReplaceKind(
        IDictionary<byte, FieldSchema> fields,
        byte fieldId,
        DatabaseValueKind kind,
        string? displayName = null,
        IReadOnlyList<DatabaseFieldProjection>? projections = null)
    {
        FieldSchema source = fields[fieldId];
        fields[fieldId] = new FieldSchema(
            kind,
            displayName ?? source.DisplayName,
            source.Editable,
            source.Key,
            projections ?? source.Projections,
            source.DisplayOrder);
    }

    private static void ReplaceList(
        IDictionary<byte, FieldSchema> fields,
        byte fieldId,
        DatabaseValueKind kind,
        IReadOnlyList<string> names)
    {
        FieldSchema source = fields[fieldId];
        string valueKind = kind switch
        {
            DatabaseValueKind.UInt32List => DatabaseValueKind.UInt32.ToString(),
            DatabaseValueKind.Float32List => DatabaseValueKind.Float32.ToString(),
            _ => DatabaseValueKind.Byte.ToString(),
        };
        DatabaseFieldProjection[] projections = names
            .Select((name, index) => new DatabaseFieldProjection(
                DatabaseFieldProjectionKind.ListElement,
                index,
                name,
                null,
                valueKind,
                source.DisplayOrder + index))
            .ToArray();
        ReplaceKind(fields, fieldId, kind, projections: projections);
    }

    private static void ReplaceArmorAbsorption(
        IDictionary<byte, FieldSchema> fields,
        byte fieldId,
        string prefix)
    {
        FieldSchema source = fields[fieldId];
        string[] names =
        [
            $"{prefix} absorption",
            $"{prefix} piercing",
            $"{prefix} slashing",
            $"{prefix} bludgeoning",
            $"{prefix} thermal",
            $"{prefix} chemical",
            $"{prefix} electrical",
            $"{prefix} general",
            $"{prefix} cripple mask",
        ];
        DatabaseFieldProjection[] projections = names
            .Select((name, index) => new DatabaseFieldProjection(
                DatabaseFieldProjectionKind.PackedField,
                index,
                name,
                null,
                index == 8 ? DatabaseValueKind.UInt32.ToString() : DatabaseValueKind.Float32.ToString(),
                source.DisplayOrder + index))
            .ToArray();
        ReplaceKind(fields, fieldId, DatabaseValueKind.PackedStructure, projections: projections);
    }

    private static IReadOnlyList<DatabaseFieldProjection> BuildProjections(
        ParsedType parsedType,
        IReadOnlyList<HeaderDefinition> headers,
        IReadOnlyDictionary<byte, FieldSchema>? nestedFields)
    {
        DatabaseFieldProjectionKind? projectionKind = parsedType.Kind switch
        {
            DatabaseValueKind.Float32List or DatabaseValueKind.UInt32List or DatabaseValueKind.ByteList =>
                DatabaseFieldProjectionKind.ListElement,
            DatabaseValueKind.UInt32 when parsedType.BitCount is not null => DatabaseFieldProjectionKind.Bit,
            DatabaseValueKind.Nested when parsedType.ComplexEncoding == DatabaseComplexEncoding.Structure =>
                DatabaseFieldProjectionKind.NestedField,
            _ => null,
        };
        if (projectionKind is null)
        {
            return [];
        }

        var result = new List<DatabaseFieldProjection>(headers.Count);
        var identities = new HashSet<(DatabaseFieldProjectionKind Kind, int Index)>();
        foreach (HeaderDefinition header in headers)
        {
            int index = header.FieldIndex;
            if (projectionKind == DatabaseFieldProjectionKind.Bit &&
                parsedType.BitCount is int bitCount && index >= bitCount)
            {
                continue;
            }

            var identity = (projectionKind.Value, index);
            if (!identities.Add(identity))
            {
                continue;
            }

            string valueKind = projectionKind switch
            {
                DatabaseFieldProjectionKind.Bit => DatabaseValueKind.Boolean.ToString(),
                DatabaseFieldProjectionKind.ListElement when parsedType.Kind == DatabaseValueKind.Float32List =>
                    DatabaseValueKind.Float32.ToString(),
                DatabaseFieldProjectionKind.ListElement when parsedType.Kind == DatabaseValueKind.UInt32List =>
                    DatabaseValueKind.UInt32.ToString(),
                DatabaseFieldProjectionKind.ListElement => DatabaseValueKind.Byte.ToString(),
                DatabaseFieldProjectionKind.NestedField when nestedFields is not null &&
                                                               nestedFields.TryGetValue((byte)index, out FieldSchema? nested) =>
                    nested.Kind.ToString(),
                _ => parsedType.Kind.ToString(),
            };
            result.Add(new DatabaseFieldProjection(
                projectionKind.Value,
                index,
                header.Name,
                header.Description,
                valueKind,
                header.SourceOrder));
        }

        return result;
    }

    private static FieldSchema CreateNestedField(string group, byte fieldId, DatabaseValueKind kind) =>
        new(kind, GetNestedFieldName(group, fieldId), false, null, null, fieldId);

    private static string GetNestedFieldName(string group, byte fieldId) => (group, fieldId) switch
    {
        ("Prints", 1) => "Power",
        ("Prints", 2) => "Lifetime",
        ("Prints", 3) => "Fadeout",
        ("Units", 0) => "Type",
        ("Units", 1) => "Type index",
        ("Units", 2) => "Weight / minimum",
        ("Units", 3) => "Vitality / maximum",
        ("Acks", 1) => "File",
        ("Acks", 2) => "Probability",
        ("Acks", 3) => "Description",
        ("Acks", 4) => "Key",
        ("Acks", 10) => "Usage flags",
        _ => $"Field {fieldId}",
    };

    private static IReadOnlyList<TypeDefinition> ReadTypes()
    {
        var result = new List<TypeDefinition>();
        string? group = null;
        int sourceOrder = 0;
        foreach (string rawLine in ReadResourceLines("dbtypes.txt"))
        {
            string line = rawLine.Trim();
            if (TryReadGroup(line, out string? nextGroup))
            {
                group = nextGroup;
                continue;
            }

            if (group is null || line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] columns = rawLine.Split('\t');
            if (columns.Length < 3 ||
                !byte.TryParse(columns[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte blockId) ||
                !byte.TryParse(columns[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte fieldId))
            {
                continue;
            }

            result.Add(new TypeDefinition(group, blockId, fieldId, columns[2].Trim(), sourceOrder++));
        }

        return result;
    }

    private static IReadOnlyList<HeaderDefinition> ReadHeaders()
    {
        var result = new List<HeaderDefinition>();
        string? group = null;
        int sourceOrder = 0;
        foreach (string rawLine in ReadResourceLines("dbheaders.txt"))
        {
            string line = rawLine.Trim();
            if (TryReadGroup(line, out string? nextGroup))
            {
                group = nextGroup;
                continue;
            }

            if (group is null || line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] columns = rawLine.Split('\t');
            if (columns.Length < 5 ||
                !byte.TryParse(columns[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte blockId) ||
                !byte.TryParse(columns[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte fieldId) ||
                !int.TryParse(columns[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fieldIndex))
            {
                continue;
            }

            string name = columns[4].Trim();
            string? description = columns.Length > 5 && !string.IsNullOrWhiteSpace(columns[5])
                ? columns[5].Trim()
                : null;

            // The legacy configuration repeats index 2 for the fadeout member of the Prints
            // structures. Native STPParams serialization confirms that this is member 3.
            if (string.Equals(group, "Prints", StringComparison.OrdinalIgnoreCase) &&
                fieldId == 3 && fieldIndex == 2 &&
                name.Contains("Fadeout", StringComparison.OrdinalIgnoreCase))
            {
                fieldIndex = 3;
            }

            result.Add(new HeaderDefinition(
                group,
                blockId,
                fieldId,
                fieldIndex,
                name,
                description,
                sourceOrder++));
        }

        return result;
    }

    private static ParsedType ParseType(string text)
    {
        Match parameterized = ParameterizedTypeRegex().Match(text);
        string name = parameterized.Success ? parameterized.Groups[1].Value : text;
        int? parameter = parameterized.Success
            ? int.Parse(parameterized.Groups[2].Value, CultureInfo.InvariantCulture)
            : null;
        return name switch
        {
            "Hex" => new(DatabaseValueKind.Hex),
            "String" => new(DatabaseValueKind.String),
            "StringList" => new(DatabaseValueKind.StringList),
            "FixedString" => new(DatabaseValueKind.FixedString, FixedElementSize: parameter),
            "MinfixedStringList" => new(DatabaseValueKind.FixedStringList, FixedElementSize: parameter),
            "UnsignedLong" => new(DatabaseValueKind.UInt32),
            "UnsignedLongList" => new(DatabaseValueKind.UInt32List),
            "SignedLong" => new(DatabaseValueKind.Int32),
            "Float" or "IndexedFloat" => new(DatabaseValueKind.Float32),
            "FloatList" or "IndexedFloatList" => new(DatabaseValueKind.Float32List),
            "Byte" => new(DatabaseValueKind.Byte),
            "ByteList" => new(DatabaseValueKind.ByteList),
            "BitByte" => new(DatabaseValueKind.Byte, BitCount: parameter ?? 8),
            "BitList" => new(DatabaseValueKind.UInt32, BitCount: parameter),
            "TypeList" => new(
                DatabaseValueKind.Nested,
                NestedBlockId: checked((byte?)parameter),
                ComplexEncoding: DatabaseComplexEncoding.Structure),
            "AcksUniqueType" => new(
                DatabaseValueKind.StructuredList,
                NestedBlockId: checked((byte?)parameter),
                ComplexEncoding: DatabaseComplexEncoding.StructureList),
            _ => new(DatabaseValueKind.Hex),
        };
    }

    private static IEnumerable<string> ReadResourceLines(string suffix)
    {
        Assembly assembly = typeof(LegacySchemaCatalog).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith($"LegacySchemas.{suffix}", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded schema resource '{resourceName}' is unavailable.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool TryReadGroup(string line, out string? group)
    {
        if (line.Length >= 3 && line[0] == '[' && line[^1] == ']')
        {
            group = line[1..^1].Trim();
            return group.Length > 0;
        }

        group = null;
        return false;
    }

    private static string ToLegacyGroup(string sectionKey)
    {
        int separator = sectionKey.IndexOf('.');
        string prefix = separator < 0 ? sectionKey : sectionKey[..separator];
        return prefix.ToLowerInvariant() switch
        {
            "items" => "Items",
            "levers" => "Levers",
            "perks" => "Perks",
            "prints" => "Prints",
            "spells" => "Spells",
            "units" => "Units",
            "acks" => "Acks",
            "quests" => "Quests",
            _ => prefix,
        };
    }

    [GeneratedRegex("^([A-Za-z]+)\\((\\d+)\\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterizedTypeRegex();

    private readonly record struct TableKey(string Group, byte BlockId);

    private sealed record TypeDefinition(
        string Group,
        byte BlockId,
        byte FieldId,
        string LegacyType,
        int SourceOrder);

    private sealed record HeaderDefinition(
        string Group,
        byte BlockId,
        byte FieldId,
        int FieldIndex,
        string Name,
        string? Description,
        int SourceOrder);

    private readonly record struct ParsedType(
        DatabaseValueKind Kind,
        int? BitCount = null,
        int? FixedElementSize = null,
        byte? NestedBlockId = null,
        DatabaseComplexEncoding ComplexEncoding = DatabaseComplexEncoding.None);
}
