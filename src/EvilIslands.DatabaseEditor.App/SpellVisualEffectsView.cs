using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App;

public sealed class SpellVisualEffectsView : UserControl
{
    private readonly Button _bind = new() { Content = "Отдельная текстура…", Margin = new(4), IsEnabled = false };
    internal sealed record ResourceRow(string Stage, string Name, string Status, string Source, string Versions);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6) };
    private readonly TextBox _search = new() { MinWidth = 180, Margin = new(4), ToolTip = "Поиск по имени текстуры, эффекту и архиву" };
    private readonly CheckBox _library = new() { Content = "Известные атласы эффектов", Margin = new(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false };
    private readonly TextureImageView _image = new() { Height = 240 };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = Brushes.Transparent };
    private readonly Button _open = new() { Content = "Открыть / заменить в RES…", Margin = new(4), IsEnabled = false };
    private MainWindowViewModel? _vm;
    private TextureResourceCatalog? _catalog;
    private string? _catalogPath;
    private ResourceRow[] _rows = [];
    private CancellationTokenSource? _loadCancellation, _previewCancellation;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;
    internal Task PreviewTask { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<ResourceRow> Rows => _rows;
    internal string StatusText => _status.Text;
    internal string DetailText => _details.Text;

    public SpellVisualEffectsView()
    {
        var layout = new DockPanel { Margin = new(8) };
        DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);
        var tools = new DockPanel();
        var refresh = new Button { Content = "Обновить", Margin = new(4) };
        refresh.Click += (_, _) => { _catalog = null; QueueLoad(); };
        DockPanel.SetDock(refresh, Dock.Right); tools.Children.Add(refresh);
        DockPanel.SetDock(_library, Dock.Right); tools.Children.Add(_library); tools.Children.Add(_search);
        DockPanel.SetDock(tools, Dock.Top); layout.Children.Add(tools);
        var limitation = new TextBlock { Margin = new(6), TextWrapping = TextWrapping.Wrap,
            Text = "Имена атласов и движение частиц заданы кодом игры/JMV. Замена общего MMP затрагивает все его эффекты. " +
                "При замене сохраняйте раскладку кадров и прозрачность. Для привязки копии к одному заклинанию используйте «Отдельная текстура…» и addon с поддержкой пакета привязок." };
        DockPanel.SetDock(limitation, Dock.Bottom); layout.Children.Add(limitation);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (header, path, width) in new[] { ("Эффект / этап", "Stage", 1d), ("Текстура", "Name", 1d), ("Наличие", "Status", .7d) })
            _list.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        grid.Children.Add(_list);
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(split, 1); grid.Children.Add(split);
        var card = new StackPanel { Margin = new(8, 0, 0, 0) };
        card.Children.Add(_open); card.Children.Add(_bind); card.Children.Add(_image); card.Children.Add(_details);
        var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2); grid.Children.Add(scroll); layout.Children.Add(grid); Content = layout;
        _search.TextChanged += (_, _) => Filter();
        _library.Checked += (_, _) => QueueLoad(); _library.Unchecked += (_, _) => QueueLoad();
        _list.SelectionChanged += (_, _) => QueuePreview();
        _open.Click += (_, _) => OpenResource();
        _bind.Click += async (_, _) => await BindResourceAsync();
        Loaded += (_, _) => Attach();
        DataContextChanged += (_, _) => Attach();
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueLoad(); else Cancel(); };
        Unloaded += (_, _) => { Cancel(); Detach(); };
    }
    private void Detach() { if (_vm is not null) _vm.PropertyChanged -= Changed; _vm = null; }
    private void Attach() { Detach(); _vm = DataContext as MainWindowViewModel; if (_vm is not null) _vm.PropertyChanged += Changed; QueueLoad(); }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRecord) or nameof(MainWindowViewModel.SourcePath))
        { _search.Text = ""; QueueLoad(); }
        if (e.PropertyName == nameof(MainWindowViewModel.TextureSourcesDescription)) { _catalog = null; QueueLoad(); }
    }
    private void Cancel()
    {
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null;
    }
    private void QueueLoad()
    {
        Cancel(); _bind.IsEnabled = false; _rows = []; _list.ItemsSource = null; _image.Bitmap = null; _details.Text = ""; _open.IsEnabled = false;
        if (!IsVisible || _vm is null) return;
        if (_vm.SelectedRecord is not { TableKey: "spells.prototypes" } row)
        { _status.Text = "Выберите прототип заклинания. Его иконка находится на вкладке «Текстуры»."; return; }
        var record = ModelRecord.Capture(row.TableKey, row.CoreRecord);
        var token = (_loadCancellation = new()).Token;
        _status.Text = "Поиск атласов эффекта…";
        LoadTask = LoadAsync(_vm.SourcePath, record, row.CoreRecord.RecordIndex, _library.IsChecked == true, _vm.SpellPreviewTextureSources, token);
    }
    private async Task LoadAsync(string path, ModelRecord record, int recordIndex, bool library, TextureSources? sources, CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token);
            var catalog = _catalog;
            if (catalog is null || _catalogPath != path || !await Task.Run(catalog.IsCurrent, token))
            {
                sources ??= await TextureSources.FromDatabaseFolderAsync(path, token);
                if (sources.GameFolder is { } game)
                    sources = TextureSources.FromGame(game, sources.ConfigurationPath, ["textures.res"]);
                catalog = await TextureResourceCatalog.OpenAsync(sources, token);
            }
            token.ThrowIfCancellationRequested(); _catalog = catalog; _catalogPath = path;
            var bindings = library ? SpellVisualEffects.Bindings : SpellVisualEffects.ForSpell(record.Text(1), recordIndex);
            _rows = bindings.GroupBy(b => b.Texture, StringComparer.OrdinalIgnoreCase).SelectMany(g => SpellVisualEffects.Variants(g.Key).Select(name =>
            {
                var check = catalog.Check(name); var match = check.Matches.FirstOrDefault();
                return new ResourceRow(string.Join("; ", g.Select(b => b.Stage).Distinct()), name,
                    match is null ? "Не найдена" : match.HeaderProblem is null ? "Найдена" : "Ошибка MMP",
                    match?.Source ?? "", string.Join(Environment.NewLine, check.Matches.Select(m => m.Source)));
            })).ToArray();
            _status.Text = record.Name + " · код: " + record.Text(1) + "\n" +
                (bindings.Count == 0 ? "Подтверждённой связи в справочнике пока нет. Это не означает отсутствия эффекта. Откройте каталог известных атласов для ручного поиска."
                    : "Известные связи EI/JMV; набор фаз неполный. Обычная и .x2 версии показаны отдельно, выбор игры зависит от графического режима.") +
                (catalog.Sources.GameFolder is null ? "\nИсточники подключены вручную или рядом с БД; группа и порядок игры не подтверждены." : "") +
                "\nИконка — отдельно на вкладке «Текстуры». Цвет, радиус и затухание света редактируются в полях БД.";
            Filter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = "Не удалось прочитать эффекты: " + ex.Message; }
    }
    private void Filter()
    {
        var previous = _list.SelectedItem as ResourceRow;
        var rows = _rows.Where(r => (r.Name + " " + r.Stage + " " + r.Source).Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.Contains(previous) ? previous : rows.FirstOrDefault(r => r.Source.Length > 0) ?? rows.FirstOrDefault();
    }
    private void QueuePreview()
    {
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new();
        _image.Bitmap = null; _open.IsEnabled = false; _bind.IsEnabled = false; _details.Text = "";
        if (_list.SelectedItem is not ResourceRow row || _catalog is null) return;
        string users = string.Join(", ", SpellVisualEffects.KnownUsers(row.Name));
        _details.Text = row.Name + "\n" + row.Stage + "\n\nИсточник:\n" + (row.Source.Length > 0 ? row.Source : "Не найден в подключённых источниках.") +
            "\n\nВсе версии в порядке источников:\n" + row.Versions + "\n\nИзвестные заклинания с этим атласом:\n" + users +
            "\nЭто неполный перечень использований: атлас также могут использовать другие частицы, предметы и сценарии.";
        _open.IsEnabled = row.Source.EndsWith(".res", StringComparison.OrdinalIgnoreCase);
        _bind.IsEnabled = row.Source.Length > 0 && _library.IsChecked != true && _vm?.SelectedRecord?.TableKey == "spells.prototypes";
        PreviewTask = PreviewAsync(row, _catalog, _previewCancellation.Token);
    }
    private async Task PreviewAsync(ResourceRow row, TextureResourceCatalog catalog, CancellationToken token)
    {
        if (row.Source.Length == 0) return;
        try
        {
            var image = await catalog.ReadPreviewAsync(row.Name, token); token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
                image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bitmap.Freeze(); _image.Bitmap = bitmap;
            _details.Text = $"{image.Width} × {image.Height} · {image.Format}\n" + _details.Text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _details.Text += "\nОшибка предпросмотра: " + ex.Message; }
    }
    private void OpenResource()
    {
        if (_vm is null || _catalog is null || _list.SelectedItem is not ResourceRow row || !row.Source.EndsWith(".res", StringComparison.OrdinalIgnoreCase)) return;
        if (!_catalog.IsCurrent()) { _catalog = null; QueueLoad(); return; }
        var locked = new[] { _vm.SourcePath, _vm.TextsPath }.Concat(_vm.SoundArchivePaths.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        var dialog = new ResFileManagerDialog(row.Source, locked) { Owner = Window.GetWindow(this), InitialResourceName = row.Name };
        dialog.ShowDialog();
        _catalog = null; QueueLoad();
    }
    private async Task BindResourceAsync()
    {
        if (_catalog is null || _library.IsChecked == true || _list.SelectedItem is not ResourceRow row ||
            _vm?.SelectedRecord is not { TableKey: "spells.prototypes" } selected) return;
        var code = ModelRecord.Capture(selected.TableKey, selected.CoreRecord).Text(1);
        var catalog = _catalog; _bind.IsEnabled = false;
        try
        {
            byte[] data = await Task.Run(() => SpellTexturePack.ReadTexture(catalog, row.Name));
            if (!ReferenceEquals(selected, _vm?.SelectedRecord) || !IsVisible) return;
            new SpellTextureBindingDialog(code, row.Name, data) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { _details.Text += "\nНе удалось подготовить привязку: " + ex.Message; }
        finally { _bind.IsEnabled = _list.SelectedItem is ResourceRow current && current.Source.Length > 0 && _library.IsChecked != true; }
    }
}
