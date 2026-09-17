using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class MobEquipmentRegression
{
    public static int Run(string database, string mob, string output)
    {
        Directory.CreateDirectory(output);
        var before = SHA256.HashData(File.ReadAllBytes(mob));
        var dbBefore = SHA256.HashData(File.ReadAllBytes(database));
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var appearance = MobAppearance.Read(mob).Single(a => a.Id == 4745);
        if (appearance.ImportEquipment != false || appearance.Primary != "default0") throw new Exception("Reproduction input differs");
        var unit = records.Single(r => r.Table == "units.prototypes" && r.Name == appearance.Prototype);
        UnitModelScene Load(MobAppearance? a) => UnitModelPreview.LoadAsync(database, unit, records, default, a).GetAwaiter().GetResult();
        var scene = Load(appearance);
        var baseline = Load(null);
        if (scene.Model != appearance.Model || scene.Textures.Count == 0 || scene.Sources.Any(s => s.StartsWith("default0")) ||
            !scene.Meshes.Select(m => m.Name).SequenceEqual(baseline.Meshes.Select(m => m.Name))) throw new Exception("Prototype equipment not composed");
        foreach (var pair in baseline.Textures)
            if (!scene.Textures[pair.Key].Bgra.SequenceEqual(pair.Value.Bgra)) throw new Exception("Prototype texture changed");
        var imported = Load(appearance with { ImportEquipment = true });
        var unknown = Load(appearance with { ImportEquipment = null });
        if (!imported.Sources.Any(s => s.StartsWith("default0.mmp")) || !unknown.Textures[1].Bgra.SequenceEqual(imported.Textures[1].Bgra))
            throw new Exception("Explicit MOB appearance overwritten");
        var morph = Load(appearance with { Morph = new(0.1f, 0.2f, 0.9f) });
        if (morph.Meshes[0].Positions.SequenceEqual(scene.Meshes[0].Positions)) throw new Exception("MOB morph discarded");
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var view = new EvilIslands.DatabaseEditor.App.UnitModelPreviewView { Width = 800, Height = 700 };
        view.Measure(new Size(800, 700)); view.Arrange(new Rect(0, 0, 800, 700)); view.ShowScene(scene); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(800, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "mob4745.png"))) encoder.Save(stream);
        File.WriteAllLines(Path.Combine(output, "sources.txt"), scene.Sources.Concat(scene.Notes));
        app.Shutdown();
        if (!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(mob))) || !dbBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))))
            throw new Exception("Input changed");
        Console.WriteLine($"PASS MOB4745: {scene.Meshes.Count} meshes, {scene.Textures.Count} textures; DB layers, explicit/unknown MOB preservation, morph retained, inputs unchanged.");
        return 0;
    }
}
