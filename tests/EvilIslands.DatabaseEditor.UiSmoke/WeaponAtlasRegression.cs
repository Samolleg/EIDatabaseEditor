using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class WeaponAtlasRegression
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Run(string database, string output, bool verify)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent();
        typeof(Application).GetField("_startupUri", Flags)!.SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(database, output, verify); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try
        {
            task.GetAwaiter().GetResult();
            Require(app.Windows.Cast<Window>().All(w => !w.IsVisible), "Visible window opened");
            app.Shutdown(); Console.WriteLine("PASS deterministic actual WPF armour32/weapon33/weapon42/ordinary renders; normal headless shutdown"); return 0;
        }
        catch (Exception ex) { Require(app.Windows.Cast<Window>().All(w => !w.IsVisible), "Visible window opened on failing test"); app.Shutdown(); Console.Error.WriteLine(ex); return 1; }
    }
    static byte Alpha(MmpImage image, Vector2 uv)
    {
        int x = Math.Clamp((int)(uv.X * image.Width), 0, image.Width - 1);
        int y = Math.Clamp((int)(uv.Y * image.Height), 0, image.Height - 1);
        return image.Bgra[(y * image.Width + x) * 4 + 3];
    }
    static void SaveImage(string path, MmpImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
    static async Task Render(UnitModelPreviewView view, string prefix)
    {
        view.Measure(new Size(1000, 1100)); view.Arrange(new Rect(0, 0, 1000, 1100)); view.UpdateLayout();
        view.PauseAnimation(); view.SetAnimationFrame(0);
        foreach (var (label, yaw) in new[] { ("front", 0d), ("quarter", .7d) })
        {
            typeof(UnitModelPreviewView).GetField("_yaw", Flags)!.SetValue(view, yaw);
            typeof(UnitModelPreviewView).GetField("_pitch", Flags)!.SetValue(view, .1d);
            typeof(UnitModelPreviewView).GetMethod("UpdateCamera", Flags)!.Invoke(view, null);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var bitmap = new RenderTargetBitmap(1000, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(prefix + "-" + label + ".png"); png.Save(file);
        }
    }
    static async Task Check(string database, string output, bool verify)
    {
        var initialHash = SHA256.HashData(File.ReadAllBytes(database));
        using var vm = new MainWindowViewModel(); await vm.OpenPathAsync(database);
        var table = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes");
        vm.SelectTableCommand.Execute(table);
        var row = table.Records.Single(r => r.CoreRecord.IdentityValue == "ZombieFemaleF14"); vm.SelectedRecord = row;
        var rows = vm.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r.CoreRecord))).ToArray();
        var unit = ModelRecord.Capture(table.Key, row.CoreRecord);
        var catalog = await TextureResourceCatalog.OpenAsync(await TextureSources.FromDatabaseFolderAsync(database));
        var source = await catalog.ReadPreviewAsync("unhumata_01.st.0.x2.mmp"); SaveImage(Path.Combine(output, "source-steel-TA.png"), source);
        var ordinary = await UnitModelPreview.LoadAsync(database, unit, rows, lua: vm.LuaWorkspace);
        var faults = ordinary.Notes.Where(n => n.StartsWith("Ошибка текстуры оружия:")).ToArray();
        Require(verify ? faults.Length == 0 : faults.Length == 2, "Original/repaired diagnostic count incorrect");
        Require(ordinary.Meshes.Where(m => m.Name is "rh3.daxe01" or "lh3.daxe01").All(m => m.TextureSlot == (verify ? 2 : 1)), "Raw texture ID masked");
        File.WriteAllText(Path.Combine(output, "visible-diagnostics.txt"), string.Join(Environment.NewLine, ordinary.Notes));
        Console.WriteLine($"PASS raw texture ID retained; weapon diagnostics={faults.Length}");
        async Task ExportRaw(UnitModelScene scene)
        {
            var sources = await TextureSources.FromDatabaseFolderAsync(database);
            foreach (string archive in UnitModelPreview.FigureArchives(database, sources))
            {
                if (!File.Exists(archive)) continue;
                var entry = ResFileManagerArchive.ReadCatalog(archive).FirstOrDefault(e => e.Name.Equals(scene.Model + ".mod", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var bytes = new byte[entry.Size]; using (var stream = File.OpenRead(archive)) { stream.Position = entry.Offset; stream.ReadExactly(bytes); }
                var mod = ResArchiveDocument.Parse("mod", bytes);
                foreach (var mesh in scene.Meshes.Where(m => m.Name.Contains("daxe") || m.Name.Contains("sword") || m.Name.Contains("shield") || m.Name.Contains("dagger")))
                    File.WriteAllBytes(Path.Combine(output, scene.Model + "-" + mesh.Name + ".fig"), mod.GetResourceBytes(mesh.Name).ToArray());
                File.WriteAllText(Path.Combine(output, scene.Model + "-sources.json"), JsonSerializer.Serialize(new { archive, modSHA256 = Convert.ToHexString(SHA256.HashData(bytes)), scene.Sources, scene.Notes }, new JsonSerializerOptions { WriteIndented = true }));
                break;
            }
        }
        await ExportRaw(ordinary);
        var data = new List<object>(); bool allExpected = true;
        foreach (var mesh in ordinary.Meshes.Where(m => m.Name is "rh3.daxe01" or "lh3.daxe01"))
        {
            var atlas = ordinary.Textures[mesh.TextureSlot]; SaveImage(Path.Combine(output, mesh.Name + "-material.png"), atlas);
            bool left = mesh.Name.StartsWith("lh");
            int mismatch = 0, opaque = 0, expectedOpaque = 0;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                var uv = (mesh.UV[mesh.Indices[i]] + mesh.UV[mesh.Indices[i + 1]] + mesh.UV[mesh.Indices[i + 2]]) / 3;
                // Independent native PutLayersWeapons contract: 512 source at
                // right(0,512), left(512,512) in a 1024 target, no UV normalization.
                var local = uv * 2 - new Vector2(left ? 1 : 0, 1);
                byte actual = Alpha(atlas, uv), expected = Alpha(source, local);
                if (actual != expected) mismatch++;
                if (actual > 200) opaque++; if (expected > 200) expectedOpaque++;
            }
            bool dimensions = atlas.Width == source.Width * 2 && atlas.Height == source.Height * 2;
            bool region = dimensions;
            if (dimensions)
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++)
                    {
                        int s = (y * source.Width + x) * 4;
                        int a = ((source.Height + y) * atlas.Width + (left ? source.Width : 0) + x) * 4;
                        for (int c = 0; c < 4; c++)
                        {
                            int expected = c == 3 || source.PremultipliedAlpha ? source.Bgra[s + c] : (source.Bgra[s + c] * source.Bgra[s + 3] + 127) / 255;
                            if (atlas.Bgra[a + c] != expected) region = false;
                        }
                    }
            allExpected &= dimensions && region && mismatch == 0 && opaque == expectedOpaque;
            data.Add(new { mesh.Name, mesh.TextureSlot, atlas.Width, atlas.Height, triangles = mesh.Indices.Length / 3, mismatch, opaque, expectedOpaque, dimensions, region, uv = mesh.UV.Select(v => new[] { v.X, v.Y }), vertices = mesh.Positions.Select(v => new[] { v.X, v.Y, v.Z }), mesh.Indices });
            Console.WriteLine($"{mesh.Name}: atlas {atlas.Width}x{atlas.Height}; alpha-centroid mismatches={mismatch}, opaque={opaque}, native expected={expectedOpaque}; exact-region={region}");
        }
        File.WriteAllText(Path.Combine(output, "native-contract.json"), JsonSerializer.Serialize(new { inputSHA256 = Convert.ToHexString(initialHash), allExpected, data }, new JsonSerializerOptions { WriteIndented = true }));
        var ordinaryView = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
        await ordinaryView.ShowEquipmentSceneAsync(ordinary, CancellationToken.None); await Render(ordinaryView, Path.Combine(output, "ordinary"));
        var records = vm.Databases.SelectMany(g => g.Tables).ToDictionary(t => t.Key, t => (IReadOnlyList<DatabaseRecord>)t.Records.Select(r => r.CoreRecord).ToArray());
        var context = new EmbeddedMagicEditorContext(EmbeddedMagicCatalog.Create(records), new Dictionary<string, string>());
        foreach (int field in new[] { 32, 33, 42 })
        {
            string[] values = field == 32 ? unit.Lists[32] : [unit.Text(field)];
            var dialog = new StructuredStringListEditorDialog("fixture", field == 32 ? "Броня" : "Оружие", values, [], _ => (true, (string?)null),
                singleValueMode: field != 32, magicContext: context, allowedElementKinds: [StructuredElementKind.RawValue, field == 32 ? StructuredElementKind.Armor : StructuredElementKind.Weapon]);
            dialog.ConfigureEquipment(vm, row, field);
            dialog.Measure(new Size(1600, 900)); dialog.Arrange(new Rect(0, 0, 1600, 900)); dialog.UpdateLayout();
            for (int pass = 0; pass < 12; pass++) { var pending = dialog.EquipmentLoadTask; await pending; await Dispatcher.Yield(DispatcherPriority.Background); if (ReferenceEquals(pending, dialog.EquipmentLoadTask)) break; }
            var view = (UnitModelPreviewView)typeof(StructuredStringListEditorDialog).GetField("_equipmentPreview", Flags)!.GetValue(dialog)!;
            var scene = (UnitModelScene?)typeof(UnitModelPreviewView).GetField("_editingScene", Flags)!.GetValue(view);
            Require(scene is not null, "Dialog scene failed: " + field);
            Require(scene!.Meshes.Count == ordinary.Meshes.Count && scene.Meshes.Zip(ordinary.Meshes).All(p => p.First.Name == p.Second.Name && p.First.UV.SequenceEqual(p.Second.UV) && p.First.Positions.SequenceEqual(p.Second.Positions)), "Draft path changes visible geometry/UV: " + field);
            Require(scene.Textures.All(p => p.Value.Bgra.SequenceEqual(ordinary.Textures[p.Key].Bgra)), "Draft path changes texture placement: " + field);
            Require(scene.Notes.Where(n => n.StartsWith("Ошибка текстуры оружия:")).SequenceEqual(faults), "Dialog diagnostic differs: " + field);
            await Render(view, Path.Combine(output, "dialog-" + field)); dialog.Close();
        }
        foreach (string name in new[] { "Human Hadagan EliteH F20", "Human Hadagan EliteL F20" })
        {
            var other = rows.Single(r => r.Table == "units.prototypes" && r.Name == name);
            var scene = await UnitModelPreview.LoadAsync(database, other, rows, lua: vm.LuaWorkspace);
            await ExportRaw(scene);
            var view = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
            await view.ShowEquipmentSceneAsync(scene, CancellationToken.None);
            await Render(view, Path.Combine(output, name.Contains("EliteH") ? "sword-shield" : "dual-daggers"));
            foreach (var mesh in scene.Meshes.Where(m => m.Name.Contains("sword") || m.Name.Contains("shield") || m.Name.Contains("dagger")))
            {
                int field = mesh.Name.StartsWith("lh") ? 42 : 33;
                string stem = StringListEditorCodec.AnalyzeElement(other.Text(field), "Оружие").PrimaryValue;
                int dot = stem.IndexOf('.');
                var shape = rows.Single(r => r.Table == "items.weapons" && r.Name == stem[..dot]);
                var material = rows.Single(r => r.Table == "items.materials" && r.Name == stem[(dot+1)..]);
                string code = shape.Integer(2) switch { 0 => "sw", 2 => "dg", 9 => "sd", _ => throw new Exception("Unexpected regression type") };
                var texture = await catalog.ReadPreviewAsync($"unhuma{code}_{shape.Integer(5):D2}.{material.Text(2)}.{shape.Integer(6)}.x2.mmp");
                var actual = scene.Textures[mesh.TextureSlot]; bool left = field == 42;
                int mismatch = 0;
                for (int i = 0; i < mesh.Indices.Length; i += 3)
                {
                    var uv = (mesh.UV[mesh.Indices[i]] + mesh.UV[mesh.Indices[i+1]] + mesh.UV[mesh.Indices[i+2]]) / 3;
                    if (Alpha(actual, uv) != Alpha(texture, uv * 2 - new Vector2(left ? 1 : 0, 1))) mismatch++;
                }
                Console.WriteLine($"Native authored {mesh.Name}: alpha mismatches={mismatch}");
                allExpected &= mismatch == 0 && mesh.UV.All(v => v.X >= (left ? .5f : 0) && v.X <= (left ? 1 : .5f) && v.Y >= .5f && v.Y <= 1);
            }
            File.WriteAllText(Path.Combine(output, name.Contains("EliteH") ? "sword-shield.json" : "dual-daggers.json"), JsonSerializer.Serialize(scene.Meshes.Where(m => m.Name.Contains("sword") || m.Name.Contains("shield") || m.Name.Contains("dagger")).Select(m => new { m.Name, m.TextureSlot, min = new[] { m.UV.Min(v => v.X), m.UV.Min(v => v.Y) }, max = new[] { m.UV.Max(v => v.X), m.UV.Max(v => v.Y) }, opaqueCentroids = Enumerable.Range(0, m.Indices.Length / 3).Count(i => Alpha(scene.Textures[m.TextureSlot], (m.UV[m.Indices[i * 3]] + m.UV[m.Indices[i * 3 + 1]] + m.UV[m.Indices[i * 3 + 2]]) / 3) > 200), triangles = m.Indices.Length / 3 }), new JsonSerializerOptions { WriteIndented = true }));
        }
        Require(initialHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "DB modified");
        if (verify) Require(allExpected, "Native texture atlas contract FAILED (201 must fail this check)");
        else Require(!allExpected, "Original faulty resource unexpectedly got a corrected weapon texture");
    }
}
