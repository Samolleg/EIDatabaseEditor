using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Models;

internal sealed record EmbeddedMagicChoice(string Value, string DisplayText)
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
            .Select(shape => new EmbeddedMagicChoice(
                shape.Value,
                $"{shape.Kind}: {shape.Value}"))
            .OrderBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
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

    public IReadOnlyList<EmbeddedMagicChoice> Spells { get; }

    public IReadOnlyList<EmbeddedMagicChoice> Modifiers { get; }

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
