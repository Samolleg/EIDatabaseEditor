using System.Globalization;
using System.Numerics;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record ModelRecord(string Table, string Name, IReadOnlyDictionary<int, string> Fields,
    IReadOnlyDictionary<int, string[]> Lists)
{
    public string Text(int id) => Fields.GetValueOrDefault(id, "");
    public int Integer(int id, int fallback = 0) => int.TryParse(Text(id), out int n) ? n : fallback;
    public float Number(int id, float fallback = 0.5f) => float.TryParse(Text(id), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? n : fallback;
    public static ModelRecord Capture(string table, DatabaseRecord record) => new(table, record.IdentityValue,
        record.Fields.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => Convert.ToString(g.First().Value, CultureInfo.InvariantCulture) ?? ""),
        record.Fields.Where(f => f.TryGetStringListValues(out _)).GroupBy(f => f.Id).ToDictionary(g => g.Key,
            g => { g.First().TryGetStringListValues(out var values); return values.ToArray(); }));
}

public sealed record UnitModelScene(string Model, IReadOnlyList<FigureMesh> Meshes,
    IReadOnlyDictionary<int, MmpImage> Textures, IReadOnlyList<string> Sources, IReadOnlyList<string> Notes)
{
    public FigureAnimationSet? Animations { get; init; }
}

