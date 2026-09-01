using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

internal static class SemanticDatabaseValidator
{
    private static readonly KeyRule[] KeyRules =
    [
        new("spells.prototypes", 1, true, true),
        new("spells.modifiers", 1, true, true),
    ];

    private static readonly NumericRangeRule[] NumericRangeRules =
    [
        new("items.weapons", 2, 0, 16, "тип оружия"),
        new("items.armors", 2, 0, 7, "тип брони"),
        new("items.quick_items", 21, 0, 8, "тип быстрого предмета"),
        new("items.quick_items", 24, 0, 6, "тип урона"),
        new("spells.prototypes", 4, 0, 3, "школа магии"),
        new("spells.prototypes", 9, 0, null, "область"),
        new("spells.prototypes", 19, 0, 8, "подтип магии"),
        new("spells.modifiers", 3, 0, 8, "тип модификатора"),
        new("units.hit_locations", 4, 0, 4, "тип зоны попадания"),
        new("units.races", 5, 0, 3, "тип передвижения"),
        new("units.races", 37, 0, 100, "вероятность звука шага"),
        new("units.races", 38, 0, 100, "вероятность звука атаки"),
        new("units.prototypes", 18, 0, 16, "тип оружия 1"),
        new("units.prototypes", 39, 0, 16, "тип оружия 2"),
        new("levers.prototypes", 2, 0, 2, "число допустимых позиций"),
    ];

    private static readonly BooleanRule[] BooleanRules =
    [
        new("items.weapons", 15),
        new("items.armors", 15),
        new("items.armors", 23),
        new("items.quick_items", 15),
        new("items.quest_items", 15),
        new("items.loot_items", 15),
        new("perks.perks", 22),
        new("units.races", 45),
        new("units.npcs", 14),
    ];

    private static readonly EmbeddedItemFieldRule[] EmbeddedItemFieldRules =
    [
        new("units.prototypes", 29, true),
        new("units.prototypes", 32, true),
        new("units.prototypes", 33, false),
        new("units.prototypes", 42, false),
        new("units.npcs", 9, true),
        new("quests.briefings", 3, true),
        new("quests.briefings", 5, true),
    ];

    private static readonly NumericProjectionRangeRule[] NumericProjectionRangeRules =
    [
        new("units.prototypes", 27, 0, 0, 100, "вероятность гарантированного лута"),
        new("units.prototypes", 28, 0, 0, 100, "вероятность случайного лута"),
    ];

    public static IReadOnlyList<ValidationDiagnostic> ValidateStatic(
        IReadOnlyList<DatabaseGroup> databases,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ValidationDiagnostic>();
        foreach (DatabaseTable table in databases.SelectMany(group => group.Tables))
        {
            foreach (DatabaseRecord record in table.Records)
            {
                if ((record.RecordIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                foreach (DatabaseField field in record.Fields.Where(candidate => candidate.Projections.Count > 0))
                {
                    ValidateProjectionLayout(table, record, field, diagnostics);
                }
            }
        }

        return diagnostics;
    }

    public static void ValidateDynamic(
        IReadOnlyList<DatabaseGroup> databases,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var tables = databases
            .SelectMany(group => group.Tables)
            .ToDictionary(table => table.Key, StringComparer.Ordinal);

        ValidatePendingAddedRecords(tables, diagnostics, cancellationToken);
        ValidateKeys(tables, diagnostics, cancellationToken);
        ValidateScalarReferences(tables, diagnostics, cancellationToken);
        ValidateRaceHitLocations(tables, diagnostics, cancellationToken);
        ValidateSpellReferences(tables, diagnostics, cancellationToken);
        ValidateStringListElementBoundaries(tables, diagnostics, cancellationToken);
        ValidateEmbeddedItemMagic(tables, diagnostics, cancellationToken);
        ValidateNumericRanges(tables, diagnostics, cancellationToken);
        ValidateNumericProjectionRanges(tables, diagnostics, cancellationToken);
        ValidateMaterialIndices(tables, diagnostics, cancellationToken);
        ValidateLootItemEnums(tables, diagnostics, cancellationToken);
        ValidatePerkSkillRequirements(tables, diagnostics, cancellationToken);
        ValidateBooleanValues(tables, diagnostics, cancellationToken);
    }

    private static void ValidateStringListElementBoundaries(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseTable table in tables.Values)
        {
            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (DatabaseField field in record.Fields)
                {
                    if (!field.TryGetStringListValues(out IReadOnlyList<string> values))
                    {
                        continue;
                    }

                    string? joinedValue = values.FirstOrDefault(ContainsTopLevelListSeparator);
                    if (joinedValue is null)
                    {
                        continue;
                    }

                    AddBlockingSemanticError(
                        diagnostics,
                        "SEM_STRING_LIST_JOINED",
                        $"«{record.Name}»: поле «{field.DisplayName}» содержит несколько значений, " +
                        "склеенных в один элемент списка. Разделите их запятыми или точками с запятой и примените ячейку.",
                        table,
                        record,
                        field);
                }
            }
        }
    }

