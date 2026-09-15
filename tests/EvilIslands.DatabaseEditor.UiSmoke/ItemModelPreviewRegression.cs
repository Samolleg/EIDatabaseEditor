using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ItemModelPreviewRegression
{
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var targets = records.Where(r => r.Table is "items.weapons" or "items.armors").GroupBy(r => (r.Table, Type: r.Integer(2))).Select(g => g.First())
            .Concat(records.Where(r => r.Table is "items.materials" or "items.quick_items" or "items.quest_items").GroupBy(r => r.Table).Select(g => g.First()))
            .Concat(records.Where(r => r.Table == "items.loot_items").GroupBy(r => (r.Integer(21), r.Integer(22))).Select(g => g.First()))
            .Concat(records.Where(r => r.Table == "items.weapons" && r.Name is "cheat dagger" or "primitive crossbow"))
            .Concat(records.Where(r => r.Table == "items.quick_items" && r.Name == "spell container"))
            .Distinct().ToArray();
        int index = 0;
        foreach (var record in targets)
        {
            var choice = ItemModelPreview.DefaultChoiceAsync(database, record, records).GetAwaiter().GetResult();
            var scene = ItemModelPreview.LoadAsync(database, record, records, default, choice).GetAwaiter().GetResult();
            if (scene.Meshes.Count == 0) throw new Exception("Missing geometry: " + record.Name);
            var view = new UnitModelPreviewView { Width = 800, Height = 700 };
            view.Measure(new Size(800, 700)); view.Arrange(new Rect(0, 0, 800, 700)); view.ShowScene(scene, true); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(800, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string name = $"{index++:D2}-{record.Table}-{string.Concat(record.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}";
            using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
            File.WriteAllLines(Path.Combine(output, name + ".txt"), scene.Sources.Concat(scene.Notes));
            Console.WriteLine($"Rendered {name}: {scene.Model}, {scene.Meshes.Count} meshes, {scene.Textures.Count} textures");
        }
        // The atlas preserves decoded HD pixels, orientation, premultiplied alpha,
        // and transparent space; it never edits mesh UV or resamples the source.
        var source = new MmpImage(2, 2, "fixture", [1,2,3,4, 5,6,7,8, 9,10,11,12, 0,0,0,0], true);
        var atlas = ItemModelPreview.InventoryAtlas(source, 64, 64, default);
        if (atlas.Width != 8 || atlas.Height != 8 || !atlas.PremultipliedAlpha || atlas.Bgra.Take(6 * 8 * 4).Any(b => b != 0) ||
            !atlas.Bgra.Skip(6 * 8 * 4).Take(8).SequenceEqual(source.Bgra.Take(8)) ||
            !atlas.Bgra.Skip(7 * 8 * 4).Take(8).SequenceEqual(source.Bgra.Skip(8))) throw new Exception("Inventory atlas orientation/alpha mismatch");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { ItemModelPreview.LoadAsync(database, records.First(r => r.Table == "items.materials"), records, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        var weaponRecord = records.First(r => r.Table == "items.weapons");
        try { ItemModelPreview.LoadAsync(database, weaponRecord, records, default, records.First(r => r.Table == "units.prototypes")).GetAwaiter().GetResult(); throw new Exception("Invalid material accepted"); }
        catch (InvalidDataException) { }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "ui-settings.json")), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        void Wait(Task task)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        Wait(vm.OpenPathAsync(database));
        var attached = new UnitModelPreviewView { Width = 800, Height = 700, DataContext = vm };
        using var presentation = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Item preview headless test")
        { Width = 800, Height = 700, WindowStyle = unchecked((int)0x80000000) }); // WS_POPUP, deliberately no WS_VISIBLE.
        presentation.RootVisual = attached;
        attached.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        attached.Measure(new Size(800, 700)); attached.Arrange(new Rect(0, 0, 800, 700));
        foreach (var table in vm.Databases.SelectMany(g => g.Tables).Where(t => ItemModelPreview.Supports(t.Key)))
        {
            vm.SelectTableCommand.Execute(table); vm.SelectedRecord = table.Records.First();
            var tab = (TabItem)window.FindName("ModelTab"); tab.IsSelected = true;
            if (!tab.IsSelected) throw new Exception("Item model tab overridden: " + table.Key);
            Task loading;
            do { loading = attached.LoadTask; Wait(loading); } while (!ReferenceEquals(loading, attached.LoadTask));
            var content = (System.Windows.Media.Media3D.ModelVisual3D)typeof(UnitModelPreviewView).GetField("_visual", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(attached)!;
            if (content.Content is null)
            {
                var status = (TextBlock)typeof(UnitModelPreviewView).GetField("_status", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(attached)!;
                throw new Exception("Item UI failed to load: " + table.Key + " visible=" + attached.IsVisible + " " + status.Text);
            }
            if (vm.HasChanges) throw new Exception("Model preview changed database");
        }
        var weapons = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "items.weapons");
        vm.SelectTableCommand.Execute(weapons); vm.SelectedRecord = weapons.Records.Single(r => r.CoreRecord.IdentityValue == "cheat dagger");
        Wait(attached.LoadTask);
        var selector = (ComboBox)typeof(UnitModelPreviewView).GetField("_itemChoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(attached)!;
        selector.SelectedItem = selector.Items.Cast<ModelRecord>().Single(r => r.Name == "iron");
        Wait(attached.LoadTask);
        var info = (TextBox)typeof(UnitModelPreviewView).GetField("_information", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(attached)!;
        if (!info.Text.Contains("Материал предпросмотра: iron") || !info.Text.Contains("unhumadg_03.ir.1.x2.mmp") || vm.HasChanges)
            throw new Exception("Material selector failed or changed database");
        attached.SetMobEntries([], "no-match"); Wait(attached.LoadTask);
        if (!info.Text.Contains("unhumadg_03.ir.1.x2.mmp")) throw new Exception("Empty MOB filter suppressed item preview");
        window.Close(); app.Shutdown();
        Console.WriteLine("Item category navigation, rendering, alpha atlas, cancellation and invalid material PASS. Game parity not tested.");
        return 0;
    }
}
