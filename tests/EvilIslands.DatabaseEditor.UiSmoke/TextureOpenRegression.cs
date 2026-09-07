using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class TextureOpenRegression
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Use a new output folder.");
        Directory.CreateDirectory(root);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task task = Check(args, root);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("Texture open regressions passed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string[] args, string root)
    {
        string database = Path.Combine(root, "database.res");
        File.Copy(args[0], database);
        byte[] empty = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
        byte[] bytes = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("fixture", empty), new Dictionary<int, byte[]>(),
            [new AddedResResource("first.mmp", new byte[80]), new AddedResResource("alias.mmp", new byte[80])]);
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        bytes.AsSpan(directory + 8, 4).CopyTo(bytes.AsSpan(directory + 22 + 8, 4));
        string texture = Path.Combine(root, "textures.res"); File.WriteAllBytes(texture, bytes);
        try { ResArchiveDocument.Open(texture); throw new InvalidOperationException("Strict overlap protection was lost."); }
        catch (DatabaseFormatException ex) { Require(ex.Message.Contains("overlap"), "Expected original overlap reproduction."); }
        var manager = await ResFileManagerArchive.OpenAsync(texture);
        string addedPath = Path.Combine(root, "added.bin"); File.WriteAllBytes(addedPath, [9]);
        var addedImport = await ResFileManagerArchive.InspectImportAsync(addedPath);
        await manager.SaveCopyAsync([new ResFileEdit(ResFileEditKind.AddOrReplace, "added.bin", addedImport)], Path.Combine(root, "unchanged.res"));
        var unchanged = ResArchiveDocument.Open(Path.Combine(root, "unchanged.res"));
        Require(unchanged.GetResourceBytes("first.mmp").Span.SequenceEqual(new byte[80]) &&
            unchanged.GetResourceBytes("alias.mmp").Span.SequenceEqual(new byte[80]), "Alias copy damaged payloads.");
        string replacement = Path.Combine(root, "replacement.mmp"); File.WriteAllBytes(replacement, [1, 2, 3]);
        var imported = await ResFileManagerArchive.InspectImportAsync(replacement);
        await manager.SaveCopyAsync([new ResFileEdit(ResFileEditKind.AddOrReplace, "first.mmp", imported)], Path.Combine(root, "replaced.res"));
        var replaced = ResArchiveDocument.Open(Path.Combine(root, "replaced.res"));
        Require(replaced.GetResourceBytes("first.mmp").Span.SequenceEqual(new byte[] { 1, 2, 3 }) &&
            replaced.GetResourceBytes("alias.mmp").Span.SequenceEqual(new byte[80]), "Replacing one alias changed another.");
        await manager.SaveCopyAsync([new ResFileEdit(ResFileEditKind.Delete, "first.mmp")], Path.Combine(root, "deleted.res"));
        var deleted = ResArchiveDocument.Open(Path.Combine(root, "deleted.res"));
        Require(deleted.Entries.Count == 1 && deleted.GetResourceBytes("alias.mmp").Span.SequenceEqual(new byte[80]), "Deleting one alias damaged another.");
        string extracted = Path.Combine(root, "extracted.mmp"); await manager.ExtractAsync("alias.mmp", extracted);
        Require(File.ReadAllBytes(extracted).AsSpan().SequenceEqual(new byte[80]), "Alias extraction failed.");
        Console.WriteLine("PASS RES manager alias open, extraction, copy, independent replacement and deletion");
        try { await manager.ReadPreviewAsync("first.mmp"); throw new Exception("Malformed MMP accepted."); }
        catch (InvalidDataException) { }
        byte[] validMmp = new byte[80];
        foreach (var (offset, value) in new (int, uint)[] { (0, 0x00504D4D), (4, 1), (8, 1), (12, 1), (16, 0x8888), (20, 32),
            (24, 0xFF000000), (36, 0xFF0000), (48, 0xFF00), (60, 0xFF), (76, 0xFF00FF00) })
            BinaryPrimitives.WriteUInt32LittleEndian(validMmp.AsSpan(offset), value);
        File.WriteAllBytes(replacement, validMmp);
        var previewImport = await ResFileManagerArchive.InspectImportAsync(replacement);
        var importedPreview = await manager.ReadPreviewAsync("first.mmp", previewImport);
        Require(importedPreview.Width == 1 && importedPreview.Bgra[1] == 255, "Pending replacement preview did not use imported bytes.");
        validMmp[77] = 42; File.WriteAllBytes(replacement, validMmp);
        try { await manager.ReadPreviewAsync("first.mmp", previewImport); throw new Exception("Changed import accepted."); }
        catch (IOException) { }
        Console.WriteLine("PASS malformed MMP, pending import preview and changed import rejection");
        Require(await TextureSources.ResolveDatabasePathAsync(database) == database, "Database route changed.");
        Require(await TextureSources.ResolveDatabasePathAsync(texture) == database, "Texture alias route failed.");
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(root, "settings.json")), new RecoverySnapshotStore(Path.Combine(root, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        string hash = Hash(database), textureHash = Hash(texture);
        await vm.OpenPathAsync(texture);
        Require(vm.HasDocument && vm.SourcePath == database && vm.NotificationMessage?.Contains("архив текстур") == true &&
            vm.TextureSourcesDescription.Contains("textures.res"), "UI must open the database and attach selected textures.");
        string orphan = Path.Combine(root, "orphan"); Directory.CreateDirectory(orphan);
        string orphanTexture = Path.Combine(orphan, "textures.res"); File.WriteAllBytes(orphanTexture, bytes);
        try { await TextureSources.ResolveDatabasePathAsync(orphanTexture); throw new Exception("Missing database accepted."); }
        catch (InvalidOperationException ex) { Require(ex.Message.Contains("Рядом нет database.res"), "Missing database guidance absent."); }
        Require(Hash(database) == hash && Hash(texture) == textureHash, "Sources changed.");
        Console.WriteLine("PASS alias reproduction, database/texture routing, UI auto-attach, missing database, unchanged sources");
        // Partial intersections remain invalid even for read-only routing.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 22 + 8), 17);
        string corrupt = Path.Combine(root, "partial.res"); File.WriteAllBytes(corrupt, bytes);
        try { await TextureSources.ResolveDatabasePathAsync(corrupt); throw new Exception("Partial overlap accepted."); }
        catch (DatabaseFormatException) { }
        try { await ResFileManagerArchive.OpenAsync(corrupt); throw new Exception("Manager accepted partial overlap."); }
        catch (DatabaseFormatException) { }
        Console.WriteLine("PASS partial overlap rejected");
        if (args.Length > 2)
        {
            string actual = Path.GetFullPath(args[2]); string actualHash = Hash(actual);
            var actualManager = await ResFileManagerArchive.OpenAsync(actual);
            Require(actualManager.Entries.Count == 591, "Manager failed to open actual archive.");
            var dialog = new EvilIslands.DatabaseEditor.App.Dialogs.ResFileManagerDialog(actual, null,
                new EditorPreferencesStore(Path.Combine(root, "manager-settings.json")));
            var load = typeof(EvilIslands.DatabaseEditor.App.Dialogs.ResFileManagerDialog).GetMethod("LoadArchiveAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)load.Invoke(dialog, [actual])!;
            var grid = (System.Windows.Controls.DataGrid)dialog.FindName("FilesGrid");
            Require(grid.Items.Count == 591, "Actual manager UI did not populate rows.");
            grid.SelectedItem = grid.Items.Cast<ResManagerFileRow>().First(row => row.ResourceName == "qitem0400.ad.x2.mmp");
            await dialog.ManagerPreviewTask;
            var preview = (TextureImageView)dialog.FindName("ManagerPreviewImage");
            Require(preview.Bitmap is not null, "Selected actual texture has no preview.");
            var zoom = (System.Windows.Controls.ComboBox)dialog.FindName("PreviewZoom");
            zoom.SelectedIndex = 2;
            Require(preview.Zoom == 2, "Manager preview zoom failed.");
            zoom.SelectedIndex = 0;
            var content = (FrameworkElement)dialog.Content;
            typeof(ResFileManagerDialog).GetMethod("SetDetailsVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog, [true]);
            content.Measure(new Size(1500, 950)); content.Arrange(new Rect(0, 0, 1500, 950)); content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var panel = (FrameworkElement)dialog.FindName("DetailsPanel");
            ((System.Windows.Controls.Grid)panel.Parent).Children.Remove(panel);
            var host = new System.Windows.Controls.Border { Child = panel, Width = 310, Height = 800 };
            host.Measure(new Size(310, 800)); host.Arrange(new Rect(0, 0, 310, 800)); host.UpdateLayout();
            var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rendered.Render(panel);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
            using (var png = File.Create(Path.Combine(root, "manager-preview.png"))) encoder.Save(png);
            grid.SelectedItem = grid.Items.Cast<ResManagerFileRow>().First(row => row.ResourceName == "clayring.mmp");
            Task stale = dialog.ManagerPreviewTask;
            grid.SelectedItem = null;
            await stale;
            Require(preview.Bitmap is null, "Stale preview appeared after deselection.");
            Console.WriteLine("PASS actual selected MMP preview, zoom, offscreen render and stale cancellation");
            Console.WriteLine("PASS actual RES manager dialog load: 591 rows, no interactive window");
            try { ResArchiveDocument.Open(actual); throw new Exception("Expected reported overlap."); }
            catch (DatabaseFormatException ex) { Console.WriteLine("REPRO " + ex.Message); }
            string actualDatabase = await TextureSources.ResolveDatabasePathAsync(actual);
            string databaseHash = Hash(actualDatabase);
            await vm.OpenPathAsync(actual);
            Require(vm.HasDocument && vm.SourcePath == actualDatabase && vm.TextureSourcesDescription.Contains("textures.res"), "Actual file route failed: " + vm.StatusText);
            var catalog = await TextureResourceCatalog.OpenAsync(TextureSources.Manual([actual]));
            Require(catalog.Check("clayring.mmp").Matches.Count == 1 && catalog.Check("casting6.mmp").Matches.Count == 1, "Actual aliases absent.");
            Require(Hash(actual) == actualHash && Hash(actualDatabase) == databaseHash, "Actual source changed.");
            Console.WriteLine($"PASS actual read-only UI open, {catalog.ResourceCount} textures; SHA256 {actualHash}");
        }
        vm.Dispose();
    }
}
