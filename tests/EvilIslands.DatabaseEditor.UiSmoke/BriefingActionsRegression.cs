using System.IO;
using System.Windows;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;
namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class BriefingActionsRegression
{
    internal static int Run(string source, string output)
    {
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(source, output);
        task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default);
        Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS briefing actions: neutral creation, edit lists, Undo/Redo before and after save, roundtrip, unrelated rows preserved"); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string source, string output)
    {
        void Require(bool b, string m) { if (!b) throw new InvalidOperationException(m); }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        string db = Path.Combine(output, "database.res");
        File.Copy(source, db, true);
        File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "texts.res"), Path.Combine(output, "texts.res"), true);
        var before = DatabaseDocument.Open(db).Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings");
        var original = before.Records.Select(r => string.Join("|", r.Fields.Select(f => f.EditorText))).ToArray();
        using var vm = new MainWindowViewModel(); await vm.OpenPathAsync(db);
        var table = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings");
        vm.SelectTableCommand.Execute(table);
        var reference = table.Records.First(r => r.IsTextOnlyBriefing);
        vm.SelectedRecord = reference;
        var row = vm.CreateSelectedBriefingRecord();
        string name = row.DisplayName;
        var window = new EvilIslands.DatabaseEditor.App.MainWindow(
            new EvilIslands.DatabaseEditor.App.Services.EditorPreferencesStore(Path.Combine(output, "settings.json")),
            new EvilIslands.DatabaseEditor.App.Services.RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var actions = window.BuildBriefingActionsDialog(row);
        var content = (FrameworkElement)actions.Content;
        content.Measure(new Size(780, 700)); content.Arrange(new Rect(0, 0, 780, 700)); content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(780, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "actions.png"))) png.Save(stream);
        actions.Close(); window.Close();
        Require(!table.Records.Contains(reference), "Duplicate reference");
        Require(row.CoreRecord.Fields.All(f => f.EditorText == (f.Id switch {0 => name, 1 or 2 or 10 => "0", _ => ""})), "Inherited actions");
        vm.UndoCommand.Execute(null); Require(table.Records.Contains(reference) && !table.Records.Contains(row), "Create undo");
        vm.RedoCommand.Execute(null); Require(!table.Records.Contains(reference) && table.Records.Contains(row), "Create redo");
        var donor = before.Records.First(r => r.Fields.Any(f => f.Id == 3 && f.EditorText.Length > 0));
        var sourceField = donor.Fields.Single(f => f.Id == 3);
        Require(sourceField.TryGetStringListValues(out var items), "Read items");
        Require(row.GetCell(3)!.TrySetStringListValues(items, out var error), error ?? "Give items");
        Require(row.GetCell(5)!.TrySetStringListValues(items, out error), error ?? "Take items");
        var saved = vm.SaveModuleFiles([], Path.Combine(output, "journals"));
        Require(saved.Complete, "Save failed");
        var after = DatabaseDocument.Open(db).Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings");
        Require(after.RecordCount == before.RecordCount + 1, "Count");
        Require(after.Records.Take(original.Length).Select(r => string.Join("|", r.Fields.Select(f => f.EditorText))).SequenceEqual(original), "Other briefings changed");
        var loaded = after.Records.Single(r => r.Name == name);
        Require(loaded.Fields.Single(f => f.Id == 3).EditorText == sourceField.EditorText && loaded.Fields.Single(f => f.Id == 5).EditorText == sourceField.EditorText, "Lists lost");
        vm.SelectTableCommand.Execute(vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings"));
        for (int i = 0; i < 3; i++) vm.UndoCommand.Execute(null);
        Require(vm.SelectedTable!.Records.Any(r => r.DisplayName == name && r.IsTextOnlyBriefing), "Postsave undo reference lost");
        for (int i = 0; i < 3; i++) vm.RedoCommand.Execute(null);
        Require(vm.SelectedTable!.Records.Count(r => r.DisplayName == name) == 1, "Postsave redo duplicate");
    }
}
