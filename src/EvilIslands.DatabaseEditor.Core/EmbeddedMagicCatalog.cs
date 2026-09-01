using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record EmbeddedMagicShapeOption(
    string Value,
    string TableKey,
    string Kind,
    string RequiredMaterialType,
    double Mana,
    int Slots);

public sealed record EmbeddedMagicMaterialOption(
    string Value,
    string MaterialType,
    double Mana,
    int Slots);

public sealed record EmbeddedMagicSpellOption(
    string Value,
    string TechnicalName,
    double BaseMana,
    int BaseComplexity);

public sealed record EmbeddedMagicModifierOption(
    string Value,
    string TechnicalName,
    int SortIndex,
    double FixedMana,
    double Multiplier,
    int Complexity);

public sealed record EmbeddedMagicEvaluation(
    bool IsRecognized,
    bool CanCalculate,
    bool IsAllowedForManualInsertion,
    string ItemValue,
    string SpellCode,
    IReadOnlyList<string> ModifierCodes,
    double? RequiredMana,
    double? AvailableMana,
    long? RequiredComplexity,
    long? AvailableComplexity,
    IReadOnlyList<string> Issues)
{
    public string StatusText => Issues.Count > 0
        ? string.Join(Environment.NewLine, Issues)
        : CanCalculate
            ? "Комбинация проходит ограничения ручной вставки."
            : "Для этой строки расчёт вложенной магии не применяется.";
}

public sealed class EmbeddedMagicCatalog
{
    private readonly IReadOnlyDictionary<string, EmbeddedMagicShapeOption> _shapes;
    private readonly IReadOnlyDictionary<string, EmbeddedMagicMaterialOption> _materials;
    private readonly IReadOnlyDictionary<string, EmbeddedMagicSpellOption> _spells;
    private readonly IReadOnlyDictionary<string, EmbeddedMagicModifierOption> _modifiers;

    private EmbeddedMagicCatalog(
        IReadOnlyList<EmbeddedMagicShapeOption> shapes,
        IReadOnlyList<EmbeddedMagicMaterialOption> materials,
        IReadOnlyList<EmbeddedMagicSpellOption> spells,
        IReadOnlyList<EmbeddedMagicModifierOption> modifiers)
    {
        Shapes = shapes;
        Materials = materials;
        Spells = spells;
        Modifiers = modifiers;
        _shapes = BuildLookup(shapes, item => item.Value);
        _materials = BuildLookup(materials, item => item.Value);
        _spells = BuildLookup(spells, item => item.Value);
        _modifiers = BuildLookup(modifiers, item => item.Value);
    }

    public IReadOnlyList<EmbeddedMagicShapeOption> Shapes { get; }

    public IReadOnlyList<EmbeddedMagicMaterialOption> Materials { get; }

    public IReadOnlyList<EmbeddedMagicSpellOption> Spells { get; }

    public IReadOnlyList<EmbeddedMagicModifierOption> Modifiers { get; }

