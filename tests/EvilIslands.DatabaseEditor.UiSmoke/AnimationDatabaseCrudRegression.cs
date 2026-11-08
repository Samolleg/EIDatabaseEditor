using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationDatabaseCrudRegression
{
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent(); typeof(Application).GetField("_startupUri",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(app,null); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult(); Check(app.Windows.Cast<Window>().All(window=>!window.IsVisible), "Headless ADB test opened a visible window"); app.Shutdown(); return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
    private static async Task Verify(string database, string output)
    {
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database, records.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), records);
        await VerifyComparisonSwitch(scene);
        await AnimationDatabaseRegression.Verify(scene, output);
        var source = scene.AnimationDatabase!;
        byte[] original = File.ReadAllBytes(source.ArchivePath);
        var draft = source.CreateDraft();
        byte[] before = draft.ExportBytes();
        bool blocked = false;
        try { draft.RemoveAt(1); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && before.SequenceEqual(draft.ExportBytes()), "middle deletion must preserve all indices and bytes");
        before = draft.ExportBytes(); draft.RemoveAt(draft.Count - 1);
        Check(draft.ExportBytes().AsSpan(8).SequenceEqual(before.AsSpan(8, before.Length - 88 - 8)), "last delete changed survivors");
        while (draft.Count > 0) draft.RemoveAt(draft.Count - 1);
        var empty = draft.ExportBytes();
        try { draft.AppendConfigured(null, "bad", 0, 0, 101, 30, 0, new uint[2], new uint[4], 0, 0, new uint[4]); throw new Exception("invalid append accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check(empty.SequenceEqual(draft.ExportBytes()), "blank append failure not atomic");
        try { draft.RemoveAt(0); throw new Exception("invalid delete accepted"); } catch (ArgumentOutOfRangeException) { }
        string copy = Path.Combine(output, "crud-copy.res"); File.WriteAllBytes(copy, original);
        var writable = (await AnimationDatabaseSource.OpenAsync(copy, null, scene.Model))!;
        await writable.SaveAsync(draft);
        var dialog = new AnimationDatabaseDialog(scene with { AnimationDatabase = writable }, scene.Animations!);
        try
        {
            await dialog.ClipLengthTask;
            var list = Field<DataGrid>(dialog, "_list");
            var template = Field<ComboBox>(dialog, "_template");
            Check(template.SelectedIndex == 0 && template.Items.Count == 1, "empty ADB requires template");
            string name = Field<TextBox>(dialog, "_id").Text;
            int frames = (await scene.Animations!.LoadAsync(name)).FrameCount;
            var primary = Field<Button>(dialog, "_applyButton");
            Check(primary.IsEnabled && Equals(primary.Content, "Добавить в ADB"), "Only ANM primary action incorrect");
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(dialog.Draft.Count == 1 && writable.Database.Count == 0, "UI blank add failed/leaked");
            var entry = dialog.Draft.Entries[0];
            Check(entry.Name == name && entry.Length == frames && entry.Probability == 100 && entry.Flags == 0x40000 && entry.Flags2 == 0 && entry.Speed == 0, "UI default fields/ANM length");
            Check(Equals(primary.Content, "Применить изменения"), "primary action not updated after registration");
            Field<TextBox>(dialog, "_probability").Text = "99";
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(dialog.Draft.Count == 1 && dialog.Draft.Entries[0].Probability == 99, "primary edit appended duplicate or lost parameters");
            dialog.UndoDraft();
            Field<TextBox>(dialog, "_search").Text = "__no_matching_animation__";
            Check(!primary.IsEnabled, "primary enabled without selected row");
            Field<TextBox>(dialog, "_search").Text = "";
            Check(primary.IsEnabled && Equals(primary.Content, "Применить изменения"), "primary not restored after filter");
            VerifyFlagControls(dialog);
            template.SelectedIndex = 1; list.SelectedIndex = 1; await dialog.ClipLengthTask;
            Invoke(dialog, "Add"); Check(dialog.Draft.Count == 2, "optional template add failed");
            var two = dialog.Draft.ExportBytes();
            dialog.DeleteRecord(1); Check(dialog.Draft.Count == 1 && dialog.Draft.Entries[0].StoredIndex == 0, "UI tail delete failed");
            dialog.UndoDraft(); Check(two.SequenceEqual(dialog.Draft.ExportBytes()), "delete undo failed");
            dialog.DeleteRecord(1);
            await writable.SaveAsync(dialog.Draft);
            var reread = (await AnimationDatabaseSource.OpenAsync(copy, null, scene.Model))!;
            Check(reread.Database.Count == 1 && reread.Database.Entries[0].Name == name, "CRUD disk readback failed");
            dialog.DiscardDraft(); dialog.DeleteRecord(0); dialog.DiscardDraft();
            Check(dialog.Draft.Count == 1, "delete discard failed");
        }
        finally { dialog.DiscardDraft(); dialog.Close(); }
        await AnimationClipDeletionRegression.Verify(scene, writable, output);
        var oldArchive = ResArchiveDocument.Open(source.ArchivePath);
        var newArchive = ResArchiveDocument.Open(copy);
        foreach (var resource in ResFileManagerArchive.ReadCatalog(source.ArchivePath))
            if (!resource.Name.Equals(source.ResourceName, StringComparison.OrdinalIgnoreCase))
                Check(oldArchive.GetResourceBytes(resource.Name).Span.SequenceEqual(newArchive.GetResourceBytes(resource.Name).Span), "unrelated RES resource changed: " + resource.Name);
        Check(original.SequenceEqual(File.ReadAllBytes(source.ArchivePath)), "installed ADB archive modified");
        Console.WriteLine("ADB CRUD PASS: blank/optional template UI, ANM length, invalid append rollback, middle deletion blocked, last/all deletion, undo/discard, RES readback and unrelated bytes preserved. Source: " + source.Description);
    }

    private static void VerifyFlagControls(AnimationDatabaseDialog dialog)
    {
        var flags = Field<TextBox>(dialog, "_flags"); var events = Field<TextBox>(dialog, "_flags2");
        string before = flags.Text, beforeEvents = events.Text;
        var checks = Field<List<(CheckBox Box, TextBox Source, uint Bit)>>(dialog, "_flagChecks");
        flags.Text = "0x80180080"; events.Text = "0x80000000";
        checks.Single(x => x.Source == flags && x.Bit == 1).Box.IsChecked = true;
        checks.Single(x => x.Source == events && x.Bit == 0x10).Box.IsChecked = true;
        Check(flags.Text == "0x80180081" && events.Text == "0x80000010", "checkbox destroyed unrelated/unknown bits");
        var modifiers = Field<List<(ComboBox Box, uint Mask)>>(dialog, "_modifierChoices");
        modifiers[0].Box.SelectedIndex = 2;
        Check(flags.Text == "0x80980081", "Russian idle modifier incorrectly encoded");
        Field<ComboBox>(dialog, "_action").SelectedIndex = 1;
        Check(flags.Text == "0x80880081" && modifiers.Count == 2, "action-dependent controls or bit preservation failed");
        modifiers[0].Box.SelectedIndex = 3; modifiers[1].Box.SelectedIndex = 1;
        Check(flags.Text == "0x81C80081", "exclusive attack directions incorrectly encoded");
        Invoke(dialog, "Apply");
        var reread = AnimationDatabase.Parse(dialog.Draft.ExportBytes()).Entries[0];
        Check(reread.Flags == 0x81C80081 && reread.Flags2 == 0x80000010, "UI flags did not survive ADB serialization");
        dialog.UndoDraft();
        flags.Text = "0x3FC40000";
        Check(modifiers[0].Box.SelectedItem?.ToString()?.Contains("Неизвестное") == true, "unknown modifier hidden or overwritten");
        flags.Text = before; events.Text = beforeEvents;
        Check(checks.All(x => x.Box.IsChecked == false), "raw values not synchronized to checkboxes");
        Console.WriteLine("ADB Russian flags PASS: weapons/equipment, unknown bits, action modifiers, raw synchronization and serialized readback.");
    }

    private static async Task VerifyComparisonSwitch(UnitModelScene scene)
    {
        var source = scene.Animations!;
        string name = source.Names.First(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase));
        var original = await source.LoadAsync(name);
        var changed = new FigureAnimationEditor(source);
        var root = original.Tracks["hp"];
        await changed.SetPoseAsync(name, "hp", 7, root.Rotation(7), root.Position(7) + new System.Numerics.Vector3(.1f, 0, 0));
        var view = AnimationEditorDialog.CreateGeneratedComparisonView();
        await AnimationEditorDialog.DisplayGeneratedComparisonAsync(view, scene, source, name, true);
        view.PauseAnimation(); view.SetAnimationFrame(7);
        var visual = Field<System.Windows.Media.Media3D.ModelVisual3D>(view, "_visual");
        var geometry = visual.Content;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        view.DataContext = new object();
        Check(view.HasVisibleScene && ReferenceEquals(geometry, visual.Content) && view.ActiveAnimationName == name,
            "comparison Loaded/DataContext lifecycle cleared supplied scene");
        for (int i = 0; i < 6; i++)
        {
            var previous = Field<FigureAnimationClip>(view, "_clip");
            var task = AnimationEditorDialog.DisplayGeneratedComparisonAsync(view, scene, i % 2 == 0 ? changed.Preview() : source, name, true);
            Check(view.ActiveAnimationName == name && ReferenceEquals(geometry, visual.Content), "comparison exposed bind pose/recreated scene while loading");
            if (!task.IsCompleted) Check(ReferenceEquals(previous, Field<FigureAnimationClip>(view, "_clip")), "old pose removed before replacement ready");
            await task; view.PauseAnimation();
            Check(view.ActiveAnimationName == name && Math.Abs(view.AnimationFrame - 7) < .001, "comparison reset frame or clip");
            Check(ReferenceEquals(geometry, visual.Content), "comparison rebuilt geometry");
        }
        Console.WriteLine("COMPARISON SWITCH PASS: original/variant six switches, pending pose retained, same geometry and frame7, playback resumed.");
        await AnimationEditorDialog.DisplayGeneratedComparisonAsync(view, scene, changed.Preview(), name, true);
        view.PauseAnimation();
        var reference = new EvilIslands.DatabaseEditor.App.UnitModelPreviewView { AllowAnimationEditing = false };
        await AnimationEditorDialog.DisplayGeneratedComparisonAsync(reference, scene, source, name, true);
        reference.PauseAnimation();
        view.SetComparison(source, original, false, true);
        Check(view.GhostMeshCount > 0 && !view.ShowingOriginal, "ghost missing or candidate hidden");
        var ghost = Field<System.Windows.Media.Media3D.ModelVisual3D>(view, "_ghostVisual");
        var expected = Field<System.Windows.Media.Media3D.ModelVisual3D>(reference, "_visual");
        foreach (int frame in new[] { 0, 7, Math.Min(12, original.FrameCount - 1) })
        {
            view.SetAnimationFrame(frame); reference.SetAnimationFrame(frame);
            Check(ghost.Content.Bounds == expected.Content.Bounds, "original ghost does not match reference pose at frame " + frame);
            Check(ReferenceEquals(geometry, visual.Content), "ghost replaced candidate geometry");
        }
        double retainedFrame = view.AnimationFrame;
        view.SetComparison(source, original, false, false);
        Check(view.GhostMeshCount == 0 && view.AnimationFrame == retainedFrame, "ghost toggle reset frame or retained meshes");
        Console.WriteLine("COMPARISON GHOST PASS: synchronized original bounds at three frames, solid candidate retained, off preserves frame.");
    }
}