    private static bool ContainsTopLevelListSeparator(string value)
    {
        var delimiters = new Stack<char>();
        foreach (char character in value)
        {
            if (character is '[' or '{')
            {
                delimiters.Push(character);
                continue;
            }

            if (character is ']' or '}')
            {
                char expected = character == ']' ? '[' : '{';
                if (delimiters.Count > 0 && delimiters.Peek() == expected)
                {
                    delimiters.Pop();
                }

                continue;
            }

            if (delimiters.Count == 0 && character is ',' or ';' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidatePendingAddedRecords(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseTable table in tables.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseRecord[] added = table.Records.Where(record => record.IsNew).ToArray();
            if (added.Length == 0)
            {
                continue;
            }

            var identityCounts = table.Records
                .Select(record => record.IdentityValue.Trim())
                .Where(value => value.Length > 0)
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (DatabaseRecord record in added)
            {
                DatabaseField? field = FindField(record, record.IdentityFieldId);
                string identity = record.IdentityValue.Trim();
                if (field is null || identity.Length == 0)
                {
                    AddBlockingAddedRecordError(
                        diagnostics,
                        "ADD_IDENTITY_REQUIRED",
                        $"Новая строка «{record.Name}» не имеет обязательного технического ключа.",
                        table,
                        record,
                        field);
                    continue;
                }

                if (identityCounts.TryGetValue(identity, out int count) && count > 1)
                {
                    AddBlockingAddedRecordError(
                        diagnostics,
                        "ADD_IDENTITY_DUPLICATE",
                        $"Новая строка использует уже существующий технический ключ «{identity}». " +
                        "Измените его перед сохранением копии.",
                        table,
                        record,
                        field);
                }

                if (table.Key.Equals("items.materials", StringComparison.Ordinal))
                {
                    DatabaseField? materialId = FindField(record, 3);
                    if (materialId is null ||
                        !TryGetInt32(materialId.Value, out int id) ||
                        id != record.RecordIndex)
                    {
                        AddBlockingAddedRecordError(
                            diagnostics,
                            "ADD_MATERIAL_ID_MISMATCH",
                            $"Новый материал «{record.Name}» должен иметь ID {record.RecordIndex}, равный " +
                            "его физической позиции в таблице. Верните автоматически назначенное значение.",
                            table,
                            record,
                            materialId);
                    }
                }
            }
        }
    }

    private static void ValidateProjectionLayout(
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField field,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        bool primitiveList = field.Kind is "Float32List" or "UInt32List" or "ByteList";
        if (primitiveList)
        {
            if (!field.TryGetPrimitiveListCount(out int actualCount))
            {
                AddWarning(
                    diagnostics,
                    "SEM_VALUE_FORMAT",
                    $"«{record.Name}»: поле «{field.DisplayName}» не удалось прочитать как типизированный список.",
                    table,
                    record,
                    field);
                return;
            }

            if (actualCount != field.Projections.Count)
            {
                AddWarning(
                    diagnostics,
                    "SEM_LIST_LENGTH",
                    $"«{record.Name}»: поле «{field.DisplayName}» содержит {actualCount} элементов; " +
                    $"по подтверждённой структуре ожидается {field.Projections.Count}.",
                    table,
                    record,
                    field);
                return;
            }
        }

        string[] missing = field.Projections
            .Where(projection => field.GetProjectedValue(projection) is null)
            .Select(projection => projection.DisplayName)
            .ToArray();
        if (missing.Length > 0)
        {
            AddWarning(
                diagnostics,
                "SEM_STRUCTURE_MEMBER_MISSING",
                $"«{record.Name}»: в поле «{field.DisplayName}» отсутствуют ожидаемые значения: " +
                string.Join(", ", missing),
                table,
                record,
                field);
            return;
        }

        if (field.Projections.All(projection => projection.Kind == DatabaseFieldProjectionKind.Bit) &&
            TryGetUInt32(field.Value, out uint mask))
        {
            uint allowedMask = 0;
            foreach (DatabaseFieldProjection projection in field.Projections)
            {
                allowedMask |= 1U << projection.Index;
            }

            uint unknownBits = mask & ~allowedMask;
            if (unknownBits != 0)
            {
                AddWarning(
                    diagnostics,
                    "SEM_UNKNOWN_MASK_BITS",
                    $"«{record.Name}»: поле «{field.DisplayName}» использует неподтверждённые биты " +
                    $"0x{unknownBits:X8}.",
                    table,
                    record,
                    field);
            }
        }

    }

    private static void ValidateKeys(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (KeyRule rule in KeyRules)
        {
            if (!tables.TryGetValue(rule.TableKey, out DatabaseTable? table))
            {
                continue;
            }

            var values = new List<(string Value, DatabaseRecord Record, DatabaseField Field)>();
            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.FieldId);
                string value = GetText(field);
                if (value.Length == 0)
                {
                    if (rule.Required && field is not null)
                    {
                        AddWarning(
                            diagnostics,
                            "SEM_REQUIRED_VALUE_MISSING",
                            $"В записи «{record.Name}» не заполнено обязательное поле «{field.DisplayName}».",
                            table,
                            record,
                            field);
                    }

                    continue;
                }

                if (field is not null)
                {
                    values.Add((value, record, field));
                }
            }

            if (!rule.Unique)
            {
                continue;
            }

            foreach (IGrouping<string, (string Value, DatabaseRecord Record, DatabaseField Field)> duplicate in values
                         .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                int count = duplicate.Count();
                foreach ((string _, DatabaseRecord record, DatabaseField field) in duplicate)
                {
                    AddWarning(
                        diagnostics,
                        "SEM_DUPLICATE_KEY",
                        $"Ключ «{field.DisplayName}» = «{duplicate.Key}» повторяется в таблице " +
                        $"«{table.Title}» {count} раза.",
                        table,
                        record,
                        field);
                }
            }
        }
    }

