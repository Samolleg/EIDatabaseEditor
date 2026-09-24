using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationDatabaseRegression
{
    public static int VerifyActive(string databasePath, string configurationPath, string model)
    {
        var source = AnimationDatabaseSource.OpenAsync(databasePath, configurationPath, model).GetAwaiter().GetResult()
            ?? throw new Exception("Configured ADB not found: " + model);
        Check(source.Database.Model.Length > 0, "ADB model header empty");
        Check(source.Database.ExportBytes().SequenceEqual(AnimationDatabase.Parse(source.Database.ExportBytes()).ExportBytes()), "configured ADB roundtrip");
        Check(source.ArchivePath.Equals(databasePath, StringComparison.OrdinalIgnoreCase), "wrong first configured ADB owner");
        Console.WriteLine($"ADB SOURCE PASS: {source.Description}, {source.Database.Count} records, last index {source.Database.Count - 1}");
        return 0;
    }
    public static async Task Verify(UnitModelScene scene, string output)
    {
        var source = scene.AnimationDatabase ?? throw new Exception("ADB source missing for " + scene.Model);
        byte[] originalArchive = File.ReadAllBytes(source.ArchivePath);
        var original = source.Database.ExportBytes();
        var parsed = AnimationDatabase.Parse(original);
        Check(parsed.ExportBytes().SequenceEqual(original), "ADB no-op roundtrip changed bytes");
        Check(parsed.Count > 0 && parsed.Entries.Select(e => e.Index).SequenceEqual(Enumerable.Range(0, parsed.Count)), "ADB index order");
        int first = parsed.Entries.Select((e, i) => (e, i)).FirstOrDefault(pair => pair.e.Name.Length > 0 && pair.e.Probability == 100).i;
        if (parsed.Entries[first].Probability != 100) first = parsed.Entries.Select((e, i) => (e, i)).First(pair => pair.e.Name.Length > 0).i;
        var entry = parsed.Entries[first];
        uint originalProbability = entry.Probability;
        uint changedProbability = entry.Probability == 100 ? 99u : entry.Probability + 1;
        parsed.Update(first, entry.Name, entry.Flags, entry.Flags2, changedProbability, entry.Length, entry.Speed,
            entry.Hides, entry.Steps, entry.Hit, entry.SfxFrame, entry.SfxIndices);
        string id = "adbtest01";
        if (parsed.Entries.Any(e => e.Name.Equals(id, StringComparison.OrdinalIgnoreCase))) id = "adbtest02";
        int appended = parsed.AppendFrom(first, id, 30);
        var edited = AnimationDatabase.Parse(parsed.ExportBytes());
        Check(appended == parsed.Count - 1 && edited.Entries[appended].StoredIndex == appended, "new ADB index");
        Check(edited.Entries[first].Probability == changedProbability && edited.Entries[appended].Name == id && edited.Entries[appended].Length == 30, "ADB fields");
        Check((edited.Entries[appended].Flags2 & 0xF) == 0 && edited.Entries[appended].Steps.All(n => n == 0), "copied event flags are inconsistent with cleared event frames");
        Check(original.AsSpan(AnimationDatabase.HeaderSize + (first + 1) * AnimationDatabase.EntrySize,
            Math.Max(0, original.Length - AnimationDatabase.HeaderSize - (first + 1) * AnimationDatabase.EntrySize))
            .SequenceEqual(parsed.ExportBytes().AsSpan(AnimationDatabase.HeaderSize + (first + 1) * AnimationDatabase.EntrySize,
                Math.Max(0, original.Length - AnimationDatabase.HeaderSize - (first + 1) * AnimationDatabase.EntrySize))), "neighbor entries changed");
        bool rejected = false;
        try { parsed.AppendFrom(first, "id_longer_than_15_chars", 30); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "long ADB ID accepted");
        foreach (string baseName in new[] { "2hacidle01", "very-long-source-name", "name-with-hyphen" })
        {
            var names = parsed.Entries.Select(e => e.Name).ToList();
            for (int copy = 0; copy < 4; copy++)
            {
                string candidate = FigureAnimationEditor.SuggestCopyId(baseName, names);
                FigureAnimationEditor.ValidateNewId(candidate);
                var test = AnimationDatabase.Parse(original);
                test.AppendFrom(first, candidate, 30);
                names.Add(candidate);
            }
        }
        rejected = false;
        try { parsed.AppendFrom(first, entry.Name, 30); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "duplicate ADB ID accepted");
        int countBeforeInvalid = parsed.Count;
        rejected = false;
        try { parsed.AppendConfigured(first, "adbtest04", entry.Flags, entry.Flags2, 101, 20, entry.Speed,
            new uint[2], new uint[4], 0, 0, new uint[4]); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && parsed.Count == countBeforeInvalid, "invalid configured append was not rolled back");
        int configured = parsed.AppendConfigured(first, "adbtest04", entry.Flags, 0, 42, 20, entry.Speed,
            new uint[2], new uint[4], 0, 0, new uint[4]);
        Check(parsed.Entries[configured].Probability == 42 && parsed.Entries[configured].Length == 20, "configured append fields lost");

        string temp = Path.Combine(Path.GetTempPath(), "ei-adb-test-" + Guid.NewGuid().ToString("N") + ".res");
        File.WriteAllBytes(temp, originalArchive);
        var writable = await AnimationDatabaseSource.OpenAsync(temp, null, scene.Model) ?? throw new Exception("ADB source in copy missing");
        var discard = writable.CreateDraft();
        var unedited = writable.Database.Entries[first];
        discard.Update(first, unedited.Name, unedited.Flags, unedited.Flags2, changedProbability, unedited.Length, unedited.Speed,
            unedited.Hides, unedited.Steps, unedited.Hit, unedited.SfxFrame, unedited.SfxIndices);
        discard.AppendFrom(first, "discard01", 30);
        Check(writable.Database.Entries[first].Probability == unedited.Probability && writable.Database.Count == parsed.Count - 2,
            "discarded draft changed shared ADB state");
        Check((await AnimationDatabaseSource.OpenAsync(temp, null, scene.Model))!.Database.Count == writable.Database.Count,
            "discarded draft reached disk");
        var rejectedDraft = writable.CreateDraft();
        rejectedDraft.AppendFrom(first, "reject01", 30);
        string hidden = temp + ".unavailable";
        File.Move(temp, hidden);
        try { await writable.SaveAsync(rejectedDraft); throw new Exception("unavailable ADB write accepted"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { File.Move(hidden, temp); }
        Check(writable.Database.Count == parsed.Count - 2 && rejectedDraft.Count == writable.Database.Count + 1,
            "failed save published ADB state or discarded draft");
        var before = writable.Database.Entries[first];
        writable.Database.Update(first, before.Name, before.Flags, before.Flags2, changedProbability, before.Length, before.Speed,
            before.Hides, before.Steps, before.Hit, before.SfxFrame, before.SfxIndices);
        writable.Database.AppendFrom(first, id, 30);
        string? backup = await writable.SaveAsync();
        Check(backup is not null && File.ReadAllBytes(backup).SequenceEqual(originalArchive), "ADB backup differs");
        var reread = await AnimationDatabaseSource.OpenAsync(temp, null, scene.Model) ?? throw new Exception("Saved ADB missing");
        Check(reread.Database.ExportBytes().SequenceEqual(writable.Database.ExportBytes()), "ADB save readback differs");
        Check(await reread.SaveAsync() is null, "no-op ADB created backup");
        var concurrent = reread.Database.Entries[first];
        reread.Database.Update(first, concurrent.Name, concurrent.Flags, concurrent.Flags2,
            concurrent.Probability == 100 ? 98u : concurrent.Probability + 1,
            concurrent.Length, concurrent.Speed, concurrent.Hides, concurrent.Steps, concurrent.Hit, concurrent.SfxFrame, concurrent.SfxIndices);
        await reread.SaveAsync();
        writable.Database.AppendFrom(first, "adbtest03", 20);
        bool staleBlocked = false;
        try { await writable.SaveAsync(); } catch (IOException) { staleBlocked = true; }
        Check(staleBlocked, "stale ADB source overwritten");
        Check(File.ReadAllBytes(source.ArchivePath).SequenceEqual(originalArchive), "installed source changed");
        var modelPreview = new UnitModelPreviewView();
        modelPreview.ShowScene(scene);
        var adbButton = Descendants(modelPreview).OfType<Button>().SingleOrDefault(b => Equals(b.Content, "Игровой список ADB"));
        Check(adbButton is { Visibility: Visibility.Visible, IsEnabled: true }, "ADB action is not available on model tab");
        using (var dialog = new DialogScope(new AnimationDatabaseDialog(scene, scene.Animations!)))
        {
            var grid = Descendants(dialog.Window).OfType<DataGrid>().Single();
            Check(grid.Items.Count >= source.Database.Count, "ADB UI does not list all records");
            var dialogEntry = dialog.Window.Draft.Entries[first];
            dialog.Window.Draft.Update(first, dialogEntry.Name, dialogEntry.Flags, dialogEntry.Flags2, changedProbability,
                dialogEntry.Length, dialogEntry.Speed, dialogEntry.Hides, dialogEntry.Steps, dialogEntry.Hit, dialogEntry.SfxFrame, dialogEntry.SfxIndices);
            dialog.Window.Draft.AppendFrom(first, "dialogdiscard", 20);
            Check(source.Database.Entries[first].Probability == originalProbability, "ADB dialog apply leaked into shared source");
            dialog.Window.DiscardDraft();
            Check(dialog.Window.Draft.Count == source.Database.Count && dialog.Window.Draft.Entries[first].Probability == originalProbability && source.Database.Entries[first].Probability == originalProbability,
                "ADB dialog discard did not restore confirmed source");
            var visual = (FrameworkElement)dialog.Window.Content;
            visual.Measure(new Size(1200, 760)); visual.Arrange(new Rect(0, 0, 1200, 760)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, scene.Model + "-adb.png")); png.Save(file);
        }
        Console.WriteLine($"ADB PASS: {scene.Model}, {source.Database.Count} original records, no-op, edit, append, backup, RES readback, installed source unchanged");
    }
    private sealed class DialogScope(AnimationDatabaseDialog window) : IDisposable
    { public AnimationDatabaseDialog Window => window; public void Dispose() => window.Close(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static void Check(bool yes, string message) { if (!yes) throw new Exception("ADB: " + message); }
}
