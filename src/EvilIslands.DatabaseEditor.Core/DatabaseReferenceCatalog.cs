namespace EvilIslands.DatabaseEditor.Core;

public sealed record DatabaseScalarReference(
    string SourceTableKey,
    int SourceFieldId,
    string TargetTableKey,
    int TargetFieldId,
    bool AllowEmpty,
    string? Sentinel = null);

public static class DatabaseReferenceCatalog
{
    public static IReadOnlyList<DatabaseScalarReference> ScalarReferences { get; } =
        Array.AsReadOnly<DatabaseScalarReference>(
        [
            new("items.weapons", 3, "items.materials", 1, true, "none"),
            new("items.armors", 3, "items.materials", 1, true, "none"),
            new("items.quick_items", 3, "items.materials", 1, true, "none"),
            new("items.quest_items", 3, "items.materials", 1, true, "none"),
            new("items.loot_items", 3, "items.materials", 1, true, "none"),
            new("perks.perks", 10, "perks.perks", 1, true, "none"),
            new("perks.perks", 11, "perks.skills", 1, true, "none"),
            new("units.prototypes", 1, "units.races", 0, false),
            new("spells.templates", 1, "spells.prototypes", 1, true),
            new("spells.armor_templates", 1, "spells.prototypes", 1, true),
            new("spells.weapon_templates", 1, "spells.prototypes", 1, true),
        ]);
}
