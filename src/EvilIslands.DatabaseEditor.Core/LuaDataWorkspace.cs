using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record LuaBindingSpec(string Name, string Title, string Category, string? EnumName = null, string? Child = null)
{
    public string FileName => Child is null ? "tables.lua" : "npc_consumables.lua";
    public bool IsArray => EnumName is not null;
    public bool Supports(string table) => Name == "ForbiddenItems" && table is "spells.prototypes" or "spells.modifiers" || Category switch
    {
        "unit" => table == "units.prototypes",
        "spell" => table == "spells.prototypes",
        "item" => table.StartsWith("items.", StringComparison.Ordinal),
        _ => table == "items." + Category
    };
    public string KeyFor(ModelRecord record) => record.Table is "spells.prototypes" or "spells.modifiers" ? record.Text(1) : record.Name;
    public string Hint => Name switch
    {
        "UnitPerk" or "UnitStat" => "Связь по точному имени прототипа. Повторные строки сохранены; текущий Lua-getter использует последнюю найденную строку. Отсутствующее поле и ноль различаются.",
        "ForbiddenItems" => "Связь по имени предмета/материала либо коду заклинания/руны. Один ключ может встречаться несколько раз. Указывается числовой ID магазина; порядок строк сохраняется. Shop = 0 сбрасывает результат проверки, это не запрет во всех магазинах.",
        "NpcCombatConsumables" => "Правило zones[карта][ID] приоритетнее prototypes. registrationOnly = true обслуживает регистрацию из MOB и само не выдаёт расходники. Каталог npc_consumables_catalog.lua и правила карт остаются отдельными источниками. Изменения не пополняют уже потраченные заряды в сохранении.",
        "DeathFearExcludedPrototypes" => "true исключает прототип из «Страха смерти». Отсутствие записи и false сохраняются как разные состояния.",
        "MeleeActiveSpells" or "RangedActiveSpells" => "Ключ — код заклинания (codeName), а не название или русский текст. Маски оружия и числовые флаги сохраняют формат Lua. Новый навык также требует поддержки игрового runtime.",
        _ => "Связь по точному техническому имени записи БД. Показаны исходные значения Lua, а не вычисленные итоговые характеристики игры."
    };
}

public sealed record LuaBoundEntry(LuaBindingSpec Spec, LuaSourceDocument Document, LuaLiteral Parent, LuaField Field, string LinkKey, int Ordinal)
{
    public string Location => System.IO.Path.GetFileName(Document.Path) + ":" + Document.Line(Field.Start);
    public string Caption => Spec.Title + " · " + (Spec.IsArray ? "строка " + Ordinal : LinkKey);
    public string Summary => Spec.Name == "ForbiddenItems" ? "Магазин: " + (Field.Value.Field("#2")?.Value.Text ?? "нет значения")
        : Field.Value.Kind == LuaLiteralKind.Table ? string.Join(" · ", Field.Value.Fields.Take(5).Select(f => f.Key.StartsWith('#') ? f.Value.Text : f.Key + (f.Value.Kind == LuaLiteralKind.Table ? "" : " = " + f.Value.Text)))
        : Field.Value.Text;
}
public sealed record LuaBoundGroup(LuaBindingSpec Spec, LuaSourceDocument? Document, LuaLiteral? Table, string LinkKey, IReadOnlyList<LuaBoundEntry> Entries, string? Problem)
{
    public string Caption => Spec.Title + " · " + Entries.Count + (Problem is null ? "" : " · недоступно");
}

public sealed class LuaDataWorkspace
{
    public static IReadOnlyList<LuaBindingSpec> Bindings { get; } = [
        new("UnitPerk", "Навыки и умения", "unit", "UnitPerkType"),
        new("UnitStat", "Характеристики прототипа", "unit", "UnitStatType"),
        new("RacialArmor", "Дополнительная броня", "unit", "RacialArmorType"),
        new("DeathFearExcludedPrototypes", "Исключение: Страх смерти", "unit"),
        new("NpcCombatConsumables", "Виртуальные расходники", "unit", Child: "prototypes"),
        new("WeaponProp", "Свойства оружия", "weapons", "WeaponPropType"),
        new("ArmorProp", "Свойства доспеха", "armors", "ArmorPropType"),
        new("MaterialProp", "Свойства материала", "materials", "MaterialPropType"),
        new("JewelryProp", "Свойства украшения", "quick_items", "JewelryPropType"),
        new("ForbiddenItems", "Запрет размножения в магазинах", "item", "ForbiddenItemsType"),
        new("MeleeActiveSpells", "Боевой навык: ближний бой", "spell"),
        new("RangedActiveSpells", "Боевой навык: стрельба", "spell") ];
    public static IReadOnlyList<string> ConsumableKinds { get; } = ["regeneration", "healing", "strength", "speed", "enlarge"];
    public string DirectoryPath { get; }
    public IReadOnlyList<LuaSourceDocument> Documents { get; }
    public IReadOnlyList<string> Problems { get; }
    public string SchemaSource { get; }
    private readonly Dictionary<string, Dictionary<string, string>> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requiredUnitStats = new(StringComparer.Ordinal);
    public event EventHandler? Changed;
    public bool HasChanges => Documents.Any(d => d.HasChanges);
    public int ZoneRuleCount { get; }

