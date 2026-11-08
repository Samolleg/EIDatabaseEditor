using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PrototypeWeaponsRegression
{
    public static int Run(string database, string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var inputHash = SHA256.HashData(File.ReadAllBytes(database));
            var db = DatabaseDocument.Open(database);
            var records = db.Databases.SelectMany(g => g.Tables)
                .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
            var lua = LuaDataWorkspace.Open(LuaDataWorkspace.FindNearby(database)!);
            ModelRecord Unit(string name) => records.Single(r => r.Table == "units.prototypes" && r.Name == name);
            UnitModelScene Load(ModelRecord unit, MobAppearance? mob = null) =>
                UnitModelPreview.LoadAsync(database, unit, records, appearance: mob, lua: lua).GetAwaiter().GetResult();
            static void Require(bool value, string message) { if (!value) throw new Exception(message); }
            var cases = new[]
            {
                ("Human Hadagan EliteL A20", "rh3.dstaff00", "lh3.dstaff00", "lh3"),
                ("Human Hadagan EliteL F20", "rh3.dagger07", "lh3.dagger07", "lh3"),
                ("Human Hadagan EliteH F20", "rh3.sword08", "lh2.shield09", "lh2"),
                ("Human Hadagan Elite M20", "rh3.sword09", "lh2.shield02", "lh2")
            };
            var scenes = new List<UnitModelScene>();
            var zombie = Unit("ZombieFemaleF14");
            Require(UnitModelPreview.StrongManLevel(zombie, lua) == 1, "Actual ZombieFemaleF14 StrongMan level not confirmed");
            var axes = Load(zombie);
            var sources = TextureSources.FromDatabaseFolderAsync(database).GetAwaiter().GetResult();
            var catalog = TextureResourceCatalog.OpenAsync(sources).GetAwaiter().GetResult();
            var axeTexture = catalog.ReadPreviewAsync("unhumata_01.st.0.x2.mmp").GetAwaiter().GetResult();
            var rightAxe = axes.Meshes.Single(m => m.Name == "rh3.daxe01");
            var leftAxe = axes.Meshes.Single(m => m.Name == "lh3.daxe01");
            Require(Math.Abs(rightAxe.UV.Min(v => v.X) - .002770696f) < .000001f &&
                Math.Abs(leftAxe.UV.Min(v => v.X) - .5027707f) < .000001f, "Original per-hand UV layout changed");
            Require(rightAxe.TextureSlot == 2 && leftAxe.TextureSlot == 2,
                "Axe raw native texture binding changed");
            Require(FigureTextureValidation.Weapon(rightAxe, false, "fixture") is null &&
                FigureTextureValidation.Weapon(rightAxe with { TextureSlot = 1 }, false, "fixture")!.StartsWith("Ошибка текстуры оружия:") &&
                FigureTextureValidation.Weapon(rightAxe with { TextureSlot = 7 }, false, "fixture")!.StartsWith("Не проверена схема") &&
                FigureTextureValidation.Weapon(rightAxe with { UV = rightAxe.UV.Select(v => v * 2).ToArray() }, false, "fixture")!.StartsWith("Не проверена схема"),
                "Shared texture validation confuses confirmed faults and custom layouts");
            CheckRegion(axes.Textures[rightAxe.TextureSlot], axeTexture, false);
            CheckRegion(axes.Textures[leftAxe.TextureSlot], axeTexture, true);
            var perk = lua.ForRecord(zombie).Single(g => g.Spec.Name == "UnitPerk");
            var entry = perk.Entries.Last();
            string key = lua.Schema(perk.Spec).Single(p => p.Value == "StrongMan" || p.Value.EndsWith(" — StrongMan", StringComparison.Ordinal)).Key;
            var value = entry.Field.Value.Field(key)!.Value;
            lua.Edit(entry, [new LuaPatch(value.Start, value.End - value.Start, "0")], "in-memory negative fixture");
            var without = Load(zombie);
            Require(without.Meshes.Any(m => m.Name == "rh3.daxe01") && !without.Meshes.Any(m => m.Name == "lh3.daxe01"), "No-perk twohand combination incorrect");
            Require(zombie.Text(42).Length > 0 && without.Notes.Any(n => n.Contains("запасное оружие")), "No-perk fixture removed inventory or omitted diagnostic");
            var noPerkShield = Load(zombie with { Fields = new Dictionary<int, string>(zombie.Fields) { [42] = Unit("Human Hadagan EliteH F20").Text(42) } });
            Require(noPerkShield.Meshes.Any(m => m.Name == "rh3.daxe01") && !noPerkShield.Meshes.Any(m => m.Name.StartsWith("lh2.shield")), "No-perk twohand+shield shown simultaneously");
            var reversedNoPerkShield = Load(zombie with { Fields = new Dictionary<int, string>(zombie.Fields) { [33] = Unit("Human Hadagan EliteH F20").Text(42), [42] = zombie.Text(33) } });
            Require(reversedNoPerkShield.Meshes.Any(m => m.Name == "rh3.daxe01") && !reversedNoPerkShield.Meshes.Any(m => m.Name.StartsWith("lh2.shield")), "Reversed no-perk twohand+shield shown simultaneously");
            entry.Document.Undo();
            Require(UnitModelPreview.StrongManLevel(zombie, lua) == 1, "Perk fixture not restored");
            var restored = Load(zombie);
            Require(restored.Meshes.Any(m => m.Name == "lh3.daxe01"), "Perk visibility cache remained stale");
            var zombieAnimations = axes.Animations!;
            var zombieClip = zombieAnimations.LoadAsync(zombieAnimations.Names.First(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase))).GetAwaiter().GetResult();
            var zombiePose = zombieAnimations.Pose(zombieClip, Math.Min(10, zombieClip.FrameCount - 1));
            Require(zombiePose.ContainsKey(rightAxe.Name) && zombiePose.ContainsKey(leftAxe.Name) &&
                zombiePose[rightAxe.Name].Position != zombiePose[leftAxe.Name].Position, "Animated axes lost separate hand attachments");
            scenes.Add(axes);
            Console.WriteLine("PASS ZombieFemaleF14: StrongMan1, two distinct steel axes, exact TA pixels, separate body/helmet materials; StrongMan0 single main weapon, slots intact; restore visibility");
            foreach (var (name, right, left, parent) in cases)
            {
                var unit = Unit(name);
                var scene = Load(unit);
                Require(!scene.Notes.Any(n => n.StartsWith("Ошибка текстуры оружия:") || n.StartsWith("Не проверена схема текстуры оружия:")), name + ": false positive texture diagnostic");
                var firstOnly = Load(unit with { Fields = new Dictionary<int, string>(unit.Fields) { [42] = "" } });
                Require(scene.Meshes.Any(m => m.Name == right) && scene.Meshes.Any(m => m.Name == left), name + ": second weapon/shield absent");
                var attachment = scene.Meshes.Single(m => m.Name == left);
                Require(scene.Textures.TryGetValue(attachment.TextureSlot, out var image) && image.Bgra.Any(b => b != 0), name + ": offhand texture absent");
                foreach (var mesh in firstOnly.Meshes)
                    Require(scene.Meshes.Single(m => m.Name == mesh.Name).Positions.SequenceEqual(mesh.Positions), name + ": existing mesh moved");
                foreach (var texture in firstOnly.Textures)
                    Require(texture.Key == 2 ? RetainedPrimaryQuarter(texture.Value, scene.Textures[2]) : scene.Textures[texture.Key].Bgra.SequenceEqual(texture.Value.Bgra), name + ": second weapon replaced primary/helmet texture");
                var set = scene.Animations ?? throw new Exception(name + ": animations lost");
                string ancestor = left;
                for (int depth = 0; depth < 128 && ancestor != parent; depth++)
                    ancestor = set.Parents.GetValueOrDefault(ancestor, "");
                Require(ancestor == parent, name + ": offhand is not attached to the correct arm through its helper nodes");
                var clip = set.LoadAsync("cidle09").GetAwaiter().GetResult();
                var start = set.Pose(clip, 0); var later = set.Pose(clip, Math.Min(10, clip.FrameCount - 1));
                Require(Vector3.Distance(start[left].Position, later[left].Position) > .00001f, name + ": attachment does not follow animation");
                Require(scene.Sources.Any(s => s.Contains(".mmp")) && !scene.Notes.Any(n => n.StartsWith("Нет детали экипировки: " + left)), name + ": unresolved attachment");
                scenes.Add(scene);
                Console.WriteLine($"PASS {name}: {right} + {left}; textured, animated; existing meshes/materials retained");
            }
            var twins = Unit(cases[1].Item1);
            var mixed = Load(twins with { Fields = new Dictionary<int, string>(twins.Fields)
                { [33] = "elven dagger.bronze", [42] = "elven dagger.iron" } });
            int rightSlot = mixed.Meshes.Single(m => m.Name == "rh3.dagger07").TextureSlot;
            int leftSlot = mixed.Meshes.Single(m => m.Name == "lh3.dagger07").TextureSlot;
            Require(leftSlot == 2 && rightSlot == 2, "Native shared weapon texture set lost");
            var daggerShape = records.Single(r => r.Table == "items.weapons" && r.Name == "elven dagger");
            MmpImage DaggerTexture(string name) => catalog.ReadPreviewAsync($"unhumadg_{daggerShape.Integer(5):D2}.{records.Single(r => r.Table == "items.materials" && r.Name == name).Text(2)}.{daggerShape.Integer(6)}.x2.mmp").GetAwaiter().GetResult();
            var bronze = DaggerTexture("bronze"); var iron = DaggerTexture("iron");
            Require(!bronze.Bgra.SequenceEqual(iron.Bgra), "Material fixture sources are identical");
            CheckRegion(mixed.Textures[rightSlot], bronze, false);
            CheckRegion(mixed.Textures[leftSlot], iron, true);
            Console.WriteLine("PASS bronze/iron: independent source pixels in respective quarters of the shared native atlas");
            var secondOnly = Load(twins with { Fields = new Dictionary<int, string>(twins.Fields) { [33] = "" } });
            Require(secondOnly.Meshes.Any(m => m.Name == "rh3.dagger07") && secondOnly.Meshes.All(m => m.Name != "lh3.dagger07"), "Single secondary slot no longer works as the main weapon");
            var shieldUnit = Unit(cases[2].Item1);
            var swapped = Load(shieldUnit with { Fields = new Dictionary<int, string>(shieldUnit.Fields)
                { [33] = shieldUnit.Text(42), [42] = shieldUnit.Text(33) } });
            Require(swapped.Meshes.Any(m => m.Name == "rh3.sword08") && swapped.Meshes.Any(m => m.Name == "lh2.shield09"), "Shield in slot1 prevents slot2 weapon display");
            var ranged = Load(Unit("Human Hadagan EliteH A20"));
            Require(ranged.Meshes.Any(m => m.Name == "rh3.crbow05main"), "Existing crossbow disappeared");
            var explicitMob = new MobAppearance("fixture", 1, "fixture", twins.Name, mixed.Model, "", "",
                new(twins.Number(5), twins.Number(6), twins.Number(7)), ["hp", "bd", "lh3"]) { ImportEquipment = true };
            var imported = Load(twins, explicitMob);
            Require(imported.Meshes.Count == 3 && imported.Meshes.All(m => explicitMob.Parts.Contains(m.Name)), "Explicit MOB appearance overwritten by prototype equipment");
            var inherited = Load(twins, explicitMob with { ImportEquipment = false });
            Require(inherited.Meshes.Any(m => m.Name == "lh3.dagger07"), "MOB using prototype equipment omitted offhand");
            var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            typeof(Application).GetField("_startupUri", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, null); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            for (int i = 0; i < scenes.Count; i++)
            {
                var view = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
                view.Measure(new Size(900, 750)); view.Arrange(new Rect(0, 0, 900, 750)); view.ShowScene(scenes[i]);
                Require(view.HasVisibleScene, "WPF lost equipped scene");
                var bitmap = new RenderTargetBitmap(900, 750, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"weapons-{i}.png")); encoder.Save(stream);
            }
            Require(app.Windows.Cast<Window>().All(w => !w.IsVisible), "Headless test opened a visible window");
            app.Shutdown();
            Require(inputHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "Input DB changed");
            Console.WriteLine("PASS independent materials, single secondary slot, reversed shield/weapon, crossbow, explicit/inherited MOB, WPF scenes, input unchanged");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void CheckRegion(MmpImage atlas, MmpImage source, bool left)
    {
        if (atlas.Width != source.Width * 2 || atlas.Height != source.Height * 2) throw new Exception("Native canvas dimensions incorrect");
        for (int y = 0; y < atlas.Height; y++)
            for (int x = 0; x < atlas.Width; x++)
            {
                int sx = x - (left ? source.Width : 0), sy = y - source.Height;
                bool inside = sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height;
                int s = inside ? (sy * source.Width + sx) * 4 : 0;
                if (!inside) continue;
                for (int c = 0; c < 4; c++)
                {
                    byte expected = inside ? source.Bgra[s + c] : (byte)0;
                    if (inside && c != 3 && !source.PremultipliedAlpha) expected = (byte)((expected * source.Bgra[s + 3] + 127) / 255);
                    if (atlas.Bgra[(y * atlas.Width + x) * 4 + c] != expected) throw new Exception("Native quadrant/source material pixel mismatch");
                }
            }
    }
    static bool RetainedPrimaryQuarter(MmpImage first, MmpImage both)
    {
        if (first.Width != both.Width || first.Height != both.Height) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (y < first.Height / 2 || x < first.Width / 2)
                    for (int c = 0; c < 4; c++)
                        if (first.Bgra[(y * first.Width + x) * 4 + c] != both.Bgra[(y * both.Width + x) * 4 + c]) return false;
        return true;
    }
}
