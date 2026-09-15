using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ItemModelPreviewAudit
{
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(database));
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var targets = records.Where(r => ItemModelPreview.Supports(r.Table)).SelectMany(r =>
        {
            var choices = ItemModelPreview.Options(r, records).Choices;
            // Enumerate every concrete blueprint/material target as well as every DB row.
            return choices.Count > 0 && (r.Table == "items.loot_items" || ItemModelPreview.Options(r, records).Label == "Заклинание")
                ? choices.Select(c => (Record: r, Choice: (ModelRecord?)c))
                : new[] { (Record: r, Choice: (ModelRecord?)null) };
        }).ToArray();
        using var writer = new StreamWriter(Path.Combine(output, "audit.jsonl")) { AutoFlush = true };
        int completed = 0;
        Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (target, token) =>
        {
            var r = target.Record;
            object result;
            try
            {
                var choice = target.Choice ?? await ItemModelPreview.DefaultChoiceAsync(database, r, records, token);
                var scene = await ItemModelPreview.LoadAsync(database, r, records, token, choice);
                if (scene.Meshes.Count == 0 || scene.Meshes.Any(m => m.Indices.Any(i => i < 0 || i >= m.Positions.Length))) throw new InvalidDataException("Invalid geometry");
                result = new { r.Table, r.Name, Choice = choice?.Name, scene.Model, Meshes = scene.Meshes.Count, Textures = scene.Textures.Count,
                    MissingSlots = scene.Meshes.Select(m => m.TextureSlot).Distinct().Where(s => !scene.Textures.ContainsKey(s)).ToArray(), scene.Sources, scene.Notes, Error = (string?)null };
            }
            catch (Exception ex) { result = new { r.Table, r.Name, Choice = target.Choice?.Name, Error = ex.Message }; }
            lock (writer)
            {
                writer.WriteLine(JsonSerializer.Serialize(result)); completed++;
                if (completed % 50 == 0 || completed == targets.Length) Console.WriteLine($"Items {completed}/{targets.Length}");
            }
        }).GetAwaiter().GetResult();
        if (!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database)))) throw new Exception("Input database changed");
        Console.WriteLine("Audit complete; database SHA-256 unchanged. Errors/notes remain explicit in audit.jsonl.");
        return 0;
    }
}
