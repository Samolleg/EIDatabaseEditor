using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakAudit215Regression
{
    internal static int Index(string database,string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            string input=FileVersionInfo.GetVersionInfo(typeof(MainWindowViewModel).Assembly.Location).FileMajorPart<215?Path.GetDirectoryName(database)!:database;
            var clock=Stopwatch.StartNew();var first=AnimationGlobalClipIndex.OpenAsync(input).GetAwaiter().GetResult();double coldMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();var second=AnimationGlobalClipIndex.OpenAsync(input).GetAwaiter().GetResult();double warmMs=clock.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(output,"animation-index.json"),JsonSerializer.Serialize(new { coldMs,warmMs,hits=first.Hits.Count,issues=first.Issues,reused=ReferenceEquals(first,second) }));
            Console.WriteLine($"Animation index: {coldMs:F1} ms; repeat {warmMs:F1} ms; {first.Hits.Count} hits; {first.Issues.Count} issues");return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
    internal static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(database, output);
        task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default);
        Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string database, string output)
    {
        var timings = new Dictionary<string, double>();
        async Task<T> Measure<T>(string name, Func<Task<T>> action)
        {
            Console.WriteLine("START " + name); var watch = Stopwatch.StartNew();
            T value = await action(); timings[name] = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"DONE {name}: {watch.Elapsed.TotalSeconds:F4} s");
            File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(timings));
            return value;
        }
        using var vm = new MainWindowViewModel();
        await Measure("workspace", async () => { await vm.OpenPathAsync(database); return vm.RecordCount; });
        if (vm.RecordCount == 0) throw new Exception(vm.NotificationMessage);
        var first = await Measure("snapshot-first", () => Task.FromResult(vm.ModuleWorkspace.Snapshot()));
        await Measure("snapshot-repeat-10", () => { for (int i=0;i<10;i++) _ = vm.ModuleWorkspace.Snapshot(); return Task.FromResult(0); });
        var maps = await Measure("models-catalog", () => MapObjectResourceCatalog.DiscoverAsync(database));
        Console.WriteLine($"Models: {maps.Count}, records: {first.Records.Length}");
        string soundPath = Path.Combine(Path.GetDirectoryName(database)!, "speech.pak");
        var sound = (await SoundArchiveCatalog.OpenAsync([soundPath])).Resources.First();
        byte[] audio = await Measure("sound-first", () => SoundArchiveCatalog.ReadResourceAsync(sound));
        byte[] again = await Measure("sound-repeat", () => SoundArchiveCatalog.ReadResourceAsync(sound));
        if (!audio.SequenceEqual(again)) throw new Exception("Audio changed between reads");
        Console.WriteLine("PASS PAK workspace, snapshot, model discovery and audio");
    }
}
