using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakOpeningRegression
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static ResArchiveDocument Empty()
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 16);
        return ResArchiveDocument.Parse("empty.res", data);
    }
    internal static int Run(string database, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS PAK opening: packaged tool, recent migrated DB, WPF RES/PAK, exact selection, CRUD, extraction, copy, original hashes"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
    private static async Task Verify(string database, string output)
    {
        Check(Hash(Path.Combine(AppContext.BaseDirectory, "EIRebuild64Pack.exe")).Equals(PakArchive.UtilitySha256, StringComparison.OrdinalIgnoreCase), "Bundled utility differs from compiled identity");
        string originalHash = Hash(database);
        string copy = Path.Combine(output, "database.pak");
        File.Copy(database, copy);
        string texts = Path.Combine(Path.GetDirectoryName(database)!, "texts.pak");
        string? textsHash = File.Exists(texts) ? Hash(texts) : null;
        if (textsHash is not null) File.Copy(texts, Path.Combine(output, "texts.pak"));
        // Preserve a real legacy fixture for the existing PAK integration suite.
        using (var pak = PakArchive.Open(copy))
        {
            pak.Materialize();
            File.WriteAllBytes(Path.Combine(output, "legacy-database.res"), ResArchiveWriter.Rebuild(Empty(),
                new Dictionary<int, byte[]>(), pak.Entries.Select(e => new AddedResResource(e.Name, pak.Read(e.Name))).ToArray()));
        }
        var preferences = new EditorPreferencesStore(Path.Combine(output, "preferences.json"));
        preferences.RememberDatabasePath(Path.ChangeExtension(copy, ".res"));
        var window = new MainWindow(preferences, new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        await window.HomeRefreshTask;
        var button = (Button)window.FindName("OpenLastDatabaseButton");
        Check(button.IsEnabled && ((TextBlock)window.FindName("HomeDatabasePath")).Text == copy, "Migrated recent database not resolved");
        Render((FrameworkElement)window.Content, Path.Combine(output, "home.png"), 1500, 950);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var vm = (MainWindowViewModel)window.DataContext;
        for (int i = 0; i < 3000 && vm.IsBusy; i++) await Task.Delay(20);
        Check(!vm.IsBusy && vm.HasDocument && vm.SourcePath == copy && vm.RecordCount > 0, "Real PAK database did not open: " + vm.NotificationMessage);
        Check(preferences.GetLastDatabasePath() == copy, "Recent database did not migrate to physical PAK path");
        Render((FrameworkElement)window.Content, Path.Combine(output, "database.png"), 1500, 950);
        Console.WriteLine($"PASS real database UI: {vm.RecordCount} records, migrated recent path");

        string legacy = Path.Combine(output, "files.res"), modern = Path.Combine(output, "files.pak");
        File.WriteAllBytes(legacy, ResArchiveWriter.Rebuild(Empty(), new Dictionary<int, byte[]>(),
            [new("keep.bin", [1, 2]), new("replace.bin", [3]), new("remove.bin", [4])]));
        PakArchive.Create(modern, new() { ["keep.bin"] = [8, 9], ["replace.bin"] = [3], ["remove.bin"] = [4] });
        Check(ResourceArchivePaths.ResolveRecent(legacy) == legacy, "Existing recent RES was shadowed");
        string payload = Path.Combine(output, "import.bin"); File.WriteAllBytes(payload, [5, 6, 7]);
        var imported = await ResFileManagerArchive.InspectImportAsync(payload);
        ResFileEdit[] edits = [new(ResFileEditKind.AddOrReplace, "added.bin", imported), new(ResFileEditKind.AddOrReplace, "replace.bin", imported), new(ResFileEditKind.Delete, "remove.bin")];
        foreach (string path in new[] { legacy, modern })
        {
            string before = Hash(path);
            var archive = await ResFileManagerArchive.OpenAsync(path);
            bool isPak = path == modern;
            Check(archive.SourcePath == path && archive.IsPak == isPak, "Explicit RES selection opened sibling PAK");
            string extracted = path + ".extracted";
            await archive.ExtractAsync("keep.bin", extracted);
            Check(File.ReadAllBytes(extracted).SequenceEqual(isPak ? new byte[] { 8, 9 } : [1, 2]), "Extraction used wrong archive");
            var dialog = new ResFileManagerDialog(path, null, preferences);
            await (Task)typeof(ResFileManagerDialog).GetMethod("LoadArchiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [path])!;
            Check(((DataGrid)dialog.FindName("FilesGrid")).Items.Count == 3, "WPF archive listing failed");
            Check(((TextBlock)dialog.FindName("ArchiveSummaryText")).Text.StartsWith(isPak ? "PAK" : "RES"), "Incorrect archive format in UI");
            ((TextBox)dialog.FindName("SearchBox")).Text = "keep";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((DataGrid)dialog.FindName("FilesGrid")).Items.Count == 1, "Archive search failed");
            ((TextBox)dialog.FindName("SearchBox")).Text = "";
            Render((FrameworkElement)dialog.Content, Path.Combine(output, isPak ? "manager-pak.png" : "manager-res.png"), 1460, 860);
            dialog.Close();
            string saved = Path.Combine(output, isPak ? "edited.pak" : "edited.res");
            await archive.SaveCopyAsync(edits, saved);
            var reopened = await ResFileManagerArchive.OpenAsync(saved);
            Check(reopened.Entries.Count == 3 && reopened.Contains("added.bin") && !reopened.Contains("remove.bin"), "CRUD copy resource list wrong");
            foreach (string name in new[] { "added.bin", "replace.bin" })
            {
                string target = saved + "." + name;
                await reopened.ExtractAsync(name, target);
                Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 5, 6, 7 }), "Edited payload mismatch");
            }
            if (!isPak)
            {
                string converted = Path.Combine(output, "converted.pak");
                await archive.SaveCopyAsync(edits, converted);
                Check(PakArchive.Read(converted, "keep.bin").SequenceEqual(new byte[] { 1, 2 }), "RES conversion used sibling PAK");
            }
            await reopened.SaveInPlaceAsync([new(ResFileEditKind.Delete, "added.bin")], createBackup: true);
            Check(!(await ResFileManagerArchive.OpenAsync(saved)).Contains("added.bin"), "In-place delete failed");
            Check(Hash(path) == before, "Source changed while saving a copy");
            Console.WriteLine("PASS file manager UI and CRUD: " + Path.GetExtension(path));
        }
        Check(Hash(database) == originalHash && (textsHash is null || Hash(texts) == textsHash), "Installed source modified");
        string utility = Path.Combine(AppContext.BaseDirectory, "EIRebuild64Pack.exe");
        byte[] verifiedUtility = File.ReadAllBytes(utility);
        try
        {
            File.WriteAllBytes(utility, [0]);
            bool rejected = false;
            try { using var unexpected = PakArchive.Open(copy); }
            catch (IOException e) { rejected = e.Message.Contains("Утилита PAK не соответствует"); }
            Check(rejected, "Modified utility was not rejected before execution");
        }
        finally { File.WriteAllBytes(utility, verifiedUtility); }
        Console.WriteLine("PASS runtime utility integrity rejection");
        vm.Dispose(); window.Close();
    }
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
