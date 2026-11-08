using System.Numerics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed class MapObjectCatalogDialog : LuaToolWindow
{
    private readonly string _databasePath;
    private readonly TextBox _search = new() { Margin = new(4), ToolTip = "Поиск по имени модели или архиву" };
    private readonly ComboBox _archive = new() { Margin = new(4) };
    private readonly ListBox _list = new() { Margin = new(4), DisplayMemberPath = nameof(MapObjectResource.Label) };
    private readonly TextBlock _count = Paragraph("Загрузка каталога…");
    private readonly TextBlock _status = Paragraph("Выберите модель для просмотра.");
    private readonly TextBlock _textureCount = Paragraph("Поиск текстур…");
    private readonly TextBox _textureSearch = new() { Margin = new(4), ToolTip = "Поиск MMP по имени" };
    private readonly ComboBox _primary = new() { Margin = new(4), ToolTip = "Основная текстура модели" };
    private readonly ComboBox _secondary = new() { Margin = new(4), ToolTip = "Текстура второго слота модели" };
    private readonly Slider[] _morph = [new(), new(), new()];
    private readonly TextBlock[] _morphValues = [new(), new(), new()];
    private readonly Slider _scale = new() { Minimum = .25, Maximum = 3, Value = 1, TickFrequency = .05,
        IsSnapToTickEnabled = true, Margin = new(4), ToolTip = "Масштаб предпросмотра; работает и для моделей FIG1" };
    private readonly TextBlock _scaleValue = new() { Text = "100%", VerticalAlignment = VerticalAlignment.Center };
    private readonly UnitModelPreviewView _preview = new() { ExternalScene = true, AllowAnimationEditing = false };
    private readonly DispatcherTimer _morphDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private IReadOnlyList<MapObjectResource> _entries = [];
    private string[] _textureNames = [];
    private TextureResourceCatalog? _textureCatalog;
    private string? _textureProblem;
    private UnitModelScene? _geometry;
    private MapObjectResource? _geometryResource;
    private CancellationTokenSource? _loading;
    private bool _settingTextures;

    internal IReadOnlyList<MapObjectResource> Entries => _entries;
    internal IReadOnlyList<MapObjectResource> VisibleEntries => (_list.ItemsSource as MapObjectResource[]) ?? [];
    internal bool HasVisibleScene => _preview.HasVisibleScene;

    public MapObjectCatalogDialog(string databasePath) : base("Объекты карт из RES")
    {
        _databasePath = databasePath;
        Width = Math.Min(1340, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(890, SystemParameters.WorkArea.Height - 32);
        MinWidth = 900;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(255), MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290), MinWidth = 240 });
        var left = new DockPanel { LastChildFill = true };
        left.Children.Add(new TextBlock { Text = "Модели RES", FontWeight = FontWeights.SemiBold,
            Margin = new(4, 2, 4, 6), ToolTip = "Все .mod из RES рядом с БД, включая модели существ и предметов." });
        DockPanel.SetDock(left.Children[^1], Dock.Top);
        left.Children.Add(new TextBlock { Text = "Поиск моделей", Margin = new(4, 2, 4, 0) });
        DockPanel.SetDock(left.Children[^1], Dock.Top);
        _search.ToolTip = "Живой поиск по имени модели или архива";
        left.Children.Add(_search); DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_archive); DockPanel.SetDock(_archive, Dock.Top);
        left.Children.Add(_count); DockPanel.SetDock(_count, Dock.Top);
        left.Children.Add(_list);
        grid.Children.Add(left);
        var leftSplitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(leftSplitter, 1); grid.Children.Add(leftSplitter);

        var viewer = new DockPanel { LastChildFill = true };
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = System.Windows.TextTrimming.CharacterEllipsis;
        _status.ToolTip = "Текущая модель, архив и назначенная текстура. Все изменения относятся только к предпросмотру.";
        DockPanel.SetDock(_status, Dock.Bottom); viewer.Children.Add(_status);
        viewer.Children.Add(_preview);
        Grid.SetColumn(viewer, 2); grid.Children.Add(viewer);
        var rightSplitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(rightSplitter, 3); grid.Children.Add(rightSplitter);

        var controls = new StackPanel { Margin = new(6, 0, 2, 0) };
        var inspector = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(inspector, 4); grid.Children.Add(inspector);
        var heading = new DockPanel(); controls.Children.Add(heading);
        var reset = ActionButton("Сбросить", () =>
        {
            foreach (var slider in _morph) slider.Value = .5;
            _scale.Value = 1;
        });
        reset.ToolTip = "Вернуть исходные форму 0,50 и размер 100%";
        DockPanel.SetDock(reset, Dock.Right); heading.Children.Add(reset);
        heading.Children.Add(new TextBlock { Text = "Вид модели", FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(4) });
        controls.Children.Add(new TextBlock { Text = "Общий размер", Margin = new(4, 8, 4, 0),
            ToolTip = "Масштаб только в предпросмотре, работает и для FIG1" });
        var scaleRow = new DockPanel(); controls.Children.Add(scaleRow);
        DockPanel.SetDock(_scaleValue, Dock.Right); scaleRow.Children.Add(_scaleValue);
        scaleRow.Children.Add(_scale);
        _scale.ValueChanged += (_, _) =>
        {
            _scaleValue.Text = $"{_scale.Value * 100:F0}%";
            _morphDelay.Stop(); _morphDelay.Start();
        };

        var morphControls = new StackPanel();
        var morphExpander = new Expander { Header = "Форма FIG/BON", Content = morphControls,
            IsExpanded = false, Margin = new(4, 10, 4, 2),
            ToolTip = "Сила, ловкость и рост как в маппере; для FIG1 форма не меняется" };
        controls.Children.Add(morphExpander);
        string[] labels = ["Сила / ширина", "Ловкость / форма", "Рост / масштаб"];
        for (int i = 0; i < _morph.Length; i++)
        {
            int axis = i;
            var row = new StackPanel { Margin = new(2, 6, 2, 2) };
            var caption = new DockPanel();
            _morphValues[i].Text = "0,50";
            DockPanel.SetDock(_morphValues[i], Dock.Right);
            caption.Children.Add(_morphValues[i]);
            caption.Children.Add(new TextBlock { Text = labels[i] });
            row.Children.Add(caption);
            _morph[i].Minimum = 0; _morph[i].Maximum = 1; _morph[i].Value = .5;
            _morph[i].TickFrequency = .01; _morph[i].IsSnapToTickEnabled = true;
            _morph[i].ToolTip = labels[i] + ": меняет геометрию FIG8 и положение BON";
            _morph[i].ValueChanged += (_, _) =>
            {
                _morphValues[axis].Text = _morph[axis].Value.ToString("F2");
                _morphDelay.Stop(); _morphDelay.Start();
            };
            row.Children.Add(_morph[i]); morphControls.Children.Add(row);
        }
        controls.Children.Add(new TextBlock { Text = "Текстуры MMP", FontWeight = FontWeights.SemiBold,
            Margin = new(4, 12, 4, 2), ToolTip = "Подбор меняет только предпросмотр, не файлы карты" });
        controls.Children.Add(new TextBlock { Text = "Поиск по имени", Margin = new(4, 4, 4, 0) });
        controls.Children.Add(_textureSearch);
        controls.Children.Add(new TextBlock { Text = "Основная", Margin = new(4, 6, 4, 0) });
        controls.Children.Add(_primary);
        controls.Children.Add(new TextBlock { Text = "Второй слот", Margin = new(4, 6, 4, 0) });
        controls.Children.Add(_secondary);
        controls.Children.Add(_textureCount);
        var refresh = ActionButton("Обновить ресурсы", async () => await ReloadAsync());
        refresh.ToolTip = "Повторно прочитать доступные RES и MMP";
        controls.Children.Add(refresh);
        Body.Children.Add(grid);
        _search.TextChanged += (_, _) => Filter();
        _archive.SelectionChanged += (_, _) => Filter();
        _list.SelectionChanged += async (_, _) => { _textureSearch.Clear(); SelectSuggestedTexture(); await ShowSelectedAsync(); };
        _textureSearch.TextChanged += (_, _) => FilterTextures();
        _primary.SelectionChanged += async (_, _) => { if (!_settingTextures) await ShowSelectedAsync(false); };
        _secondary.SelectionChanged += async (_, _) => { if (!_settingTextures) await ShowSelectedAsync(false); };
        _morphDelay.Tick += async (_, _) => { _morphDelay.Stop(); await ShowSelectedAsync(); };
        Loaded += async (_, _) => await ReloadAsync();
        Closing += (_, _) => { _morphDelay.Stop(); _loading?.Cancel(); _loading?.Dispose(); _loading = null; };
    }

    private async Task ReloadAsync()
    {
        try
        {
            _entries = await MapObjectResourceCatalog.DiscoverAsync(_databasePath);
            try
            {
                var sources = await TextureSources.FromDatabaseFolderAsync(_databasePath);
                _textureCatalog = await TextureResourceCatalog.OpenAsync(sources);
                _textureNames = _textureCatalog.ResourceNames.ToArray();
                _textureProblem = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _textureCatalog = null; _textureNames = [];
                _textureProblem = "Текстуры недоступны: " + ex.Message;
            }
            _archive.ItemsSource = new[] { "Все архивы" }
                .Concat(_entries.Select(e => e.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
            _archive.SelectedIndex = 0;
            Filter();
            _list.SelectedItem = VisibleEntries.FirstOrDefault(e => e.ArchiveName.Contains("Mapobject", StringComparison.OrdinalIgnoreCase))
                ?? VisibleEntries.FirstOrDefault();
            SelectSuggestedTexture();
            await ShowSelectedAsync();
        }
        catch (Exception ex) { _count.Text = "Не удалось прочитать каталог: " + ex.Message; }
    }

    private void Filter()
    {
        string query = _search.Text.Trim();
        string? archive = _archive.SelectedIndex > 0 ? _archive.SelectedItem as string : null;
        var previous = _list.SelectedItem as MapObjectResource;
        var matches = _entries.Where(e => (archive is null || e.ArchiveName.Equals(archive, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || e.Model.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             e.ArchiveName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _list.ItemsSource = matches;
        if (previous is not null && matches.Contains(previous)) _list.SelectedItem = previous;
        _count.Text = $"Найдено: {matches.Length} из {_entries.Count}";
    }

    private void FilterTextures()
    {
        string query = _textureSearch.Text.Trim();
        string model = (_list.SelectedItem as MapObjectResource)?.Model ?? "";
        string? primary = _primary.SelectedItem as string;
        string? secondary = _secondary.SelectedItem as string;
        var options = _textureNames.Where(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name.StartsWith(model, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        foreach (var selected in new[] { primary, secondary })
            if (selected is not null && selected != "Без текстуры" && !options.Contains(selected, StringComparer.OrdinalIgnoreCase)) options.Add(selected);
        options.Insert(0, "Без текстуры");
        _settingTextures = true;
        _primary.ItemsSource = options; _secondary.ItemsSource = options;
        _primary.SelectedItem = primary is not null && options.Contains(primary) ? primary : "Без текстуры";
        _secondary.SelectedItem = secondary is not null && options.Contains(secondary) ? secondary : "Без текстуры";
        _settingTextures = false;
        int found = _textureNames.Count(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase));
        _textureCount.Text = _textureProblem ?? $"MMP: {found} совпадений, показаны первые {Math.Min(300, found)}. " +
            (_textureCatalog?.Complete == true ? "" : "Доступна лишь часть игровых ресурсов; выбранный вид проверьте на карте.");
    }

    private void SelectSuggestedTexture()
    {
        string model = (_list.SelectedItem as MapObjectResource)?.Model ?? "";
        FilterTextures();
        string? exact = new[] { model + ".x4.mmp", model + ".x2.mmp", model + ".mmp" }
            .FirstOrDefault(candidate => _textureNames.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        _settingTextures = true;
        _primary.SelectedItem = exact is not null && (_primary.ItemsSource as List<string>)?.Contains(exact) == true ? exact : "Без текстуры";
        _secondary.SelectedItem = "Без текстуры";
        _settingTextures = false;
    }

    private async Task ShowSelectedAsync(bool reloadGeometry = true)
    {
        _loading?.Cancel(); _loading?.Dispose();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        if (_list.SelectedItem is not MapObjectResource selected) return;
        _status.Text = "Загрузка: " + selected.Label;
        try
        {
            UnitModelScene geometry;
            if (!reloadGeometry && _geometry is not null && selected.Equals(_geometryResource)) geometry = _geometry;
            else
            {
                var morph = new Vector3((float)_morph[0].Value, (float)_morph[1].Value, (float)_morph[2].Value);
                geometry = await MapObjectResourceCatalog.LoadAsync(selected, morph, token);
            }
            UnitModelScene scene = geometry;
            if (_textureCatalog is not null)
                scene = await MapObjectResourceCatalog.WithTexturesAsync(geometry, _textureCatalog,
                    SelectedTexture(_primary), SelectedTexture(_secondary), token);
            scene = MapObjectResourceCatalog.Scale(scene, (float)_scale.Value);
            if (token.IsCancellationRequested || !selected.Equals(_list.SelectedItem)) return;
            bool sameModel = selected.Equals(_geometryResource);
            _geometry = geometry; _geometryResource = selected;
            _preview.ShowScene(scene, item: true, preserveCamera: sameModel);
            int textured = scene.Meshes.Count(mesh => scene.Textures.ContainsKey(mesh.TextureSlot));
            string textureInfo = SelectedTexture(_primary) is { } name
                ? $" · MMP: {name} ({System.IO.Path.GetFileName(_textureCatalog?.Check(name).Source)})"
                : " · MMP не выбрана";
            _status.Text = $"{selected.Label} · {scene.Meshes.Count} деталей, с текстурой {textured}" + textureInfo;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = selected.Label + ": " + ex.Message; }
    }

    private static string? SelectedTexture(ComboBox choice) => choice.SelectedItem is string name && name != "Без текстуры" ? name : null;
}