    private LuaDataWorkspace(string directory)
    {
        DirectoryPath = System.IO.Path.GetFullPath(directory);
        List<LuaSourceDocument> docs = []; List<string> problems = [];
        foreach (var file in Bindings.GroupBy(b => b.FileName))
        {
            string path = System.IO.Path.Combine(DirectoryPath, file.Key);
            try { docs.Add(LuaSourceDocument.Open(path, file.Select(b => b.Name).ToArray())); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            { problems.Add(file.Key + ": " + ex.Message); }
        }
        Documents = docs; Problems = problems;
        string main = System.IO.Path.Combine(DirectoryPath, "..", "main.lua"); SchemaSource = main;
        try
        {
            var enumNames = Bindings.Select(b => b.EnumName).OfType<string>().ToArray();
            var enums = LuaSourceDocument.Open(main, enumNames);
            foreach (var name in enumNames)
            {
                if (!enums.Tables.TryGetValue(name, out var table) || table.Value is null) continue;
                var labels = new Dictionary<string, string>();
                // Lua overwrites repeated named keys; do not label the obsolete Backstab slot as the active perk.
                foreach (var f in table.Value.Fields.GroupBy(f => f.Key).Select(g => g.Last()))
                    if (int.TryParse(f.Value.Text, out int n))
                    {
                        int end = enums.Text.IndexOf('\n', f.Value.End); if (end < 0) end = enums.Text.Length;
                        string tail = enums.Text[f.Value.End..end]; int comment = tail.IndexOf("--", StringComparison.Ordinal);
                        string label = comment < 0 ? "" : tail[(comment + 2)..].Trim();
                        labels["#" + n] = label.Length > 0 ? label + " — " + f.Key : f.Key;
                        if (name == "UnitStatType" && f.Key is "Exp" or "StatStrength" or "StatDexterity" or "StatIntelligence" or "CriticalChance" or "CriticalDamage")
                            _requiredUnitStats["#" + n] = labels["#" + n];
                    }
                _labels[name] = labels;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        { problems.Add("Подписи полей main.lua недоступны: " + ex.Message); }
        var npc = docs.FirstOrDefault(d => d.Tables.ContainsKey("NpcCombatConsumables"));
        ZoneRuleCount = npc?.Tables["NpcCombatConsumables"].Value?.Field("zones")?.Value.Fields.Count ?? 0;
    }
    public static LuaDataWorkspace Open(string folder)
    {
        string dir = System.IO.Path.GetFullPath(folder);
        if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "lua"))) dir = System.IO.Path.Combine(dir, "lua");
        return new(dir);
    }
    public static string? FindNearby(string databasePath)
    {
        if (!File.Exists(databasePath)) return null;
        string? folder = System.IO.Path.GetDirectoryName(databasePath);
        foreach (var candidate in new[] { folder, folder is null ? null : System.IO.Path.GetDirectoryName(folder) }.OfType<string>())
            if (File.Exists(System.IO.Path.Combine(candidate, "lua", "tables.lua")) && File.Exists(System.IO.Path.Combine(candidate, "main.lua"))) return System.IO.Path.Combine(candidate, "lua");
        return null;
    }
    public string FieldName(LuaBindingSpec spec, string key) => spec.EnumName is not null && _labels.TryGetValue(spec.EnumName, out var labels) && labels.TryGetValue(key, out var label)
        ? label + " [" + key[1..] + "]" : key.StartsWith('#') ? "Поле [" + key[1..] + "]" : NamedLabel(key);
    private static string NamedLabel(string key)
    {
        string? label = key switch
        {
            "displayName" => "Название", "enabled" => "Включён (0/1)", "requiredMelee" => "Требование ближнего боя", "requiredArchery" => "Требование стрельбы",
            "staminaMultiplier" => "Множитель затрат", "staminaCost" => "Затраты запаса сил", "cooldownSeconds" => "Перезарядка, с",
            "damageMultiplier" => "Множитель урона", "weaponMask" => "Битовая маска оружия", "accuracyBonus" => "Бонус точности",
            "projectileCount" => "Число снарядов", "areaRadius" => "Радиус действия", "scaleDamageWithMelee" => "Масштабировать урон (0/1)",
            "grantPrototype" => "Выдать прототип (0/1)", "allowDualWield" => "Два оружия (0/1)", "allowDeconstruction" => "Разбор (0/1)",
            "allowItemInsertion" => "Встраивание в предмет (0/1)", "allowSale" => "Продажа (0/1)", "allowNpc" => "Применение NPC (0/1)",
            "formula" or "spell" => "Формула", "charges" => "Заряды", "registrationOnly" => "Только регистрация из MOB", _ => null
        };
        return label is null ? key : label + " · " + key;
    }
    public IReadOnlyDictionary<string, string> Schema(LuaBindingSpec spec) => spec.EnumName is not null && _labels.TryGetValue(spec.EnumName, out var labels) ? labels : new Dictionary<string, string>();

