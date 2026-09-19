using System.Globalization;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record EquipmentStatRow(string Name, string Value);
public sealed record EquipmentStatSection(string Title, IReadOnlyList<EquipmentStatRow> Rows);
public sealed class EquipmentStatReport
{
    public List<EquipmentStatRow> General { get; } = [];
    public List<EquipmentStatSection> Details { get; } = [];
    public List<string> Notes { get; } = [];
}

/// <summary>Deterministic item/race breakdown; never executes arbitrary Lua or assumes a saved actor's state.</summary>
public static class EquipmentInspection
{
    public static readonly string[] Slots = ["Шлем", "Панцирь", "Поножи", "Рубашка", "Штаны", "Сапоги", "Перчатки"];
    public static readonly string[] DamageTypes = ["Колющий", "Рубящий", "Дробящий", "Огонь", "Вода", "Воздух", "Общий"];
    public static double[] Numbers(ModelRecord record, int field) => record.Text(field).Split(',', StringSplitOptions.TrimEntries)
        .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : double.NaN).ToArray();
    private static string N(double n) => double.IsFinite(n) ? n.ToString("0.###", CultureInfo.InvariantCulture) : "нет данных";

    public static EquipmentStatReport Inspect(ModelRecord unit, IReadOnlyList<ModelRecord> records, LuaDataWorkspace? lua)
    {
        var report = new EquipmentStatReport();
        Describe(unit, records, lua, report);
        PrototypeCombatCalculation.Append(report, unit, records, lua);
        return report;
    }

    public static string Describe(ModelRecord unit, IReadOnlyList<ModelRecord> records, LuaDataWorkspace? lua, EquipmentStatReport? report = null)
    {
        var text = new StringBuilder("Расчёт исходной экипировки\n");
        var problems = new List<string>();
        report?.General.AddRange([
            new("Прототип", unit.Name), new("Раса", unit.Text(1)),
            new("Атака прототипа", unit.Text(15)), new("Защита прототипа", unit.Text(16)),
            new("Урон без оружия", N(unit.Number(19, float.NaN)) + "–" + N(unit.Number(19, float.NaN) + unit.Number(20, float.NaN))) ]);
        double? PerkModifier(string enumName)
        {
            if (lua is null) return null;
            var group = lua.ForRecord(unit).Single(g => g.Spec.Name == "UnitPerk");
            if (group.Problem is not null) return null;
            string? key = lua.Schema(group.Spec).FirstOrDefault(p => p.Value == enumName || p.Value.EndsWith(" — " + enumName, StringComparison.Ordinal)).Key;
            if (key is null || !int.TryParse(key.TrimStart('#'), out int index) || index is < 13 or > 44) return null;
            var row = group.Entries.LastOrDefault();
            string raw = row?.Field.Value.Field(key)?.Value.Text ?? "0";
            if (!int.TryParse(raw, out int level) || level is < 0 or > 3) return null;
            if (level == 0) return 0;
            var perks = records.Where(r => r.Table == "perks.perks").ToArray();
            int offset = (index - 13) * 3 + level - 1;
            return offset < perks.Length ? perks[offset].Number(19, float.NaN) : null;
        }
        ModelRecord? Find(string table, string name)
        {
            var found = records.Where(r => r.Table == table && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 1) return found[0];
            problems.Add((found.Length == 0 ? "Не найдена" : "Неоднозначна") + " запись " + table + ": " + name);
            return null;
        }
        (ModelRecord Shape, ModelRecord Material)? Item(string value, string table)
        {
            var a = StringListEditorCodec.AnalyzeElement(value, table == "items.armors" ? "Броня" : "Оружие");
            int dot = a.PrimaryValue.IndexOf('.');
            if (!a.IsValid || dot < 1) { problems.Add("Не разобран предмет: " + value); return null; }
            if (a.Kind == "Предмет с магией") problems.Add("Вложенная магия " + value + " не входит в базовые числа.");
            var shape = Find(table, a.PrimaryValue[..dot]); var material = Find("items.materials", a.PrimaryValue[(dot + 1)..]);
            return shape is null || material is null ? null : (shape, material);
        }
        var locations = new double[4, 7];
        var equipped = new List<ModelRecord>();
        var occupied = new HashSet<int>();
        double weight = 0;
        foreach (string value in unit.Lists.GetValueOrDefault(32, []))
        {
            if (Item(value, "items.armors") is not { } item) continue;
            int slot = item.Shape.Integer(2, -1);
            if (slot is < 0 or > 6) { problems.Add("Нет поддерживаемого слота: " + value); continue; }
            if (!occupied.Add(slot)) { problems.Add(Slots[slot] + ": несколько вариантов, в расчёте первый."); continue; }
            equipped.Add(item.Shape); equipped.Add(item.Material);
            weight += item.Shape.Number(8, float.NaN) * item.Material.Number(5, float.NaN);
            int location = slot switch { 0 => 0, 1 or 3 => 1, 6 => 2, _ => 3 };
            double[] main = Numbers(item.Shape, 21), additional = Numbers(item.Shape, 22), material = Numbers(item.Material, 11);
            for (int i = 0; i < 7; i++)
            {
                locations[location, i] += main.Length >= 8 && material.Length == 7 ? main[0] * main[i + 1] * material[i] : double.NaN;
                if (slot is 1 or 3) locations[2, i] += additional.Length >= 8 && material.Length == 7 ? additional[0] * additional[i + 1] * material[i] : double.NaN;
            }
        }
        text.AppendLine("Броня предметов по зонам (до перков и заклинаний):");
        text.AppendLine("                         " + string.Join(" / ", DamageTypes));
        string[] names = ["Голова", "Тело", "Руки", "Ноги"];
        for (int j = 0; j < 4; j++) report?.Details.Add(new("Броня предметов · " + names[j],
            Enumerable.Range(0, 7).Select(i => new EquipmentStatRow(DamageTypes[i], N(locations[j, i]))).ToArray()));
        for (int j = 0; j < 4; j++) text.AppendLine(names[j] + ": " + string.Join(" / ", Enumerable.Range(0, 7).Select(i => N(locations[j, i]))));
        if (PerkModifier("Armory") is { } armory && PerkModifier("Flow") is { } flow)
        {
            report?.Details.Add(new("Модификаторы перков брони", [new("Ношение доспехов", N(armory) + "%"), new("Управление потоками", N(flow) + "%")]));
            text.AppendLine($"Вклад перков: Ношение доспехов +{N(armory)}% физической брони; Управление потоками +{N(flow)}% огня, воды и воздуха.");
            text.AppendLine("Изолированный вклад этих перков в броню предметов (без расовой основы и остальных модификаторов):");
            for (int j = 0; j < 4; j++) text.AppendLine(names[j] + ": " + string.Join(" / ", Enumerable.Range(0, 7)
                .Select(i => N(locations[j, i] * (i < 3 ? armory : i < 6 ? flow : 0) * .01))));
        }
        var race = Find("units.races", unit.Text(1));
        if (race is not null)
        {
            var defense = Numbers(race, 39);
            report?.Details.Add(new("Расовая основа брони", defense.Select((n, i) => new EquipmentStatRow(
                i < DamageTypes.Length ? DamageTypes[i] : "Тип " + i, N(n * unit.Number(11, float.NaN)))).ToArray()));
            text.AppendLine("Расовая основа × броня прототипа: " + string.Join(" / ", defense.Select(n => N(n * unit.Number(11, float.NaN)))));
        }
        foreach (int field in new[] { 33, 42 })
        {
            string value = unit.Text(field);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (Item(value, "items.weapons") is not { } item) continue;
            double damage = item.Material.Number(10, float.NaN);
            double min = item.Shape.Number(24, float.NaN) * damage;
            double max = (item.Shape.Number(24, float.NaN) + item.Shape.Number(25, float.NaN)) * damage;
            string weapon = "Оружие " + (field == 33 ? 1 : 2);
            report?.General.Add(new(weapon + " · базовый урон", N(min) + "–" + N(max)));
            report?.Details.Add(new(weapon + " · параметры", [new("Предмет", value),
                new("Бонус атаки", item.Shape.Text(29)), new("Бонус защиты", item.Shape.Text(30)), new("Дальность", item.Shape.Text(23))]));
            text.AppendLine($"Оружие {(field == 33 ? 1 : 2)}: {value} · базовый урон {N(min)}–{N(max)}");
            text.AppendLine($"  Модификаторы чертежа: атака {item.Shape.Text(29)}, защита {item.Shape.Text(30)}; дальность {item.Shape.Text(23)}");
            weight += item.Shape.Number(8, float.NaN) * item.Material.Number(5, float.NaN);
        }
        text.AppendLine("Вес перечисленной экипировки: " + N(weight));
        report?.General.Add(new("Вес экипировки", N(weight)));
        report?.Notes.Add("Показаны исходные значения. Полный итог боя с перками, нагрузкой и SpellAddon пока не рассчитан.");
        text.AppendLine($"Прототип {unit.Name}; раса {unit.Text(1)}; атака {unit.Text(15)}, защита {unit.Text(16)}; урон без оружия {unit.Text(19)} + разброс {unit.Text(20)}.");
        if (lua is null)
        {
            text.AppendLine("Lua не подключён: дополнительные свойства и перки неизвестны.");
            report?.Notes.Add("Lua не подключён: дополнительные свойства и перки неизвестны.");
        }
        else
        {
            text.AppendLine("Контекст Lua прототипа (исходные значения, не готовый итог боя):");
            foreach (var group in lua.ForRecord(unit).Where(g => g.Spec.Name is "UnitPerk" or "UnitStat" or "RacialArmor"))
            {
                if (group.Problem is not null) { text.AppendLine(group.Spec.Title + ": " + group.Problem); report?.Notes.Add(group.Spec.Title + ": " + group.Problem); continue; }
                var row = group.Entries.LastOrDefault();
                if (row is null) { text.AppendLine(group.Spec.Title + ": записи нет"); continue; }
                var labels = lua.Schema(group.Spec);
                report?.Details.Add(new(group.Spec.Title + " · Lua", row.Field.Value.Fields.Where(f => f.Key != "#1")
                    .Select(f => new EquipmentStatRow(labels.GetValueOrDefault(f.Key) ?? f.Key, f.Value.Text)).ToArray()));
                text.AppendLine(group.Spec.Title + ": " + string.Join("; ", row.Field.Value.Fields.Where(f => f.Key != "#1")
                    .Select(f => (labels.GetValueOrDefault(f.Key) ?? f.Key) + " = " + f.Value.Text)));
                if (group.Spec.Name == "UnitPerk")
                {
                    var perks = records.Where(r => r.Table == "perks.perks").ToArray();
                    foreach (var f in row.Field.Value.Fields)
                    {
                        if (!int.TryParse(f.Key.TrimStart('#'), out int index) || index is < 13 or > 44 ||
                            !int.TryParse(f.Value.Text, out int level) || level == 0) continue;
                        int perkIndex = (index - 13) * 3 + level - 1;
                        if (level is < 1 or > 3 || perkIndex >= perks.Length)
                        { problems.Add("Не разрешён уровень перка: " + f.Key + " = " + f.Value.Text); continue; }
                        var perk = perks[perkIndex];
                        report?.Details.Add(new("Перк · " + (labels.GetValueOrDefault(f.Key) ?? f.Key),
                            [new("Уровень", level.ToString()), new("Модификатор", perk.Text(19))]));
                        text.AppendLine($"  {labels.GetValueOrDefault(f.Key) ?? f.Key}: уровень {level}, модификатор {perk.Text(19)} · {perk.Name}");
                    }
                }
            }
            var sums = new Dictionary<string, double>();
            foreach (var record in equipped)
                foreach (var group in lua.ForRecord(record).Where(g => g.Spec.Name is "ArmorProp" or "MaterialProp"))
                {
                    var labels = lua.Schema(group.Spec);
                    foreach (var row in group.Entries)
                        foreach (var f in row.Field.Value.Fields.Where(f => f.Key != "#1"))
                            if (double.TryParse(f.Value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                            {
                                string key = group.Spec.Title + " · " + (labels.GetValueOrDefault(f.Key) ?? f.Key);
                                sums[key] = sums.GetValueOrDefault(key) + v;
                            }
                }
            text.AppendLine("Суммы Lua по надетой броне (в единицах исходных таблиц):");
            foreach (var pair in sums.Where(p => p.Value != 0)) text.AppendLine(pair.Key + ": " + N(pair.Value));
            report?.Details.Add(new("Дополнительные свойства экипировки · Lua", sums.Where(p => p.Value != 0)
                .Select(p => new EquipmentStatRow(p.Key, N(p.Value))).ToArray()));
        }
        text.AppendLine("Итоговый бой пока не рассчитан: применение перков, SpellAddon, нагрузка, комплекты и состояние героя требуют отдельного расчёта. Расовая основа и броня выше показаны отдельно.");
        foreach (string problem in problems.Distinct()) text.AppendLine(problem);
        report?.Notes.AddRange(problems.Distinct());
        return text.ToString();
    }
}
