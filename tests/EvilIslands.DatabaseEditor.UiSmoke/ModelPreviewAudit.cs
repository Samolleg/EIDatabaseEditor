using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ModelPreviewAudit
{
    public static int Gallery(string database, string audit, string output, string[]? selectedNames = null)
    {
        Directory.CreateDirectory(output);
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var weaponRepresentatives = records.Where(r => r.Table == "units.prototypes").Select(r =>
        {
            string weapon = StringListEditorCodec.AnalyzeElement(r.Text(33), "Оружие").PrimaryValue;
            string shapeName = weapon.Split('.')[0];
            return (Record: r, Shape: records.FirstOrDefault(s => s.Table == "items.weapons" && s.Name == shapeName));
        }).Where(p => p.Shape is not null && p.Shape.Integer(2) >= 9)
            .GroupBy(p => p.Shape!.Integer(2)).Select(g => g.First().Record.Name);
        var names = selectedNames ?? File.ReadLines(audit).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(r => r.GetProperty("Error").ValueKind == JsonValueKind.Null)
            .GroupBy(r => r.GetProperty("Model").GetString()).Select(g => g.First().GetProperty("Name").GetString()!)
            .Concat(new[] { "ZombieMaleF12", "ZombieFemaleF12", "GZOrcMaleF6", "GZOrcFemaleF5", "SkeletonWarriorF10", "Human Hero Hadagan1", "Human Mercenary Hadagan Thief", "ArmoredTurtle90" })
            .Concat(weaponRepresentatives).Distinct().ToArray();
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        foreach (string name in names)
        {
            var record = records.First(r => r.Table == "units.prototypes" && r.Name == name);
            var scene = UnitModelPreview.LoadAsync(database, record, records).GetAwaiter().GetResult();
            var view = new EvilIslands.DatabaseEditor.App.UnitModelPreviewView { Width = 800, Height = 700 };
            view.Measure(new System.Windows.Size(800, 700)); view.Arrange(new System.Windows.Rect(0, 0, 800, 700));
            view.ShowScene(scene); view.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
            File.WriteAllLines(Path.Combine(output, name + ".txt"), scene.Sources.Concat(scene.Notes));
            Console.WriteLine($"Rendered {name}: {scene.Model}");
        }
        app.Shutdown();
        return 0;
    }

    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(database));
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var targets = records.Where(r => r.Table is "units.prototypes" or "units.npcs").ToArray();
        using var writer = new StreamWriter(Path.Combine(output, "audit.jsonl")) { AutoFlush = true };
        int completed = 0;
        Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (r, token) =>
        {
            object result;
            try
            {
                var scene = await UnitModelPreview.LoadAsync(database, r, records, token);
                if (scene.Meshes.Count == 0 || scene.Meshes.Any(m => m.Indices.Any(i => i < 0 || i >= m.Positions.Length)))
                    throw new InvalidDataException("Invalid geometry");
                result = new { r.Table, r.Name, scene.Model, Meshes = scene.Meshes.Count, Textures = scene.Textures.Count,
                    MissingSlots = scene.Meshes.Select(m => m.TextureSlot).Distinct().Where(s => !scene.Textures.ContainsKey(s)).ToArray(), scene.Sources, scene.Notes, Error = (string?)null };
            }
            catch (Exception ex) { result = new { r.Table, r.Name, Error = ex.Message }; }
            lock (writer)
            {
                writer.WriteLine(JsonSerializer.Serialize(result));
                completed++;
                if (completed % 25 == 0 || completed == targets.Length) Console.WriteLine($"Audited {completed}/{targets.Length}: {r.Name}");
            }
        }).GetAwaiter().GetResult();
        if (!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database)))) throw new Exception("Input database changed");
        Console.WriteLine("Audit complete; database SHA-256 unchanged. See per-record errors/notes in audit.jsonl.");
        return 0;
    }
}
