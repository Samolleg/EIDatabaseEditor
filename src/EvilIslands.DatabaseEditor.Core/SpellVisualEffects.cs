namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Known native EI/JMV bindings, not editable fields of spells.sdb.
/// This is a partial resource map, not a simulation of the game's effect/state machine.</summary>
public sealed record SpellVisualBinding(string Code, string Stage, string Texture);

public static class SpellVisualEffects
{
    private static readonly string[] NativeOrder = ("arrow lightning acid_ray fireball inv_lit acid_column firewall litnwall acid_fog " +
        "prot_fire prot_electro prot_acid eagle_sight infravision detect_life invisibility silence lichdom fireworks stench " +
        "clairvoyence possession shapechange vision_fog healing stun antimagic link teleport strength weak regeneration " +
        "feeblemind speed slow comprehend charm enlarge shrink campfire rick_magic curse_magic").Split(' ');
    // See database-editor-spell-fx-preview100 evidence: native StartSpell / UpdateEffectParticles,
    // and the installed JMV CParticle::CreateImpl. Do not infer new bindings from filenames.
    public static IReadOnlyList<SpellVisualBinding> Bindings { get; } = Build();
    private static SpellVisualBinding[] Build()
    {
        var rows = new List<SpellVisualBinding>();
        void Add(string codes, string stage, params string[] textures)
        {
            foreach (string code in codes.Split(' '))
                foreach (string texture in textures) rows.Add(new(code, stage, texture + ".mmp"));
        }
        Add("arrow", "Снаряд", "FireArrowSmoke");
        Add("rick_magic", "Снаряд", "RikArrowSmoke");
        Add("acid_ray", "Снаряд", "AcidRay");
        Add("arrow", "Попадание", "FireArrowSmoke");
        Add("acid_ray", "Попадание", "AcidRay");
        Add("lightning litnwall", "Молния", "Lightning", "SmtgLight");
        Add("inv_lit", "Вспышка", "TendriLight");
        Add("acid_column", "Столб", "AcidRay");
        Add("fireball", "Полёт", "FireSmoke");
        Add("fireball", "Взрыв", "Explosion");
        Add("firewall", "Стена", "TendriSmoke");
        Add("acid_fog", "Облако", "PoisonCloud");
        Add("prot_fire", "Защитная сфера", "ProtectionF");
        Add("prot_electro", "Защитная сфера", "ProtectionE");
        Add("prot_acid", "Защитная сфера", "ProtectionA");
        Add("antimagic", "Защитная сфера", "AntiMagic");
        Add("eagle_sight infravision detect_life", "Звёзды над головой · общий атлас", "Stun");
        Add("regeneration silence", "Длительный эффект · общий атлас", "Stun");
        Add("feeblemind", "Частицы у головы", "Link");
        Add("speed slow", "Облако у ног", "VisionFog");
        Add("stench", "Облако", "Stench");
        Add("healing", "Исцеление", "Healing");
        Add("fireworks", "Фонтан частиц", "Fireworks");
        Add("clairvoyence", "Кольцо", "ClayRing");
        Add("strength weak", "Изменение силы", "Stun");
        Add("link", "Связь", "Link");
        Add("teleport", "Телепортация", "Teleport");
        return rows.ToArray();
    }

    public static IReadOnlyList<SpellVisualBinding> ForSpell(string code, int recordIndex) =>
        recordIndex < 0 || recordIndex >= NativeOrder.Length || !NativeOrder[recordIndex].Equals(code, StringComparison.OrdinalIgnoreCase)
            ? [] : Bindings.Where(b => b.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static IReadOnlyList<string> KnownUsers(string texture) => Bindings
        .Where(b => b.Texture.Equals(BaseName(texture), StringComparison.OrdinalIgnoreCase))
        .Select(b => b.Code).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string BaseName(string texture) => texture.EndsWith(".x2.mmp", StringComparison.OrdinalIgnoreCase)
        ? texture[..^7] + ".mmp" : texture;

    public static string[] Variants(string texture) => [texture, texture[..^4] + ".x2.mmp"];
}
