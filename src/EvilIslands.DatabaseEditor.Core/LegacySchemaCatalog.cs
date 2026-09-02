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
    private static readonly string[] ManualSkillNames = ["Наука", "Кража", "Приручение"];

    private static readonly string[] AutoSkillNames =
        ["Ближний бой", "Стрельба", "Удар в спину", "Стихийная магия", "Магия чувств", "Астральная магия", "Скрытность", "Внимательность"];

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
            Rename(fields, 2, "ID типа предмета");
            Rename(fields, 4, "Индекс типа материала");
            ReplaceList(fields, 22, DatabaseValueKind.UInt32List, "Модификаторы характеристик", ["Модификатор 1", "Модификатор 2"]);
            ReplaceList(fields, 27, DatabaseValueKind.ByteList, "Ручные навыки", ManualSkillNames);
            ReplaceList(fields, 28, DatabaseValueKind.ByteList, "Автонавыки", AutoSkillNames);
        }
        else if (table == new TableKey("Items", 3))
        {
            Rename(fields, 2, "ID типа предмета");
            Rename(fields, 4, "Индекс типа материала");
            ReplaceArmorAbsorption(fields, 21, "Основное");
            ReplaceArmorAbsorption(fields, 22, "Дополнительное");
            ReplaceList(fields, 25, DatabaseValueKind.ByteList, "Ручные навыки", ManualSkillNames);
            ReplaceList(fields, 26, DatabaseValueKind.ByteList, "Автонавыки", AutoSkillNames);
        }
        else if (table == new TableKey("Items", 4))
        {
            Rename(fields, 2, "ID типа предмета");
            Rename(fields, 4, "Индекс типа материала");
            ReplaceKind(fields, 21, DatabaseValueKind.Int32, "Quick item type");
            Rename(fields, 22, "Уровень навыка");
            Rename(fields, 23, "Урон");
            ReplaceKind(fields, 24, DatabaseValueKind.Int32, "Damage type");
            Rename(fields, 25, "Заклинание");
            ReplaceList(fields, 26, DatabaseValueKind.ByteList, "Ручные навыки", ManualSkillNames);
            ReplaceList(fields, 27, DatabaseValueKind.ByteList, "Автонавыки", AutoSkillNames);
            ReplaceEnum(fields, 21,
                "Датчик опасности", "Убеждение", "Ловушка", "Отмычка", "Лом",
                "Жезл", "Ключ", "Заглушка", "Свиток");
            ReplaceEnum(fields, 24,
                "Колющий", "Рубящий", "Дробящий", "Термический", "Химический",
                "Электрический", "Общий");
        }
        else if (table == new TableKey("Items", 5))
        {
            Rename(fields, 2, "ID типа предмета");
            Rename(fields, 4, "Индекс типа материала");
            Rename(fields, 21, "ID сценария");
            Rename(fields, 22, "Зоны");
        }
        else if (table == new TableKey("Items", 6))
        {
            Rename(fields, 2, "ID типа предмета");
            Rename(fields, 4, "Индекс типа материала");
            ReplaceKind(fields, 21, DatabaseValueKind.Int32, "Loot type");
            ReplaceKind(fields, 22, DatabaseValueKind.Int32, "Instruction type");
            Rename(fields, 23, "Индекс материала");
            ReplaceEnum(fields, 21, "Сокровище", "Материал", "Инструкция");
            ReplaceEnum(fields, 22, "Оружие", "Броня", "Быстрый предмет", "Прототип", "Руна");
        }
        else if (table == new TableKey("Levers", 1))
        {
            Rename(fields, 1, "Координаты мест использования");
            Rename(fields, 2, "Количество мест использования");
            Rename(fields, 3, "Минимальный масштаб");
            Rename(fields, 4, "Время переключения");
            Rename(fields, 5, "Материал");
            Rename(fields, 6, "Звук переключения");
            Rename(fields, 7, "Описание");
        }
        else if (table == new TableKey("Perks", 1))
        {
            Rename(fields, 0, "Имя");
            Rename(fields, 1, "Код");
            Rename(fields, 2, "Индекс текстуры");
            Rename(fields, 10, "Базовые характеристики");
        }
        else if (table == new TableKey("Perks", 2))
        {
            string[] names =
            [
                "Требуемый перк", "Требуемый навык", "Автоматический навык", "Ручной навык",
                "Уровень навыка", "Сила", "Ловкость", "Разум", "Стоимость", "Модификатор",
                "Множитель", "Прибавка", "Активный", "Взаимоисключающий перк",
            ];
            Rename(fields, 0, "Имя");
            Rename(fields, 1, "Код");
            Rename(fields, 2, "Индекс текстуры");
            for (var index = 0; index < names.Length; index++)
            {
                Rename(fields, (byte)(10 + index), names[index]);
            }
            ReplaceEnum(fields, 12,
                "Наука", "Кража", "Приручение");
            ReplaceEnum(fields, 13,
                "Ближний бой", "Стрельба", "Удар в спину", "Стихийная магия",
                "Магия чувств", "Астральная магия", "Скрытность", "Внимательность");
        }
        else if (table.Group == "Prints")
        {
            Rename(fields, 1, "Тип поверхности");
            Rename(fields, 2, "Ясная погода");
            Rename(fields, 3, "Осадки");
            if (table.BlockId == 2)
            {
                Rename(fields, 10, "Кровавый след");
            }
        }
        else if (table == new TableKey("Quests", 1))
        {
            ReplaceKind(fields, 2, DatabaseValueKind.Float32, "Total experience");
            ReplaceKind(fields, 5, DatabaseValueKind.Float32, "Money");
            ReplaceKind(fields, 7, DatabaseValueKind.Float32List, "Places (X, Y)", []);
        }
        else if (table == new TableKey("Spells", 1))
        {
            ReplaceEnum(fields, 4, "Стихийная", "Чувства", "Астральная", "Неизвестная");
            ReplaceEnum(fields, 19,
                "Огонь", "Молния", "Кислота", "Иллюзия", "Прорицание",
                "Чары", "Исцеление", "Подчинение", "Неизвестный");
        }
        else if (table == new TableKey("Spells", 2))
        {
            ReplaceEnum(fields, 3,
                "Дальность", "Цели", "Область", "Эффект", "Длительность",
                "Мана", "Фильтр", "Предмет", "Неизвестный");
        }
        else if (table == new TableKey("Quests", 2))
        {
            string[] names =
            [
                "Опыт", "Деньги", "Выдать предметы", "Сценарий брифинга", "Забрать предметы",
                "Выдать квесты", "Завершить квесты", "Открыть зоны", "Завершить зоны", "Специальный номер",
            ];
            for (var index = 0; index < names.Length; index++)
            {
                Rename(fields, (byte)(1 + index), names[index]);
            }
        }
        else if (table == new TableKey("Units", 1))
        {
            Rename(fields, 3, "Маска травм");
            Rename(fields, 4, "Тип зоны попадания");
            ReplaceEnum(fields, 4, "Голова", "Тело", "Рука", "Нога", "Неизвестный");
        }
        else if (table == new TableKey("Units", 2))
        {
            ReplaceEnum(fields, 5, "Ходьба", "Левитация", "Неподвижный", "Полёт");
            ReplaceKind(fields, 47, DatabaseValueKind.Int32);
            ReplaceKind(fields, 48, DatabaseValueKind.Int32);
            ReplaceKind(fields, 49, DatabaseValueKind.Int32);
            ReplaceKind(fields, 50, DatabaseValueKind.Int32);
        }
        else if (table == new TableKey("Units", 3))
        {
            ReplaceKind(fields, 2, DatabaseValueKind.UInt32);
            Rename(fields, 2, "Сохранённый индекс расы");
            Rename(fields, 8, "Путь SFX");
            ReplaceKind(fields, 4, DatabaseValueKind.UInt32);
            ReplaceKind(fields, 41, DatabaseValueKind.Int32, "Base level");
            string[] weaponTypes =
            [
                "Меч", "Топор", "Кинжал", "Копьё", "Молот", "Лук", "Арбалет",
                "Неизвестный", "Без оружия", "Без оружия 1", "Без оружия 2", "Без оружия 3",
                "Без оружия 4", "Без оружия 5", "Без оружия 6", "Без оружия 7", "Магия",
            ];
            ReplaceEnum(fields, 18, weaponTypes);
            ReplaceEnum(fields, 39, weaponTypes);
        }
        else if (table == new TableKey("Units", 4))
        {
            string[] names =
            [
                "ID типа персонажа", "Опыт", "Сила", "Ловкость", "Разум", "Ручные навыки",
                "Автонавыки", "Перки", "Оружие", "Быстрые предметы", "Заклинания",
                "Опыт к распределению", "Деньги", "Голосовой прототип",
            ];
            for (var index = 0; index < names.Length; index++)
            {
                Rename(fields, (byte)(1 + index), names[index]);
            }
            ReplaceList(fields, 6, DatabaseValueKind.ByteList, "Ручные навыки", ManualSkillNames);
            ReplaceList(fields, 7, DatabaseValueKind.ByteList, "Автонавыки", AutoSkillNames);
        }
        else if (table == new TableKey("Acks", 1))
        {
            string[] names =
            [
                "Выбор", "Движение", "Атака", "Заклинание", "Подбор", "Использование объекта",
                "Кража", "Следование", "Зелье", "Смена позы", "Нет пути для атаки",
                "Нельзя сменить позу", "Путь не найден", "Нельзя колдовать", "Нельзя телепортироваться",
                "Проверка науки не удалась", "Цель не видна", "Заклинание слишком сложное",
                "Решил атаковать", "Нет выносливости", "Повреждена рука", "Повреждена нога",
                "Скука", "Цель слишком высоко", "Перегрузка", "Травма", "Слишком большая атака",
                "Повреждена броня", "Повреждено оружие", "Атакован при защите", "Ожидание спутников",
                "Сценарная реплика", "Кража: пусто", "Шёпот: да", "Шёпот: нет",
            ];
            for (var index = 0; index < names.Length; index++)
            {
                Rename(fields, (byte)(10 + index), names[index]);
            }
        }
        else if (table == new TableKey("Acks", 2))
        {
            string[] names = ["Агрессия", "Подозрение", "Убийство", "Отдых", "Агрессивное состояние"];
            for (var index = 0; index < names.Length; index++)
            {
                Rename(fields, (byte)(10 + index), names[index]);
            }
        }
        else if (table == new TableKey("Acks", 3))
        {
            Rename(fields, 10, "Выбран для разговора");
            Rename(fields, 11, "Отдых");
        }
    }

    private static void Rename(IDictionary<byte, FieldSchema> fields, byte fieldId, string displayName)
    {
        if (fields.TryGetValue(fieldId, out FieldSchema? source))
        {
            fields[fieldId] = source with { DisplayName = displayName };
        }
    }

    private static void ReplaceEnum(
        IDictionary<byte, FieldSchema> fields,
        byte fieldId,
        params string[] labels)
    {
        if (!fields.TryGetValue(fieldId, out FieldSchema? source))
        {
            return;
        }

        IReadOnlyDictionary<int, string> values = labels
            .Select((label, index) => (label, index))
            .ToDictionary(item => item.index, item => item.label);
        DatabaseFieldProjection projection = new(
            DatabaseFieldProjectionKind.EnumValue,
            0,
            source.DisplayName,
            "Подтверждённое перечисление из штатных структур игры. Можно вводить номер или название.",
            source.Kind.ToString(),
            source.DisplayOrder,
            values);
        fields[fieldId] = source with { ProjectionMetadata = [projection] };
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
        string displayName,
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
        ReplaceKind(fields, fieldId, kind, displayName, projections);
    }

    private static void ReplaceArmorAbsorption(
        IDictionary<byte, FieldSchema> fields,
        byte fieldId,
        string prefix)
    {
        FieldSchema source = fields[fieldId];
        string[] names =
        [
            $"{prefix} поглощение",
            $"{prefix} — колющий",
            $"{prefix} — рубящий",
            $"{prefix} — дробящий",
            $"{prefix} — термический",
            $"{prefix} — химический",
            $"{prefix} — электрический",
            $"{prefix} — общий",
            $"{prefix} — маска травм",
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
        ReplaceKind(fields, fieldId, DatabaseValueKind.PackedStructure, $"{prefix} поглощение", projections);
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
            string displayName = projectionKind == DatabaseFieldProjectionKind.NestedField &&
                                 nestedFields is not null &&
                                 nestedFields.TryGetValue((byte)index, out FieldSchema? nestedDisplay)
                ? nestedDisplay.DisplayName
                : header.Name;
            result.Add(new DatabaseFieldProjection(
                projectionKind.Value,
                index,
                displayName,
                header.Description,
                valueKind,
                header.SourceOrder));
        }

        return result;
    }

    private static FieldSchema CreateNestedField(string group, byte fieldId, DatabaseValueKind kind) =>
        new(kind, GetNestedFieldName(group, fieldId), true, null, null, fieldId);

    private static string GetNestedFieldName(string group, byte fieldId) => (group, fieldId) switch
    {
        ("Prints", 1) => "Сила / прозрачность",
        ("Prints", 2) => "Время жизни",
        ("Prints", 3) => "Время затухания",
        ("Units", 0) => "Тип / вероятность",
        ("Units", 1) => "Индекс типа / маска",
        ("Units", 2) => "Вес / минимальная цена",
        ("Units", 3) => "Живучесть / максимальная цена",
        ("Acks", 1) => "Файл",
        ("Acks", 2) => "Вероятность",
        ("Acks", 3) => "Описание",
        ("Acks", 4) => "Ключ",
        ("Acks", 10) => "Флаги условий",
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
