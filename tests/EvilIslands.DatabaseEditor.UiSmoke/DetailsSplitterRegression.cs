using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.App.Models;
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
        await CheckCompactTexts(window, vm, output, Layout);
        foreach (double scale in new[] { 0.75, 0.8, 0.9, 1.0 })
        {
            window.SetInterfaceScale(scale, persist: true);
            Layout();
            if (window.CurrentInterfaceScale != scale || new EditorPreferencesStore(settings).GetInterfaceScale() != scale)
                throw new Exception("Scale did not persist");
        }
        string informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string revision = informational;
        if (((TextBlock)window.FindName("ProgramVersionText")).Text.Trim() != "·   Версия " + revision) throw new Exception("Incorrect version footer");
        await CheckOtherCompactTabs(window, vm, output, Layout);
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

    private static async Task CheckOtherCompactTabs(MainWindow window, MainWindowViewModel vm, string output, Action layout)
    {
        T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var pane = (TabControl)window.FindName("DetailsPane");
        var materials = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "items.materials");
        vm.SelectTableCommand.Execute(materials); vm.SelectedRecord = materials.Records.First();
        ((TabItem)window.FindName("TextureTab")).IsSelected = true;
        await vm.RecheckTexturesAsync();
        layout();
        var search = (TextBox)window.FindName("TextureSearchBox");
        if (search.ActualWidth > 321 || search.ActualHeight > 29) throw new Exception("Texture search oversized");
        int count = vm.TextureView.Cast<object>().Count();
        if (count == 0) throw new Exception("Texture fixture contains no references");
        search.Text = "__no_such_texture__";
        if (vm.TextureView.Cast<object>().Any()) throw new Exception("Texture search binding lost");
        search.Clear();
        if (vm.TextureView.Cast<object>().Count() != count) throw new Exception("Texture search clear lost rows");
        var filter = (ComboBox)window.FindName("TextureFilterBox");
        filter.SelectedIndex = 1;
        if (vm.TextureFilterIndex != 1) throw new Exception("Texture filter binding lost");
        filter.SelectedIndex = 0;
        Render("compact-textures.png");

        ((TabItem)window.FindName("ModelTab")).IsSelected = true;
        var model = (UnitModelPreviewView)window.FindName("DetailsModelPreview");
        // Show the full animation toolbar without loading game resources or starting playback.
        Field<WrapPanel>(model, "_animationTools").Visibility = Visibility.Visible;
        layout();
        var info = ((Grid)model.Content).Children.OfType<Expander>().Single();
        if (info.IsExpanded || ((Grid)model.Content).RowDefinitions[2].ActualHeight > 32) throw new Exception("Model info still reserves large row");
        var clipSearch = Field<TextBox>(model, "_clipSearch");
        if (clipSearch.ActualHeight > 29) throw new Exception("Model controls not compact");
        info.IsExpanded = true; layout();
        if (((TextBox)info.Content).ActualHeight < 70) throw new Exception("Model details inaccessible");
        info.IsExpanded = false;
        Render("compact-model.png");

        var effectsTab = (TabItem)window.FindName("SpellEffectsTab"); effectsTab.IsSelected = true;
        var effects = ((DockPanel)effectsTab.Content).Children.OfType<SpellVisualEffectsView>().Single();
        var rows = new[] { new SpellVisualEffectsView.ResourceRow("Удар", "spark.mmp", "Не найдена", "", ""), new SpellVisualEffectsView.ResourceRow("Свет", "glow.mmp", "Не найдена", "", "") };
        typeof(SpellVisualEffectsView).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(effects, rows);
        var effectSearch = Field<TextBox>(effects, "_search");
        var effectList = Field<DataGrid>(effects, "_list");
        effectSearch.Text = "spark";
        if (effectList.Items.Count != 1) throw new Exception("Effects live search failed");
        effectSearch.Clear();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); layout();
        if (effectList.Items.Count != 2 || effectSearch.ActualHeight > 29 || effectSearch.ActualWidth > 321) throw new Exception("Compact effects filter failed");
        if (effectList.Columns.Any(c => c.ActualWidth < 80)) throw new Exception("Effect columns clipped: " + string.Join(",", effectList.Columns.Select(c => c.ActualWidth)));
        Render("compact-effects.png");
        pane.SelectedIndex = 0;
        Console.WriteLine("PASS other details tabs: compact controls, texture/effect filtering, expandable model details, four renders; no game-resource writes/playback");

        void Render(string name)
        {
            layout(); var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name)); png.Save(file);
        }
    }

    private static async Task CheckCompactTexts(MainWindow window, MainWindowViewModel vm, string output, Action layout)
    {
        var grid = (DataGrid)window.FindName("LocalizationGrid");
        var search = (TextBox)window.FindName("RelatedTextSearch");
        var record = vm.SelectedTable!.Records.First(r => r.RelatedTexts.Count >= 2);
        vm.SelectedRecord = record;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        layout();
        var entry = record.RelatedTexts.First(t => !string.IsNullOrWhiteSpace(t.Description));
        int count = grid.Items.Count;
        search.Text = "__no_such_related_text__";
        if (grid.Items.Count != 0) throw new Exception("Related text search did not filter live");
        search.Text = entry.ResourceName;
        if (grid.Items.Count != 1 || !ReferenceEquals(grid.Items[0], entry)) throw new Exception("Related text key search failed");
        search.Clear();
        if (grid.Items.Count != count) throw new Exception("Clearing search lost text rows");
        layout();
        if (search.ActualWidth > 321 || search.ActualHeight > 29 || search.ActualHeight < 26)
            throw new Exception("Related search is not compact");
        var column = grid.Columns.Single(c => Equals(c.Header, "Описание RU"));
        grid.ScrollIntoView(entry, column); layout();
        var text = (TextBlock)column.GetCellContent(entry);
        if (text.ActualHeight > 28 || text.FontSize < 13 || text.ToolTip?.ToString() != entry.Description)
            throw new Exception("Compact text clipped or full description inaccessible");
        grid.SelectedCells.Clear();
        grid.CurrentCell = new DataGridCellInfo(entry, column);
        grid.SelectedCells.Add(grid.CurrentCell);
        if (!grid.BeginEdit()) throw new Exception("Related text edit unavailable");
        layout();
        var editor = (TextBox)column.GetCellContent(entry);
        if (editor.ActualHeight > 28 || editor.ActualHeight < 18) throw new Exception("Related text editor clipped");
        string original = entry.Description;
        editor.Text = original + " compact-check";
        editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (entry.Description == original) throw new Exception("Related description edit did not commit to draft");
        vm.UndoCommand.Execute(null);
        if (entry.Description != original) throw new Exception("Related description undo failed");
        layout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "compact-details.png"))) png.Save(file);
        // Synthetic missing sound links exercise layout/filtering without loading live audio archives.
        var soundTable = vm.Databases.SelectMany(d => d.Tables).First(t => t.Key.StartsWith("acks."));
        vm.SelectTableCommand.Execute(soundTable);
        var soundRecord = soundTable.Records.First();
        soundRecord.SetRelatedSounds(new[] { "first.wav", "second.wav" }.Select(name => new SoundLinkViewModel(
            new(EvilIslands.DatabaseEditor.Core.DatabaseSoundLinkKind.DirectResource, "Тестовая реплика", name, null, true))).ToArray());
        vm.SelectedRecord = soundRecord;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); layout();
        var sounds = (DataGrid)window.FindName("SoundLinksGrid");
        var soundSearch = (TextBox)window.FindName("RelatedSoundSearch");
        int soundCount = sounds.Items.Count;
        soundSearch.Text = "__no_such_sound__";
        if (sounds.Items.Count != 0) throw new Exception("Sound search did not filter live");
        soundSearch.Clear(); layout();
        if (sounds.Items.Count != soundCount || soundSearch.ActualWidth > 321 || soundSearch.ActualHeight > 29)
            throw new Exception("Compact sound search failed");
        bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "compact-sounds.png"))) png.Save(file);
        Console.WriteLine("PASS compact details: live text/sound filtering, full description, inline edit/undo, headless renders");
    }
}
