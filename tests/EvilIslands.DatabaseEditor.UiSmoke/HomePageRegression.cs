using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class HomePageRegression
{
    internal static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output);
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background));
        Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS home page: metadata, actual module/model resolver, missing paths, layout, open/reopen, no interactive window"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Check(bool pass, string what) { if (!pass) throw new InvalidOperationException(what); }
    private static async Task Verify(string database, string output)
    {
        var preferences = new EditorPreferencesStore(Path.Combine(output, Guid.NewGuid()+".json"));
        var window = new MainWindow(preferences, new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        await window.HomeRefreshTask;
        var vm = (MainWindowViewModel)window.DataContext;
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = Find<FrameworkElement>("MainRoot");
        var page = Find<ScrollViewer>("HomePage");
        var cards = Find<WrapPanel>("HomeCards");
        var open = Find<Button>("OpenLastDatabaseButton");
        Check(!open.IsEnabled && Find<TextBlock>("HomeDatabaseName").Text.Contains("не выбрана"), "Empty recent DB state");
        Check(vm.PageTitle == "Главная" && !vm.BreadcrumbText.Contains("database.res"), "Startup title implies open document");
        Check(Find<StackPanel>("DatabaseSaveActions").Visibility == Visibility.Collapsed, "Save shown without document");
        Check(Find<TextBlock>("HomeEditorVersion").Text == Find<TextBlock>("ProgramVersionText").Text.Trim().TrimStart('·').Trim(), "Editor revision differs");
        var productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(MainWindow).Assembly.Location);
        Check(int.TryParse(productVersion.ProductVersion, out int revision) && revision > 0,
            "ProductVersion must be an integer without preview or source suffix");
        Check(productVersion.FileVersion == $"{revision}.0.0.0", "Windows file version differs from product revision");
        Check(Find<TextBlock>("HomeEditorVersion").Text == $"Версия {revision}" &&
            Find<TextBox>("HomeComponentDetails").Text.StartsWith($"Редактор: Версия {revision}\n"),
            "Home details and card must display the same integer version");
        Check(!Find<TextBlock>("HomeMalachiteVersion").Text.Contains("0.4.0"), "Legacy module version leaked");
        Check(Find<TextBlock>("HomeMalachiteStatus").Text.StartsWith("Модуль загружен"), "Actual module not loaded");
        Check(Find<TextBlock>("HomeModelName").Text.Contains("Qwen"), "Installed module model resolver not used");
        Check(Find<TextBlock>("HomeModelStatus").Text.StartsWith("Файлы найдены"), "File discovery claimed server readiness");
        foreach (var size in new[] { (1440,900,1d), (1180,680,1d), (1180,680,1.5d) })
        {
            window.SetInterfaceScale(size.Item3, persist:false);
            root.Measure(new Size(size.Item1,size.Item2)); root.Arrange(new Rect(0,0,size.Item1,size.Item2)); root.UpdateLayout();
            foreach (FrameworkElement card in cards.Children)
            {
                var bounds=card.TransformToAncestor(page).TransformBounds(new Rect(card.RenderSize));
                Check(bounds.Right <= page.ActualWidth + 1 && bounds.Left >= 0, "Card clipped horizontally");
            }
            var bitmap = new RenderTargetBitmap(size.Item1,size.Item2,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(output,$"home-{size.Item1}-{size.Item3}.png")); png.Save(stream);
        }
        preferences.RememberDatabasePath(database);
        Console.WriteLine("PASS responsive layout; refresh recent DB");
        Find<Button>("HomeRefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.HomeRefreshTask;
        Check(open.IsEnabled && Find<TextBlock>("HomeDatabasePath").Text == Path.GetFullPath(database), "Recent DB path not refreshed");
        window.SetInterfaceScale(1, persist:false);
        root.Measure(new Size(1440,900)); root.Arrange(new Rect(0,0,1440,900)); root.UpdateLayout();
        var recentBitmap = new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32); recentBitmap.Render(root);
        var recentPng = new PngBitmapEncoder(); recentPng.Frames.Add(BitmapFrame.Create(recentBitmap));
        using (var stream=File.Create(Path.Combine(output,"home-recent-enabled.png"))) recentPng.Save(stream);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Console.WriteLine("Recent DB action dispatched");
        for(int n=0;n<300 && !vm.HasDocument;n++) await Task.Delay(20);
        Check(vm.HasDocument && vm.SourcePath == Path.GetFullPath(database), "Recent button did not open chosen DB");
        Check(page.Visibility == Visibility.Collapsed && Find<StackPanel>("DatabaseSaveActions").Visibility == Visibility.Visible, "Workspace transition broken");
        Check(vm.PageTitle == "Обзор базы", "Loaded title stale");
        Console.WriteLine("PASS opened DB; missing-path and metadata checks");
        string missing=Path.Combine(output,"removed.res"); File.WriteAllText(missing,"fixture"); preferences.RememberDatabasePath(missing); File.Delete(missing);
        Find<Button>("HomeRefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.HomeRefreshTask;
        Check(!open.IsEnabled && Find<TextBlock>("HomeDatabasePath").Text.Contains("Файл не найден"), "Missing recent file misreported");
        string fixture=Path.Combine(output,"module-fixture"); Directory.CreateDirectory(fixture);
        foreach(string name in new[]{"Malachite.Core.dll","Malachite.Module.dll","Malachite.UI.Wpf.dll"}) File.WriteAllText(Path.Combine(fixture,name),name);
        var first=HomeComponentInfo.Read(fixture,null);
        File.AppendAllText(Path.Combine(fixture,"Malachite.Core.dll"),"changed");
        Check(first.ModuleVersion!=HomeComponentInfo.Read(fixture,null).ModuleVersion,"Core-only update did not change build identifier");
        File.WriteAllText(Path.Combine(fixture,"manifest.json"),"{\"buildRevision\":12}");
        Check(HomeComponentInfo.Read(fixture,null).ModuleVersion=="Сборка №12","Explicit revision unsupported");
        File.WriteAllText(Path.Combine(fixture,"manifest.json"),"malformed");
        Check(HomeComponentInfo.Read(fixture,null).Details.Contains("Метаданные сборки"),"Corrupt metadata not explained");
        Check(!window.IsVisible,"Interactive window opened");
        if (Environment.GetEnvironmentVariable("DBE_HOME_INSTALLED_ROOT") is { Length: > 0 } installed)
        {
            var ui=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="Malachite.UI.Wpf");
            var real=HomeComponentInfo.Read(Path.Combine(installed,"Modules","malachite","current"),ui);
            Check(real.ModelName=="Qwen3.5-4B-Q4_K_M" && real.ModelStatus.StartsWith("Файлы найдены"),"Real installed Qwen resolution");
            File.WriteAllText(Path.Combine(output,"installed-components.txt"),real.ToString());
        }
        vm.Dispose();
        Console.WriteLine("PASS home verification complete");
    }
}
