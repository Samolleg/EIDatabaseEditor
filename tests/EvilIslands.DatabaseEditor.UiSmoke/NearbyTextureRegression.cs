using System.Buffers.Binary;
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

internal static class NearbyTextureRegression
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Put(byte[] data, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p), value);
    private static byte[] Mmp()
    {
        byte[] data = new byte[80];
        Put(data, 0, 0x00504D4D); Put(data, 4, 1); Put(data, 8, 1); Put(data, 12, 1);
        Put(data, 16, 0x8888); Put(data, 20, 32);
        Put(data, 24, 0xFF000000); Put(data, 36, 0xFF0000); Put(data, 48, 0xFF00); Put(data, 60, 0xFF);
        Put(data, 76, 0xFF00FF00); return data;
    }
    private static void Res(string path, string name, byte[] payload)
    {
        byte[] empty = new byte[16]; Put(empty, 0, ResArchiveDocument.Signature); Put(empty, 8, 16);
        File.WriteAllBytes(path, ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("fixture", empty),
            new Dictionary<int, byte[]>(), [new AddedResResource(name, payload)]));
        _ = ResArchiveDocument.Open(path);
    }
    internal static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Use a new output folder.");
        Directory.CreateDirectory(root);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(Path.GetFullPath(args[0]), root);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("Nearby textures: PASS=5, FAIL=0."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string source, string root)
    {
        string a = Path.Combine(root, "A"), b = Path.Combine(root, "B");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string database = Path.Combine(a, "database.res");
        File.Copy(source, database); File.Copy(source, Path.Combine(b, "database.res"));
        string hash = ResArchiveDocument.Open(database).Sha256;
        string texture = Path.Combine(a, "arbitrary-pack.res");
        Res(texture, "spell0000.mmp", Mmp());
        Res(Path.Combine(a, "speech.res"), "voice.mp3", [0, 1, 2]);
        Res(Path.Combine(a, "unrelated.res"), "readme.txt", [1]);
        File.WriteAllBytes(Path.Combine(a, "loose.mmp"), Mmp());
        File.WriteAllBytes(Path.Combine(a, "broken.res"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(a, "nested"));
        Res(Path.Combine(a, "nested", "hidden.res"), "hidden.mmp", Mmp());
        var sources = await TextureSources.FromDatabaseFolderAsync(database);
        Require(sources.Archives.SequenceEqual(new[] { texture }) && sources.NearbyDatabasePath == database &&
            !sources.Complete && sources.Notes.Any(n => n.Contains("broken.res")), "Discovery must select texture-bearing RES only and disclose unreadable files.");
        var catalog = await TextureResourceCatalog.OpenAsync(sources);
        Require(catalog.ResourceCount == 2 && catalog.Check("loose.mmp").Matches.Count == 1 &&
            catalog.Check("spell0000.mmp").State == TextureCheckState.Incomplete &&
            catalog.Check("hidden.mmp").Matches.Count == 0, "Loose files/nonrecursive/partial coverage.");
        Console.WriteLine("PASS nearby-discovery-and-partial-coverage");

        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(root, "settings.json")), new RecoverySnapshotStore(Path.Combine(root, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(database);
        Require(vm.HasDocument && vm.AttachTexturesButtonText.Contains("2") && vm.TextureSourcesDescription.Contains("arbitrary-pack.res"), "Open must attach nearby textures automatically.");
        var spells = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "spells.prototypes");
        vm.SelectTableCommand.Execute(spells);
        vm.SelectedRecord = spells.Records.First(r => r.GetCell(18)?.EditorText == "0");
        await vm.RecheckTexturesAsync(); await vm.RefreshTexturePreviewAsync();
        Require(vm.TexturePreviewItems[0].Image is not null, "Auto-attached resource must be available to preview.");
        Require(vm.HasSoundArchives, "Existing nearby sound discovery must remain functional.");
        Console.WriteLine("PASS database-open-auto-attach-and-preview");

        Res(Path.Combine(a, "new-pack.res"), "new.mmp", Mmp());
        await vm.ReloadTexturesAsync();
        Require(vm.AttachTexturesButtonText.Contains("3") && vm.TextureSourcesDescription.Contains("new-pack.res"), "Reload must discover new neighboring archives.");
        await vm.OpenPathAsync(Path.Combine(b, "database.res"));
        Require(vm.TextureSourcesDescription.Contains(b) && !vm.TextureSourcesDescription.Contains(texture) &&
            vm.AttachTexturesButtonText.Contains("Подключить"), "Opening another folder must clear previous texture sources.");
        await vm.OpenPathAsync(database);
        Require(vm.AttachTexturesButtonText.Contains("3"), "Reopening must rescan sources.");
        Console.WriteLine("PASS reload-and-document-switch");

        var button = (Button)window.FindName("TextureSourcesButton");
        Require(button.Parent is StackPanel && button.ContextMenu is { Items.Count: 7 }, "Sidebar source menu missing.");
        var rootView = (FrameworkElement)window.Content;
        rootView.Measure(new Size(1600, 1000)); rootView.Arrange(new Rect(0, 0, 1600, 1000)); rootView.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); rootView.UpdateLayout();
        Require(button.IsEnabled && button.Content.ToString()!.Contains("3"), "Sidebar must display attached resource count.");
        var overview = Descendants(rootView).OfType<TextBlock>().First(t => t.Text == "ОБЗОР");
        Require(button.TranslatePoint(new Point(), rootView).Y + button.ActualHeight < overview.TranslatePoint(new Point(), rootView).Y,
            "Connection must appear above Overview.");
        Require(!Descendants(rootView).OfType<Expander>().Any(e => Equals(e.Header, "Подключение ресурсов игры")),
            "Old hidden connection expander remains.");
        var sidebar = (FrameworkElement)button.Parent;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sidebar.ActualWidth) + 20, (int)Math.Ceiling(sidebar.ActualHeight) + 30, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sidebar);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "sidebar.png"))) encoder.Save(stream);
        var menu = button.ContextMenu!;
        menu.Visibility = Visibility.Visible;
        menu.Width = 320;
        menu.ApplyTemplate();
        menu.Measure(new Size(320, double.PositiveInfinity));
        menu.Arrange(new Rect(new Point(), menu.DesiredSize));
        menu.UpdateLayout();
        var menuBorder = (Border)menu.Template.FindName("TextureMenuBorder", menu);
        Require(menuBorder.Background is SolidColorBrush background && background.Color.R < 50 && background.Color.G < 50,
            "Texture menu must not use the light Windows background.");
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.ApplyTemplate();
            Require(item.Foreground is SolidColorBrush foreground && foreground.Color.R > 200 &&
                item.Template.Triggers.OfType<Trigger>().Any(t => t.Property == MenuItem.IsHighlightedProperty),
                "Menu labels/highlight must use the dark theme.");
        }
        var menuBitmap = new RenderTargetBitmap(320, (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        menuBitmap.Render(menu);
        var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
        using (var stream = File.Create(Path.Combine(root, "texture-menu.png"))) menuEncoder.Save(stream);
        Require(!menu.IsOpen, "Menu rendering must not open a native popup.");
        menu.Visibility = Visibility.Collapsed;
        Require(!window.IsVisible, "Headless check displayed an interactive window.");
        Console.WriteLine("PASS sidebar-position-and-headless-render");

        Require(ResArchiveDocument.Open(database).Sha256 == hash && ResArchiveDocument.Open(source).Sha256 == hash, "Opening/discovery changed database.");
        Console.WriteLine("PASS source-unchanged");
        vm.Dispose(); window.Close();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