    public IReadOnlyList<LuaBoundGroup> ForRecord(ModelRecord record) => Bindings.Where(b => b.Supports(record.Table)).Select(spec =>
    {
        string key = spec.KeyFor(record);
        var doc = Documents.FirstOrDefault(d => System.IO.Path.GetFileName(d.Path).Equals(spec.FileName, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return new LuaBoundGroup(spec, null, null, key, [], spec.FileName + " не подключён.");
        if (!doc.Tables.TryGetValue(spec.Name, out var assignment)) return new(spec, doc, null, key, [], "Таблица не найдена в файле.");
        var table = assignment.Value;
        if (spec.Child is not null) table = table?.Field(spec.Child)?.Value;
        if (table?.Kind != LuaLiteralKind.Table) return new(spec, doc, null, key, [], assignment.Error ?? "Нет таблицы " + spec.Child);
        if (key.Length == 0) return new(spec, doc, table, key, [], "В БД не задан ключ связи.");
        var entries = table.Fields.Select((field, i) => new LuaBoundEntry(spec, doc, table, field, key, i + 1))
            .Where(e => (spec.IsArray ? e.Field.Value.Field("#1")?.Value.Text : e.Field.Key) == key).ToArray();
        return new(spec, doc, table, key, entries, null);
    }).ToArray();

    public IReadOnlyList<(string Name, LuaLiteral Value)> Templates(LuaBoundGroup group) => group.Table?.Fields
        .Where(f => f.Value.Kind == LuaLiteralKind.Table)
        .Select(f => (group.Spec.IsArray ? f.Value.Field("#1")?.Value.Text ?? "?" : f.Key, f.Value)).ToArray() ?? [];

    public void Edit(LuaBoundEntry entry, IEnumerable<LuaPatch> patches, string description)
    {
        EnsureCurrent(entry.Document, entry.Spec, entry.Parent);
        var array = patches.ToArray();
        if (array.Any(p => p.Start < entry.Field.Value.Start || p.Start + p.Length > entry.Field.Value.End)) throw new InvalidOperationException("Правка за пределами выбранной записи.");
        string text = entry.Document.Text[entry.Field.Value.Start..entry.Field.Value.End];
        foreach (var p in array.OrderByDescending(p => p.Start)) text = text.Remove(p.Start - entry.Field.Value.Start, p.Length).Insert(p.Start - entry.Field.Value.Start, p.Text);
        Validate(entry.Spec, LuaLiteralReader.Read(text), entry.LinkKey);
        entry.Document.Apply(array, entry.Spec.Name + " / " + entry.LinkKey + ": " + description); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Add(LuaBoundGroup group, string literal)
    {
        if (group.Document is null || group.Table is null || group.Problem is not null) throw new InvalidOperationException("Источник недоступен.");
        EnsureCurrent(group.Document, group.Spec, group.Table);
        if (!group.Spec.IsArray && group.Entries.Count > 0) throw new InvalidOperationException("Ключ уже существует; редактируйте имеющуюся запись.");
        var value = LuaLiteralReader.Read(literal); Validate(group.Spec, value, group.LinkKey);
        string field = group.Spec.IsArray ? literal : "[" + LuaLiteralReader.Quote(group.LinkKey) + "] = " + literal;
        group.Document.Apply(group.Document.Append(group.Table, field), group.Spec.Name + " / " + group.LinkKey + ": добавлена запись\n" + field); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Remove(LuaBoundEntry entry)
    {
        EnsureCurrent(entry.Document, entry.Spec, entry.Parent);
        entry.Document.Apply(entry.Document.Remove(entry.Parent, entry.Field), entry.Spec.Name + " / " + entry.LinkKey + ": удалена строка " + entry.Ordinal + "\n" + entry.Document.Text[entry.Field.Start..entry.Field.Value.End]);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private static void EnsureCurrent(LuaSourceDocument doc, LuaBindingSpec spec, LuaLiteral table)
    {
        var current = doc.Tables.GetValueOrDefault(spec.Name).Value;
        if (spec.Child is not null) current = current?.Field(spec.Child)?.Value;
        if (!ReferenceEquals(current, table)) throw new InvalidOperationException("Запись обновилась. Выберите её заново.");
    }
    public static void Validate(LuaBindingSpec spec, LuaLiteral value, string key)
    {
        if (spec.IsArray)
        {
            if (value.Kind != LuaLiteralKind.Table || value.Field("#1")?.Value is not { Kind: LuaLiteralKind.String } name || name.Text != key)
                throw new FormatException("Первое поле должно совпадать с техническим именем выбранной записи БД.");
            foreach (var f in value.Fields.Where(f => f.Key != "#1"))
                if (f.Value.Kind is not (LuaLiteralKind.Number or LuaLiteralKind.Nil)) throw new FormatException("Числовое поле " + f.Key + " должно содержать число или nil.");
            if (spec.Name == "ForbiddenItems" && (!int.TryParse(value.Field("#2")?.Value.Text, out int shop) || shop < 0)) throw new FormatException("ID магазина должен быть целым неотрицательным числом.");
        }
        else if (spec.Name == "DeathFearExcludedPrototypes")
        { if (value.Kind != LuaLiteralKind.Boolean) throw new FormatException("Исключение должно быть true или false."); }
        else if (value.Kind != LuaLiteralKind.Table) throw new FormatException("Ожидается таблица параметров.");
        if (spec.Name is "MeleeActiveSpells" or "RangedActiveSpells")
            foreach (var f in value.Fields)
                if (f.Value.Kind != (f.Key == "displayName" ? LuaLiteralKind.String : LuaLiteralKind.Number))
                    throw new FormatException(f.Key + ": " + (f.Key == "displayName" ? "ожидается строка." : "ожидается число; флаги записываются как 0/1."));
        if (spec.Name == "NpcCombatConsumables")
        {
            foreach (var kind in value.Fields.Where(f => ConsumableKinds.Contains(f.Key)))
            {
                var node = kind.Value;
                var formula = node.Field("formula")?.Value ?? node.Field("spell")?.Value;
                if (formula?.Kind != LuaLiteralKind.String || string.IsNullOrWhiteSpace(formula.Text)) throw new FormatException(kind.Key + ": нужна непустая формула заклинания.");
                if (!int.TryParse(node.Field("charges")?.Value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int charges) || charges < 1 || charges > 7)
                    throw new FormatException(kind.Key + ": число зарядов должно быть от 1 до 7.");
                if (node.Field("registrationOnly") is { } flag && flag.Value.Kind != LuaLiteralKind.Boolean) throw new FormatException("registrationOnly должен быть true/false.");
            }
        }
    }
    public void Undo(LuaSourceDocument doc) { doc.Undo(); Changed?.Invoke(this, EventArgs.Empty); }
    public void Redo(LuaSourceDocument doc) { doc.Redo(); Changed?.Invoke(this, EventArgs.Empty); }
    public void Discard() { foreach (var d in Documents) d.Discard(); Changed?.Invoke(this, EventArgs.Empty); }
    public void ValidateForSave(LuaSourceDocument doc)
    {
        if (!doc.HasChanges || !doc.Tables.TryGetValue("UnitStat", out var assignment) || assignment.Value is not { } table) return;
        var problems = new List<string>();
        foreach (var row in table.Fields)
        {
            var name = row.Value.Field("#1")?.Value.Text ?? "?";
            var missing = _requiredUnitStats.Where(p => row.Value.Field(p.Key)?.Value.Kind != LuaLiteralKind.Number)
                .Select(p => "[" + p.Key[1..] + "] " + p.Value).ToArray();
            if (missing.Length > 0) problems.Add(name + ": " + string.Join(", ", missing));
        }
        if (problems.Count > 0)
            throw new InvalidDataException("Сохранение остановлено: неполные строки UnitStat в " + System.IO.Path.GetFileName(doc.Path) +
                ". Заполните числовые значения: " + string.Join("; ", problems) + ". Ноль нужно указать явно, если он предусмотрен для прототипа.");
    }
    public string Save(LuaSourceDocument doc) { ValidateForSave(doc); string backup = doc.Save(); Changed?.Invoke(this, EventArgs.Empty); return backup; }
}
