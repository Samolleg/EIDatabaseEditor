using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

// Immutable, minimal snapshot: no WPF objects or mutable document accessed by background checks.
public sealed record TextureRecord(string Table, int Index, string Name,
    IReadOnlyDictionary<int, string> Fields, IReadOnlyDictionary<int, IReadOnlyList<string>> Lists)
{
    public string Text(int id) => Fields.GetValueOrDefault(id, "");
    public int? Number(int id) => int.TryParse(Text(id), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    public static TextureRecord Capture(string table, DatabaseRecord record)
    {
        int[] ids = table switch
        {
            "spells.prototypes" => [18], "items.materials" => [1, 2],
            "items.weapons" or "items.armors" => [2, 3, 5, 6], "items.quick_items" => [3, 5],
            "items.quest_items" => [5], "items.loot_items" => [5, 21, 22, 23],
            "units.races" => [30, 31, 32], "units.prototypes" => [1, 3], _ => [],
        };
        var fields = record.Fields.Where(f => ids.Contains(f.Id)).GroupBy(f => f.Id).Select(g => g.First()).ToArray();
        return new(table, record.RecordIndex, record.IdentityValue,
            fields.ToDictionary(f => f.Id, f => Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? ""),
            fields.Where(f => f.TryGetStringListValues(out _)).ToDictionary(f => f.Id,
                f => { f.TryGetStringListValues(out var values); return (IReadOnlyList<string>)values.ToArray(); }));
    }
}

public sealed record TextureReference(TextureRecord Record, string Purpose, string Name,
    string Detail, bool NeedsContext = false);

public static class DatabaseTextureReferences
{
    private static readonly string[] Weapons = ["sw", "ax", "dg", "sp", "hm", "bw", "cb"];
    private static readonly string[] Armor = ["hl", "pl", "lg", "sh", "pt", "bt", "gl"];
    private static readonly Dictionary<int, string> ExtendedWeapons = new()
    { [9] = "sd", [10] = "tp", [11] = "sf", [12] = "ts", [13] = "ta", [14] = "th", [15] = "tf" };

    public static IReadOnlyList<TextureReference> Resolve(TextureRecord record,
        IReadOnlyList<TextureRecord> records, bool modProfile)
    {
        var result = new List<TextureReference>();
        void Context(string purpose, string detail) => result.Add(new(record, purpose, "", detail, true));
        void Add(string purpose, string name, string detail, bool context = false, bool graphicVariant = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            string stem = name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            result.Add(new(record, purpose, stem + ".mmp", detail, context || (modProfile && graphicVariant)));
            if (modProfile && graphicVariant && !stem.EndsWith(".x2", StringComparison.OrdinalIgnoreCase))
                result.Add(new(record, purpose + " (.x2)", stem + ".x2.mmp",
                    detail + " Вариант addon; применимость зависит от настроек графики.", true));
        }
        bool Index(int id, out int n)
        {
            int? value = record.Number(id);
            n = value ?? -1;
            return value.HasValue && n >= 0 && n != int.MaxValue;
        }
        void Icon(string prefix, int field, string purpose)
        {
            if (Index(field, out int n)) Add(purpose, $"{prefix}{n:D4}", $"Индекс из поля {field}: {n}.");
            else Context(purpose, "Индекс отсутствует или имеет специальное значение.");
        }

        switch (record.Table)
        {
            case "spells.prototypes":
                Icon("spell", 18, "Иконка заклинания");
                break;
            case "items.materials":
                Add("Иконка материала", $"material{record.Index:D4}", "Физический индекс материала.");
                break;
            case "items.quest_items":
                Icon("quitem", 5, "Квестовый предмет");
                break;
            case "items.loot_items":
                if (record.Number(21) == 0) Icon("litem", 5, "Лут");
                else if (record.Number(21) == 1) Icon("material", 23, "Материал в луте");
                else Context("Инструкция / специальный лут", "Имя зависит от типа инструкции и связанного прототипа; правило пока не подтверждено.");
                break;
            case "items.weapons":
            case "items.armors":
            case "items.quick_items":
            {
                bool quick = record.Table == "items.quick_items";
                if (!Index(5, out int first) || (!quick && !Index(6, out _)))
                { Context("Текстура предмета", "Не заданы обычные индексы текстуры."); break; }
                string materialType = record.Text(3);
                if (quick && materialType.Equals("none", StringComparison.OrdinalIgnoreCase))
                { Add("Быстрый предмет", $"qitem{first:D4}", "Предмет без материала."); break; }
                var materials = records.Where(r => r.Table == "items.materials" &&
                    r.Text(1).Equals(materialType, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (materials.Length == 0)
                { Context("Материал предмета", "Нет совместимых материалов для типа «" + materialType + "»."); break; }
                string? prefix = null;
                bool extended = false;
                if (!quick)
                {
                    int type = record.Number(2) ?? -1;
                    string[] types = record.Table == "items.weapons" ? Weapons : Armor;
                    if (type >= 0 && type < types.Length) prefix = types[type];
                    else if (record.Table == "items.weapons" && ExtendedWeapons.TryGetValue(type, out string? code))
                    { prefix = code; extended = true; }
                    if (prefix is null)
                    { Context("Тип предмета", "Не подтверждено правило имени для этого типа."); break; }
                }
                foreach (TextureRecord material in materials)
                {
                    string code = material.Text(2);
                    if (string.IsNullOrWhiteSpace(code))
                    { Context("Материал " + material.Name, "У материала нет кода для имени текстуры."); continue; }
                    string stem = quick ? $"qitem{first:D4}.{code}" :
                        $"unhuma{prefix}_{first:D2}.{code}.{record.Number(6)}";
                    Add("Предмет / " + material.Name, stem,
                        "Форма и совместимый материал. Для брони на существе дополнительно требуется маска его модели.", extended);
                }
                break;
            }
            case "units.races":
                foreach (int field in new[] { 31, 32 })
                    foreach (string name in record.Lists.GetValueOrDefault(field, []))
                        Add(field == 31 ? "Основная текстура из БД" : "Доп. текстура из БД", name,
                            "Объявление в БД; использование конкретной моделью/экземпляром требует контекста.", true, false);
                Context("Составная текстура", "Итоговые текстуры могут создаваться игрой из кожи и экипировки; готовый файл не обязателен.");
                break;
            case "units.prototypes":
            {
                var races = records.Where(r => r.Table == "units.races" && r.Name.Equals(record.Text(1), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (races.Length != 1) { Context("Раса", "Раса не найдена либо имя неоднозначно."); break; }
                TextureRecord race = races[0];
                foreach (TextureReference reference in Resolve(race, records, modProfile).Where(r => r.Name.Length > 0))
                    result.Add(reference with { Record = record, Detail = "Через расу «" + race.Name + "». " + reference.Detail });
                if (Index(3, out int skin) && !string.IsNullOrWhiteSpace(race.Text(30)))
                    Add("Кандидат слоя кожи", $"{race.Text(30)}skin_{skin:D2}",
                        "Формула составной модели. Применимость и экипировка требуют контекста.", true);
                Context("Составная модель", "Проверены объявленные ресурсы и кандидаты слоёв; полный набор зависит от модели и экипировки.");
                break;
            }
            default:
                Context("Связи с текстурами", "Для этой таблицы правила ещё не подтверждены; отсутствие текстур не заявляется.");
                break;
        }
        return result;
    }
}
