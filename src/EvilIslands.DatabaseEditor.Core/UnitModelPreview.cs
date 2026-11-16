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
    public AnimationDatabaseSource? AnimationDatabase { get; init; }
}

public static class UnitModelPreview
{
    internal static int StrongManLevel(ModelRecord unit, LuaDataWorkspace lua)
    {
        var group = lua.ForRecord(unit).Single(g => g.Spec.Name == "UnitPerk");
        if (group.Problem is not null) throw new InvalidDataException(group.Problem);
        var keys = lua.Schema(group.Spec).Where(p => p.Value == "StrongMan" || p.Value.EndsWith(" — StrongMan", StringComparison.Ordinal)).ToArray();
        if (keys.Length != 1) throw new InvalidDataException("Не распознано поле UnitPerk.StrongMan.");
        var row = group.Entries.LastOrDefault();
        if (row is null) return 0; // Installed Lua getter's absent-row default.
        string? value = row.Field.Value.Field(keys[0].Key)?.Value.Text;
        if (!int.TryParse(value, out int level) || level is < 0 or > 3)
            throw new InvalidDataException("Не распознано значение UnitPerk.StrongMan.");
        return level;
    }

    public static async Task<UnitModelScene> LoadAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, CancellationToken token = default, MobAppearance? appearance = null, LuaDataWorkspace? lua = null)
        => await LoadInternalAsync(databasePath, selected, records, token, appearance, lua, null).ConfigureAwait(false);

    internal static Task<UnitModelScene> ValidateResourcesAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, ResourceValidationSession session, CancellationToken token, LuaDataWorkspace? lua) =>
        LoadInternalAsync(databasePath, selected, records, token, null, lua, session);

    private static async Task<UnitModelScene> LoadInternalAsync(string databasePath, ModelRecord selected,
        IReadOnlyList<ModelRecord> records, CancellationToken token, MobAppearance? appearance, LuaDataWorkspace? lua,
        ResourceValidationSession? validation)
    {
        TextureSources sources = validation?.Sources ?? await TextureSources.FromDatabaseFolderAsync(databasePath, token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            using var pakFiles = new PakArchiveSet();
            var notes = new List<string>();
            var used = new List<string>();
            ModelRecord Find(string table, string name) => records.SingleOrDefault(r => r.Table == table && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Не найдена запись {table}: {name}");
            var unit = selected.Table == "units.npcs" ? Find("units.prototypes", selected.Name) : selected;
            if (unit.Table != "units.prototypes") throw new NotSupportedException("Выберите прототип NPC или монстра.");
            var race = Find("units.races", unit.Text(1));
            string mask = appearance?.Model ?? race.Text(30);
            if (string.IsNullOrWhiteSpace(mask)) throw new InvalidDataException("В расе не задана маска модели.");
            if (validation is not null) validation.Context = "Модель: " + mask;
            var archives = FigureArchives(databasePath, sources);
            var indexes = new List<(string Path, IReadOnlyList<ResEntry> Entries, long Length, long Written)>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            if (validation is not null) indexes = validation.Indexes;
            foreach (string path in validation is null ? archives : [])
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    long length = info.Length; long written = ResourceFileStamp.Read(path);
                    if (PakArchive.IsPak(path)) foreach (string name in pakFiles.Get(path, token).Deleted) claimed.Add(PakArchive.LookupKey(name));
                    IReadOnlyList<ResEntry> entries = (PakArchive.IsPak(path) ? pakFiles.Get(path, token).Entries : ResFileManagerArchive.ReadCatalog(path, token)).Where(e => claimed.Add(PakArchive.LookupKey(e.Name))).ToArray();
                    info.Refresh();
                    if (info.Length != length || ResourceFileStamp.Read(path) != written) throw new IOException("Архив модели изменился во время чтения.");
                    indexes.Add((path, entries, length, written));
                }
            }
            AnimationSaveTarget? animationTarget = null;
            byte[] Resource(string name)
            {
                if(ResourceArchivePaths.FindLoose(sources.ConfigurationPath,name) is { } loose){byte[] bytes=File.ReadAllBytes(loose);used.Add(name+" — "+loose);if(name.EndsWith(".anm",StringComparison.OrdinalIgnoreCase))animationTarget=new(loose,null,bytes.ToArray());return bytes;}
                if (validation is not null) return validation.Read(name, token);
                foreach (var archive in indexes)
                {
                    var entry = archive.Entries.FirstOrDefault(e => PakArchive.LookupKey(e.Name) == PakArchive.LookupKey(name));
                    if (entry is null) continue;
                    using var stream = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length != archive.Length || ResourceFileStamp.Read(archive.Path) != archive.Written)
                        throw new IOException("Архив модели изменился. Обновите предпросмотр.");
                    byte[] bytes;
                    if (PakArchive.IsPak(archive.Path)) bytes = pakFiles.Get(archive.Path, token).Read(entry.Name, token);
                    else { stream.Position = entry.Offset; bytes = new byte[entry.Size]; stream.ReadExactly(bytes); }
                    if (name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) animationTarget = new(archive.Path, entry.Name, bytes.ToArray()) { ArchiveSha256 = PakArchive.IsPak(archive.Path) ? pakFiles.Get(archive.Path, token).Sha256 : AnimationPairSave.HashFile(archive.Path) };
                    used.Add(name + " — " + archive.Path);
                    return bytes;
                }
                throw new FileNotFoundException("Не найден ресурс модели: " + name);
            }
            // Base FIGs already use the secondary atlas coordinates. Keep this
            // tied to the selected resource, even when a mod DB falls back to it.
            string? modelArchive = ResourceArchivePaths.FindLoose(sources.ConfigurationPath,mask + ".mod") ?? indexes.FirstOrDefault(a => a.Entries.Any(e => e.Name.Equals(mask + ".mod", StringComparison.OrdinalIgnoreCase))).Path;
            string? gameFolder = sources.GameFolder;
            if (gameFolder is null && modelArchive is not null &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(modelArchive)), "Res", StringComparison.OrdinalIgnoreCase))
            {
                string? candidate = Path.GetDirectoryName(Path.GetDirectoryName(modelArchive));
                if (candidate is not null && File.Exists(Path.Combine(candidate, "game.exe"))) gameFolder = candidate;
            }
            bool baseFigure = ResourceArchivePaths.IsBaseFigure(modelArchive, gameFolder);
            ResArchiveDocument ModelArchive(string name)
            {
                string? source = ResourceArchivePaths.FindLoose(sources.ConfigurationPath, name) ?? indexes.FirstOrDefault(a => a.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).Path;
                if (source is null) throw new FileNotFoundException("Не найден ресурс модели: " + name);
                used.Add(name + " — " + source);
                return PreviewModelArchiveCache.Parse(source, name, () => Resource(name), token);
            }
            var mod = validation is null ? ModelArchive(mask + ".mod") :
                validation.Parse(mask + ".mod", () => validation.Read(mask + ".mod", token), token);
            var bon = validation is null ? ModelArchive(mask + ".bon") :
                validation.Parse(mask + ".bon", () => validation.Read(mask + ".bon", token), token);
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
            var catalog = validation?.Catalog ?? await TextureResourceCatalog.OpenCachedAsync(sources, token).ConfigureAwait(false);
            // A humanoid hierarchy alone does not imply a composited skin (e.g. goblins).
            // Keep their explicit MOB skin unless the model's redress skin actually exists.
            bool hasCompositeSkin = new[] { ".x4.mmp", ".x2.mmp", ".mmp" }
                .Any(suffix => catalog.Check($"{mask}skin_{unit.Integer(3):D2}" + suffix).Matches.Count > 0);
            bool prototypeEquipment = appearance is null || (dressed && hasCompositeSkin && appearance.ImportEquipment == false);
            var visible = new HashSet<string>(dressed ? bodyParts : parents.Keys, StringComparer.OrdinalIgnoreCase);
            void AddPart(string root)
            {
                if (!parents.ContainsKey(root)) { notes.Add("Нет детали экипировки: " + root);
                    validation?.Add(new(DiagnosticSeverity.Error, "RESOURCE_MISSING", "Нет детали экипировки: " + root,
                        validation.FigurePath(mask + ".mod") + " → " + root, Domain: DiagnosticDomain.Resources)); return; }
                // Descendants also contain invisible attachment/helper geometry.
                // Visibility is an explicit equipment list, independent of transforms.
                visible.Add(root);
            }
            if (dressed && unit.Integer(4, -1) >= 0) visible.Add($"hr.{unit.Integer(4):D2}");
            var primary = new List<string>(); var secondary = new List<string>();
            if (dressed && hasCompositeSkin) primary.Add($"{mask}skin_{unit.Integer(3):D2}");
            else
            {
                string[] skins = race.Lists.GetValueOrDefault(31, []);
                int index = unit.Integer(3);
                if (index < 0 || index >= skins.Length)
                {
                    notes.Add("Индекс основной текстуры вне списка расы.");
                    validation?.Skip("skin-index:" + unit.Name, "Основная текстура не проверена: индекс вне списка расы.");
                }
                else primary.Add(skins[index]);
                secondary.AddRange(race.Lists.GetValueOrDefault(32, []).Take(1));
            }
            string[] armorCodes = ["hl", "pl", "lg", "sh", "pt", "bt", "gl"];
            var armorItems = new List<(ModelRecord Shape, string Material)>();
            if (appearance is null && !dressed && (unit.Lists.GetValueOrDefault(32, []).Length > 0 || unit.Text(33).Length > 0 || unit.Text(42).Length > 0))
            {
                notes.Add("В геометрии нет слотов составной экипировки. Показана исходная модель; внешний вид экземпляра можно выбрать из MOB.");
                validation?.Skip("equipment-layout:" + unit.Name, "Надетая экипировка не проверена: в геометрии нет поддержанных слотов.");
            }
            foreach (string item in prototypeEquipment && dressed ? unit.Lists.GetValueOrDefault(32, []) : [])
            {
                var analysis = StringListEditorCodec.AnalyzeElement(item, "Броня");
                if (!analysis.IsValid) { notes.Add("Не распознана экипировка: " + analysis.Error);
                    validation?.Skip("armor-list:" + unit.Name + ":" + item, "Не проверен элемент списка брони: " + analysis.Error); continue; }
                string baseItem = analysis.PrimaryValue;
                int dot = baseItem.IndexOf('.');
                if (dot < 0) { notes.Add("Не распознана экипировка: " + item); continue; }
                try { armorItems.Add((Find("items.armors", baseItem[..dot]), Find("items.materials", baseItem[(dot + 1)..]).Text(2))); }
                catch (InvalidDataException ex) { notes.Add(ex.Message); validation?.Skip("armor-reference:" + unit.Name + ":" + ex.Message, "Ссылка экипировки не проверена: " + ex.Message); }
            }
            if (armorItems.GroupBy(i => i.Shape.Integer(2)).Any(g => g.Count() > 1))
            {
                notes.Add("В БД несколько вариантов одного слота: показан первый вариант.");
            }
            foreach (var item in armorItems.GroupBy(i => i.Shape.Integer(2)).Select(g => g.First()).OrderBy(i => i.Shape.Integer(2) < 3 ? 1 : 0).ThenBy(i => i.Shape.Integer(2) < 3 ? i.Shape.Integer(24) : i.Shape.Integer(2)))
            {
                int type = item.Shape.Integer(2), index = item.Shape.Integer(5);
                if (type < 0 || type >= armorCodes.Length) { notes.Add("Неизвестный тип брони: " + item.Shape.Name);
                    validation?.Skip("armor-type:" + item.Shape.Name, "Тип брони не поддержан: " + item.Shape.Name); continue; }
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
                // These bracers occupy the gloves slot, but have two additional
                // forearm meshes. Keep the underlying arms visible.
                if (type == 6 && index == 31 && item.Shape.Name == "ancient bronze bracers" &&
                    mask is "unhusk" or "unhuma" or "unhufe")
                {
                    foreach (string part in new[] { "lh2.glove31", "rh2.glove31" })
                        if (parents.ContainsKey(part)) AddPart(part);
                }
            }
            var weaponMaterials = new List<(HashSet<string> Parts, List<string> Layers, bool Left)>();
            bool strongMan = false;
            try
            {
                if (prototypeEquipment && dressed && unit.Text(33).Length > 0 && unit.Text(42).Length > 0)
                {
                lua ??= LuaDataWorkspace.FindNearby(databasePath) is { } folder ? LuaDataWorkspace.Open(folder) : null;
                if (lua is null)
                {
                    notes.Add("Навык StrongMan не подтверждён: Lua недоступен; двуручное парное оружие не показано.");
                    validation?.Skip("strongman:" + unit.Name, "Lua недоступен; парный двуручный вариант не проверен.");
                }
                else strongMan = StrongManLevel(unit, lua) >= 1;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { notes.Add("Навык StrongMan не подтверждён: " + ex.Message);
                validation?.Skip("strongman:" + unit.Name, "Навык StrongMan не подтверждён: " + ex.Message); }
            int? primaryWeaponType = null;
            bool hasRightWeapon = false;
            int visibleWeapons = 0;
            foreach (int field in prototypeEquipment && dressed ? new[] { 33, 42 } : [])
            {
                string weapon = unit.Text(field);
                if (weapon.Length == 0) continue;
                var analysis = StringListEditorCodec.AnalyzeElement(weapon, "Оружие");
                if (!analysis.IsValid) { notes.Add("Не распознано оружие: " + analysis.Error);
                    validation?.Skip("weapon:" + unit.Name + ":" + weapon, "Не проверена ссылка оружия: " + analysis.Error); continue; }
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
                    if (kind.Item1.Length == 0) { notes.Add("Сборка этого типа оружия пока не поддержана: " + shape.Name);
                        validation?.Skip("weapon-type:" + shape.Name, "Тип оружия не поддержан: " + shape.Name); continue; }
                    if (type == 9)
                    {
                        string otherValue = unit.Text(field == 33 ? 42 : 33);
                        var otherItem = StringListEditorCodec.AnalyzeElement(otherValue, "Оружие");
                        int otherDot = otherItem.PrimaryValue.IndexOf('.');
                        var otherShape = otherItem.IsValid && otherDot > 0 ? records.SingleOrDefault(r =>
                            r.Table == "items.weapons" && r.Name.Equals(otherItem.PrimaryValue[..otherDot], StringComparison.OrdinalIgnoreCase)) : null;
                        if (otherShape is not null && otherShape.Integer(2) != 9 &&
                            !PrototypeCombatCalculation.UsesOtherHand(otherShape.Integer(2), type, strongMan))
                        { notes.Add($"Оружие {(field == 33 ? 1 : 2)} ({shape.Name}) — запасной щит; основное оружие требует обеих рук.");
                            continue; }
                    }
                    bool leftHand = hasRightWeapon && type != 9;
                    if (leftHand && !PrototypeCombatCalculation.UsesOtherHand(primaryWeaponType!.Value, type, strongMan))
                    {
                        notes.Add($"Оружие 2 ({shape.Name}) — запасное оружие; текущий тип основного оружия и подтверждённый навык не разрешают парное крепление.");
                        continue;
                    }
                    string[] parts = type switch
                    {
                        9 => [$"lh2.shield{index:D2}"],
                        5 => [$"lh3.bwpartb{index:D2}", $"bwparta{index:D2}", $"bwtetivaa{index:D2}", $"bwtetivab{index:D2}", "quiver", "arrows", "rh3.arrow00"],
                        6 => [$"rh3.crbow{index:D2}main", $"crbow{index:D2}part01", $"crbow{index:D2}part02", $"crbow{index:D2}tetiva01", $"crbow{index:D2}tetiva02", "quiver", "arrows"],
                        _ => [$"{(leftHand ? "lh3" : "rh3")}.{kind.Item1}{index:D2}"]
                    };
                    foreach (string part in parts) AddPart(part);
                    if (!parts.Any(parents.ContainsKey)) continue;
                    // Keep each item's pixels independent of the body, helmet and other hand.
                    // Identical shape IDs still represent distinct equipped instances.
                    weaponMaterials.Add((parts.ToHashSet(StringComparer.OrdinalIgnoreCase),
                        [$"{mask}{kind.Item2}_{index:D2}.{material.Text(2)}.{shape.Integer(6)}"], type == 9 || leftHand));
                    visibleWeapons++;
                    if (type != 9) { hasRightWeapon = true; primaryWeaponType = type; }
                    notes.Add($"Оружие {(field == 33 ? 1 : 2)}: {shape.Name} · {string.Join(", ", parts)} · {mask}.mod.");
                }
                catch (InvalidDataException ex) { notes.Add(ex.Message); validation?.Skip("weapon-reference:" + unit.Name + ":" + ex.Message, "Ссылка экипировки не проверена: " + ex.Message); }
            }
            if (visibleWeapons > 1) notes.Add("Показаны оба оружейных слота прототипа. В игре активный комплект зависит от состояния и навыков персонажа.");
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
                if (!mod.TryGetEntry(part, out var entry) || entry is null)
                {
                    validation?.Add(new(DiagnosticSeverity.Error, "RESOURCE_MISSING", "Нет FIG детали модели: " + part,
                        validation.FigurePath(mask + ".mod") + " → " + part + ".fig", Domain: DiagnosticDomain.Resources));
                    continue;
                }
                FigureMesh mesh;
                try { mesh = validation is null ? FigurePreviewDecoder.Decode(part, mod.GetResourceBytes(entry).ToArray(), morph) :
                    validation.Decode(part, () => mod.GetResourceBytes(entry).ToArray(), morph, validation.FigurePath(mask + ".mod") + " → " + part + ".fig"); }
                catch (Exception ex) when (validation is not null && ResourceValidationSession.IsResourceFailure(ex)) { validation.Failure(ex); continue; }
                if (mesh.Indices.Length == 0)
                {
                    validation?.Skip(validation.Path, "Пустая FIG/служебная геометрия: визуальная схема не проверена."); continue;
                }
                var weaponMaterial = weaponMaterials.FirstOrDefault(w => w.Parts.Contains(part));
                if (validation is null && weaponMaterial.Parts is not null && FigureTextureValidation.Weapon(mesh, weaponMaterial.Left, used[0]) is { } warning)
                    notes.Insert(0, warning);
                if (validation is not null)
                {
                    if (weaponMaterial.Parts is not null && FigureTextureValidation.CheckWeapon(mesh, weaponMaterial.Left,
                        validation.FigurePath(mask + ".mod")) is { } diagnostic) validation.Add(diagnostic);
                    meshes.Add(mesh); continue;
                }
                // A shared native secondary atlas includes helmet and both hands.
                // Keep authored coordinates when that complete atlas is assembled.
                if (prototypeEquipment && armorSlots && !baseFigure && mesh.TextureSlot == 2 && weaponMaterials.Count == 0)
                    for (int i = 0; i < mesh.UV.Length; i++) mesh.UV[i] *= 2;
                Vector3 offset = Position(part);
                for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i] += offset;
                meshes.Add(mesh);
            }
            if (meshes.Count == 0 && validation is null) throw new InvalidDataException("В модели нет доступной геометрии.");
            var textures = new Dictionary<int, MmpImage>();
            var textureGroups = new List<(int, List<string>, bool?)> { (1, primary, null), (2, secondary, null) };
            int weaponSlot = Math.Max(2, meshes.Select(m => m.TextureSlot).DefaultIfEmpty(2).Max()) + 1;
            foreach (var weaponMaterial in weaponMaterials)
            {
                // Build weapon layers for the native secondary set. Raw FIG IDs
                // decide which set actually draws each part; never repair IDs here.
                textureGroups.Add((weaponSlot++, weaponMaterial.Layers, weaponMaterial.Left));
            }
            foreach (var group in textureGroups)
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
                        if (validation is not null) { await validation.TextureAsync(name, token).ConfigureAwait(false); continue; }
                        layers.Add(await catalog.ReadPreviewAsync(name, token).ConfigureAwait(false));
                        used.Add(name + " — " + catalog.Check(name).Source);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { notes.Add(name + ": " + ex.Message); }
                }
                if (layers.Count > 0)
                {
                    var image = Composite(layers, token);
                    textures[group.Item1] = group.Item3 is { } leftHand ? WeaponAtlas(image, leftHand, token) : image;
                }
            }
            if (validation is not null) return new UnitModelScene(mask, [], new Dictionary<int, MmpImage>(), [], notes);
            if (weaponMaterials.Count > 0)
            {
                var atlasLayers = new List<MmpImage>();
                if (textures.TryGetValue(2, out var helmet))
                    atlasLayers.Add(EquipmentAtlasLayer(helmet, 0, 0, token));
                foreach (int slot in textureGroups.Where(g => g.Item3 is not null).Select(g => g.Item1))
                    if (textures.Remove(slot, out var weapon)) atlasLayers.Add(weapon);
                if (atlasLayers.Count > 0) textures[2] = Composite(atlasLayers, token);
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
                    positions.Keys.ToDictionary(p => p, p => parents[p], StringComparer.OrdinalIgnoreCase), bones, positions, token) { SaveTarget = animationTarget };
                foreach (string skipped in animations.SkippedResources) notes.Add("ANM: пропущен вложенный архив или пустая заглушка: " + skipped);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DatabaseFormatException or NotSupportedException or ArgumentException)
            { notes.Add("Анимации недоступны: " + ex.Message); }
            AnimationDatabaseSource? animationDatabase = null;
            try { animationDatabase = await AnimationDatabaseSource.OpenAsync(databasePath, sources.ConfigurationPath, mask, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DatabaseFormatException or NotSupportedException or ArgumentException)
            { notes.Add("ADB недоступен: " + ex.Message); }
            if (animations is not null && animationDatabase is not null)
                animations = animations.WithRootSpace(new FigureRootSpace(animationDatabase, morph.Z));
            if (animations is not null && animationDatabase is not null)
            {
                try { notes.Add($"Корень ANM: игровая поправка XYZ по росту {morph.Z:R}, коэффициент {animations.RootSpace.Coefficient:R}; {animationDatabase.Description}."); }
                catch (InvalidDataException ex) { notes.Add("Поправка корня ADB недоступна: " + ex.Message + ". Дорожки доступны для чтения; игровая поза не вычисляется."); }
            }
            if (race.Number(33, 0) != 0) notes.Add("Ненулевой сдвиг центра расы: предпросмотр не подтверждает игровой контекст unit/player; world placement не воспроизводится.");
            if (animationDatabase is null) notes.Add("Корень показан в сырых координатах ANM: ADB отсутствует, игровая поправка не подтверждена.");
            if (animationDatabase is null) notes.Add("ADB модели не найден: игровая регистрация анимаций недоступна.");
            notes.Add("Клипы показываются отдельно от игрового состояния; звуки, эффекты, AI и переходы между действиями не воспроизводятся.");
            notes.Add("Скорость предпросмотра задаётся в кадрах/с. Игровые настройки темпа и коррекция перемещения по росту не применяются.");
            if (dressed) notes.Add("Составная экипировка: визуальное совпадение с игрой ещё требует проверки.");
            if (selected.Table == "units.npcs") notes.Add("Внешность — из одноимённого прототипа монстра. Инвентарь NPC не определяет, что надето в конкретном сохранении.");
            return new UnitModelScene(mask, meshes, textures, used.Distinct().ToArray(), notes.Distinct().ToArray()) { Animations = animations, AnimationDatabase = animationDatabase };
        }, token).ConfigureAwait(false);
    }

    internal static IEnumerable<string> FigureArchives(string database, TextureSources sources)
    {
        if(sources.ConfigurationPath is { } graphicsConfig&&sources.GameFolder is { } graphicsBase)
            return TextureSources.FromGame(graphicsBase,graphicsConfig,["figures.res","menus.res"]).Archives.Where(File.Exists);
        var paths = new List<string>();
        bool ignoreBase = false;
        if (sources.ConfigurationPath is { } config)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string section = "";
            foreach (string raw in File.ReadAllLines(config, Encoding.GetEncoding(1251)))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
                int eq = line.IndexOf('=');
                if (section.Equals("RES", StringComparison.OrdinalIgnoreCase) && eq > 0 && line[..eq].Trim().Equals("figures.res", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line[(eq + 1)..].Trim().Trim('"');
                    if (value.StartsWith('-')) ignoreBase = true;
                    else paths.Add(Path.GetFullPath(value, Path.GetDirectoryName(config)!));
                }
            }
        }
        else paths.AddRange(ResourceArchivePaths.Enumerate(Path.GetDirectoryName(database)!));
        var baseline = !ignoreBase && sources.GameFolder is { } game ? new[] { Path.Combine(game, "Res", "figures.res") } : Array.Empty<string>();
        return ResourceArchivePaths.Order(paths, baseline);
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

    internal static MmpImage WeaponAtlas(MmpImage image, bool leftHand, CancellationToken token)
        => EquipmentAtlasLayer(image, leftHand ? image.Width : 0, image.Height, token);

    private static MmpImage EquipmentAtlasLayer(MmpImage image, int x, int yOffset, CancellationToken token)
    {
        // Native CTextureManager::PutLayersWeapons places each 512 layer in a
        // 1024 atlas: right(0,512), left(512,512). FIG UVs address that atlas,
        // not the input layer. Preserve pixels/alpha and the original mesh UVs.
        int width = checked(image.Width * 2), height = checked(image.Height * 2);
        if ((long)width * height > 4194304) throw new NotSupportedException("Оружейный атлас превышает лимит 4 млн пикселей.");
        var bytes = new byte[checked(width * height * 4)];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Buffer.BlockCopy(image.Bgra, y * image.Width * 4, bytes, ((yOffset + y) * width + x) * 4, image.Width * 4);
        }
        return new(width, height, image.Format + " · оружейный атлас", bytes, image.PremultipliedAlpha);
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
