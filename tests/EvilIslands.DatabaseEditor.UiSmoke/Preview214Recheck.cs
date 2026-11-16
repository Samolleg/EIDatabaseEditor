using System.IO;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class Preview214Recheck
{
    internal static int ExpandOptions(string database, string previousItems, string output)
    {
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var seen = new HashSet<(string Table, string Name, string? Choice)>();
        using var writer = new StreamWriter(output) { AutoFlush = true };
        foreach (string line in File.ReadLines(previousItems)) {
            var old = JsonSerializer.Deserialize<JsonElement>(line);
            seen.Add((old.GetProperty("Table").GetString()!, old.GetProperty("Name").GetString()!, old.GetProperty("Choice").GetString()));
            writer.WriteLine(line);
        }
        int added = 0;
        foreach (var record in records.Where(r => ItemModelPreview.Supports(r.Table)))
            foreach (var choice in ItemModelPreview.Options(record, records).Choices) {
                if (!seen.Add((record.Table, record.Name, choice.Name))) continue;
                try {
                    var scene = ItemModelPreview.LoadAsync(database, record, records, choice: choice).GetAwaiter().GetResult();
                    if (scene.Meshes.Count == 0 || scene.Meshes.Any(m => m.Indices.Any(i => i < 0 || i >= m.Positions.Length))) throw new InvalidDataException("Invalid geometry");
                    writer.WriteLine(JsonSerializer.Serialize(new { record.Table, record.Name, Choice = choice.Name, scene.Model,
                        Meshes = scene.Meshes.Count, Textures = scene.Textures.Count, MissingSlots = scene.Meshes.Select(m => m.TextureSlot).Distinct().Where(s => !scene.Textures.ContainsKey(s)).ToArray(), scene.Sources, scene.Notes, Error = (string?)null }));
                }
                catch (Exception e) { writer.WriteLine(JsonSerializer.Serialize(new { record.Table, record.Name, Choice = choice.Name, Error = e.Message })); }
                if (++added % 100 == 0) Console.WriteLine("Additional material choices: " + added);
            }
        Console.WriteLine("PASS enumerated all item choices; added " + added);
        return 0;
    }
    internal static int Run(string database, string previous, string output)
    {
        Directory.CreateDirectory(output);
        var db = DatabaseDocument.Open(database);
        var records = db.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        foreach (string category in new[] { "items", "units" }) {
            using var writer = new StreamWriter(Path.Combine(output, category + ".jsonl")) { AutoFlush = true };
            int checkedCount = 0, retained = 0;
            foreach (string line in File.ReadLines(Path.Combine(previous, category, "audit.jsonl"))) {
                var old = JsonSerializer.Deserialize<JsonElement>(line);
                bool affected = old.GetProperty("Error").ValueKind != JsonValueKind.Null ||
                    old.TryGetProperty("MissingSlots", out var slots) && slots.GetArrayLength() > 0;
                if (!affected) { writer.WriteLine(line); retained++; continue; }
                string table = old.GetProperty("Table").GetString()!, name = old.GetProperty("Name").GetString()!;
                var record = records.First(r => r.Table == table && r.Name == name);
                ModelRecord? choice = null;
                try {
                    if (category == "items") {
                        string? oldChoice = old.GetProperty("Choice").GetString();
                        choice = ItemModelPreview.Options(record, records).Choices.FirstOrDefault(c => c.Name == oldChoice) ??
                            ItemModelPreview.DefaultChoiceAsync(database, record, records).GetAwaiter().GetResult();
                    }
                    var scene = (category == "items" ? ItemModelPreview.LoadAsync(database, record, records, choice: choice) :
                        UnitModelPreview.LoadAsync(database, record, records)).GetAwaiter().GetResult();
                    writer.WriteLine(JsonSerializer.Serialize(new { Table = table, Name = name, Choice = choice?.Name, scene.Model,
                        Meshes = scene.Meshes.Count, Textures = scene.Textures.Count, MissingSlots = scene.Meshes.Select(m => m.TextureSlot).Distinct().Where(s => !scene.Textures.ContainsKey(s)).ToArray(), scene.Sources, scene.Notes, Error = (string?)null }));
                }
                catch (Exception e) { writer.WriteLine(JsonSerializer.Serialize(new { Table = table, Name = name, Choice = choice?.Name, Error = e.Message })); }
                checkedCount++;
            }
            Console.WriteLine($"{category}: rechecked {checkedCount}, retained unchanged successful cases {retained}");
        }
        return 0;
    }
}