    public static EmbeddedMagicCatalog Create(IEnumerable<DatabaseTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var byKey = tables.ToDictionary(table => table.Key, StringComparer.Ordinal);
        return Create(byKey.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<DatabaseRecord>)pair.Value.Records,
            StringComparer.Ordinal));
    }

    public static EmbeddedMagicCatalog Create(
        IReadOnlyDictionary<string, IReadOnlyList<DatabaseRecord>> recordsByTable)
    {
        ArgumentNullException.ThrowIfNull(recordsByTable);

        var shapes = new List<EmbeddedMagicShapeOption>();
        AddShapes("items.weapons", "Оружие");
        AddShapes("items.armors", "Доспех");

        EmbeddedMagicMaterialOption[] materials = GetRecords("items.materials")
            .Select(record => new EmbeddedMagicMaterialOption(
                GetText(record, 0),
                GetText(record, 1),
                GetDouble(record, 6),
                GetInt32(record, 7)))
            .Where(option => option.Value.Length > 0)
            .ToArray();
        EmbeddedMagicSpellOption[] spells = GetRecords("spells.prototypes")
            .Select(record => new EmbeddedMagicSpellOption(
                GetText(record, 1),
                GetText(record, 0),
                GetDouble(record, 5),
                GetInt32(record, 21)))
            .Where(option => option.Value.Length > 0)
            .ToArray();
        EmbeddedMagicModifierOption[] modifiers = GetRecords("spells.modifiers")
            .Select(record => new EmbeddedMagicModifierOption(
                GetText(record, 1),
                GetText(record, 0),
                record.RecordIndex,
                GetDouble(record, 4),
                GetDouble(record, 5),
                GetInt32(record, 6)))
            .Where(option => option.Value.Length > 0)
            .ToArray();

        return new EmbeddedMagicCatalog(shapes, materials, spells, modifiers);

        IReadOnlyList<DatabaseRecord> GetRecords(string tableKey) =>
            recordsByTable.TryGetValue(tableKey, out IReadOnlyList<DatabaseRecord>? records)
                ? records
                : [];

        void AddShapes(string tableKey, string kind)
        {
            shapes.AddRange(GetRecords(tableKey)
                .Select(record => new EmbeddedMagicShapeOption(
                    GetText(record, 0),
                    tableKey,
                    kind,
                    GetText(record, 3),
                    GetDouble(record, 10),
                    GetInt32(record, 11)))
                .Where(option => option.Value.Length > 0));
        }
    }

    public IReadOnlyList<EmbeddedMagicMaterialOption> GetCompatibleMaterials(string? shapeValue)
    {
        if (string.IsNullOrWhiteSpace(shapeValue) ||
            !_shapes.TryGetValue(shapeValue.Trim(), out EmbeddedMagicShapeOption? shape))
        {
            return Materials;
        }

        return Materials
            .Where(material => material.MaterialType.Equals(
                shape.RequiredMaterialType,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public EmbeddedMagicEvaluation Evaluate(
        string? itemValue,
        string? spellCode,
        IReadOnlyList<string>? modifierCodes)
    {
        string item = itemValue?.Trim() ?? string.Empty;
        string spell = spellCode?.Trim() ?? string.Empty;
        string[] runes = modifierCodes?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray() ?? [];
        var issues = new List<string>();

        int separator = item.IndexOf('.');
        string shapeValue = separator > 0 ? item[..separator].Trim() : item;
        string materialValue = separator > 0 ? item[(separator + 1)..].Trim() : string.Empty;
        bool hasShape = _shapes.TryGetValue(shapeValue, out EmbeddedMagicShapeOption? shape);
        bool hasMaterial = _materials.TryGetValue(materialValue, out EmbeddedMagicMaterialOption? material);
        bool hasSpell = _spells.TryGetValue(spell, out EmbeddedMagicSpellOption? spellOption);

        if (!hasShape)
        {
            issues.Add(shapeValue.Length == 0
                ? "Выберите форму оружия или доспеха."
                : $"Форма «{shapeValue}» не найдена в database.res.");
        }
        if (!hasMaterial)
        {
            issues.Add(materialValue.Length == 0
                ? "Выберите материал предмета."
                : $"Материал «{materialValue}» не найден в database.res.");
        }
        if (!hasSpell)
        {
            issues.Add(spell.Length == 0
                ? "Выберите заклинание."
                : $"Заклинание «{spell}» не найдено в database.res.");
        }

        var resolvedModifiers = new List<EmbeddedMagicModifierOption>();
        foreach (string rune in runes)
        {
            if (rune.Equals("xa0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (_modifiers.TryGetValue(rune, out EmbeddedMagicModifierOption? modifier))
            {
                resolvedModifiers.Add(modifier);
            }
            else
            {
                issues.Add($"Модификатор «{rune}» не найден в database.res.");
            }
        }

        if (!hasShape || !hasMaterial || !hasSpell)
        {
            return new EmbeddedMagicEvaluation(
                IsRecognized: hasShape || hasMaterial || hasSpell,
                CanCalculate: false,
                IsAllowedForManualInsertion: false,
                item,
                spell,
                runes,
                null,
                null,
                null,
                null,
                issues);
        }

        if (!shape!.RequiredMaterialType.Equals(material!.MaterialType, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(
                $"Материал типа «{material.MaterialType}» несовместим с формой, которой нужен тип «{shape.RequiredMaterialType}».");
        }
        if (resolvedModifiers.Count > 8)
        {
            issues.Add($"Распознано рун: {resolvedModifiers.Count}; ручная вставка учитывает только первые 8 после сортировки.");
        }

        bool hasItemRune = resolvedModifiers
            .OrderBy(modifier => modifier.SortIndex)
            .Take(8)
            .Any(modifier => modifier.Value.Equals("ic", StringComparison.OrdinalIgnoreCase) ||
                modifier.Value.Equals("it", StringComparison.OrdinalIgnoreCase));
        if (!hasItemRune)
        {
            issues.Add("Для ручной вставки нужна руна ic или it среди первых восьми рун после сортировки.");
        }

        EmbeddedMagicCapacity capacity = EmbeddedMagicComputation.Calculate(
            shape,
            material,
            spellOption!,
            runes,
            _modifiers);
        if (capacity.RequiredComplexity > capacity.AvailableComplexity)
        {
            issues.Add(
                $"Сложность превышена: требуется {capacity.RequiredComplexity}, доступно {capacity.AvailableComplexity} " +
                $"(форма {shape.Slots} + материал {material.Slots}).");
        }
        if (capacity.RequiredMana > capacity.AvailableMana)
        {
            issues.Add(
                $"Энергия превышена: требуется {FormatNumber(capacity.RequiredMana)}, доступно {FormatNumber(capacity.AvailableMana)} " +
                $"(форма {FormatNumber(shape.Mana)} + материал {FormatNumber(material.Mana)})."
            );
        }

        return new EmbeddedMagicEvaluation(
            IsRecognized: true,
            CanCalculate: true,
            IsAllowedForManualInsertion: issues.Count == 0,
            item,
            spell,
            runes,
            capacity.RequiredMana,
            capacity.AvailableMana,
            capacity.RequiredComplexity,
            capacity.AvailableComplexity,
            issues);
    }

    private static IReadOnlyDictionary<string, T> BuildLookup<T>(
        IEnumerable<T> source,
        Func<T, string> keySelector) =>
        source
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static string GetText(DatabaseRecord record, int fieldId) =>
        record.Fields.FirstOrDefault(field => field.Id == fieldId)?.Value.ToString()?.Trim() ?? string.Empty;

    private static double GetDouble(DatabaseRecord record, int fieldId) =>
        TryGetDouble(record.Fields.FirstOrDefault(field => field.Id == fieldId)?.Value, out double value)
            ? value
            : 0;

    private static int GetInt32(DatabaseRecord record, int fieldId) =>
        TryGetInt32(record.Fields.FirstOrDefault(field => field.Id == fieldId)?.Value, out int value)
            ? value
            : 0;

    private static bool TryGetDouble(object? value, out double result)
    {
        if (value is IConvertible convertible)
        {
            try
            {
                result = convertible.ToDouble(CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        result = 0;
        return false;
    }

    private static bool TryGetInt32(object? value, out int result)
    {
        if (value is IConvertible convertible)
        {
            try
            {
                result = convertible.ToInt32(CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        result = 0;
        return false;
    }

    private static string FormatNumber(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed record EmbeddedMagicCapacity(
    float RequiredMana,
    float AvailableMana,
    long RequiredComplexity,
    long AvailableComplexity);

internal static class EmbeddedMagicComputation
{
    public static EmbeddedMagicCapacity Calculate(
        EmbeddedMagicShapeOption shape,
        EmbeddedMagicMaterialOption material,
        EmbeddedMagicSpellOption spell,
        IReadOnlyList<string> modifierCodes,
        IReadOnlyDictionary<string, EmbeddedMagicModifierOption> modifierByCode)
    {
        float baseMana = (float)spell.BaseMana;
        float requiredMana = baseMana;
        long requiredComplexity = spell.BaseComplexity;
        foreach (string runeCode in modifierCodes)
        {
            if (runeCode.Equals("xa0", StringComparison.OrdinalIgnoreCase) ||
                !modifierByCode.TryGetValue(runeCode, out EmbeddedMagicModifierOption? modifier))
            {
                continue;
            }

            if (runeCode.Length > 0 && char.ToLowerInvariant(runeCode[0]) == 'm')
            {
                requiredMana += baseMana * (float)modifier.Multiplier;
                requiredMana = MathF.Max(0.0f, requiredMana);
            }

            requiredMana += (float)modifier.FixedMana;
            requiredComplexity += modifier.Complexity;
        }

        requiredMana = MathF.Max(baseMana, requiredMana);
        return new EmbeddedMagicCapacity(
            requiredMana,
            (float)shape.Mana + (float)material.Mana,
            requiredComplexity,
            (long)shape.Slots + material.Slots);
    }
}
