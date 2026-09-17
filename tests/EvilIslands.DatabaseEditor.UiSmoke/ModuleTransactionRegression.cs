using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class ModuleTransactionRegression
{
    private sealed class Codec : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot d) => d.State.Length == 1 ? Result.Ok : new(ResultStatus.Failed, "state");
        public PreparedDocument Prepare(DocumentSnapshot d, string kind) => new(Result.Ok, System.Text.Encoding.UTF8.GetBytes(d.Text).ToImmutableArray());
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static int Run(string source)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(source); task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS module atomic DB/text/document transaction, failure rollback, shared undo/redo, stale generation, recovery without codec"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "module-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string copy = Path.Combine(root, "database.res"); File.Copy(source, copy);
        File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "texts.res"), Path.Combine(root, "texts.res"));
        using var vm = new MainWindowViewModel(); await vm.OpenPathAsync(copy);
        var reader = vm.ModuleWorkspace; var snapshot = reader.Snapshot();
        var record = snapshot.Records.First(r => r.Fields.Any(f => f.Value.Kind == "String" && f.Value.Editable) && r.Texts.Length > 0);
        var field = record.Fields.First(f => f.Value.Kind == "String" && f.Value.Editable);
        var text = record.Texts[0];
        vm.ModuleDocuments.RegisterCodec("fixture", "text", 1, new Codec());
        var document = vm.ModuleDocuments.Open(new("fixture", "text", 1, "fixture", "original", [1]));
        var address = new FieldAddress(record.Reference, field.FieldId, field.Occurrence, []);
        ChangeSet Change(bool bad) => new(Guid.NewGuid(), reader.Snapshot().Stamp, "mixed", [
            new SetField(address, field.Value, field.Value with { ExactValue = field.Value.ExactValue + "_module" }),
            new UpdateDocument(document.DocumentId, vm.ModuleDocuments.Get(document.DocumentId)!.Revision, "edited", [2]),
            new SetText(new(text.ResourceKey, "title"), text.Title, bad ? "bad\nline" : text.Title + " module")]);
        var invalid = vm.ApplyModuleTransaction(Change(true)); Require(!invalid.Succeeded, "Invalid last operation accepted");
        Require(vm.ModuleDocuments.Get(document.DocumentId)!.Text == "original", "Partial document mutation");
        Require(reader.Snapshot().Records.Single(r => r.Reference == record.Reference).Fields.Single(f => f.FieldId == field.FieldId && f.Occurrence == field.Occurrence).Value.ExactValue == field.Value.ExactValue, "Partial field mutation");
        var ok = vm.ApplyModuleTransaction(Change(false)); Require(ok.Succeeded, ok.Message);
        Require(vm.ModuleDocuments.Get(document.DocumentId)!.Text == "edited", "Document not committed");
        vm.UndoCommand.Execute(null); Require(vm.ModuleDocuments.Get(document.DocumentId)!.Text == "original", "Shared Undo document failed");
        Require(reader.Snapshot().Records.Single(r => r.Reference == record.Reference).Texts.Any(t => t.ResourceKey == text.ResourceKey && t.Title == text.Title), "Shared Undo text failed");
        vm.RedoCommand.Execute(null); Require(vm.ModuleDocuments.Get(document.DocumentId)!.Text == "edited", "Redo failed");
        var save = vm.SaveModuleFiles([new(document.DocumentId, Path.Combine(root, "document.txt"), "", System.Text.Encoding.UTF8.GetBytes("edited"))], Path.Combine(root, "journals"));
        Require(save.Complete, string.Join(";", save.Files.Select(f => f.Status + ":" + f.Message)));
        Require(reader.Navigate(record.Reference).Status == ResultStatus.Conflict, "Save reload retained stale generation");
        Require(vm.ApplyModuleHistory(false).Succeeded && vm.ModuleDocuments.Get(document.DocumentId)!.Text == "original", "Undo after save failed");
        Require(vm.TextResources.Any(t => t.CoreResource.Name == text.ResourceKey && t.CoreResource.Title == text.Title), "Text Undo after save failed");
        Require(vm.ApplyModuleHistory(true).Succeeded && vm.ModuleDocuments.Get(document.DocumentId)!.Text == "edited", "Redo after save failed");
        var state = vm.ModuleDocuments.CaptureRecovery();
        var empty = new global::DatabaseEditor.Modules.Host.DocumentStore(); Require(empty.RestoreRecovery(state).Succeeded, "Recovery requires installed module");
        Require(empty.Get(document.DocumentId)?.Text == "edited", "Unknown codec data lost");
        var recovery = new EvilIslands.DatabaseEditor.App.Services.RecoverySnapshotStore(Path.Combine(root, "recovery"));
        var draft = recovery.Prepare(copy);
        var pair = await vm.SaveRecoveryCopyPathAsync(draft.DatabasePath);
        Require(pair is not null, "Recovery pair not prepared");
        var completed = recovery.Complete(copy, draft, pair!, null, state);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(copy).AddSeconds(5));
        var restarted = new EvilIslands.DatabaseEditor.App.Services.RecoverySnapshotStore(Path.Combine(root, "recovery"));
        Require(restarted.FindLatest(copy) is not null, "Partial-save source timestamp hid module recovery");
        using var restoredVm = new MainWindowViewModel(); await restoredVm.OpenPathAsync(completed.DatabasePath);
        Require(restoredVm.ModuleDocuments.RestoreRecovery(File.ReadAllBytes(completed.ModulesPath!)).Succeeded, "Unknown module restore failed");
        Require(restoredVm.TextResources.Any(t => t.CoreResource.Name == text.ResourceKey && t.CoreResource.Title == text.Title + " module"), "Recovery text snapshot inconsistent");
        Require(restoredVm.ModuleDocuments.Get(document.DocumentId)!.Text == "edited", "Recovery last input lost");
        vm.UndoCommand.Execute(null);
        await vm.OpenPathAsync(source);
        Require(reader.Navigate(record.Reference).Status == ResultStatus.Conflict, "Old-generation navigation accepted");
        await CheckStructural(source);
    }

    private static async Task CheckStructural(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "module-structural-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string copy = Path.Combine(root, "database.res"); File.Copy(source, copy);
        File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "texts.res"), Path.Combine(root, "texts.res"));
        using var vm = new MainWindowViewModel(); await vm.OpenPathAsync(copy);
        var table = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "items.materials");
        int count = table.Records.Count;
        vm.SelectTableCommand.Execute(table); vm.SelectedRecord = table.Records[0];
        vm.AppendRecordCommand.Execute(null);
        Require(table.Records.Count == count + 1, "Append fixture failed");
        var added = table.Records[^1];
        added.GetCell(added.IdentityFieldId)!.EditorText = "module structural fixture";
        vm.AddMissingTextsCommand.Execute(null);
        var keys = added.RelatedTexts.Select(t => t.ResourceName).ToArray();
        Require(keys.Length > 0, "Missing texts fixture failed");
        SaveReport Save()
        {
            var report = vm.SaveModuleFiles([], Path.Combine(root, "journals"));
            Require(report.Complete, string.Join(";", report.Files.Select(f => f.Status + ":" + f.Message)));
            return report;
        }
        Save();
        Require(!vm.HasChanges, "Save did not checkpoint structural dirty state");
        // Undo the combined rename/autotext operation, then the row creation.
        for (int i = 0; i < 2; i++) Require(vm.ApplyModuleHistory(false).Succeeded, "Structural Undo failed " + i);
        Require(vm.HasChanges, "Undo persisted additions was not dirty");
        Require(vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == table.Key).Records.Count == count, "Undo row count");
        Save(); // Save while all structural frames are in Redo, then restore detached identities.
        Require(keys.All(k => !vm.TextResources.Any(t => t.ResourceName == k)), "Undone texts persisted");
        for (int i = 0; i < 2; i++) Require(vm.ApplyModuleHistory(true).Succeeded, "Structural Redo failed " + i);
        Require(vm.HasChanges, "Redo additions was not dirty");
        Require(vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == table.Key).Records.Count == count + 1, "Redo row count");
        Save();
        Require(keys.All(k => vm.TextResources.Any(t => t.ResourceName == k)), "Restored texts lost");
        Require(vm.ApplyModuleHistory(false).Succeeded && vm.HasChanges, "Text Undo after second save");
        Require(vm.ApplyModuleHistory(true).Succeeded && !vm.HasChanges, "Text Redo to checkpoint stays dirty");
    }
}

