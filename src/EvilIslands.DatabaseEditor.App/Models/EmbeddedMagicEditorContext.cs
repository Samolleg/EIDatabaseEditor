using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Models;

internal sealed record EmbeddedMagicChoice(string Value, string DisplayText)
{
    public string SearchText { get; init; } = string.Empty;
    public string ModelText { get; init; } = string.Empty;
    public string Description => ModelText.Length == 0 ? DisplayText : DisplayText + "\nКоды моделей по БД: " + ModelText;

    public bool MatchesSearch(string? query)
    {
        string searchable = NormalizeSearch(Value + "\n" + DisplayText + "\n" + SearchText);
        return NormalizeSearch(query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSearch(string text) => text.Replace('ё', 'е').Replace('Ё', 'Е').Replace('_', ' ');
    public override string ToString() => DisplayText;
}

internal enum StructuredElementKind
{
    RawValue,
    Spell,
    Weapon,
    Armor,
}

internal sealed record StructuredElementKindChoice(
    StructuredElementKind Kind,
    string DisplayText,
    string Description)
{
    public override string ToString() => DisplayText;
}

internal sealed class EmbeddedMagicEditorContext
{
    private readonly IReadOnlyDictionary<string, string> _localizedTitles;

    public EmbeddedMagicEditorContext(
        EmbeddedMagicCatalog catalog,
        IReadOnlyDictionary<string, string> localizedTitles)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _localizedTitles = localizedTitles ?? new Dictionary<string, string>();
        Shapes = catalog.Shapes
            .Select(CreateShapeChoice)
            .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Weapons = CreateShapeChoices("items.weapons");
        Armors = CreateShapeChoices("items.armors");
        Spells = catalog.Spells
            .Select(spell => CreateChoice(spell.Value, "SPELL"))
            .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Modifiers = catalog.Modifiers
            .Select(modifier => CreateChoice(modifier.Value, "MODIFIER"))
            .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public EmbeddedMagicCatalog Catalog { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Shapes { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Weapons { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Armors { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Spells { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Modifiers { get; }

    public IReadOnlyList<EmbeddedMagicChoice> GetShapes(StructuredElementKind kind) => kind switch
    {
        StructuredElementKind.Weapon => Weapons,
        StructuredElementKind.Armor => Armors,
        _ => Shapes,
    };

    public StructuredElementKind? GetItemKind(string? itemValue)
    {
        string value = itemValue?.Trim() ?? string.Empty;
        int separator = value.IndexOf('.');
        string shapeValue = separator > 0 ? value[..separator].Trim() : value;
        EmbeddedMagicShapeOption? shape = Catalog.Shapes.FirstOrDefault(option =>
            option.Value.Equals(shapeValue, StringComparison.OrdinalIgnoreCase));
        return shape?.TableKey switch
        {
            "items.weapons" => StructuredElementKind.Weapon,
            "items.armors" => StructuredElementKind.Armor,
            _ => null,
        };
    }

    public IReadOnlyList<EmbeddedMagicChoice> GetMaterials(string? shapeValue) =>
        Catalog.GetCompatibleMaterials(shapeValue)
            .Select(material => CreateChoice(material.Value, "MATERIAL"))
            .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public string GetItemRussianName(string? shapeValue, string? materialValue)
    {
        EmbeddedMagicShapeOption? shape = Catalog.Shapes.FirstOrDefault(option =>
            option.Value.Equals(shapeValue, StringComparison.OrdinalIgnoreCase));
        if (shape is null || string.IsNullOrWhiteSpace(materialValue))
        {
            return string.Empty;
        }

        string prefix = shape.TableKey == "items.weapons" ? "WEAPON" : "ARMOR";
        return FindTitle($"{prefix} {Normalize(shape.Value)} {Normalize(materialValue)}");
    }

    private IReadOnlyList<EmbeddedMagicChoice> CreateShapeChoices(string tableKey) => Catalog.Shapes
        .Where(shape => shape.TableKey.Equals(tableKey, StringComparison.Ordinal))
        .Select(CreateShapeChoice)
        .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public EmbeddedMagicChoice LocalizeShapeChoice(EmbeddedMagicChoice choice, string? material)
    {
        var shape = Catalog.Shapes.FirstOrDefault(s => s.Value.Equals(choice.Value, StringComparison.OrdinalIgnoreCase));
        return shape is null ? choice : choice with { DisplayText = CreateShapeDisplayText(shape, material) };
    }

    public EmbeddedMagicChoice ReferenceChoice(string value)
    {
        var shape = Shapes.FirstOrDefault(s => s.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (shape is not null) return shape;
        foreach (string prefix in new[] { "UNIT", "SPELL", "MATERIAL", "MODIFIER", "PERK", "INSTR" })
        {
            string title = FindTitle($"{prefix} {Normalize(value)}");
            if (title.Length > 0) return new(value, value + " — " + title);
        }
        return new(value, value);
    }

    private EmbeddedMagicChoice CreateShapeChoice(EmbeddedMagicShapeOption shape)
    {
        string prefix = shape.TableKey == "items.weapons" ? "WEAPON" : "ARMOR";
        string key = $"{prefix} {Normalize(shape.Value)}";
        // Include every compatible material title, not just the example currently displayed.
        var titles = Catalog.GetCompatibleMaterials(shape.Value)
            .Select(m => FindTitle($"{key} {Normalize(m.Value)}"));
        string models = string.Join(", ", shape.ModelNames);
        return new(shape.Value, CreateShapeDisplayText(shape))
        {
            SearchText = FindTitle(key) + "\n" + string.Join("\n", titles) + "\n" + models,
            ModelText = models
        };
    }

    private string CreateShapeDisplayText(EmbeddedMagicShapeOption shape, string? material = null)
    {
        string prefix = shape.TableKey == "items.weapons" ? "WEAPON" : "ARMOR";
        string title = FindTitle($"{prefix} {Normalize(shape.Value)}");
        if (title.Length > 0) return $"{shape.Value} — {title}";
        var compatible = Catalog.GetCompatibleMaterials(shape.Value).ToArray();
        if (compatible.Any(m => m.Value.Equals(material, StringComparison.OrdinalIgnoreCase)))
        {
            title = GetItemRussianName(shape.Value, material);
            if (title.Length > 0) return $"{shape.Value} — {title}";
        }
        // Some databases localize only a complete shape/material combination.
        // Show an explicitly labelled example instead of inventing a generic item name.
        title = compatible.Select(m => GetItemRussianName(shape.Value, m.Value)).FirstOrDefault(t => t.Length > 0) ?? "";
        return title.Length > 0 ? $"{shape.Value} — пример: {title}" : $"{shape.Value} — название RU не найдено";
    }

    private EmbeddedMagicChoice CreateChoice(string value, string prefix)
    {
        string title = FindTitle($"{prefix} {Normalize(value)}");
        return new EmbeddedMagicChoice(
            value,
            title.Length == 0 ? value : $"{title} — {value}");
    }

    private string FindTitle(string resourceName) =>
        _localizedTitles.TryGetValue(resourceName, out string? title)
            ? title.Trim()
            : string.Empty;

    private static string Normalize(string value) => value.Trim().Replace(' ', '_');
}
