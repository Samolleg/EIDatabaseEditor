using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class DetailsSplitterRegression
{
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task task = Check(database, output);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS details splitter: no selection, native resize, tab/selection persistence, expand/return; no windows shown"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Check(string database, string output)
    {
        string settings = Path.Combine(output, "settings.json");
        var window = new MainWindow(new EditorPreferencesStore(settings), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var detached=new EvilIslands.DatabaseEditor.App.Dialogs.DetachedDetailsWindow();window.BindDetachedScale(detached);
        foreach(double scale in new[]{.75,1.25,1.0}){window.SetInterfaceScale(scale,false);var transform=(System.Windows.Media.ScaleTransform)((FrameworkElement)detached.Content).LayoutTransform;if(transform.ScaleX!=scale||transform.ScaleY!=scale)throw new Exception("Detached scale mismatch");}
        var names=new AnimationNames(Path.Combine(output,"animation-names.json"));names.Save("test-model","cidle18","Ожидание с оружием");
        if(new AnimationNames(Path.Combine(output,"animation-names.json")).Get("test-model","cidle18")!="Ожидание с оружием")throw new Exception("Animation label not persisted");
        if(names.Get("other-model","cidle18")!="")throw new Exception("Animation label crossed model boundary");
        Console.WriteLine("CATALOG/SCALE PASS: persistent labels and detached75/125/100 percent");
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(database);
        var table = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "items.armors");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = null;
        var content = (FrameworkElement)window.Content;
        void Layout() { content.Measure(new Size(1600, 1000)); content.Arrange(new Rect(0, 0, 1600, 1000)); content.UpdateLayout(); }
        var splitter = (GridSplitter)window.FindName("DetailsPaneSplitter");
        var pane = (TabControl)window.FindName("DetailsPane");
        var row = (RowDefinition)window.FindName("DetailsPaneRow");
        Layout();
        if (splitter.Visibility != Visibility.Visible || splitter.ActualHeight < 1) throw new Exception("No-selection splitter unavailable");
        // Execute WPF's actual keyboard resize path, including InitializeData and MoveSplitter.
        double before = row.ActualHeight;
        var move = typeof(GridSplitter).GetMethod("KeyboardMoveSplitter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        move.Invoke(splitter, [0.0, 40.0]);
        Layout();
        if (Math.Abs(row.ActualHeight - before) < 20) throw new Exception("Native splitter did not resize rows");
        splitter.RaiseEvent(new DragCompletedEventArgs(0, 40, false) { RoutedEvent = Thumb.DragCompletedEvent });
        double saved = new EditorPreferencesStore(settings).GetLayout().DetailsPaneHeight;
        if (Math.Abs(saved - row.ActualHeight) > 1) throw new Exception("Empty-selection height not persisted");
        vm.SelectedRecord = table.Records.First();
        var keys = (CheckBox)window.FindName("ShowTextsKeysCheckBox");
        var records = (DataGrid)window.FindName("RecordsGrid");
        var relatedKey = (DataGridColumn)window.FindName("RelatedTextsKeyColumn");
        DataGridColumn KeyColumn() => records.Columns.Single(c => c.Header?.ToString() == "Ключ texts.res");
        if (keys.IsChecked == true || KeyColumn().Visibility != Visibility.Collapsed || relatedKey.Visibility != Visibility.Collapsed)
            throw new Exception("Key columns must default hidden");
        keys.IsChecked = true;
        if (KeyColumn().Visibility != Visibility.Visible || relatedKey.Visibility != Visibility.Visible || !new EditorPreferencesStore(settings).GetShowTextsKeys())
            throw new Exception("Key toggle did not show/persist both columns");
        vm.SelectTableCommand.Execute(vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "items.materials"));
        vm.SelectedRecord = vm.SelectedTable!.Records.First();
        if (KeyColumn().Visibility != Visibility.Visible) throw new Exception("Table switch lost key preference");
        keys.IsChecked = false;
        if (KeyColumn().Visibility != Visibility.Collapsed || relatedKey.Visibility != Visibility.Collapsed) throw new Exception("Key toggle did not hide both columns");
        foreach (double scale in new[] { 0.75, 0.8, 0.9, 1.0 })
        {
            window.SetInterfaceScale(scale, persist: true);
            Layout();
            if (window.CurrentInterfaceScale != scale || new EditorPreferencesStore(settings).GetInterfaceScale() != scale)
                throw new Exception("Scale did not persist");
        }
        string informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string revision = informational.Split("preview.").Last().Split('+')[0];
        if (((TextBlock)window.FindName("ProgramVersionText")).Text.Trim() != "·   Версия " + revision) throw new Exception("Incorrect version footer");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = table.Records.First();

        foreach (string name in new[] { "TextureTab", "ModelTab", "SpellEffectsTab" })
        {
            ((TabItem)window.FindName(name)).IsSelected = true;
            Layout();
            if (Math.Abs(row.ActualHeight - saved) > 1) throw new Exception("Tab reset height: " + name);
        }
        ((TabItem)window.FindName("TextureTab")).IsSelected = true;
        ((Button)window.FindName("TextureExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (splitter.Visibility != Visibility.Collapsed) throw new Exception("Expanded pane exposes splitter");
        ((Button)window.FindName("TextureExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (splitter.Visibility != Visibility.Visible || Math.Abs(row.ActualHeight - saved) > 1) throw new Exception("Expand/return lost height");
        pane.SelectedIndex = 0;
        vm.SelectedRecord = null;
        Layout();
        if (splitter.Visibility != Visibility.Visible || Math.Abs(row.ActualHeight - saved) > 1) throw new Exception("Clearing selection lost height");
        window.Close();
    }
}
