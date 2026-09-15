using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ModelPreviewRegression
{
    public static int Run(string database, string output)
    {
        CheckDecoder();
        Directory.CreateDirectory(output);
        byte[] originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        foreach (string name in new[] { "Human Hero", "BoarDarkF2", "Human Hero + dagger", "Human Hero Hadagan1", "Human Mercenary Hadagan Thief", "MOB GoblinA0", "GZOrcMaleF6", "SkeletonWarriorF10", "ZombieMaleF12", "ZombieFemaleF12", "ArmoredTurtle90" })
        {
            MobAppearance? appearance = null;
            if (name == "MOB GoblinA0")
            {
                string mob = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(database)!, "../Maps/zone1.mob"));
                byte[] original = File.ReadAllBytes(mob);
                appearance = MobAppearance.Read(mob).Single(a => a.Id == 1030);
                if (appearance.Primary != "Goblinsha00" || appearance.Secondary != "GoblinAxeBag00" || appearance.Parts.Length != 19 ||
                    Math.Abs(appearance.Morph.Z + .1f) > .0001f) throw new Exception("MOB screenshot contract mismatch");
                if (!original.SequenceEqual(File.ReadAllBytes(mob))) throw new Exception("MOB changed");
                try { MobAppearance.Parse(mob, original[..^1]); throw new Exception("Truncated MOB accepted"); } catch (InvalidDataException) { }
            }
            var selected = records.Single(r => r.Table == "units.prototypes" && r.Name == (appearance?.Prototype ?? (name == "Human Hero + dagger" ? "Human Hero" : name)));
            if (name == "Human Hero + dagger") selected = selected with { Fields = new Dictionary<int, string>(selected.Fields) { [33] = "cheat dagger.bronze" } };
            var scene = UnitModelPreview.LoadAsync(database, selected, records, default, appearance).GetAwaiter().GetResult();
            if (appearance is not null && (scene.Meshes.Count != 19 || scene.Meshes.Any(m => !appearance.Parts.Contains(m.Name)) ||
                !scene.Sources.Any(s => s.StartsWith("Goblinsha00")))) throw new Exception("MOB appearance not applied");
            if (scene.Meshes.Count == 0 || scene.Textures.Count == 0) throw new Exception("No model/texture: " + name);
            if (name is "ZombieMaleF12" or "ZombieFemaleF12")
            {
                if (scene.Textures.Count != 2 || scene.Sources.Count(s => s.StartsWith("unhuma")) != 8 ||
                    !scene.Meshes.Any(m => m.Name == "rh3.daxe01") || scene.Notes.Any(s => s.Contains("Файл не найден")))
                    throw new Exception("Zombie shared equipment / two-handed axe regression");
            }
            if (name == "ArmoredTurtle90" && (!scene.Sources.Any(s => s.StartsWith("turtleblue")) ||
                !scene.Meshes.Any(m => m.Name == "tsh") || scene.Notes.Any(s => s.Contains("skin_"))))
                throw new Exception("Natural armor mistaken for humanoid equipment");
            if (name == "SkeletonWarriorF10" && (scene.Textures.Count != 2 || scene.Sources.Count(s => s.StartsWith("unhuma")) != 8 ||
                !scene.Sources.Any(s => s.StartsWith("unhuskskin_01.x4.mmp")) || scene.Notes.Any(s => s.Contains("Файл не найден"))))
                throw new Exception("Skeleton equipment or race skin missing");
            if (name == "GZOrcMaleF6" && (scene.Textures.Count != 2 || scene.Sources.Count(s => s.StartsWith("unhuma")) != 8 ||
                !scene.Sources.Any(s => s.StartsWith("unormaskin_00.x4.mmp")) || scene.Notes.Any(s => s.Contains("Файл не найден"))))
                throw new Exception("Orc equipment or race skin missing");
            if (name == "Human Hero" && scene.Meshes.Count > 20) throw new Exception("Unselected equipment leaked into body.");
            if (name == "Human Hero")
                foreach (string code in new[] { "sh", "pt", "bt", "gl" })
                    if (!scene.Sources.Any(s => s.StartsWith($"unhuma{code}_12.cw.0.x4.mmp — ")))
                        throw new Exception("Base female clothing hides common HD layer: " + code);
            if (name == "Human Mercenary Hadagan Thief")
            {
                if (scene.Notes.Any(n => n.Contains("Не найдена запись items.")) || scene.Textures.Count != 2)
                    throw new Exception("Enchanted equipment not resolved");
                var plain = selected with
                {
                    Lists = new Dictionary<int, string[]>(selected.Lists)
                    { [32] = selected.Lists[32].Select(v => StringListEditorCodec.AnalyzeElement(v, "Броня").PrimaryValue).ToArray() },
                    Fields = new Dictionary<int, string>(selected.Fields)
                    { [33] = StringListEditorCodec.AnalyzeElement(selected.Text(33), "Оружие").PrimaryValue,
                      [42] = StringListEditorCodec.AnalyzeElement(selected.Text(42), "Оружие").PrimaryValue }
                };
                var reference = UnitModelPreview.LoadAsync(database, plain, records).GetAwaiter().GetResult();
                if (!scene.Sources.SequenceEqual(reference.Sources) || !scene.Meshes.Select(m => m.Name).SequenceEqual(reference.Meshes.Select(m => m.Name)) ||
                    scene.Textures.Any(t => !t.Value.Bgra.SequenceEqual(reference.Textures[t.Key].Bgra)))
                    throw new Exception("Embedded magic changed equipment appearance");
            }
            if (name == "Human Hero Hadagan1")
            {
                if (!scene.Meshes.Any(m => m.Name == "hd") || !scene.Meshes.Any(m => m.Name == "hd.armor20") ||
                    scene.Meshes.Any(m => m.Name == "hr.01")) throw new Exception("Helmet/head/hair visibility regression");
                if (!scene.Sources.Any(s => s.Contains("unhufeskin_10.x4.mmp") && s.Contains("person.res")))
                    throw new Exception("High-resolution face/skin source missing");
                if (!scene.Sources.Any(s => s.Contains("unhumahl_20.ub.0.x2.mmp")) ||
                    !scene.Sources.Any(s => s.Contains("unhumapl_21.ub.0.x4.mmp")) || scene.Textures.Count != 2)
                    throw new Exception("Shared JMV armor layers missing");
            }
            foreach (var mesh in scene.Meshes)
            {
                if (mesh.Indices.Any(i => i < 0 || i >= mesh.Positions.Length)) throw new Exception("Bad index");
                if (mesh.Positions.Any(p => !float.IsFinite(p.X + p.Y + p.Z))) throw new Exception("Bad coordinates");
            }
            var view = new UnitModelPreviewView { Width = 1100, Height = 780 };
            if (appearance is not null)
            {
                var entries = MobAppearance.Read(appearance.Path);
                view.SetMobEntries(entries, "goblina0");
                if (view.MatchingObjects.Count == 0 || view.MatchingObjects.Any(a => !a.Prototype.Contains("GoblinA0", StringComparison.OrdinalIgnoreCase))) throw new Exception("Prototype filter failed");
                view.SearchObjects("GoblV-1030");
                if (!view.MatchingObjects.Any(a => a.Id == 1030)) throw new Exception("Name filter failed");
                view.SearchObjects("1030");
                if (!view.MatchingObjects.Any(a => a.Id == 1030)) throw new Exception("ID filter failed");
                view.SearchObjects("__not_existing__");
                if (view.MatchingObjects.Count != 0) throw new Exception("Empty filter failed");
                view.SearchObjects("GoblinA0");
            }
            view.Measure(new Size(1100, 780)); view.Arrange(new Rect(0, 0, 1100, 780));
            view.ShowScene(scene); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 780, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
            File.WriteAllLines(Path.Combine(output, name + ".txt"), scene.Sources.Concat(scene.Notes));
            Console.WriteLine($"{name}: {scene.Model}, meshes={scene.Meshes.Count}, triangles={scene.Meshes.Sum(m => m.Indices.Length / 3)}, textures={scene.Textures.Count}");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { UnitModelPreview.LoadAsync(database, records.First(), records, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        try { FigurePreviewDecoder.Decode("bad", new byte[8], new()); throw new Exception("Bad FIG accepted"); }
        catch (NotSupportedException) { }
        if (!originalHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database)))) throw new Exception("Input database changed");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "ui-settings.json")), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        var opening = vm.OpenPathAsync(database);
        var frame = new DispatcherFrame();
        _ = opening.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!opening.IsCompleted) Dispatcher.PushFrame(frame);
        opening.GetAwaiter().GetResult();
        if (!vm.HasDocument) throw new Exception("UI database did not open");
        var table = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = table.Records.Single(r => r.CoreRecord.IdentityValue == "BoarDarkF2");
        var tab = (TabItem)window.FindName("ModelTab"); tab.IsSelected = true;
        if (!tab.IsSelected) throw new Exception("Model tab selection was overridden");
        window.Close();
        Console.WriteLine("Main window XAML, real database navigation and model tab selection PASS (no interactive window shown).");
        app.Shutdown(); Console.WriteLine("Model preview file/render smoke PASS; visual game comparison pending.");
        return 0;
    }

    private static void CheckDecoder()
    {
        foreach (int samples in new[] { 1, 8 })
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("FIG" + samples));
            foreach (int n in new[] { 1, 1, 3, 3, 3, 0, 0, 19, 1 }) writer.Write(n);
            writer.Write(new byte[40 * samples]);
            for (int axis = 0; axis < 3; axis++)
                for (int sample = 0; sample < samples; sample++)
                    for (int lane = 0; lane < 4; lane++)
                        writer.Write((axis == 0 && lane == 1 || axis == 1 && lane == 2 ? 1f : 0f) +
                            ((sample & (1 << axis)) != 0 ? (axis + 1) * 10f : 0f));
            writer.Write(new byte[64]);
            foreach (float n in new[] { 0f, 0f, 1f, 0f, 0f, 1f }) writer.Write(n);
            long triangleOffset = stream.Position;
            foreach (ushort n in new ushort[] { 0, 1, 2, 0, 0, 0, 1, 1, 1, 2, 2, 2 }) writer.Write(n);
            byte[] bytes = stream.ToArray();
            var mesh = FigurePreviewDecoder.Decode("fixture", bytes, new(0.2f, 0.3f, 0.85f));
            var expected = samples == 1 ? new System.Numerics.Vector3(1, 0, 0) : new System.Numerics.Vector3(3, 6, 25.5f);
            if (System.Numerics.Vector3.Distance(mesh.Positions[1], expected) > 0.0001f || mesh.Indices.Length != 3) throw new Exception("Packed morph decoding failed");
            try { FigurePreviewDecoder.Decode("truncated", bytes[..^1], new()); throw new Exception("Truncated accepted"); } catch (InvalidDataException) { }
            bytes[triangleOffset] = 3;
            try { FigurePreviewDecoder.Decode("bad-index", bytes, new()); throw new Exception("Bad index accepted"); } catch (InvalidDataException) { }
        }
        byte[] bones = new byte[96];
        for (int i = 0; i < 8; i++)
            for (int axis = 0; axis < 3; axis++) BitConverter.GetBytes((i & (1 << axis)) != 0 ? (axis + 1) * 10f : 0f).CopyTo(bones, i * 12 + axis * 4);
        if (System.Numerics.Vector3.Distance(FigurePreviewDecoder.Bone(bones, new(0.2f, 0.3f, 0.85f)), new(2, 6, 25.5f)) > 0.0001f) throw new Exception("Bone morph failed");
    }
}