public static class UnitModelPreview
{
    public static async Task<UnitModelScene> LoadAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, CancellationToken token = default, MobAppearance? appearance = null)
    {
        TextureSources sources = await TextureSources.FromDatabaseFolderAsync(databasePath, token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var notes = new List<string>();
            var used = new List<string>();
            ModelRecord Find(string table, string name) => records.SingleOrDefault(r => r.Table == table && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Не найдена запись {table}: {name}");
            var unit = selected.Table == "units.npcs" ? Find("units.prototypes", selected.Name) : selected;
            if (unit.Table != "units.prototypes") throw new NotSupportedException("Выберите прототип NPC или монстра.");
            var race = Find("units.races", unit.Text(1));
            string mask = appearance?.Model ?? race.Text(30);
            if (string.IsNullOrWhiteSpace(mask)) throw new InvalidDataException("В расе не задана маска модели.");
            var archives = FigureArchives(databasePath, sources);
            var indexes = new List<(string Path, IReadOnlyList<ResEntry> Entries, long Length, long Written)>();
            foreach (string path in archives)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    long length = info.Length; long written = ResourceFileStamp.Read(path);
                    var entries = ResFileManagerArchive.ReadCatalog(path, token);
                    info.Refresh();
                    if (info.Length != length || ResourceFileStamp.Read(path) != written) throw new IOException("Архив модели изменился во время чтения.");
                    indexes.Add((path, entries, length, written));
                }
            }
            byte[] Resource(string name)
            {
                foreach (var archive in indexes)
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;
                    if (entry.Size > 128 * 1024 * 1024) throw new InvalidDataException("Модель превышает лимит 128 МиБ.");
                    using var stream = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length != archive.Length || ResourceFileStamp.Read(archive.Path) != archive.Written)
                        throw new IOException("Архив модели изменился. Обновите предпросмотр.");
                    stream.Position = entry.Offset;
                    var bytes = new byte[entry.Size]; stream.ReadExactly(bytes);
                    used.Add(name + " — " + archive.Path);
                    return bytes;
                }
                throw new FileNotFoundException("Не найден ресурс модели: " + name);
            }
            var mod = ResArchiveDocument.Parse(mask + ".mod", Resource(mask + ".mod"), token);
            var bon = ResArchiveDocument.Parse(mask + ".bon", Resource(mask + ".bon"), token);
            var parents = ReadHierarchy(mod.GetResourceBytes(mask).ToArray());
            Vector3 morph = appearance?.Morph ?? new(unit.Number(5), unit.Number(6), unit.Number(7));
            var positions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vector3 Position(string part)
            {
                if (positions.TryGetValue(part, out var cached)) return cached;
                if (visiting.Count > 128) throw new InvalidDataException("Слишком глубокая иерархия модели.");
                if (!visiting.Add(part)) throw new InvalidDataException("Цикл в иерархии модели.");
                if (!parents.TryGetValue(part, out string? parent)) throw new InvalidDataException("Нет родителя детали " + part);
                Vector3 offset = FigurePreviewDecoder.Bone(bon.GetResourceBytes(part).ToArray(), morph);
                var result = offset + (parent.Length == 0 ? Vector3.Zero : Position(parent));
                visiting.Remove(part); positions[part] = result; return result;
            }
            string[] bodyParts = ["hp", "bd", "hd", "rh1", "rh2", "rh3", "lh1", "lh2", "lh3", "rl1", "rl2", "rl3", "ll1", "ll2", "ll3"];
            // Equipment capability belongs to the geometry, not a whitelist of race names.
            // Natural parts such as a turtle's hd.armor.nose are not clothing slots.
            bool armorSlots = bodyParts.All(parents.ContainsKey) && parents.Keys.Any(p =>
                bodyParts.Any(body => p.StartsWith(body + ".armor", StringComparison.OrdinalIgnoreCase) &&
                    p.Length == body.Length + 8 && char.IsAsciiDigit(p[^2]) && char.IsAsciiDigit(p[^1])));
            // Original humanoids have selectable weapons but no armorNN meshes.
            // They still need body-only visibility and race-prefixed skin/layers.
            bool weaponSlots = bodyParts.All(parents.ContainsKey) && parents.Keys.Any(p =>
                System.Text.RegularExpressions.Regex.IsMatch(p, @"^(rh3\.(sword|axe|dagger|pike|club)\d{2}|lh2\.shield\d{2}|lh3\.bwpartb\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            bool dressed = armorSlots || weaponSlots;
            var catalog = await TextureResourceCatalog.OpenAsync(sources, token).ConfigureAwait(false);
            // A humanoid hierarchy alone does not imply a composited skin (e.g. goblins).
            // Keep their explicit MOB skin unless the model's redress skin actually exists.
            bool hasCompositeSkin = new[] { ".x4.mmp", ".x2.mmp", ".mmp" }
                .Any(suffix => catalog.Check($"{mask}skin_{unit.Integer(3):D2}" + suffix).Matches.Count > 0);
            bool prototypeEquipment = appearance is null || (dressed && hasCompositeSkin && appearance.ImportEquipment == false);
            var visible = new HashSet<string>(dressed ? bodyParts : parents.Keys, StringComparer.OrdinalIgnoreCase);
            void AddPart(string root)
            {
                if (!parents.ContainsKey(root)) { notes.Add("Нет детали экипировки: " + root); return; }
                // Descendants also contain invisible attachment/helper geometry.
                // Visibility is an explicit equipment list, independent of transforms.
                visible.Add(root);
            }
            if (dressed && unit.Integer(4, -1) >= 0) visible.Add($"hr.{unit.Integer(4):D2}");
            var primary = new List<string>(); var secondary = new List<string>();
            if (dressed) primary.Add($"{mask}skin_{unit.Integer(3):D2}");
            else
            {
                string[] skins = race.Lists.GetValueOrDefault(31, []);
                int index = unit.Integer(3);
                if (index < 0 || index >= skins.Length) notes.Add("Индекс основной текстуры вне списка расы.");
                else primary.Add(skins[index]);
                secondary.AddRange(race.Lists.GetValueOrDefault(32, []).Take(1));
            }
            string[] armorCodes = ["hl", "pl", "lg", "sh", "pt", "bt", "gl"];
            var armorItems = new List<(ModelRecord Shape, string Material)>();
            if (appearance is null && !dressed && (unit.Lists.GetValueOrDefault(32, []).Length > 0 || unit.Text(33).Length > 0 || unit.Text(42).Length > 0))
                notes.Add("В геометрии нет слотов составной экипировки. Показана исходная модель; внешний вид экземпляра можно выбрать из MOB.");
            foreach (string item in prototypeEquipment && dressed ? unit.Lists.GetValueOrDefault(32, []) : [])
            {
                var analysis = StringListEditorCodec.AnalyzeElement(item, "Броня");
                if (!analysis.IsValid) { notes.Add("Не распознана экипировка: " + analysis.Error); continue; }
                string baseItem = analysis.PrimaryValue;
                int dot = baseItem.IndexOf('.');
                if (dot < 0) { notes.Add("Не распознана экипировка: " + item); continue; }
                try { armorItems.Add((Find("items.armors", baseItem[..dot]), Find("items.materials", baseItem[(dot + 1)..]).Text(2))); }
                catch (InvalidDataException ex) { notes.Add(ex.Message); }
            }
            if (armorItems.GroupBy(i => i.Shape.Integer(2)).Any(g => g.Count() > 1))
                notes.Add("В БД несколько вариантов одного слота: показан первый вариант.");
            foreach (var item in armorItems.GroupBy(i => i.Shape.Integer(2)).Select(g => g.First()).OrderBy(i => i.Shape.Integer(2) < 3 ? 1 : 0).ThenBy(i => i.Shape.Integer(2) < 3 ? i.Shape.Integer(24) : i.Shape.Integer(2)))
            {
                int type = item.Shape.Integer(2), index = item.Shape.Integer(5);
                if (type < 0 || type >= armorCodes.Length) { notes.Add("Неизвестный тип брони: " + item.Shape.Name); continue; }
                (type == 0 ? secondary : primary).Add($"{mask}{armorCodes[type]}_{index:D2}.{item.Material}.{item.Shape.Integer(6)}");
                string[] body = type switch { 0 => ["hd", $"hr.{unit.Integer(4):D2}"], 1 => ["bd", "rh1", "rh2", "rh3", "lh1", "lh2", "lh3"], 2 => ["hp", "rl1", "rl2", "rl3", "ll1", "ll2", "ll3"], _ => [] };
                foreach (string part in body)
                {
                    string replacement = $"{part}.armor{index:D2}";
                    if (parents.ContainsKey(replacement))
                    {
                        // A helmet is an additional mesh around the head, not a replacement face.
                        if (part != "hd") visible.Remove(part);
                        AddPart(replacement);
                    }
                    else if (part.StartsWith("hr.", StringComparison.OrdinalIgnoreCase))
                        visible.Remove(part); // This helmet has no compatible hair mesh.
                    // An absent armor segment uses the naked body segment (e.g. hands).
                }
            }
            if (unit.Text(33).Length > 0 && unit.Text(42).Length > 0) notes.Add("Показано первое оружие прототипа; выбор активного оружия в игре зависит от состояния.");
            foreach (string weapon in new[] { unit.Text(33), unit.Text(42) }.Where(w => prototypeEquipment && dressed && w.Length > 0).Take(1))
            {
                var analysis = StringListEditorCodec.AnalyzeElement(weapon, "Оружие");
                if (!analysis.IsValid) { notes.Add("Не распознано оружие: " + analysis.Error); continue; }
                string baseItem = analysis.PrimaryValue;
                int dot = baseItem.IndexOf('.');
                if (dot < 0) { notes.Add("Не распознано оружие: " + weapon); continue; }
                try
                {
                    var shape = Find("items.weapons", baseItem[..dot]);
                    var material = Find("items.materials", baseItem[(dot + 1)..]);
                    int type = shape.Integer(2), index = shape.Integer(5);
                    var kind = type switch
                    {
                        0 => ("sword", "sw"), 1 => ("axe", "ax"), 2 => ("dagger", "dg"), 3 => ("pike", "sp"),
                        4 => ("club", "hm"), 5 => ("bow", "bw"), 6 => ("crossbow", "cb"), 9 => ("shield", "sd"),
                        10 => ("dpike", "tp"), 11 => ("staff", "sf"), 12 => ("dsword", "ts"),
                        13 => ("daxe", "ta"), 14 => ("dclub", "th"), 15 => ("dstaff", "tf"), _ => ("", "")
                    };
                    if (kind.Item1.Length == 0) { notes.Add("Сборка этого типа оружия пока не поддержана: " + shape.Name); continue; }
                    string[] parts = type switch
                    {
                        9 => [$"lh2.shield{index:D2}"],
                        5 => [$"lh3.bwpartb{index:D2}", $"bwparta{index:D2}", $"bwtetivaa{index:D2}", $"bwtetivab{index:D2}", "quiver", "arrows", "rh3.arrow00"],
                        6 => [$"rh3.crbow{index:D2}main", $"crbow{index:D2}part01", $"crbow{index:D2}part02", $"crbow{index:D2}tetiva01", $"crbow{index:D2}tetiva02", "quiver", "arrows"],
                        _ => [$"rh3.{kind.Item1}{index:D2}"]
                    };
                    foreach (string part in parts) AddPart(part);
                    secondary.Add($"{mask}{kind.Item2}_{index:D2}.{material.Text(2)}.{shape.Integer(6)}");
                }
                catch (InvalidDataException ex) { notes.Add(ex.Message); }
            }
            if (appearance is not null)
            {
                if (!prototypeEquipment)
                {
                visible = new HashSet<string>(appearance.Parts.Length == 0 ? parents.Keys : appearance.Parts, StringComparer.OrdinalIgnoreCase);
                primary.Clear(); secondary.Clear();
                if (appearance.Primary.Length > 0) primary.Add(appearance.Primary);
                if (appearance.Secondary.Length > 0) secondary.Add(appearance.Secondary);
                }
                used.Add($"MOB: {appearance.Path} · объект {appearance.Id} · {appearance.Prototype}");
                notes.Add(prototypeEquipment
                    ? "Модель и комплекция из MOB; кожа и экипировка прототипа из БД — импорт экипировки MOB отключён."
                    : "Внешность из MOB: исходные части, текстуры и комплекция. Импорт инвентаря и изменения экипировки в игре не воспроизводятся.");
            }
            var meshes = new List<FigureMesh>();
            foreach (string part in visible)
            {
                token.ThrowIfCancellationRequested();
                if (!mod.TryGetEntry(part, out var entry) || entry is null) continue;
                var mesh = FigurePreviewDecoder.Decode(part, mod.GetResourceBytes(entry).ToArray(), morph);
                if (mesh.Indices.Length == 0) continue;
                // Human secondary equipment occupies half-size UV space in the shared model.
                if (prototypeEquipment && armorSlots && mesh.TextureSlot == 2)
                    for (int i = 0; i < mesh.UV.Length; i++) mesh.UV[i] *= 2;
                Vector3 offset = Position(part);
                for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i] += offset;
                meshes.Add(mesh);
            }
            if (meshes.Count == 0) throw new InvalidDataException("В модели нет доступной геометрии.");
            var textures = new Dictionary<int, MmpImage>();
            foreach (var group in new[] { (1, primary), (2, secondary) })
            {
                var layers = new List<MmpImage>();
                foreach (string raw in group.Item2)
                {
                    string stem = raw.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
                    // JMV shares human equipment layers; skin remains specific to the race.
                    bool sharedEquipment = prototypeEquipment && dressed &&
                        !mask.Equals("unhuma", StringComparison.OrdinalIgnoreCase) &&
                        armorCodes.Concat(new[] { "sw", "ax", "dg", "sp", "hm", "bw", "cb", "sd", "tp", "sf", "ts", "ta", "th", "tf" })
                            .Any(code => stem.StartsWith(mask + code + "_", StringComparison.OrdinalIgnoreCase));
                    string[] stems = sharedEquipment ? [stem, "unhuma" + stem[mask.Length..]] : [stem];
                    // Rank resolution before compatible naming aliases: a base female layer
                    // must not hide an available HD common layer. Ties prefer the exact race.
                    string? resolved = new[] { ".x4.mmp", ".x2.mmp", ".mmp" }
                        .SelectMany(suffix => stems.Select(value => value + suffix))
                        .FirstOrDefault(n => catalog.Check(n).Matches.Count > 0);
                    if (resolved is not null && sharedEquipment && resolved.StartsWith("unhuma", StringComparison.OrdinalIgnoreCase))
                        notes.Add("Общий слой экипировки: " + stem + " → " + resolved);
                    string name = resolved ?? stem + ".mmp";
                    try
                    {
                        layers.Add(await catalog.ReadPreviewAsync(name, token).ConfigureAwait(false));
                        used.Add(name + " — " + catalog.Check(name).Source);
                    }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { notes.Add(name + ": " + ex.Message); }
                }
                if (layers.Count > 0) textures[group.Item1] = Composite(layers, token);
            }
            foreach (int slot in meshes.Select(m => m.TextureSlot).Distinct().Where(slot => !textures.ContainsKey(slot)))
                notes.Add($"Нет текстуры для слота модели {slot}. " + (slot is 1 or 2
                    ? "Проверьте список текстур расы и внешний вид экземпляра из MOB."
                    : "Специальный материал или эффект пока не поддержан предпросмотром."));
            FigureAnimationSet? animations = null;
            try
            {
                // Only required visible nodes and their ancestors; attachment meshes
                // without a track inherit the animated parent using the original BON.
                var bones = positions.Keys.ToDictionary(p => p, p => FigurePreviewDecoder.Bone(bon.GetResourceBytes(p).ToArray(), morph), StringComparer.OrdinalIgnoreCase);
                animations = new FigureAnimationSet(mask, Resource(mask + ".anm"),
                    positions.Keys.ToDictionary(p => p, p => parents[p], StringComparer.OrdinalIgnoreCase), bones, positions, token);
                foreach (string skipped in animations.SkippedResources) notes.Add("ANM: пропущен вложенный архив или пустая заглушка: " + skipped);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DatabaseFormatException or NotSupportedException or ArgumentException)
            { notes.Add("Анимации недоступны: " + ex.Message); }
            notes.Add("Клипы показываются отдельно от игрового состояния; звуки, эффекты, AI и переходы между действиями не воспроизводятся.");
            notes.Add("Скорость предпросмотра задаётся в кадрах/с. Игровые настройки темпа и коррекция перемещения по росту не применяются.");
            if (dressed) notes.Add("Составная экипировка: визуальное совпадение с игрой ещё требует проверки.");
            if (selected.Table == "units.npcs") notes.Add("Внешность — из одноимённого прототипа монстра. Инвентарь NPC не определяет, что надето в конкретном сохранении.");
            return new UnitModelScene(mask, meshes, textures, used.Distinct().ToArray(), notes.Distinct().ToArray()) { Animations = animations };
        }, token).ConfigureAwait(false);
    }

    internal static IEnumerable<string> FigureArchives(string database, TextureSources sources)
    {
        var paths = new List<string>();
        if (sources.ConfigurationPath is { } config)
        {
            string section = "";
            foreach (string raw in File.ReadAllLines(config))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
                int eq = line.IndexOf('=');
                if (section.Equals("RES", StringComparison.OrdinalIgnoreCase) && eq > 0 && line[..eq].Trim().Equals("figures.res", StringComparison.OrdinalIgnoreCase))
                    paths.Add(Path.GetFullPath(line[(eq + 1)..].Trim().Trim('"'), Path.GetDirectoryName(config)!));
            }
        }
        else paths.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(database)!, "*.res"));
        if (sources.GameFolder is { } game) paths.Add(Path.Combine(game, "Res", "figures.res"));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, string> ReadHierarchy(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.ASCII);
        int count = reader.ReadInt32();
        if (count < 1 || count > 10000) throw new InvalidDataException("Некорректный размер иерархии.");
        string Text()
        {
            int size = reader.ReadInt32();
            if (size < 0 || size > 1024 || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Повреждена иерархия.");
            return Encoding.ASCII.GetString(reader.ReadBytes(size)).TrimEnd('\0');
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++) result.Add(Text(), Text());
        if (reader.BaseStream.Position != bytes.Length) throw new InvalidDataException("Лишние данные иерархии.");
        return result;
    }

    private static MmpImage Composite(List<MmpImage> layers, CancellationToken token)
    {
        int width = layers.Max(i => i.Width), height = layers.Max(i => i.Height);
        if ((long)width * height > 4194304) throw new NotSupportedException("Составная текстура превышает лимит 4 млн пикселей.");
        var output = new byte[width * height * 4];
        foreach (var layer in layers)
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int source = ((y * layer.Height / height) * layer.Width + x * layer.Width / width) * 4;
                    int dest = (y * width + x) * 4, alpha = layer.Bgra[source + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        int value = layer.PremultipliedAlpha ? layer.Bgra[source + c] : (layer.Bgra[source + c] * alpha + 127) / 255;
                        output[dest + c] = (byte)Math.Min(255, value + (output[dest + c] * (255 - alpha) + 127) / 255);
                    }
                    output[dest + 3] = (byte)(alpha + (output[dest + 3] * (255 - alpha) + 127) / 255);
                }
            }
        return new(width, height, "Составная", output, true);
    }
}
