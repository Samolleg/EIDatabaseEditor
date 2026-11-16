using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakPerformanceRegression
{
    internal static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var results = new Dictionary<string, object>();
        T Measure<T>(string name, Func<T> action)
        {
            Console.WriteLine("START " + name);
            var clock = Stopwatch.StartNew();
            T result = action();
            results[name] = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"DONE {name}: {clock.Elapsed.TotalSeconds:F3} s");
            File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return result;
        }
        try
        {
            var document = Measure("database", () => DatabaseDocument.Open(database));
            results["records"] = document.Databases.SelectMany(g => g.Tables).Sum(t => t.Records.Count);
            string folder = Path.GetDirectoryName(database)!;
            var texts = Measure("texts", () => TextsDocument.Open(Path.Combine(folder, "texts.pak")));
            results["textsCount"] = texts.Resources.Count;
            var sounds = Measure("speech", () => SoundArchiveCatalog.Open([Path.Combine(folder, "speech.pak")]));
            results["soundsCount"] = sounds.Resources.Count;
            var sources = Measure("textureSources", () => TextureSources.FromDatabaseFolderAsync(database).GetAwaiter().GetResult());
            results["sourceArchives"] = sources.Archives;
            // A bounded baseline avoids spending minutes on thousands of process launches.
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                var catalog = Measure("textureCatalog", () => TextureResourceCatalog.OpenAsync(sources, cancel.Token).GetAwaiter().GetResult());
                results["catalogCompleted"] = true;
            }
            catch (OperationCanceledException)
            {
                results["catalogCompleted"] = false;
                results["textureCatalogLowerBoundMs"] = 60000;
                Console.WriteLine("Texture catalog exceeded 60 seconds; cancelled at next checkpoint.");
            }
            File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
