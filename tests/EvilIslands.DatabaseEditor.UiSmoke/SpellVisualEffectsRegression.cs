using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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

internal static class SpellVisualEffectsRegression
{
    private static void Require(bool b, string message) { if (!b) throw new Exception(message); }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        byte[] before = SHA256.HashData(File.ReadAllBytes(database));
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var preferences = new EditorPreferencesStore(Path.Combine(output, "settings.json"));
        var window = new MainWindow(preferences, new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext; Wait(vm.OpenPathAsync(database));
        var table = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "spells.prototypes");
        vm.SelectTableCommand.Execute(table);
        var view = new SpellVisualEffectsView { Width = 1000, Height = 680, DataContext = vm };
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Spell effects headless")
        { Width = 1000, Height = 680, WindowStyle = unchecked((int)0x80000000) }); // no WS_VISIBLE
        host.RootVisual = view; view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        void Loaded() { Task task; do { task = view.LoadTask; Wait(task); } while (!ReferenceEquals(task, view.LoadTask)); Wait(view.PreviewTask); }
        vm.SelectedRecord = table.Records.Single(r => r.CoreRecord.IdentityValue == "eagle_sight"); Loaded();
        Require(view.Rows.Any(r => r.Name == "Stun.mmp" && r.Source.Length > 0), "Eagle Sight shared atlas missing");
        Require(view.DetailText.Contains("infravision") && !view.DetailText.Contains("Ошибка предпросмотра"), "Shared usage / decode");
        view.Measure(new Size(1000, 680)); view.Arrange(new Rect(0, 0, 1000, 680)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1000, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var s = File.Create(Path.Combine(output, "eagle-sight.png"))) png.Save(s);
        var search = Field<TextBox>(view, "_search"); search.Text = "not-a-resource";
        Require(Field<DataGrid>(view, "_list").Items.Count == 0 && Field<TextureImageView>(view, "_image").Bitmap is null, "Empty search retained stale preview");
        vm.SelectedRecord = table.Records.Single(r => r.CoreRecord.IdentityValue == "healing"); Loaded();
        Require(search.Text == "" && view.Rows.Any(r => r.Name == "Healing.mmp"), "Selection/search reset");
        vm.SelectedRecord = table.Records.Single(r => r.CoreRecord.IdentityValue == "comprehend"); Loaded();
        Require(view.Rows.Count == 0 && view.StatusText.Contains("не означает"), "Unknown mapping silently invented");
        Field<CheckBox>(view, "_library").IsChecked = true; Loaded();
        Require(view.Rows.Count > 10, "Manual known atlas catalog");
        var catalog = Field<TextureResourceCatalog>(view, "_catalog");
        var report = new List<object>(); int decoded = 0;
        foreach (string name in SpellVisualEffects.Bindings.SelectMany(b => SpellVisualEffects.Variants(b.Texture)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var check = catalog.Check(name);
            if (check.Matches.Count == 0) { report.Add(new { Name = name, Missing = true }); continue; }
            var image = catalog.ReadPreviewAsync(name).GetAwaiter().GetResult(); decoded++;
            report.Add(new { Name = name, image.Width, image.Height, image.Format, Source = check.Source });
        }
        File.WriteAllText(Path.Combine(output, "resource-audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "spell-coverage.json"), JsonSerializer.Serialize(table.Records.Select(r => new {
            Code = ModelRecord.Capture(table.Key, r.CoreRecord).Text(1),
            Bindings = SpellVisualEffects.ForSpell(ModelRecord.Capture(table.Key, r.CoreRecord).Text(1), r.CoreRecord.RecordIndex) }), new JsonSerializerOptions { WriteIndented = true }));
        Require(SpellVisualEffects.ForSpell("eagle_sight", 0).Count == 0, "Reordered prototype must not inherit an unverified binding");
        // Small private fixture: use actual MMP bytes, preserve an unrelated resource through the safe writer.
        byte[] Read(string name)
        {
            var m = catalog.Check(name).Matches.First(); using var s = File.OpenRead(m.Source);
            s.Position = m.Offset; var bytes = new byte[m.Length]; s.ReadExactly(bytes); return bytes;
        }
        var rows = new[] {
            new SpellTextureOverride("eagle_sight", "stun.mmp", "fx_eagle_stun.mmp"),
            new SpellTextureOverride("infravision", "stun.mmp", "fx_infra_stun.mmp") };
        byte[] stun = Read("Stun.mmp");
        var packTextures = new Dictionary<string, byte[]> { [rows[0].Replacement] = stun, [rows[1].Replacement] = stun };
        string packPath = Path.Combine(output, "sample-spell-textures");
        SpellTexturePack.Export(packPath, rows, packTextures);
        var pack = SpellTexturePack.Open(packPath);
        Require(pack.Rows.SequenceEqual(rows) && pack.Textures.Values.All(b => b.AsSpan().SequenceEqual(stun)), "Pack roundtrip / separate spell names");
        Require(!ResArchiveDocument.Open(Path.Combine(packPath, "res", "spell-textures.res")).Entries.Any(e => e.Name.Equals("stun.mmp", StringComparison.OrdinalIgnoreCase)), "Shared atlas overwritten");
        bool rejected = false;
        try { SpellTexturePack.Export(packPath, rows, packTextures); } catch (IOException) { rejected = true; }
        Require(rejected, "Existing package overwritten");
        foreach (string invalid in new[] { "bad", "EISPELLTEXTURES1\neagle_sight\tstun.mmp\t../bad.mmp\n", "EISPELLTEXTURES1\neagle_sight\tstun.mmp\tstun.mmp\n", SpellTexturePack.Serialize(rows) + "eagle_sight\tstun.mmp\tduplicate.mmp\n", "EISPELLTEXTURES1\neagle_sight\tstun.mmp\tкириллица.mmp\n" })
        {
            rejected = false; try { SpellTexturePack.Parse(invalid); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Invalid binding accepted");
        }
        var bindingDialog = new SpellTextureBindingDialog("eagle_sight", "Stun.mmp", stun);
        typeof(SpellTextureBindingDialog).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bindingDialog, null);
        Require(Field<List<SpellTextureOverride>>(bindingDialog, "_rows").Count == 1, "Add button handler did not stage binding");
        Require(bindingDialog.WindowStyle == WindowStyle.None && bindingDialog.Background is SolidColorBrush { Color: var background } && background == ((SolidColorBrush)app.FindResource("WindowBrush")).Color, "Dialog retained native white window background");
        Require(bindingDialog.Content is Border { Background: SolidColorBrush { Opacity: 1 } }, "Dialog shell is transparent");
        var dialogContent = (FrameworkElement)bindingDialog.Content;
        dialogContent.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, bindingDialog.FontSize);
        bindingDialog.Content = null;
        using var dialogHost = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Spell binding headless")
        { Width = 800, Height = 690, WindowStyle = unchecked((int)0x80000000) });
        dialogHost.RootVisual = dialogContent;
        dialogContent.Measure(new Size(800, 690)); dialogContent.Arrange(new Rect(0, 0, 800, 690)); dialogContent.UpdateLayout();
        Wait(dialogContent.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        dialogContent.UpdateLayout();
        var dialogBitmap = new RenderTargetBitmap(800, 690, 96, 96, PixelFormats.Pbgra32); dialogBitmap.Render(dialogContent);
        var dialogPng = new PngBitmapEncoder(); dialogPng.Frames.Add(BitmapFrame.Create(dialogBitmap));
        using (var stream = File.Create(Path.Combine(output, "binding-dialog.png"))) dialogPng.Save(stream);
        byte[] shellPixel = new byte[4]; dialogBitmap.CopyPixels(new Int32Rect(5, 55, 1, 1), shellPixel, 4, 0);
        var expectedBackground = ((SolidColorBrush)app.FindResource("WindowBrush")).Color;
        Require(shellPixel[3] == 255 && shellPixel[0] == expectedBackground.B && shellPixel[1] == expectedBackground.G && shellPixel[2] == expectedBackground.R, "Rendered dialog background is not opaque theme color");
        dialogContent.Measure(new Size(640, 500)); dialogContent.Arrange(new Rect(0, 0, 640, 500)); dialogContent.UpdateLayout();
        Wait(dialogContent.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        var shellGrid = (Grid)((Border)dialogContent).Child;
        var body = (DockPanel)shellGrid.Children[1];
        var bodyScroll = body.Children.OfType<ScrollViewer>().Single();
        bodyScroll.ScrollToBottom(); dialogContent.UpdateLayout();
        Require(bodyScroll.ScrollableHeight > 0 && bodyScroll.VerticalOffset > 0, "Small dialog cannot scroll to binding list");
        var smallBitmap = new RenderTargetBitmap(640, 500, 96, 96, PixelFormats.Pbgra32); smallBitmap.Render(dialogContent);
        var smallPng = new PngBitmapEncoder(); smallPng.Frames.Add(BitmapFrame.Create(smallBitmap));
        using (var stream = File.Create(Path.Combine(output, "binding-dialog-small.png"))) smallPng.Save(stream);
        var dialogGrid = Field<DataGrid>(bindingDialog, "_list"); dialogGrid.SelectedItem = dialogGrid.Items[0];
        typeof(SpellTextureBindingDialog).GetMethod("Remove", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bindingDialog, null);
        Require(dialogGrid.Items.Count == 0, "Remove binding");
        typeof(SpellTextureBindingDialog).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bindingDialog, false);
        bindingDialog.Close();
        byte[] empty = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
        string fixture = Path.Combine(output, "source.res");
        File.WriteAllBytes(fixture, ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
            [new AddedResResource("fx\\Stun.mmp", Read("Stun.mmp")), new AddedResResource("unchanged.bin", [1, 2, 3])]));
        byte[] fixtureHash = SHA256.HashData(File.ReadAllBytes(fixture));
        var manager = new ResFileManagerDialog(fixture, null, preferences) { InitialResourceName = "FX\\STUN.MMP" };
        Wait((Task)typeof(ResFileManagerDialog).GetMethod("LoadArchiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, [fixture])!);
        manager.SelectInitialResource();
        Require(((DataGrid)manager.FindName("FilesGrid")).SelectedItem is not null, "Exact resource preselection");
        Wait(manager.ManagerPreviewTask);
        string replacement = Path.Combine(output, "replacement.mmp"); File.WriteAllBytes(replacement, Read("Healing.mmp"));
        var archive = ResFileManagerArchive.OpenAsync(fixture).GetAwaiter().GetResult();
        var import = ResFileManagerArchive.InspectImportAsync(replacement).GetAwaiter().GetResult();
        string saved = Path.Combine(output, "result.res");
        archive.SaveCopyAsync([new(ResFileEditKind.AddOrReplace, "fx\\Stun.mmp", import)], saved).GetAwaiter().GetResult();
        var reopened = ResArchiveDocument.Open(saved);
        Require(reopened.GetResourceBytes("fx\\Stun.mmp").Span.SequenceEqual(File.ReadAllBytes(replacement)), "Replacement bytes");
        Require(reopened.GetResourceBytes("unchanged.bin").Span.SequenceEqual(new byte[] {1,2,3}), "Unrelated resource changed");
        _ = MmpPreviewDecoder.Decode(reopened.GetResourceBytes("fx\\Stun.mmp").ToArray());
        Require(fixtureHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture))), "Source RES changed");
        manager.Close();
        var items = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "items.weapons");
        vm.SelectTableCommand.Execute(items); vm.SelectedRecord = items.Records.First(); Loaded();
        Require(view.Rows.Count == 0 && view.StatusText.StartsWith("Выберите"), "Non-spell stale effects");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(!vm.HasChanges && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "Database changed");
        window.Close(); app.Shutdown();
        Console.WriteLine($"PASS: {table.Records.Count} spell coverage; {decoded} actual MMP variants decoded; search, unknown binding, shared use, exact RES selection, safe replacement roundtrip, source immutability.");
        return 0;
    }
}
