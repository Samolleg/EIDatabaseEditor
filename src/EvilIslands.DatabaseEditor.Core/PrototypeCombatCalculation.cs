using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>
/// Initial NPC/prototype arithmetic for the JMV Lua contract. No saved hero, temporary
/// effects, inventory or inferred left-hand assignment. Each weapon is evaluated separately.
/// Missing/malformed inputs propagate as unavailable, never as plausible zeroes.
/// </summary>
public static class PrototypeCombatCalculation
{
    // CUnit::GetLeftHand for the two populated prototype weapon slots.
    // A shield selected as the primary weapon is not a supported attack scenario.
    internal static bool UsesOtherHand(int currentType, int otherType, bool strongMan)
    {
        if (currentType is < 0 or > 15 || otherType is < 0 or > 15) return false;
        static bool TwoHanded(int type, bool strong) => type is 5 or 6 || (!strong && type is 10 or 12 or 13 or 14 or 15);
        if (currentType == 9 || TwoHanded(currentType, strongMan)) return false;
        if (otherType == 9) return true;
        if (TwoHanded(otherType, strongMan)) return false;
        bool currentStaff = currentType is 11 or 15, otherStaff = otherType is 11 or 15;
        return currentStaff == otherStaff;
    }
    // These are formula-source identities, not restrictions on launching the game or editor.
    // Changed Lua formulas must not silently retain the old calculator. Table edits stay live.
    private static readonly Dictionary<string, string> FormulaSignatures = new()
    {
        ["GetDamageModAttr"] = "99042D361253E245E5EB2A73281B6C095EFA208FF9B0FC786E6AB3DA96D33EEB",
        ["GetSkillMod"] = "44A0671E2F16CFA9B8823929D5B770B96C0E636636E1C87EF74DB5AD14951BCF",
        ["OverloadDamageModsJmv"] = "613487A1701CCEF8D5B8E502735B15B1F7287EB12FE0FDBDC874F9F822E0C39F",
        ["OverloadAttackMods"] = "3F1CDA91A801A6BFF663B8BCE3139CEFF7DAC060DB68848D6B258DAA7A60A8C0",
        ["OverloadDefenceMods"] = "41871245471FBF570EE1839BA5858C377DB4F0625A153858C69F017B2FF32BF4",
        ["GetMaxEncumbrance"] = "CF965D933D8DA26806C690827190DBC498F67F44A6C780BC9DA82D54B4DEEE92",
        ["ApplyAttackParams"] = "9306D083489A2D7522F32BF3E784422AB4A739B2948181AE8C508DF71B7CB066",
        ["ApplyPerks"] = "D1BA14CA12B70C2211BB79EE398D1A55F094FD5D26B51B68C14A5C47A6585879",
        ["RecalcInitialJmvStats"] = "8D4F9C6ABFFBB357796FB93427A6652EB5CB02BCD05CF97B87C516C3EFDDE77C",
        ["GetModifiedDamageJmv"] = "43D608D84BC0B070B2D92B6DEB7206F38D86FCCC45733A9A1F88070D45E29750"
    };
    internal static bool SupportedFormulas(string source) => FormulaSignatures.All(pair =>
    {
        string body = Regex.Match(source, @"(?ms)^function " + pair.Key + @"\([^\n]*\n.*?(?=^function |\z)").Value;
        body = Regex.Replace(Regex.Replace(body, @"--[^\r\n]*", ""), @"\s+", "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) == pair.Value;
    });
    public static void Append(EquipmentStatReport report, ModelRecord unit, IReadOnlyList<ModelRecord> records, LuaDataWorkspace? lua)
    {
        if (lua is null) return;
        try
        {
            if (!SupportedFormulas(File.ReadAllText(lua.SchemaSource)))
            { report.Notes.Add("Боевой расчёт недоступен: формулы main.lua отличаются от поддержанного профиля JMV. Исходные числа показаны без подстановки чужих формул."); return; }
            bool correctedArmor = true;
            string config = Path.GetFullPath(Path.Combine(lua.DirectoryPath, "..", "config", "ai.ini"));
            if (File.Exists(config))
            {
                bool addon = false;
                foreach (var raw in File.ReadLines(config))
                {
                    string line = raw.Trim();
                    if (line.StartsWith('[')) { addon = line.Equals("[Addon]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!addon || line.StartsWith(';') || line.StartsWith('#')) continue;
                    var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || !pair[0].Equals("JMVNpcArmorCalculation", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair[1] is not ("0" or "1")) { report.Notes.Add("Не распознана настройка расчёта брони NPC; боевой расчёт не выполнен."); return; }
                    correctedArmor = pair[1] == "1";
                }
            }
            else report.Notes.Add("Настройки мода недоступны: для предварительного расчёта принят стандартный расчёт брони NPC JMV.");
            CalculateJmvReference(report, unit, records, lua, correctedArmor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { report.Notes.Add("Не удалось проверить формулы расчёта: " + ex.Message); }
    }
    private static string N(double value) => double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "нет данных";
    public static (double Attack, double Defense, double Damage) Overload(double ratio) => !double.IsFinite(ratio)
        ? (double.NaN, double.NaN, double.NaN)
        : ratio >= 1.5 ? (-11, -22, .3) : ratio >= 1.25 ? (-7, -14, .6)
        : ratio >= 1 ? (-4, -8, .75) : ratio >= .8 ? (-2, -4, .85)
        : ratio >= .65 ? (-1, -2, .95) : ratio >= .5 ? (0, -1, 1) : (0, 0, 1);

    public static double AttributeDamage(int type, double strength, double dexterity, double intelligence, double coefficient)
    {
        var weights = type switch
        {
            0 or 12 => (.7, .3, 0d), 1 or 13 => (.9, .1, 0d), 2 => (.2, .8, 0d),
            3 or 10 => (.3, .7, 0d), 4 or 14 => (1d, 0d, 0d), 5 => (.5, .5, 0d),
            11 or 15 => (0d, 0d, 1d), _ => (0d, 0d, 0d)
        };
        return Math.Max(0, 1 + coefficient * (weights.Item1 * (strength / 25 - 1)
            + weights.Item2 * (dexterity / 25 - 1) + weights.Item3 * (intelligence / 25 - 1)));
    }

    internal static void CalculateJmvReference(EquipmentStatReport report, ModelRecord unit, IReadOnlyList<ModelRecord> records, LuaDataWorkspace? lua, bool correctedArmor = true)
    {
        if (lua is null) return;
        var issues = new HashSet<string>();
        var perks = records.Where(r => r.Table == "perks.perks").ToArray();
        double Read(ModelRecord record, string table, string property, bool sum = false)
        {
            var group = lua.ForRecord(record).Single(g => g.Spec.Name == table);
            if (group.Problem is not null) { issues.Add(table + ": " + group.Problem); return double.NaN; }
            var keys = lua.Schema(group.Spec).Where(p => p.Value == property || p.Value.EndsWith(" — " + property, StringComparison.Ordinal)).ToArray();
            if (keys.Length != 1) { issues.Add("Не распознано поле " + table + "." + property); return double.NaN; }
            double Parse(LuaBoundEntry row)
            {
                var field = row.Field.Value.Field(keys[0].Key);
                // Absent row defaults to zero; an incomplete existing row is not silently repaired.
                if (field is not null && double.TryParse(field.Value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)) return value;
                issues.Add("Нет числового значения " + table + "." + property + " · " + record.Name);
                return double.NaN;
            }
            return sum ? group.Entries.Sum(Parse) : group.Entries.LastOrDefault() is { } last ? Parse(last) : 0;
        }
        double Level(string name) => Read(unit, "UnitPerk", name);
        double Perk(string name)
        {
            double level = Level(name);
            if (level == 0) return 0;
            var spec = LuaDataWorkspace.Bindings.Single(b => b.Name == "UnitPerk");
            string? key = lua.Schema(spec).FirstOrDefault(p => p.Value == name || p.Value.EndsWith(" — " + name, StringComparison.Ordinal)).Key;
            if (!double.IsFinite(level) || level != Math.Truncate(level) || level is < 1 or > 3 || key is null || !int.TryParse(key.TrimStart('#'), out int index) || index is < 13 or > 44)
            { issues.Add("Недопустимый уровень перка " + name); return double.NaN; }
            int offset = (index - 13) * 3 + (int)level - 1;
            if (offset >= perks.Length) { issues.Add("В БД отсутствует уровень перка " + name); return double.NaN; }
            return perks[offset].Number(19, float.NaN);
        }
        ModelRecord? Find(string table, string name)
        {
            var matches = records.Where(r => r.Table == table && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
            issues.Add("Не найдена однозначная запись " + table + ": " + name); return null;
        }
        (ModelRecord Shape, ModelRecord Material)? Item(string value, string table)
        {
            var parsed = StringListEditorCodec.AnalyzeElement(value, table == "items.armors" ? "Броня" : "Оружие");
            int dot = parsed.PrimaryValue.IndexOf('.');
            if (!parsed.IsValid || dot < 1) { issues.Add("Не разобран предмет " + value); return null; }
            if (parsed.Kind == "Предмет с магией") issues.Add("Вложенная магия не рассчитана: " + value);
            var shape = Find(table, parsed.PrimaryValue[..dot]);
            var material = Find("items.materials", parsed.PrimaryValue[(dot + 1)..]);
            return shape is null || material is null ? null : (shape, material);
        }
        var armor = new List<(ModelRecord Shape, ModelRecord Material)>();
        var occupied = new HashSet<int>();
        double weight = 0;
        bool completeArmor = true;
        foreach (var value in unit.Lists.GetValueOrDefault(32, []))
        {
            if (Item(value, "items.armors") is not { } item) { weight = double.NaN; completeArmor = false; continue; }
            int slot = item.Shape.Integer(2, -1);
            if (slot is < 0 or > 6) { issues.Add("Неизвестный слот " + value); weight = double.NaN; completeArmor = false; continue; }
            if (!occupied.Add(slot)) continue;
            armor.Add(item); weight += item.Shape.Number(8, float.NaN) * item.Material.Number(5, float.NaN);
        }
        var weapons = new List<(int Field, ModelRecord Shape, ModelRecord Material)>();
        foreach (int field in new[] { 33, 42 })
        {
            if (string.IsNullOrWhiteSpace(unit.Text(field))) continue;
            if (Item(unit.Text(field), "items.weapons") is not { } item) { weight = double.NaN; continue; }
            weapons.Add((field, item.Shape, item.Material));
            weight += item.Shape.Number(8, float.NaN) * item.Material.Number(5, float.NaN);
        }
        double Armor(string property) => completeArmor ? armor.Sum(a => Read(a.Shape, "ArmorProp", property, true)) : double.NaN;
        double Material(string property) => completeArmor ? armor.Sum(a => Read(a.Material, "MaterialProp", property, true)) : double.NaN;
        double combatStrength = Read(unit, "UnitStat", "StatStrength") + Perk("Strength");
        double combatDexterity = Read(unit, "UnitStat", "StatDexterity") + Perk("Dexterity");
        double combatIntelligence = Read(unit, "UnitStat", "StatIntelligence") + Perk("Intelligence");
        double strength = combatStrength + Material("Strength");
        double dexterity = combatDexterity + Material("Dexterity");
        double intelligence = combatIntelligence + Material("Intelligence");
        double capacity = strength * 12 * (1 + Perk("Lift") * .01);
        double ratio = capacity > 0 ? weight / capacity : double.NaN;
        var overload = Overload(ratio);
        var general = new List<EquipmentStatRow> {
            new("Сила", N(strength)), new("Ловкость", N(dexterity)), new("Разум", N(intelligence)),
            new("Нагрузка экипировки", N(weight) + " / " + N(capacity) + " (" + N(ratio * 100) + "%)")
        };
        double attackArmor = Armor("Attack"), defenseArmor = Armor("Defense"), armory = Level("Armory");
        double mitigationAttack = armory is >= 0 and <= 3 && armory == Math.Truncate(armory) ? new double[] { 0, 1, 2, 4 }[(int)armory] : double.NaN;
        double mitigationDefense = armory is >= 0 and <= 3 && armory == Math.Truncate(armory) ? new double[] { 0, 2, 4, 7 }[(int)armory] : double.NaN;
        foreach (var weapon in weapons)
        {
            int type = weapon.Shape.Integer(2, -1);
            string title = "Оружие " + (weapon.Field == 33 ? "1" : "2");
            if (type is < 0 or > 15 || type is 7 or 8 or 9) { issues.Add(title + ": боевой тип не поддержан расчётом одиночного оружия"); continue; }
            bool ranged = type is 5 or 6 or 11 or 15;
            double skill = Level(ranged ? "Archery" : "Melee");
            double category = Read(weapon.Shape, "WeaponProp", "WeightType");
            double mastery = category switch { 1 => Perk("WeaponsLight"), 2 => Perk("WeaponsMiddle"), 3 => Perk("WeaponsHeavy"), 0 => 0, _ => double.NaN };
            double baseAttack = Math.Truncate(skill + weapon.Shape.Number(29, float.NaN));
            double baseDefense = Math.Truncate(skill + weapon.Shape.Number(30, float.NaN));
            var other = weapons.FirstOrDefault(w => w.Field != weapon.Field);
            bool paired = other.Shape is not null && UsesOtherHand(type, other.Shape.Integer(2, -1), Level("StrongMan") >= 1);
            double leftAttack = 0, leftDefense = 0, pairPenalty = 0;
            if (paired)
            {
                leftAttack = other.Shape!.Number(29, float.NaN);
                leftDefense = other.Shape.Number(30, float.NaN);
                if (other.Shape.Integer(2, -1) != 9)
                {
                    leftAttack = (weapon.Shape.Number(29, float.NaN) + leftAttack) * .5;
                    leftDefense = (weapon.Shape.Number(30, float.NaN) + leftDefense) * .5;
                    baseAttack -= weapon.Shape.Number(29, float.NaN);
                    baseDefense -= weapon.Shape.Number(30, float.NaN);
                    pairPenalty = Level("WeaponsMaster") == 1 ? -3 : -7;
                }
                // Lua uses a single penalty even when both weapons are two-handed.
                if (type is 10 or 12 or 13 or 14 or 15 || other.Shape.Integer(2, -1) is 10 or 12 or 13 or 14)
                    pairPenalty -= 4;
            }
            double attack = baseAttack + leftAttack + pairPenalty + attackArmor + mastery + Math.Max(0, Math.Min(-attackArmor, mitigationAttack)) + overload.Attack;
            double duelist = !paired && (type is 0 or 1 or 2 or 3 or 4 or 11) && Level("Duelist") == 1 ? 3 : 0;
            double defense = baseDefense + leftDefense + defenseArmor + mastery + Math.Max(0, Math.Min(-defenseArmor, mitigationDefense)) + overload.Defense + duelist;
            // The NPC attack hook calls RecalcInitialJmvStatsExt immediately before damage:
            // it resets attributes to UnitStat + perks, without GetModifiedHealth's material additions.
            double attribute = AttributeDamage(type, combatStrength, combatDexterity, combatIntelligence, .5) - 1;
            double dexPerk = AttributeDamage(type, combatStrength, combatDexterity, combatIntelligence, .25 * Level("DexMan")) - 1;
            double skillDamage = -.1 + skill / 800;
            double loadDamage = type is 6 or 11 or 15 ? 0 : overload.Damage - 1;
            double masteryDamage = category switch { 1 => mastery * .03, 2 => mastery * .01, _ => 0 };
            double factor = 1 + attribute + dexPerk + skillDamage + loadDamage + masteryDamage;
            double min = weapon.Shape.Number(24, float.NaN) * weapon.Material.Number(10, float.NaN);
            double max = (weapon.Shape.Number(24, float.NaN) + weapon.Shape.Number(25, float.NaN)) * weapon.Material.Number(10, float.NaN);
            // Lua SAttackParams bindings store attack/defense as ints (truncate towards zero).
            general.Add(new(title + " · атака", N(Math.Truncate(attack))));
            general.Add(new(title + " · защита", N(Math.Truncate(defense))));
            general.Add(new(title + " · урон", N(Math.Max(1, min * factor)) + "–" + N(Math.Max(1, max * factor))));
            general.Add(new(title + " · левая рука", paired ? unit.Text(other.Field) : "нет"));
            report.Details.Add(new(title + " · расчёт", [new("Навык", N(skill)), new("Атака: основа после учёта пары", N(baseAttack)),
                new("Защита: навык + оружие", N(baseDefense)), new("Броня: атака / защита", N(attackArmor) + " / " + N(defenseArmor)),
                new("Левая рука: атака / защита", N(leftAttack) + " / " + N(leftDefense)), new("Парное оружие: штраф атаки", N(pairPenalty)),
                new("Мастерство оружия", N(mastery)), new("Снятие штрафа брони: атака / защита", N(Math.Max(0, Math.Min(-attackArmor, mitigationAttack))) + " / " + N(Math.Max(0, Math.Min(-defenseArmor, mitigationDefense)))),
                new("Перегруз: атака / защита", N(overload.Attack) + " / " + N(overload.Defense)), new("Дуэлянт: защита", N(duelist)),
                new("Урон: атрибуты", N(attribute * 100) + "%"), new("Урон: Ловкач", N(dexPerk * 100) + "%"),
                new("Урон: навык", N(skillDamage * 100) + "%"), new("Урон: перегруз", N(loadDamage * 100) + "%"),
                new("Урон: мастерство", N(masteryDamage * 100) + "%"), new("Общий множитель урона", N(factor))]));
        }
        if (weapons.Count == 0 && string.IsNullOrWhiteSpace(unit.Text(33)) && string.IsNullOrWhiteSpace(unit.Text(42)))
        {
            general.Add(new("Атака без оружия", N(Math.Truncate(unit.Number(15, float.NaN) + attackArmor + Math.Max(0, Math.Min(-attackArmor, mitigationAttack)) + overload.Attack))));
            general.Add(new("Защита без оружия", N(Math.Truncate(unit.Number(16, float.NaN) + defenseArmor + Math.Max(0, Math.Min(-defenseArmor, mitigationDefense)) + overload.Defense))));
        }
        var race = Find("units.races", unit.Text(1));
        if (race is not null)
        {
            var baseArmor = EquipmentInspection.Numbers(race, 39);
            var zone = new double[4, 7];
            foreach (var item in armor)
            {
                int slot = item.Shape.Integer(2, -1), location = slot switch { 0 => 0, 1 or 3 => 1, 6 => 2, _ => 3 };
                var main = EquipmentInspection.Numbers(item.Shape, 21);
                var additional = EquipmentInspection.Numbers(item.Shape, 22);
                var material = EquipmentInspection.Numbers(item.Material, 11);
                for (int i = 0; i < 7; i++)
                {
                    zone[location, i] += main.Length >= 8 && material.Length == 7 ? main[0] * main[i + 1] * material[i] : double.NaN;
                    if (slot is 1 or 3) zone[2, i] += additional.Length >= 8 && material.Length == 7 ? additional[0] * additional[i + 1] * material[i] : double.NaN;
                }
            }
            var armorRows = new List<EquipmentStatRow>();
            for (int i = 0; i < 7; i++)
            {
                double basis = baseArmor.Length == 7 ? baseArmor[i] * unit.Number(11, float.NaN) : double.NaN;
                double items = correctedArmor ? zone[0, i] * .1 + (zone[1, i] + zone[2, i] + zone[3, i]) * .3 : zone[3, i] * .3;
                double beforePerks = completeArmor ? basis + items : double.NaN;
                double bonus = i < 3 ? (beforePerks > 0 ? Perk("Armory") : 0) : i < 6 ? Perk("Flow") : 0;
                armorRows.Add(new(EquipmentInspection.DamageTypes[i], N(beforePerks * (1 + bonus * .01))));
            }
            report.Details.Insert(0, new("Броня NPC · раса + экипировка + перки, до SpellAddon", armorRows));
            general.Add(new("Броня до SpellAddon · колющая / рубящая / дробящая", string.Join(" / ", armorRows.Take(3).Select(r => r.Value))));
        }
        if (report.General.Count > 2)
        {
            report.Details.Add(new("Исходные значения БД и предметов", report.General.Skip(2).ToArray()));
            report.General.RemoveRange(2, report.General.Count - 2);
        }
        report.General.InsertRange(2, general);
        report.Notes.RemoveAll(n => n == "Показаны исходные значения. Полный итог боя с перками, нагрузкой и SpellAddon пока не рассчитан.");
        report.General.Insert(2, new("Режим расчёта", "Прототип NPC · JMV · предварительный"));
        report.Notes.Add("Расчёт JMV для исходного прототипа: показан выбор каждого оружия основным, совместимая левая рука учитывается. Без критического удара, ранений и временных эффектов. Нагрузка включает только перечисленную экипировку, без инвентаря.");
        report.Notes.Add("Комплекты, украшения, вложенная магия и поправки SpellAddon ещё не включены. Это не окончательные характеристики игрового экземпляра.");
        report.Notes.AddRange(issues);
    }
}
