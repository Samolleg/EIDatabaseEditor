using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ModuleCrashRegression
{
    private sealed class Codec : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot document) => Result.Ok;
        public PreparedDocument Prepare(DocumentSnapshot document, string kind) => new(Result.Ok, []);
    }
    private sealed record Expected(string Table, int Row, int Field, string Value, string TextKey, string Title, Guid Document);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Process Start(string phase, string root)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ModuleCrashRegression).Assembly.Location);
        start.ArgumentList.Add("--module-crash-child"); start.ArgumentList.Add(phase); start.ArgumentList.Add(root);
        return Process.Start(start)!;
    }
    internal static int Run(string source)
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "module-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            File.Copy(source, Path.Combine(root, "database.res"));
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "texts.res"), Path.Combine(root, "texts.res"));
            using (var writer = Start("write", root))
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "ready.json")) && !writer.HasExited && watch.Elapsed < TimeSpan.FromSeconds(40)) Thread.Sleep(50);
                if (!File.Exists(Path.Combine(root, "ready.json")))
                {
                    if (!writer.HasExited) writer.Kill(true);
                    writer.WaitForExit(); throw new InvalidOperationException("Recovery writer failed: " + writer.StandardError.ReadToEnd());
                }
                writer.Kill(true); writer.WaitForExit(); // No Closing/Dispose/autosave-on-exit code runs.
            }
            using var reader = Start("read", root);
            if (!reader.WaitForExit(40000)) { reader.Kill(true); throw new TimeoutException("Recovery reader"); }
            Require(reader.ExitCode == 0, reader.StandardError.ReadToEnd());
            Console.WriteLine("PASS killed process: fresh process restores DB, texts and last module input without module codec");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    internal static int Child(string phase, string root)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Work(phase, root); task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Work(string phase, string root)
    {
        string source = Path.Combine(root, "database.res");
        var store = new RecoverySnapshotStore(Path.Combine(root, "recovery"));
        using var vm = new MainWindowViewModel();
        if (phase == "write")
        {
            await vm.OpenPathAsync(source);
            var snapshot = vm.ModuleWorkspace.Snapshot();
            var row = snapshot.Records.First(r => r.Reference.TableKey == "items.materials" && r.Texts.Length > 0);
            var field = row.Fields.First(f => f.Value.Editable && f.Value.Kind == "String");
            var text = row.Texts[0];
            vm.ModuleDocuments.RegisterCodec("crash-fixture", "text", 1, new Codec());
            var document = vm.ModuleDocuments.Open(new("crash-fixture", "text", 1, "fixture", "before", []));
            string value = field.Value.ExactValue + "_crash";
            var applied = vm.ApplyModuleTransaction(new(Guid.NewGuid(), snapshot.Stamp, "crash fixture", [
                new SetField(new(row.Reference, field.FieldId, field.Occurrence, []), field.Value, field.Value with { ExactValue = value }),
                new SetText(new(text.ResourceKey, "title"), text.Title, "Recovery title"),
                new UpdateDocument(document.DocumentId, vm.ModuleDocuments.Get(document.DocumentId)!.Revision, "last keystroke", [7, 8, 9])]));
            Require(applied.Succeeded, applied.Message);
            var draft = store.Prepare(source);
            var pair = await vm.SaveRecoveryCopyPathAsync(draft.DatabasePath);
            Require(pair is not null, "DB recovery not prepared");
            store.Complete(source, draft, pair!, null, vm.ModuleDocuments.CaptureRecovery());
            var original = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == row.Reference.TableKey).Records.First(r => r.GetCell(field.FieldId)?.EditorText == value);
            File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new Expected(row.Reference.TableKey, original.SourceRecordIndex, field.FieldId, value, text.ResourceKey, "Recovery title", document.DocumentId)));
            await Task.Delay(Timeout.Infinite); // Parent kills this process after the durable snapshot marker.
        }
        else
        {
            var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(root, "ready.json")))!;
            var recovery = store.FindLatest(source) ?? throw new InvalidOperationException("Snapshot not found after crash");
            await vm.OpenPathAsync(recovery.DatabasePath);
            Require(vm.ModuleDocuments.RestoreRecovery(File.ReadAllBytes(recovery.ModulesPath!)).Succeeded, "Module restore without codec");
            Require(vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == expected.Table).Records.Single(r => r.SourceRecordIndex == expected.Row).GetCell(expected.Field)!.EditorText == expected.Value, "DB value lost");
            Require(vm.TextResources.Single(t => t.ResourceName == expected.TextKey).Title == expected.Title, "Text value lost");
            Require(vm.ModuleDocuments.Get(expected.Document) is { Text: "last keystroke", IsDirty: true } d && d.State.SequenceEqual(new byte[] { 7, 8, 9 }), "Last module input lost");
        }
    }
}
