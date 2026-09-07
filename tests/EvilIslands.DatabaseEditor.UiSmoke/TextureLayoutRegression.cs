using System.Globalization;
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

internal static class TextureLayoutRegression
{
    internal static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(root);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task task = Check(Path.GetFullPath(args[0]), root);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("Texture layout PASS=5, FAIL=0; no windows shown."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task Check(string source, string root)
    {
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(root, "settings.json")),
            new RecoverySnapshotStore(Path.Combine(root, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(source);
        var table = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "items.armors");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = table.Records.First();
        await vm.RecheckTexturesAsync();
        var tab = (TabItem)window.FindName("TextureTab");
        tab.IsSelected = true;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1600, 1000));
        content.Arrange(new Rect(0, 0, 1600, 1000));
        content.UpdateLayout();
        var paneRow = (RowDefinition)window.FindName("DetailsPaneRow");
        Require(paneRow.Height.Value >= 380, "Texture tab must open taller than the legacy text pane.");
        var expand = (Button)window.FindName("TextureExpandButton");
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(((DataGrid)window.FindName("RecordsGrid")).Visibility == Visibility.Collapsed &&
            paneRow.Height.IsStar, "Expand must allocate the table workspace to textures.");
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(((DataGrid)window.FindName("RecordsGrid")).Visibility == Visibility.Visible, "Return must restore the database table.");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await vm.RecheckTexturesAsync();
        Console.WriteLine("PASS pane-height-expand-return");

        // Synthetic UI-only descriptions avoid packaging private database contents in the render.
        var found = new TextureRow("items.armors", 0, "Демонстрационный доспех", "Предмет / железо",
            "unhumapl_09.ir.3.mmp", TextureCheckState.Context, "textures.res",
            "Форма и совместимый материал. Файл найден в подключённых ресурсах. Для брони на существе дополнительно требуется маска его модели.",
            "1. textures.res — unhumapl_09.ir.3.mmp\n2. redress.res — unhumapl_09.ir.3.mmp")
            { FileState = TextureCheckState.Found, HasMatch = true, NeedsContext = true };
        var absent = found with { ResourceName = "unhumapl_09.ir.3.x2.mmp", Archive = "", Versions = "",
            FileState = TextureCheckState.Missing, HasMatch = false,
            Explanation = "Вариант addon отсутствует в подключённых источниках. Его обязательность зависит от графического режима." };
        vm.TextureRows.Clear();
        vm.TextureRows.Add(found);
        vm.TextureRows.Add(absent);
        foreach (string material in new[] { "бронза", "сталь", "серебро", "золото", "камень", "кость", "дерево", "ткань" })
            vm.TextureRows.Add(found with { Purpose = "Предмет / " + material, ResourceName = $"armor_{material}.mmp" });
        vm.TextureFilterIndex = 1;
        Require(vm.TextureView.Cast<TextureRow>().Count() == 9 && vm.TextureView.Contains(found) &&
            found.PresenceText == "Файл найден" && found.ApplicabilityText.Contains("уточнения"),
            "Found candidates must remain visibly found while applicability is uncertain.");
        vm.TextureFilterIndex = 2;
        Require(vm.TextureView.Cast<TextureRow>().Single() == absent, "Missing/corrupt filter failed.");
        vm.TextureFilterIndex = 0;
        vm.TextureSearch = "СТАЛЬ";
        Require(vm.TextureView.Cast<TextureRow>().Single().Purpose.EndsWith("сталь"), "Search must include material and ignore case.");
        vm.TextureSearch = "no matching name";
        Require(vm.SelectedTexture is null && !vm.TextureView.Cast<TextureRow>().Any(), "Empty filter must clear stale details.");
        vm.TextureSearch = "";
        vm.SelectedTexture = found;
        typeof(MainWindowViewModel).GetMethod("UpdateTextureSummary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
        Console.WriteLine("PASS presence-applicability-filters-empty-selection");

        var browser = (Grid)window.FindName("TextureBrowser");
        tab.Content = null;
        content.UpdateLayout();
        browser.DataContext = vm;
        var host = new Border { Child = browser, Background = new SolidColorBrush(Color.FromRgb(27, 30, 28)) };
        foreach (var size in new[] { new Size(1180, 650), new Size(780, 560) })
        {
            host.Width = size.Width;
            host.Height = size.Height;
            host.Measure(size);
            host.Arrange(new Rect(new Point(), size));
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            host.UpdateLayout();
            var list = (ListBox)window.FindName("TextureLinksGrid");
            Require(list.ActualHeight >= 250, "List viewport remains too short.");
            Require(list.ActualWidth >= 280, "Resource names have insufficient width.");
            Require(list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem, "Visible rows must be realized.");
            Require(vm.SelectedTexture == found, "Rendering must preserve the selected resource.");
            Require(Math.Abs(host.ActualWidth - size.Width) < 1 && browser.ActualWidth <= size.Width, "Panel must fit the requested width.");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(root, $"texture-panel-{size.Width:0}.png"));
            encoder.Save(stream);
        }
        Console.WriteLine("PASS offscreen-layout-1180-and-780");
        Require(!window.IsVisible, "Headless check must not show a window.");
        Console.WriteLine("PASS no-interactive-window");
        vm.Dispose();
        window.Close();
    }
}
