using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record ItemModelOptions(string Label, IReadOnlyList<ModelRecord> Choices);

// Inventory models are distinct assets, not dressed unit meshes or map conversions.
public static class ItemModelPreview
{
    public static bool Supports(string table) => table is "items.materials" or "items.weapons" or "items.armors" or
        "items.quick_items" or "items.quest_items" or "items.loot_items";

    public static ItemModelOptions Options(ModelRecord item, IReadOnlyList<ModelRecord> records)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return new("", []);
        if (IsSpellContainer(item)) return new("Заклинание", records.Where(r => r.Table == "spells.prototypes").ToArray());
        if (item.Table is "items.weapons" or "items.armors" or "items.quick_items" &&
            !item.Text(3).Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            var graphics = QuickItemMaterialGraphics.ForItem(item);
            return new("Материал", records.Where(r => r.Table == "items.materials" &&
                (r.Text(1).Equals(item.Text(3), StringComparison.OrdinalIgnoreCase) || graphics.ContainsKey(r.Text(2)))).ToArray());
        }
        if (item.Table == "items.loot_items" && item.Integer(21) is 1 or 2 && item.Integer(23, -1) < 0)
        {
            string table = TargetTable(item);
            return new(item.Integer(21) == 1 ? "Материал" : "Содержимое чертежа / руны", records.Where(r => r.Table == table && !string.IsNullOrWhiteSpace(r.Name)).ToArray());
        }
        return new("", []);
    }

    private static string TargetTable(ModelRecord item) => item.Integer(21) == 1 ? "items.materials" : item.Integer(22) switch
    {
        0 => "items.weapons", 1 => "items.armors", 2 => "items.quick_items", 3 => "spells.prototypes", 4 => "spells.modifiers",
        _ => ""
    };

    private static bool IsSpellContainer(ModelRecord item) => item.Table == "items.quick_items" && item.Integer(21) == 8 && item.Integer(5) == 1;

    private static string WeaponCode(int type) => type switch
    {
        0 => "sw", 1 => "ax", 2 => "dg", 3 => "sp", 4 => "hm", 5 => "bw", 6 => "cb", 9 => "sd",
        10 => "tp", 11 => "sf", 12 => "ts", 13 => "ta", 14 => "th", 15 => "tf",
        _ => throw new NotSupportedException("Неизвестный класс оружия: " + type)
    };
    private static string ArmorCode(int type) => type switch
    { 0 => "hl", 1 => "pl", 2 => "lg", 3 => "sh", 4 => "pt", 5 => "bt", 6 => "gl", _ => throw new NotSupportedException("Неизвестный слот брони: " + type) };

    public static async Task<ModelRecord?> DefaultChoiceAsync(string database, ModelRecord item, IReadOnlyList<ModelRecord> records, CancellationToken token = default)
    {
        var options = Options(item, records);
        if (options.Choices.Count == 0 || options.Label != "Материал" || item.Table == "items.loot_items") return options.Choices.FirstOrDefault();
        var sources = await TextureSources.FromDatabaseFolderAsync(database, token).ConfigureAwait(false);
        var catalog = await TextureResourceCatalog.OpenAsync(sources, token).ConfigureAwait(false);
        return DefaultChoice(item, records, catalog, token);
    }

    internal static ModelRecord? DefaultChoice(ModelRecord item, IReadOnlyList<ModelRecord> records,
        TextureResourceCatalog catalog, CancellationToken token)
    {
        var options = Options(item, records);
        if (options.Choices.Count == 0 || options.Label != "Материал" || item.Table == "items.loot_items") return options.Choices.FirstOrDefault();
        foreach (var material in options.Choices)
        {
            token.ThrowIfCancellationRequested();
            string stem = item.Table == "items.quick_items" ? $"qitem{QuickItemMaterialGraphics.Index(item, material):D4}.{material.Text(2)}" :
                $"unhuma{(item.Table == "items.weapons" ? WeaponCode(item.Integer(2)) : ArmorCode(item.Integer(2)))}_{item.Integer(5):D2}.{material.Text(2)}.{item.Integer(6)}";
            if (new[] { ".x4.mmp", ".x2.mmp", ".mmp" }.Any(s => catalog.Check(stem + s).Matches.Count > 0)) return material;
        }
        return options.Choices.First(); // Explicit missing-texture diagnostic in LoadAsync.
    }

    public static async Task<UnitModelScene> LoadAsync(string databasePath, ModelRecord selected, IReadOnlyList<ModelRecord> records,
        CancellationToken token = default, ModelRecord? choice = null)
        => await LoadInternalAsync(databasePath, selected, records, token, choice, null).ConfigureAwait(false);

    internal static Task<UnitModelScene> ValidateResourcesAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, ResourceValidationSession session, CancellationToken token, ModelRecord? choice) =>
        LoadInternalAsync(databasePath, selected, records, token, choice, session);

    private static async Task<UnitModelScene> LoadInternalAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, CancellationToken token, ModelRecord? choice, ResourceValidationSession? validation)
    {
        if (!Supports(selected.Table)) throw new NotSupportedException("Выберите предмет.");
        if (string.IsNullOrWhiteSpace(selected.Name)) throw new InvalidDataException("Пустая служебная запись: предмет и его модель не заданы.");
        var options = Options(selected, records);
        if (options.Label.Length > 0 && (choice is null || !options.Choices.Contains(choice)))
            throw new InvalidDataException(options.Choices.Count == 0 ? "В БД нет совместимых вариантов: " + options.Label : "Выберите: " + options.Label);
        var sources = validation?.Sources ?? await TextureSources.FromDatabaseFolderAsync(databasePath, token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var used = new List<string>(); var notes = new List<string>();
            if (choice is not null && options.Label == "Материал" && selected.Table != "items.loot_items") notes.Add("Материал предпросмотра: " + choice.Name + ". Значение БД не изменяется.");
            var item = selected;
            bool instruction = item.Table == "items.loot_items" && item.Integer(21) == 2;
            bool spellContainer = IsSpellContainer(item);
            if (spellContainer)
            {
                item = choice!;
                notes.Add("Заклинание: " + item.Name + ". Выбран пример содержимого контейнера; БД не изменяется.");
            }
            if (item.Table == "items.loot_items" && item.Integer(21) is 1 or 2)
            {
                var targets = records.Where(r => r.Table == TargetTable(item)).ToArray();
                int target = item.Integer(23, -1);
                item = target < 0 ? choice! : target < targets.Length ? targets[target] : throw new InvalidDataException("Индекс содержимого вне таблицы: " + target);
                notes.Add("Содержимое: " + item.Name + (target < 0 ? ". Это выбранный пример: общая запись БД не задаёт конкретное содержимое." : ""));
            }
            int index = QuickItemMaterialGraphics.Index(item, instruction ? null : choice);
            string root, part, texture; int logical = 64;
            switch (item.Table)
            {
                case "items.weapons":
                    string weapon = WeaponCode(item.Integer(2));
                    root = $"initwe{weapon}{index}"; part = "weapon"; logical = 128;
                    texture = instruction ? $"{weapon}_{index:D2}.{item.Integer(6)}" : $"unhuma{weapon}_{index:D2}.{choice!.Text(2)}.{item.Integer(6)}";
                    break;
                case "items.armors":
                    string armor = ArmorCode(item.Integer(2));
                    root = $"initar{armor}{index}"; part = "armor"; logical = item.Integer(2) == 0 ? 128 : 256;
                    texture = instruction ? $"{armor}_{index:D2}.{item.Integer(6)}" : $"unhuma{armor}_{index:D2}.{choice!.Text(2)}.{item.Integer(6)}";
                    break;
                case "items.quick_items":
                    root = $"initqi{index}"; part = "item"; texture = $"qitem{index:D4}";
                    if (!instruction && !item.Text(3).Equals("None", StringComparison.OrdinalIgnoreCase)) texture += "." + choice!.Text(2);
                    break;
                case "items.quest_items": root = $"initqu{index}"; part = "item"; texture = $"quitem{index:D4}"; break;
                case "items.materials":
                    index = Array.FindIndex(records.Where(r => r.Table == item.Table).ToArray(), r => r.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw new InvalidDataException("Материал отсутствует в БД.");
                    root = $"initlimt{index}"; part = "item"; texture = $"material{index:D4}"; break;
                case "items.loot_items" when item.Integer(21) == 0:
                    root = $"initlitr{index}"; part = "item"; texture = $"litem{index:D4}"; break;
                case "spells.prototypes" when instruction:
                    root = "initqi1"; part = "item"; texture = $"prototype{item.Integer(18):D4}"; break;
                case "spells.prototypes" when spellContainer:
                    root = "initqi1"; part = "item"; texture = $"spell{item.Integer(18):D4}"; break;
                case "spells.modifiers" when instruction:
                    index = Array.FindIndex(records.Where(r => r.Table == item.Table).ToArray(), r => r.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    root = "initqi1"; part = "item"; texture = $"modifier{index:D4}"; break;
                default: throw new NotSupportedException("Для этой записи не определён вид предмета: " + item.Table);
            }
            if (validation is not null) validation.Context = "Инвентарная модель: " + root;
            var reader = new FigureResources(databasePath, sources, used, token, validation);
            Dictionary<string, string> parents;
            Func<string, byte[]> figure, bone;
            if (reader.Contains(root + ".mod"))
            {
                var mod = validation is null ? ResArchiveDocument.Parse(root + ".mod", reader.Read(root + ".mod"), token) :
                    validation.Parse(root + ".mod", () => reader.Read(root + ".mod"), token);
                var bon = validation is null ? ResArchiveDocument.Parse(root + ".bon", reader.Read(root + ".bon"), token) :
                    validation.Parse(root + ".bon", () => reader.Read(root + ".bon"), token);
                parents = UnitModelPreview.ReadHierarchy(mod.GetResourceBytes(root).ToArray());
                figure = p => mod.GetResourceBytes(p).ToArray(); bone = p => bon.GetResourceBytes(p).ToArray();
            }
            else if (reader.Contains(root + part + ".fig"))
            {
                // Inventory requests the complete component directly, not its LNK.
                // Some mod LNKs retain unrelated authoring names; BON is optional here.
                parents = new(StringComparer.OrdinalIgnoreCase) { [part] = "" };
                figure = p => reader.Read(root + p + ".fig"); bone = _ => new byte[12];
            }
            else
            {
                parents = reader.Contains(root + ".lnk") ? UnitModelPreview.ReadHierarchy(reader.Read(root + ".lnk")) : new(StringComparer.OrdinalIgnoreCase) { [part] = "" };
                figure = p => reader.Read(root + p + ".fig"); bone = p => reader.Read(root + p + ".bon");
            }
            var positions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vector3 Position(string name)
            {
                token.ThrowIfCancellationRequested();
                if (positions.TryGetValue(name, out var value)) return value;
                if (visiting.Count >= 128 || !visiting.Add(name)) throw new InvalidDataException("Цикл или слишком глубокая иерархия предмета.");
                if (!parents.TryGetValue(name, out var parent)) throw new InvalidDataException("Нет родителя детали: " + name);
                value = FigurePreviewDecoder.Bone(bone(name), new(.5f)) + (parent.Length == 0 ? Vector3.Zero : Position(parent));
                visiting.Remove(name); positions.Add(name, value); return value;
            }
            var meshes = new List<FigureMesh>();
            foreach (string name in parents.Keys)
            {
                token.ThrowIfCancellationRequested();
                FigureMesh mesh;
                try { mesh = validation is null ? FigurePreviewDecoder.Decode(name, figure(name), new(.5f)) :
                    validation.Decode(name, () => figure(name), new(.5f), validation.FigurePath(reader.Contains(root + ".mod") ? root + ".mod" : root + name + ".fig") + " → " + name); }
                catch (Exception ex) when (validation is not null && ResourceValidationSession.IsResourceFailure(ex)) { validation.Failure(ex); continue; }
                if (validation is not null) { meshes.Add(mesh); continue; }
                Vector3 offset = Position(name);
                for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i] += offset;
                if (mesh.Indices.Length > 0) meshes.Add(mesh);
            }
            if (meshes.Count == 0 && validation is null) throw new InvalidDataException("В инвентарной модели нет геометрии.");
            var catalog = validation?.Catalog ?? await TextureResourceCatalog.OpenAsync(sources, token).ConfigureAwait(false);
            string? resolved = new[] { ".x4.mmp", ".x2.mmp", ".mmp" }.Select(s => texture + s).FirstOrDefault(n => catalog.Check(n).Matches.Count > 0);
            if (validation is not null)
            {
                await validation.TextureAsync(resolved ?? texture + ".mmp", token).ConfigureAwait(false);
                return new UnitModelScene(root, [], new Dictionary<int, MmpImage>(), used, notes);
            }
            var textures = new Dictionary<int, MmpImage>();
            if (resolved is null) notes.Add("Не найдена текстура предмета: " + texture + ".mmp");
            else
            {
                var image = await catalog.ReadPreviewAsync(resolved, token).ConfigureAwait(false);
                int width = logical, height = logical;
                if (catalog.Check(texture + ".mmp").Matches.Count > 0)
                {
                    var original = await catalog.ReadPreviewAsync(texture + ".mmp", token).ConfigureAwait(false);
                    if (original.Width <= 256 && original.Height <= 256) { width = original.Width; height = original.Height; }
                    used.Add("Раскладка: " + texture + ".mmp — " + catalog.Check(texture + ".mmp").Source);
                }
                else notes.Add($"Базовый размер инвентарного слоя: {width}×{height} по типу предмета; обычная версия MMP отсутствует.");
                var atlas = InventoryAtlas(image, width, height, token);
                foreach (int slot in meshes.Select(m => m.TextureSlot).Distinct()) textures[slot] = atlas;
                used.Add(resolved + " — " + catalog.Check(resolved).Source);
            }
            notes.Add("Инвентарная модель предмета; не внешний вид надетой вещи или объекта карты. Файлы модели и UV не изменяются.");
            return new UnitModelScene(root, meshes, textures, used.Distinct().ToArray(), notes);
        }, token).ConfigureAwait(false);
    }

    internal static MmpImage InventoryAtlas(MmpImage image, int logicalWidth, int logicalHeight, CancellationToken token)
    {
        // Original inventory places the layer at (0, 256-h). Keep original UV and
        // the HD pixels: the suffix does not specify a reliable scale in JMV.
        int width = checked(image.Width * 256 / logicalWidth), height = checked(image.Height * 256 / logicalHeight);
        if (width > 4096 || height > 4096 || width < image.Width || height < image.Height) throw new NotSupportedException("Слишком большой инвентарный атлас.");
        var bytes = new byte[checked(width * height * 4)];
        for (int y = 0; y < image.Height; y++)
        { token.ThrowIfCancellationRequested(); Buffer.BlockCopy(image.Bgra, y * image.Width * 4, bytes, ((height - image.Height + y) * width) * 4, image.Width * 4); }
        return new(width, height, image.Format + " · инвентарный атлас", bytes, image.PremultipliedAlpha);
    }

    private sealed class FigureResources
    {
        private readonly Dictionary<string, (string Path, ResEntry Entry, long Length, long Written)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _used;
        private readonly CancellationToken _token;
        private readonly ResourceValidationSession? _validation;
        public FigureResources(string database, TextureSources sources, List<string> used, CancellationToken token, ResourceValidationSession? validation = null)
        {
            _used = used; _token = token; _validation = validation;
            if (validation is not null) return;
            foreach (string path in UnitModelPreview.FigureArchives(database, sources).Where(File.Exists))
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path); long length = info.Length; long written = ResourceFileStamp.Read(path);
                var entries = ResFileManagerArchive.ReadCatalog(path, token);
                info.Refresh();
                if (length != info.Length || written != ResourceFileStamp.Read(path)) throw new IOException("Архив изменился во время чтения.");
                foreach (var entry in entries) _entries.TryAdd(entry.Name, (path, entry, length, written));
            }
        }
        public bool Contains(string name) => _validation?.Contains(name) ?? _entries.ContainsKey(name);
        public byte[] Read(string name)
        {
            _token.ThrowIfCancellationRequested();
            if (_validation is not null) return _validation.Read(name, _token);
            if (!_entries.TryGetValue(name, out var resource)) throw new FileNotFoundException("Не найдена инвентарная модель: " + name);
            using var stream = new FileStream(resource.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != resource.Length || ResourceFileStamp.Read(resource.Path) != resource.Written) throw new IOException("Архив модели изменился. Обновите предпросмотр.");
            stream.Position = resource.Entry.Offset; var bytes = new byte[resource.Entry.Size]; stream.ReadExactly(bytes);
            _used.Add(name + " — " + resource.Path); return bytes;
        }
    }
}