    private static void ValidateScalarReferences(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseScalarReference rule in DatabaseReferenceCatalog.ScalarReferences)
        {
            if (!tables.TryGetValue(rule.SourceTableKey, out DatabaseTable? source) ||
                !tables.TryGetValue(rule.TargetTableKey, out DatabaseTable? target))
            {
                continue;
            }

            HashSet<string> targetKeys = BuildKeySet(target, rule.TargetFieldId);
            foreach (DatabaseRecord record in source.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.SourceFieldId);
                string value = GetText(field);
                if (value.Length == 0 ||
                    (rule.Sentinel is not null && value.Equals(rule.Sentinel, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!rule.AllowEmpty && field is not null)
                    {
                        AddWarning(
                            diagnostics,
                            "SEM_REQUIRED_VALUE_MISSING",
                            $"В записи «{record.Name}» не заполнено обязательное поле «{field.DisplayName}».",
                            source,
                            record,
                            field);
                    }

                    continue;
                }

                if (field is not null && !targetKeys.Contains(value))
                {
                    AddMissingReference(diagnostics, source, record, field, value, target);
                }
            }
        }
    }

    private static void ValidateRaceHitLocations(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("units.races", out DatabaseTable? races) ||
            !tables.TryGetValue("units.hit_locations", out DatabaseTable? hitLocations))
        {
            return;
        }

        HashSet<string> hitLocationNames = BuildKeySet(hitLocations, 0);
        foreach (DatabaseRecord race in races.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int fieldId = 12; fieldId <= 17; fieldId++)
            {
                DatabaseField? field = FindField(race, fieldId);
                if (field is null)
                {
                    continue;
                }

                DatabaseFieldProjection? typeProjection = field.Projections.FirstOrDefault(projection =>
                    projection.Kind == DatabaseFieldProjectionKind.NestedField && projection.Index == 0);
                DatabaseFieldProjection? weightProjection = field.Projections.FirstOrDefault(projection =>
                    projection.Kind == DatabaseFieldProjectionKind.NestedField && projection.Index == 2);
                string type = typeProjection is null ? string.Empty :
                    field.GetProjectedValue(typeProjection)?.ToString()?.Trim() ?? string.Empty;
                double weight = 0;
                bool hasWeight = weightProjection is not null &&
                    TryGetDouble(field.GetProjectedValue(weightProjection), out weight);
                bool finiteWeight = hasWeight && double.IsFinite(weight);
                bool active = finiteWeight && weight != 0;

                if (active && type.Length == 0)
                {
                    AddWarning(
                        diagnostics,
                        "SEM_REQUIRED_VALUE_MISSING",
                        $"«{race.Name}»: активная зона попадания «{field.DisplayName}» не содержит типа.",
                        races,
                        race,
                        field);
                }
                else if (active && type.Length > 0 && !hitLocationNames.Contains(type))
                {
                    AddMissingReference(diagnostics, races, race, field, type, hitLocations);
                }

                if (hasWeight && !finiteWeight)
                {
                    AddWarning(
                        diagnostics,
                        "SEM_NONFINITE_FLOAT",
                        $"«{race.Name}»: вес зоны «{field.DisplayName}» не является конечным числом.",
                        races,
                        race,
                        field);
                }
                else if (finiteWeight && (weight < 0 || (fieldId == 13 && weight <= 0)))
                {
                    string expectation = fieldId == 13 ? "больше нуля" : "не меньше нуля";
                    AddWarning(
                        diagnostics,
                        "SEM_VALUE_OUT_OF_RANGE",
                        $"«{race.Name}»: вес зоны «{field.DisplayName}» равен {FormatNumber(weight)}; " +
                        $"ожидается значение {expectation}.",
                        races,
                        race,
                        field);
                }
            }
        }
    }

    private static void ValidateSpellReferences(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("spells.prototypes", out DatabaseTable? spells) ||
            !tables.TryGetValue("spells.modifiers", out DatabaseTable? modifiers))
        {
            return;
        }

        HashSet<string> spellCodes = BuildKeySet(spells, 1);
        HashSet<string> modifierCodes = BuildKeySet(modifiers, 1);
        HashSet<string> runeFamilies = modifierCodes
            .Select(GetRuneFamilyKey)
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The shipped databases use xa/xa0 as a legacy placeholder rather than
        // as a real modifier row. The native generator accepts these values.
        runeFamilies.Add("xa");

        ValidateSpellSpecificationLists(
            tables,
            "units.prototypes",
            31,
            spellCodes,
            modifierCodes,
            diagnostics,
            cancellationToken);
        ValidateSpellSpecificationLists(
            tables,
            "units.npcs",
            11,
            spellCodes,
            modifierCodes,
            diagnostics,
            cancellationToken);
        ValidateSpellSpecificationScalars(
            tables,
            "items.quick_items",
            25,
            spellCodes,
            modifierCodes,
            diagnostics,
            cancellationToken,
            requiredFieldId: 21,
            requiredValue: 8);

        foreach (string tableKey in new[]
                 {
                     "spells.templates",
                     "spells.armor_templates",
                     "spells.weapon_templates",
                 })
        {
            if (!tables.TryGetValue(tableKey, out DatabaseTable? templates))
            {
                continue;
            }

            ValidateStringListReferences(
                templates,
                2,
                modifiers,
                runeFamilies,
                diagnostics,
                cancellationToken);
            ValidateStringListReferences(
                templates,
                3,
                modifiers,
                runeFamilies,
                diagnostics,
                cancellationToken);
            ValidateScalarReferenceValues(
                templates,
                4,
                modifiers,
                runeFamilies,
                allowEmpty: true,
                diagnostics,
                cancellationToken);
        }

        if (tables.TryGetValue("units.npcs", out DatabaseTable? npcs) &&
            tables.TryGetValue("perks.perks", out DatabaseTable? perks))
        {
            ValidateStringListReferences(
                npcs,
                8,
                perks,
                BuildKeySet(perks, 1),
                diagnostics,
                cancellationToken);
        }

        ValidateBriefingRuneItems(
            tables,
            modifierCodes,
            diagnostics,
            cancellationToken);
    }

    private static void ValidateBriefingRuneItems(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        IReadOnlySet<string> modifierCodes,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("quests.briefings", out DatabaseTable? briefings))
        {
            return;
        }

        foreach (int fieldId in new[] { 3, 5 })
        {
            foreach (DatabaseRecord record in briefings.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, fieldId);
                if (field is null ||
                    !field.TryGetStringListValues(out IReadOnlyList<string> values))
                {
                    continue;
                }

                foreach (string descriptor in values)
                {
                    string normalized = descriptor.Trim();
                    if (!normalized.StartsWith("rune.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string modifierCode = normalized["rune.".Length..].Trim();
                    if (modifierCode.Length > 0 && !modifierCodes.Contains(modifierCode))
                    {
                        AddWarning(
                            diagnostics,
                            "SEM_REFERENCE_NOT_FOUND",
                            $"«{record.Name}»: предмет «{normalized}» из поля «{field.DisplayName}» " +
                            $"ссылается на отсутствующий код модификатора «{modifierCode}».",
                            briefings,
                            record,
                            field);
                    }
                }
            }
        }
    }

    private static void ValidateSpellSpecificationLists(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        string sourceTableKey,
        int fieldId,
        IReadOnlySet<string> spellCodes,
        IReadOnlySet<string> modifierCodes,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue(sourceTableKey, out DatabaseTable? table))
        {
            return;
        }

        foreach (DatabaseRecord record in table.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? field = FindField(record, fieldId);
            if (field is null)
            {
                continue;
            }

            if (!field.TryGetStringListValues(out IReadOnlyList<string> values))
            {
                AddListFormatWarning(diagnostics, table, record, field);
                continue;
            }

            foreach (string value in values)
            {
                ValidateSpellSpecification(
                    table,
                    record,
                    field,
                    value,
                    spellCodes,
                    modifierCodes,
                    diagnostics);
            }
        }
    }

    private static void ValidateSpellSpecificationScalars(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        string sourceTableKey,
        int fieldId,
        IReadOnlySet<string> spellCodes,
        IReadOnlySet<string> modifierCodes,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        int? requiredFieldId = null,
        double? requiredValue = null)
    {
        if (!tables.TryGetValue(sourceTableKey, out DatabaseTable? table))
        {
            return;
        }

        foreach (DatabaseRecord record in table.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requiredFieldId is int conditionFieldId &&
                (!TryGetDouble(FindField(record, conditionFieldId)?.Value, out double conditionValue) ||
                 requiredValue is not double expectedValue ||
                 conditionValue != expectedValue))
            {
                continue;
            }

            DatabaseField? field = FindField(record, fieldId);
            string value = GetText(field);
            if (field is not null && value.Length > 0)
            {
                ValidateSpellSpecification(
                    table,
                    record,
                    field,
                    value,
                    spellCodes,
                    modifierCodes,
                    diagnostics);
            }
        }
    }

    private static void ValidateSpellSpecification(
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField field,
        string specification,
        IReadOnlySet<string> spellCodes,
        IReadOnlySet<string> modifierCodes,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        string trimmed = specification.Trim();
        int openBrace = trimmed.IndexOf('{');
        string spellCode = (openBrace < 0 ? trimmed : trimmed[..openBrace]).Trim();
        if (spellCode.Length == 0)
        {
            AddWarning(
                diagnostics,
                "SEM_VALUE_FORMAT",
                $"«{record.Name}»: поле «{field.DisplayName}» содержит заклинание без кода прототипа.",
                table,
                record,
                field);
            return;
        }

        if (!spellCodes.Contains(spellCode))
        {
            AddWarning(
                diagnostics,
                "SEM_REFERENCE_NOT_FOUND",
                $"«{record.Name}»: заклинание «{spellCode}» из поля «{field.DisplayName}» " +
                "не найдено среди CodeName прототипов заклинаний.",
                table,
                record,
                field);
        }

        if (openBrace < 0)
        {
            return;
        }

        int closeBrace = trimmed.LastIndexOf('}');
        bool invalidBraces = closeBrace < openBrace ||
            (closeBrace >= 0 && trimmed[(closeBrace + 1)..].Trim().Length > 0) ||
            trimmed.IndexOf('{', openBrace + 1) >= 0 ||
            (closeBrace >= 0 && trimmed.IndexOf('}', openBrace + 1) != closeBrace);
        if (invalidBraces)
        {
            AddWarning(
                diagnostics,
                "SEM_VALUE_FORMAT",
                $"«{record.Name}»: у записи заклинания «{specification}» некорректны фигурные скобки.",
                table,
                record,
                field);
            if (closeBrace < openBrace)
            {
                return;
            }
        }

        foreach (string rune in trimmed[(openBrace + 1)..closeBrace]
                     .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!modifierCodes.Contains(rune) &&
                !rune.Equals("xa0", StringComparison.OrdinalIgnoreCase))
            {
                AddWarning(
                    diagnostics,
                    "SEM_REFERENCE_NOT_FOUND",
                    $"«{record.Name}»: модификатор «{rune}» в заклинании «{spellCode}» не найден.",
                    table,
                    record,
                    field);
            }
        }
    }

    private static void ValidateEmbeddedItemMagic(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("items.weapons", out DatabaseTable? weapons) ||
            !tables.TryGetValue("items.armors", out DatabaseTable? armors) ||
            !tables.TryGetValue("items.materials", out DatabaseTable? materials) ||
            !tables.TryGetValue("spells.prototypes", out DatabaseTable? spells) ||
            !tables.TryGetValue("spells.modifiers", out DatabaseTable? modifiers))
        {
            return;
        }

        IReadOnlyDictionary<string, DatabaseRecord> weaponByName = BuildRecordLookup(weapons, 0);
        IReadOnlyDictionary<string, DatabaseRecord> armorByName = BuildRecordLookup(armors, 0);
        IReadOnlyDictionary<string, DatabaseRecord> materialByName = BuildRecordLookup(materials, 0);
        IReadOnlyDictionary<string, DatabaseRecord> spellByCode = BuildRecordLookup(spells, 1);
        IReadOnlyDictionary<string, DatabaseRecord> modifierByCode = BuildRecordLookup(modifiers, 1);
        HashSet<string> spellCodes = spellByCode.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> modifierCodes = modifierByCode.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (EmbeddedItemFieldRule rule in EmbeddedItemFieldRules)
        {
            if (!tables.TryGetValue(rule.TableKey, out DatabaseTable? source))
            {
                continue;
            }

            foreach (DatabaseRecord record in source.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.FieldId);
                if (field is null)
                {
                    continue;
                }

                if (rule.IsList)
                {
                    if (!field.TryGetStringListValues(out IReadOnlyList<string> values))
                    {
                        AddListFormatWarning(diagnostics, source, record, field);
                        continue;
                    }

                    foreach (string value in values)
                    {
                        ValidateEmbeddedItemDescriptor(
                            source,
                            record,
                            field,
                            value,
                            weaponByName,
                            armorByName,
                            materialByName,
                            spellByCode,
                            modifierByCode,
                            spellCodes,
                            modifierCodes,
                            diagnostics);
                    }
                }
                else
                {
                    ValidateEmbeddedItemDescriptor(
                        source,
                        record,
                        field,
                        GetText(field),
                        weaponByName,
                        armorByName,
                        materialByName,
                        spellByCode,
                        modifierByCode,
                        spellCodes,
                        modifierCodes,
                        diagnostics);
                }
            }
        }
    }

    private static void ValidateEmbeddedItemDescriptor(
        DatabaseTable source,
        DatabaseRecord sourceRecord,
        DatabaseField sourceField,
        string descriptor,
        IReadOnlyDictionary<string, DatabaseRecord> weaponByName,
        IReadOnlyDictionary<string, DatabaseRecord> armorByName,
        IReadOnlyDictionary<string, DatabaseRecord> materialByName,
        IReadOnlyDictionary<string, DatabaseRecord> spellByCode,
        IReadOnlyDictionary<string, DatabaseRecord> modifierByCode,
        IReadOnlySet<string> spellCodes,
        IReadOnlySet<string> modifierCodes,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        string text = descriptor.Trim();
        int openBracket = text.IndexOf('[');
        if (openBracket <= 0)
        {
            return;
        }

        string itemPart = text[..openBracket].Trim();
        string[] itemParts = itemPart.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (itemParts.Length == 0)
        {
            return;
        }

        string shapeName = itemParts[0];
        bool isWeapon = weaponByName.TryGetValue(shapeName, out DatabaseRecord? shape);
        bool isArmor = !isWeapon && armorByName.TryGetValue(shapeName, out shape);
        if (!isWeapon && !isArmor)
        {
            return;
        }

        // CItemManager uses CString::TrimLeft('[')/TrimRight(']'), so repeated
        // outer brackets such as "spell]]" are accepted by the game. Brackets
        // remaining inside the payload expose concatenated item descriptors.
        string spellTail = text[(openBracket + 1)..].Trim();
        bool hasClosingBracket = spellTail.EndsWith(']');
        spellTail = spellTail.TrimStart('[').TrimEnd(']').Trim();
        bool invalidBrackets = !hasClosingBracket ||
            spellTail.Contains('[') ||
            spellTail.Contains(']');
        if (invalidBrackets)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_FORMAT",
                $"«{sourceRecord.Name}»: у описания предмета «{descriptor}» некорректны квадратные скобки вложенной магии.",
                source,
                sourceRecord,
                sourceField);
        }

        string spellSpecification = spellTail;
        if (spellSpecification.Length == 0)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_FORMAT",
                $"«{sourceRecord.Name}»: у предмета «{itemPart}» указаны пустые скобки вложенной магии.",
                source,
                sourceRecord,
                sourceField);
            return;
        }

        ValidateSpellSpecification(
            source,
            sourceRecord,
            sourceField,
            spellSpecification,
            spellCodes,
            modifierCodes,
            diagnostics);
        if (!TryParseSpellSpecification(spellSpecification, out string spellCode, out string[] runeCodes) ||
            !spellByCode.TryGetValue(spellCode, out DatabaseRecord? spell))
        {
            return;
        }

        if (itemParts.Length < 2 || !materialByName.TryGetValue(itemParts[1], out DatabaseRecord? material))
        {
            string materialName = itemParts.Length < 2 ? "не указан" : $"«{itemParts[1]}»";
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_MATERIAL_NOT_FOUND",
                $"«{sourceRecord.Name}»: для {(isWeapon ? "оружия" : "доспеха")} «{shapeName}» материал {materialName}; " +
                "энергию и сложность вложенной магии рассчитать нельзя.",
                source,
                sourceRecord,
                sourceField);
            return;
        }

        DatabaseRecord resolvedShape = shape!;
        string requiredMaterialType = GetText(FindField(resolvedShape, 3));
        string actualMaterialType = GetText(FindField(material, 1));
        if (requiredMaterialType.Length > 0 &&
            !requiredMaterialType.Equals(actualMaterialType, StringComparison.OrdinalIgnoreCase))
        {
            AddWarning(
                diagnostics,
                "SEM_ITEM_MATERIAL_TYPE_MISMATCH",
                $"«{sourceRecord.Name}»: материал «{material.Name}» имеет тип «{actualMaterialType}», " +
                $"но форма «{shapeName}» требует «{requiredMaterialType}».",
                source,
                sourceRecord,
                sourceField);
        }

        var resolvedModifiers = new List<DatabaseRecord>(runeCodes.Length);
        foreach (string runeCode in runeCodes)
        {
            if (runeCode.Equals("xa0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (modifierByCode.TryGetValue(runeCode, out DatabaseRecord? resolvedModifier))
            {
                resolvedModifiers.Add(resolvedModifier);
            }
        }

        if (resolvedModifiers.Count > 8)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_TOO_MANY_RUNES",
                $"«{sourceRecord.Name}»: вложенная магия «{spellSpecification}» у предмета «{itemPart}» " +
                $"содержит {resolvedModifiers.Count} распознанных рун; движок сортирует их и учитывает " +
                "только первые 8 при проверке ручной вставки.",
                source,
                sourceRecord,
                sourceField);
        }

        bool canInsertToItem = resolvedModifiers
            .OrderBy(modifier => modifier.RecordIndex)
            .Take(8)
            .Select(modifier => GetText(FindField(modifier, 1)))
            .Any(code => code.Equals("ic", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("it", StringComparison.OrdinalIgnoreCase));
        if (!canInsertToItem)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_ITEM_RUNE_REQUIRED",
                $"«{sourceRecord.Name}»: заклинание «{spellSpecification}» у предмета «{itemPart}» " +
                "не содержит руну ic или it среди первых 8 рун после сортировки и не проходит " +
                "ограничение ручной вставки в оружие/доспех.",
                source,
                sourceRecord,
                sourceField);
        }

        if (!TryGetDouble(FindField(resolvedShape, 10)?.Value, out double shapeManaValue) ||
            !TryGetInt32(FindField(resolvedShape, 11)?.Value, out int shapeSlots) ||
            !TryGetDouble(FindField(material, 6)?.Value, out double materialManaValue) ||
            !TryGetInt32(FindField(material, 7)?.Value, out int materialSlots) ||
            !TryGetDouble(FindField(spell, 5)?.Value, out double baseManaValue) ||
            !TryGetInt32(FindField(spell, 21)?.Value, out int baseComplexity))
        {
            return;
        }

        var shapeOption = new EmbeddedMagicShapeOption(
            shapeName,
            isWeapon ? "items.weapons" : "items.armors",
            isWeapon ? "Оружие" : "Доспех",
            requiredMaterialType,
            shapeManaValue,
            shapeSlots);
        var materialOption = new EmbeddedMagicMaterialOption(
            material.Name,
            actualMaterialType,
            materialManaValue,
            materialSlots);
        var spellOption = new EmbeddedMagicSpellOption(
            spellCode,
            spell.Name,
            baseManaValue,
            baseComplexity);
        var modifierOptions = new Dictionary<string, EmbeddedMagicModifierOption>(StringComparer.OrdinalIgnoreCase);
        foreach ((string code, DatabaseRecord modifier) in modifierByCode)
        {
            if (!TryGetDouble(FindField(modifier, 4)?.Value, out double fixedManaValue) ||
                !TryGetDouble(FindField(modifier, 5)?.Value, out double multiplierValue) ||
                !TryGetInt32(FindField(modifier, 6)?.Value, out int complexityValue))
            {
                return;
            }

            modifierOptions[code] = new EmbeddedMagicModifierOption(
                code,
                modifier.Name,
                modifier.RecordIndex,
                fixedManaValue,
                multiplierValue,
                complexityValue);
        }

        EmbeddedMagicCapacity capacity = EmbeddedMagicComputation.Calculate(
            shapeOption,
            materialOption,
            spellOption,
            runeCodes,
            modifierOptions);
        float requiredMana = capacity.RequiredMana;
        float availableMana = capacity.AvailableMana;
        long requiredComplexity = capacity.RequiredComplexity;
        long availableComplexity = capacity.AvailableComplexity;
        if (requiredComplexity > availableComplexity)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_COMPLEXITY_EXCEEDED",
                $"«{sourceRecord.Name}»: вложенная магия «{spellSpecification}» у предмета «{itemPart}» " +
                $"требует сложности {requiredComplexity}, доступно {availableComplexity} " +
                $"(форма {shapeSlots} + материал {materialSlots}); она не проходит ограничение " +
                "ручной вставки в лагере.",
                source,
                sourceRecord,
                sourceField);
        }

        if (requiredMana > availableMana)
        {
            AddWarning(
                diagnostics,
                "SEM_EMBEDDED_MAGIC_ENERGY_EXCEEDED",
                $"«{sourceRecord.Name}»: вложенная магия «{spellSpecification}» у предмета «{itemPart}» " +
                $"требует энергии {FormatNumber(requiredMana)}, доступно {FormatNumber(availableMana)} " +
                $"(форма {FormatNumber(shapeManaValue)} + материал {FormatNumber(materialManaValue)}); " +
                "она не проходит ограничение ручной вставки в лагере.",
                source,
                sourceRecord,
                sourceField);
        }
    }

    private static IReadOnlyDictionary<string, DatabaseRecord> BuildRecordLookup(DatabaseTable table, int fieldId) =>
        table.Records
            .Select(record => (Key: GetText(FindField(record, fieldId)), Record: record))
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Record, StringComparer.OrdinalIgnoreCase);

    private static bool TryParseSpellSpecification(
        string specification,
        out string spellCode,
        out string[] runeCodes)
    {
        string trimmed = specification.Trim();
        int openBrace = trimmed.IndexOf('{');
        spellCode = (openBrace < 0 ? trimmed : trimmed[..openBrace]).Trim();
        runeCodes = [];
        if (spellCode.Length == 0)
        {
            return false;
        }

        if (openBrace < 0)
        {
            return true;
        }

        int closeBrace = trimmed.LastIndexOf('}');
        if (closeBrace < openBrace || trimmed[(closeBrace + 1)..].Trim().Length > 0)
        {
            return false;
        }

        runeCodes = trimmed[(openBrace + 1)..closeBrace]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    private static void ValidateStringListReferences(
        DatabaseTable source,
        int sourceFieldId,
        DatabaseTable target,
        IReadOnlySet<string> targetKeys,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseRecord record in source.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? field = FindField(record, sourceFieldId);
            if (field is null)
            {
                continue;
            }

            if (!field.TryGetStringListValues(out IReadOnlyList<string> values))
            {
                AddListFormatWarning(diagnostics, source, record, field);
                continue;
            }

            foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                string normalized = value.Trim();
                if (!targetKeys.Contains(normalized))
                {
                    AddMissingReference(diagnostics, source, record, field, normalized, target);
                }
            }
        }
    }

    private static void ValidateScalarReferenceValues(
        DatabaseTable source,
        int sourceFieldId,
        DatabaseTable target,
        IReadOnlySet<string> targetKeys,
        bool allowEmpty,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseRecord record in source.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? field = FindField(record, sourceFieldId);
            string value = GetText(field);
            if (value.Length == 0)
            {
                if (!allowEmpty && field is not null)
                {
                    AddWarning(
                        diagnostics,
                        "SEM_REQUIRED_VALUE_MISSING",
                        $"В записи «{record.Name}» не заполнено обязательное поле «{field.DisplayName}».",
                        source,
                        record,
                        field);
                }

                continue;
            }

            if (field is not null && !targetKeys.Contains(value))
            {
                AddMissingReference(diagnostics, source, record, field, value, target);
            }
        }
    }

    private static void ValidateNumericRanges(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (NumericRangeRule rule in NumericRangeRules)
        {
            if (!tables.TryGetValue(rule.TableKey, out DatabaseTable? table))
            {
                continue;
            }

            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.FieldId);
                if (field is null || !TryGetDouble(field.Value, out double value))
                {
                    continue;
                }

                bool outside = !double.IsFinite(value) || value < rule.Minimum ||
                    (rule.Maximum is double maximum && value > maximum);
                if (!outside)
                {
                    continue;
                }

                string expected = rule.Maximum is double upper
                    ? $"от {FormatNumber(rule.Minimum)} до {FormatNumber(upper)}"
                    : $"не меньше {FormatNumber(rule.Minimum)}";
                AddWarning(
                    diagnostics,
                    !double.IsFinite(value) ? "SEM_NONFINITE_FLOAT" : "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: {rule.Description} имеет значение {FormatNumber(value)}; " +
                    $"ожидается {expected}.",
                    table,
                    record,
                    field);
            }
        }
    }

    private static void ValidateNumericProjectionRanges(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (NumericProjectionRangeRule rule in NumericProjectionRangeRules)
        {
            if (!tables.TryGetValue(rule.TableKey, out DatabaseTable? table))
            {
                continue;
            }

            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.FieldId);
                DatabaseFieldProjection? projection = field?.Projections.FirstOrDefault(candidate =>
                    candidate.Index == rule.ProjectionIndex);
                if (field is null || projection is null ||
                    !TryGetDouble(field.GetProjectedValue(projection), out double value))
                {
                    continue;
                }

                if (double.IsFinite(value) && value >= rule.Minimum && value <= rule.Maximum)
                {
                    continue;
                }

                AddWarning(
                    diagnostics,
                    !double.IsFinite(value) ? "SEM_NONFINITE_FLOAT" : "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: {rule.Description} «{projection.DisplayName}» равна {FormatNumber(value)}; " +
                    $"ожидается от {FormatNumber(rule.Minimum)} до {FormatNumber(rule.Maximum)}.",
                    table,
                    record,
                    field);
            }
        }
    }

    private static void ValidateMaterialIndices(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("items.materials", out DatabaseTable? materials))
        {
            return;
        }

        foreach (DatabaseRecord record in materials.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? field = FindField(record, 3);
            if (field is null || !TryGetDouble(field.Value, out double value))
            {
                continue;
            }

            if (double.IsFinite(value) && value >= 0 && value < materials.Records.Count &&
                value == Math.Truncate(value))
            {
                continue;
            }

            AddWarning(
                diagnostics,
                !double.IsFinite(value) ? "SEM_NONFINITE_FLOAT" : "SEM_VALUE_OUT_OF_RANGE",
                $"«{record.Name}»: индекс материала равен {FormatNumber(value)}; " +
                $"ожидается целое значение от 0 до {materials.Records.Count - 1}.",
                materials,
                record,
                field);
        }
    }

    private static void ValidateLootItemEnums(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("items.loot_items", out DatabaseTable? lootItems))
        {
            return;
        }

        foreach (DatabaseRecord record in lootItems.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? subtypeField = FindField(record, 21);
            DatabaseField? instructionField = FindField(record, 22);
            if (subtypeField is null || instructionField is null ||
                !TryGetInt32(subtypeField.Value, out int subtype) ||
                !TryGetInt32(instructionField.Value, out int instructionType))
            {
                continue;
            }

            bool subtypeValid = subtype == int.MaxValue || subtype is >= 0 and <= 4;
            if (!subtypeValid)
            {
                AddWarning(
                    diagnostics,
                    "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: тип лута равен {subtype}; ожидается 0–4 или служебное {int.MaxValue}.",
                    lootItems,
                    record,
                    subtypeField);
            }

            bool instructionValid = instructionType == int.MaxValue || instructionType is >= 0 and <= 6;
            if (!instructionValid)
            {
                AddWarning(
                    diagnostics,
                    "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: тип инструкции равен {instructionType}; " +
                    $"ожидается 0–6 или служебное {int.MaxValue}.",
                    lootItems,
                    record,
                    instructionField);
            }

            if (subtype is 0 or 1 && instructionType != int.MaxValue)
            {
                AddWarning(
                    diagnostics,
                    "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: для типа лута {subtype} поле инструкции должно содержать " +
                    $"служебное значение {int.MaxValue}.",
                    lootItems,
                    record,
                    instructionField);
            }

            if (subtype != 2 || instructionType is < 0 or > 4)
            {
                continue;
            }

            string targetTableKey = instructionType switch
            {
                0 => "items.weapons",
                1 => "items.armors",
                2 => "items.quick_items",
                3 => "spells.prototypes",
                4 => "spells.modifiers",
                _ => string.Empty,
            };
            if (!tables.TryGetValue(targetTableKey, out DatabaseTable? targetTable))
            {
                continue;
            }

            DatabaseField? targetIndexField = FindField(record, 23);
            if (targetIndexField is null ||
                !TryGetInt32(targetIndexField.Value, out int targetIndex) ||
                targetIndex == -3 ||
                targetIndex is >= 0 && targetIndex < targetTable.Records.Count)
            {
                continue;
            }

            AddWarning(
                diagnostics,
                "SEM_REFERENCE_NOT_FOUND",
                $"«{record.Name}»: индекс {targetIndex} выходит за таблицу «{targetTable.Title}» " +
                $"(записей: {targetTable.Records.Count}).",
                lootItems,
                record,
                targetIndexField);
        }
    }

    private static void ValidatePerkSkillRequirements(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!tables.TryGetValue("perks.perks", out DatabaseTable? perks))
        {
            return;
        }

        foreach (DatabaseRecord record in perks.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? skillField = FindField(record, 11);
            string skill = GetText(skillField);
            if (skill.Length == 0 || skill.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DatabaseField? skillIdField = FindField(record, 12);
            if (skillIdField is null ||
                !TryGetInt32(skillIdField.Value, out int skillId) ||
                skillId is >= 0 and <= 7)
            {
                continue;
            }

            AddWarning(
                diagnostics,
                "SEM_VALUE_OUT_OF_RANGE",
                $"«{record.Name}»: для требования навыка «{skill}» индекс равен {skillId}; ожидается 0–7.",
                perks,
                record,
                skillIdField);
        }
    }

    private static void ValidateBooleanValues(
        IReadOnlyDictionary<string, DatabaseTable> tables,
        ICollection<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (BooleanRule rule in BooleanRules)
        {
            if (!tables.TryGetValue(rule.TableKey, out DatabaseTable? table))
            {
                continue;
            }

            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DatabaseField? field = FindField(record, rule.FieldId);
                if (field is null || !TryGetDouble(field.Value, out double value) || value is 0 or 1)
                {
                    continue;
                }

                AddWarning(
                    diagnostics,
                    "SEM_VALUE_OUT_OF_RANGE",
                    $"«{record.Name}»: поле-флаг «{field.DisplayName}» равно {FormatNumber(value)}; " +
                    "ожидается 0 или 1.",
                    table,
                    record,
                    field);
            }
        }

        if (!tables.TryGetValue("spells.prototypes", out DatabaseTable? spells))
        {
            return;
        }

        foreach (DatabaseRecord spell in spells.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseField? allowedRunes = FindField(spell, 20);
            if (allowedRunes is null)
            {
                continue;
            }

            foreach (DatabaseFieldProjection projection in allowedRunes.Projections)
            {
                object? raw = allowedRunes.GetProjectedValue(projection);
                if (TryGetDouble(raw, out double value) && value is not (0 or 1))
                {
                    AddWarning(
                        diagnostics,
                        "SEM_VALUE_OUT_OF_RANGE",
                        $"«{spell.Name}»: флаг «{projection.DisplayName}» равен {FormatNumber(value)}; " +
                        "ожидается 0 или 1.",
                        spells,
                        spell,
                        allowedRunes);
                }
            }
        }
    }

    private static HashSet<string> BuildKeySet(DatabaseTable table, int fieldId) =>
        table.Records
            .Select(record => GetText(FindField(record, fieldId)))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static DatabaseField? FindField(DatabaseRecord record, int fieldId) =>
        record.Fields.FirstOrDefault(field => field.Id == fieldId);

    private static string GetText(DatabaseField? field) => field?.Value.ToString()?.Trim() ?? string.Empty;

    private static string? GetRuneFamilyKey(string code)
    {
        string normalized = code.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return char.ToLowerInvariant(normalized[0]) switch
        {
            'r' or 't' or 'a' or 'e' or 'd' or 'm' => normalized[..1],
            'f' or 'i' when normalized.Length >= 2 => normalized[..2],
            _ => null,
        };
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        switch (value)
        {
            case byte number:
                result = number;
                return true;
            case sbyte number:
                result = number;
                return true;
            case short number:
                result = number;
                return true;
            case ushort number:
                result = number;
                return true;
            case int number:
                result = number;
                return true;
            case uint number:
                result = number;
                return true;
            case long number:
                result = number;
                return true;
            case ulong number:
                result = number;
                return true;
            case float number:
                result = number;
                return true;
            case double number:
                result = number;
                return true;
            case string text when double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetUInt32(object value, out uint result)
    {
        switch (value)
        {
            case uint number:
                result = number;
                return true;
            case int number when number >= 0:
                result = (uint)number;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetInt32(object? value, out int result)
    {
        switch (value)
        {
            case int number:
                result = number;
                return true;
            case uint number when number <= int.MaxValue:
                result = (int)number;
                return true;
            case short number:
                result = number;
                return true;
            case ushort number:
                result = number;
                return true;
            case byte number:
                result = number;
                return true;
            case string text when int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string FormatNumber(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static void AddListFormatWarning(
        ICollection<ValidationDiagnostic> diagnostics,
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField field) =>
        AddWarning(
            diagnostics,
            "SEM_VALUE_FORMAT",
            $"«{record.Name}»: поле «{field.DisplayName}» не удалось прочитать как список строк.",
            table,
            record,
            field);

    private static void AddMissingReference(
        ICollection<ValidationDiagnostic> diagnostics,
        DatabaseTable source,
        DatabaseRecord record,
        DatabaseField field,
        string missingValue,
        DatabaseTable target) =>
        AddWarning(
            diagnostics,
            "SEM_REFERENCE_NOT_FOUND",
            $"«{record.Name}»: значение «{missingValue}» из поля «{field.DisplayName}» " +
            $"не найдено в таблице «{target.Title}».",
            source,
            record,
            field);

    private static void AddWarning(
        ICollection<ValidationDiagnostic> diagnostics,
        string code,
        string message,
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField field)
    {
        diagnostics.Add(new ValidationDiagnostic(
            DiagnosticSeverity.Warning,
            code,
            message,
            field.Path,
            field.Offset,
            DiagnosticDomain.Semantic,
            new DiagnosticLocation(
                table.ResourceName,
                table.Key,
                record.RecordIndex,
                record.Name,
                field.Id,
                field.Key)));
    }

    private static void AddBlockingAddedRecordError(
        ICollection<ValidationDiagnostic> diagnostics,
        string code,
        string message,
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField? field) =>
        AddBlockingSemanticError(diagnostics, code, message, table, record, field);

    private static void AddBlockingSemanticError(
        ICollection<ValidationDiagnostic> diagnostics,
        string code,
        string message,
        DatabaseTable table,
        DatabaseRecord record,
        DatabaseField? field)
    {
        diagnostics.Add(new ValidationDiagnostic(
            DiagnosticSeverity.Error,
            code,
            message,
            field?.Path ?? record.Path,
            field is { Offset: >= 0 } ? field.Offset : null,
            DiagnosticDomain.Semantic,
            new DiagnosticLocation(
                table.ResourceName,
                table.Key,
                record.RecordIndex,
                record.Name,
                field?.Id,
                field?.Key),
            BlockingOverride: true));
    }

    private readonly record struct KeyRule(
        string TableKey,
        int FieldId,
        bool Required,
        bool Unique);

    private readonly record struct NumericRangeRule(
        string TableKey,
        int FieldId,
        double Minimum,
        double? Maximum,
        string Description);

    private readonly record struct NumericProjectionRangeRule(
        string TableKey,
        int FieldId,
        int ProjectionIndex,
        double Minimum,
        double Maximum,
        string Description);

    private readonly record struct BooleanRule(string TableKey, int FieldId);

    private readonly record struct EmbeddedItemFieldRule(string TableKey, int FieldId, bool IsList);
}
