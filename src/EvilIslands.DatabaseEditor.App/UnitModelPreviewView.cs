using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App;

public sealed partial class UnitModelPreviewView : UserControl
{
    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _visual = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly TextBox _information = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 40, UpDirection = new Vector3D(0, 1, 0) };
    private MainWindowViewModel? _vm;
    private readonly ComboBox _objects = new() { Width = 280, DisplayMemberPath = "Label", IsTextSearchEnabled = true, Visibility = Visibility.Collapsed };
    private readonly TextBox _objectSearch = new() { Width = 240, Margin = new Thickness(4), ToolTip = "Поиск по прототипу, имени объекта или ID", Visibility = Visibility.Collapsed };
    private readonly TextBlock _objectCount = new() { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<MobAppearance> _mobEntries = [];
    private bool _mobMode;
    private readonly WrapPanel _mobTools = new();
    private readonly WrapPanel _itemTools = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _choiceLabel = new() { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _itemChoice = new() { Width = 320, DisplayMemberPath = "Name", IsTextSearchEnabled = true, Margin = new Thickness(4) };
    private bool _settingChoice;
    private string _choiceContext = "";
    private CancellationTokenSource? _cancellation;
    private Point? _drag;
    private Point3D _center;
    private double _radius = 1, _distance = 5, _yaw = 0.7, _pitch = 0.15;
    private bool _itemScene;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    public UnitModelPreviewView()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(105) });
        var toolbar = new DockPanel();
        var mobTools = _mobTools;
        var mob = new Button { Content = "Внешность из MOB…", Margin = new Thickness(4) };
        mob.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Карта MOB|*.mob", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            mob.IsEnabled = false;
            try
            {
                var entries = await Task.Run(() => MobAppearance.Read(dialog.FileName));
                SetMobEntries(entries, _vm?.SelectedRecord?.CoreRecord.IdentityValue ?? "");
            }
            catch (Exception ex) { _status.Text = "Не удалось прочитать MOB: " + ex.Message; }
            finally { mob.IsEnabled = true; }
        };
        var database = new Button { Content = "Из БД", Margin = new Thickness(4) };
        database.Click += (_, _) => { _mobMode = false; _objects.SelectedItem = null; _objects.Visibility = _objectSearch.Visibility = Visibility.Collapsed; _objectCount.Text = ""; QueueLoad(); };
        _objectSearch.TextChanged += (_, _) => FilterObjects();
        _objects.SelectionChanged += (_, _) => QueueLoad();
        mobTools.Children.Add(mob); mobTools.Children.Add(_objectSearch); mobTools.Children.Add(_objects); mobTools.Children.Add(_objectCount); mobTools.Children.Add(database);
        DockPanel.SetDock(mobTools, Dock.Top); toolbar.Children.Add(mobTools);
        _itemTools.Children.Add(_choiceLabel); _itemTools.Children.Add(_itemChoice);
        _itemChoice.SelectionChanged += (_, _) => { if (!_settingChoice) QueueLoad(); };
        DockPanel.SetDock(_itemTools, Dock.Top); toolbar.Children.Add(_itemTools);
        InitializeAnimationControls(toolbar);
        InitializeViewSettings(toolbar);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var fit = new Button { Content = "Вписать", Margin = new Thickness(4) };
        fit.Click += (_, _) => { if (_visual.Content is { } content) SetCameraBounds(content.Bounds); Fit(); };
        var refresh = new Button { Content = "Обновить", Margin = new Thickness(4) };
        refresh.Click += (_, _) => QueueLoad();
        buttons.Children.Add(fit); buttons.Children.Add(refresh);
        DockPanel.SetDock(buttons, Dock.Right); toolbar.Children.Add(buttons); toolbar.Children.Add(_status);
        layout.Children.Add(toolbar);
        var sceneLayers = new Grid(); sceneLayers.Children.Add(_viewport); sceneLayers.Children.Add(_skeletonCanvas);
        var surface = new Border { Background = _sceneBackground, ClipToBounds = true, Child = sceneLayers };
        InitializeSkeletonInput();
        Grid.SetRow(surface, 1); layout.Children.Add(surface);
        Grid.SetRow(_information, 2); layout.Children.Add(_information);
        _viewport.Camera = _camera; _viewport.Children.Add(_visual);
        _viewport.Children.Add(_lighting);
        UpdateLighting();
        surface.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseLeftButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.LostMouseCapture += (_, _) => _drag = null;
        surface.MouseMove += (_, e) =>
        {
            if (_drag is not { } previous) return;
            Point next = e.GetPosition(surface);
            _yaw -= (next.X - previous.X) * 0.01;
            _pitch = Math.Clamp(_pitch + (next.Y - previous.Y) * 0.01, -1.4, 1.4);
            _drag = next; UpdateCamera();
        };
        surface.MouseWheel += (_, e) => { _distance = Math.Clamp(_distance * Math.Exp(-e.Delta / 1000.0), _radius * 1.1, _radius * 30); UpdateCamera(); e.Handled = true; };
        _viewport.SizeChanged += (_, _) => Fit();
        Content = layout;
        Loaded += (_, _) => Attach();
        DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => { Detach(); Cancel(); _visual.Content = null; };
        IsVisibleChanged += (_, _) =>
        {
            if (ExternalScene)
            {
                if (!IsVisible) PauseAnimation();
                else if (_clip is not null) AnimationLoadTask = PlayAnimationAsync();
            }
            else if (IsVisible) QueueLoad(); else Cancel();
        };
    }

    internal void UseCompactDetailsLayout()
    {
        if (Content is not Grid layout || _information.Parent != layout) return;
        PreviewChrome.Compact((FrameworkElement)layout.Children[0]);
        _clips.Width = 300;
        _frame.Width = 120;
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = _status });
        layout.Children.Remove(_information);
        _information.Height = 80;
        _information.MinHeight = 0;
        _information.Padding = new Thickness(6, 2, 6, 2);
        var details = new Expander { Header = "Сведения о модели и ресурсах", Content = _information, Margin = new Thickness(4, 2, 4, 2) };
        Grid.SetRow(details, 2); layout.Children.Add(details);
        layout.RowDefinitions[2].Height = GridLength.Auto;
    }

    private void Attach()
    {
        Detach();
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += Changed;
        QueueLoad();
    }
    private void Detach() { if (_vm is not null) _vm.PropertyChanged -= Changed; _vm = null; }
    internal void SetMobEntries(IReadOnlyList<MobAppearance> entries, string query)
    {
        _mobEntries = entries; _mobMode = true;
        _objects.Visibility = _objectSearch.Visibility = Visibility.Visible;
        _objectSearch.Text = query; FilterObjects();
    }
    internal void SearchObjects(string query) => _objectSearch.Text = query;
    internal IReadOnlyList<MobAppearance> MatchingObjects => (_objects.ItemsSource as MobAppearance[]) ?? [];
    private void FilterObjects()
    {
        if (!_mobMode) return;
        string query = _objectSearch.Text.Trim();
        var previous = _objects.SelectedItem;
        var matches = _mobEntries.Where(a => a.Prototype.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _objects.ItemsSource = matches;
        _objects.SelectedItem = matches.Contains(previous) ? previous : matches.FirstOrDefault();
        _objectCount.Text = $"Найдено: {matches.Length} из {_mobEntries.Count}";
        QueueLoad();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SourcePath))
        { _mobMode = false; _mobEntries = []; _objects.SelectedItem = null; _objects.ItemsSource = null; _objects.Visibility = _objectSearch.Visibility = Visibility.Collapsed; _objectCount.Text = ""; }
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedRecord) && _mobMode)
        { _objectSearch.Text = _vm?.SelectedRecord?.CoreRecord.IdentityValue ?? ""; FilterObjects(); }
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRecord) or nameof(MainWindowViewModel.SourcePath)) QueueLoad();
    }
    private void Cancel() { ResetAnimation(); _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; }
    private void QueueLoad()
    {
        if (ExternalScene) return;
        Cancel();
        _visual.Content = null;
        _information.Text = "";
        if (!IsVisible || _vm is null) return;
        var row = _vm.SelectedRecord;
        bool item = row is not null && ItemModelPreview.Supports(row.TableKey);
        _mobTools.Visibility = item ? Visibility.Collapsed : Visibility.Visible;
        _itemTools.Visibility = Visibility.Collapsed;
        if (row is null || !item && row.TableKey is not ("units.prototypes" or "units.npcs"))
        { _status.Text = "Выберите предмет, прототип монстра или NPC."; return; }
        if (!item && _mobMode && _objects.SelectedItem is null)
        { _status.Text = "В MOB нет совпадений. Измените поиск по прототипу, имени или ID."; return; }
        var selected = ModelRecord.Capture(row.TableKey, row.CoreRecord);
        var records = _vm.Databases.SelectMany(g => g.Tables).Where(t => t.Key.StartsWith("units.") || t.Key.StartsWith("items.") || t.Key.StartsWith("spells."))
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r.CoreRecord))).ToArray();
        ModelRecord? choice = null;
        if (item)
        {
            var options = ItemModelPreview.Options(selected, records);
            _itemTools.Visibility = options.Label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _choiceLabel.Text = options.Label;
            string context = _vm.SourcePath + "|" + selected.Table + "|" + selected.Name;
            string? previous = context == _choiceContext ? (_itemChoice.SelectedItem as ModelRecord)?.Name : null;
            _settingChoice = true;
            _itemChoice.ItemsSource = options.Choices;
            _itemChoice.SelectedItem = options.Choices.FirstOrDefault(r => r.Name == previous);
            _settingChoice = false;
            _choiceContext = context;
            choice = _itemChoice.SelectedItem as ModelRecord;
        }
        _status.Text = "Загрузка модели: " + selected.Name;
        _cancellation = new CancellationTokenSource();
        var appearance = !item && _mobMode ? _objects.SelectedItem as MobAppearance : null;
        if (appearance is not null)
        {
            var prototype = records.FirstOrDefault(r => r.Table == "units.prototypes" && r.Name.Equals(appearance.Prototype, StringComparison.OrdinalIgnoreCase));
            if (prototype is null) { _status.Text = "В открытой БД нет прототипа объекта MOB: " + appearance.Prototype; return; }
            selected = prototype;
        }
        LoadTask = LoadAsync(_vm.SourcePath, selected, records, _cancellation.Token, appearance, choice);
    }
    private async Task LoadAsync(string path, ModelRecord selected, ModelRecord[] records, CancellationToken token, MobAppearance? appearance, ModelRecord? choice)
    {
        try
        {
            await Task.Delay(150, token);
            if (ItemModelPreview.Supports(selected.Table) && choice is null)
            {
                choice = await ItemModelPreview.DefaultChoiceAsync(path, selected, records, token);
                token.ThrowIfCancellationRequested();
                _settingChoice = true; _itemChoice.SelectedItem = choice; _settingChoice = false;
            }
            var scene = ItemModelPreview.Supports(selected.Table)
                ? await ItemModelPreview.LoadAsync(path, selected, records, token, choice)
                : await UnitModelPreview.LoadAsync(path, selected, records, token, appearance);
            token.ThrowIfCancellationRequested();
            ShowScene(scene, ItemModelPreview.Supports(selected.Table));
            _status.Text = $"{(appearance is null ? "БД: " + selected.Name : "MOB: " + appearance.Label)} · {scene.Model} · {scene.Meshes.Count} деталей · мышь: вращать, колесо: масштаб";
            _information.Text = string.Join("\n", scene.Notes) + "\n\nРесурсы:\n" + string.Join("\n", scene.Sources);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = "Не удалось показать модель: " + ex.Message; }
    }

    internal bool ExternalScene { get; init; }
    internal bool HasVisibleScene => _visual.Content is not null;
    internal async Task ShowEquipmentSceneAsync(UnitModelScene scene, CancellationToken token)
    {
        // Keep the current animated scene visible while the replacement clip and bounds load.
        string? idle = scene.Animations?.Names.FirstOrDefault(n => n.Equals("cidle09", StringComparison.OrdinalIgnoreCase));
        FigureAnimationClip? preparedClip = null;
        Rect3D? preparedBounds = null;
        if (idle is not null && scene.Animations is { } animations)
        {
            preparedClip = await animations.LoadAsync(idle, token);
            var meshes = scene.Meshes.ToArray();
            preparedBounds = await Task.Run(() => AnimationBounds(animations, preparedClip, meshes, token), token);
        }
        token.ThrowIfCancellationRequested();
        double retainedFrame = _clip?.Name.Equals(idle, StringComparison.OrdinalIgnoreCase) == true ? _frame.Value : 0;
        // No await between replacing geometry and applying its pose: WPF cannot render bind pose.
        ShowScene(scene);
        if (Content is Grid layout)
        {
            layout.Children[0].Visibility = Visibility.Collapsed;
            layout.RowDefinitions[2].Height = new GridLength(70);
        }
        _information.Text = string.Join("\n", scene.Notes);
        if (preparedClip is null)
        {
            _information.Text += "\nАнимация cidle09 у этой модели отсутствует; показана исходная поза.";
            return;
        }
        EditorLoop = true;
        _clip = preparedClip;
        _settingClips = true; _clips.SelectedItem = idle; _settingClips = false;
        _settingFrame = true;
        _frame.Maximum = preparedClip.FrameCount - 1;
        _frame.Value = retainedFrame % Math.Max(1, preparedClip.FrameCount);
        _settingFrame = false;
        ApplyAnimationFrame();
        if (preparedBounds is { } bounds) { SetCameraBounds(bounds); Fit(); }
        UpdateAnimationControls();
        await PlayAnimationAsync();
    }

    internal void ShowScene(UnitModelScene scene, bool item = false, bool preserveCamera = false)
    {
        bool retainCamera = preserveCamera && _visual.Content is not null;
        _editingScene = scene;
        ResetAnimation();
        if (_itemScene != item)
        { _itemScene = item; _yaw = item ? .35 : .7; _pitch = item ? 1.15 : .15; }
        _mobTools.Visibility = item ? Visibility.Collapsed : Visibility.Visible;
        var group = new Model3DGroup();
        var materials = new Dictionary<int, Material>();
        foreach (var pair in scene.Textures)
        {
            var image = pair.Value;
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            brush.Freeze();
            var material = new DiffuseMaterial(brush); material.Freeze(); materials[pair.Key] = material;
        }
        foreach (var part in scene.Meshes)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(part.Positions.Select(p => new Point3D(p.X, p.Z, -p.Y))),
                Normals = new Vector3DCollection(part.Normals.Select(p => new Vector3D(p.X, p.Z, -p.Y))),
                TextureCoordinates = new PointCollection(part.UV.Select(p => new Point(p.X, p.Y))),
                TriangleIndices = new Int32Collection(part.Indices)
            };
            var material = materials.GetValueOrDefault(part.TextureSlot) ?? new DiffuseMaterial(Brushes.DarkGray);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            group.Children.Add(model);
            if (!item && scene.Animations is not null) _animatedMeshes.Add(new(part, mesh, model, mesh.Positions));
            else mesh.Freeze();
        }
        if (item || scene.Animations is null) group.Freeze();
        _visual.Content = group;
        UpdateGround(scene);
        SetAnimations(item ? null : scene.Animations);
        if (retainCamera) UpdateCamera();
        else { SetCameraBounds(group.Bounds); Fit(); }
    }
    private void SetCameraBounds(Rect3D bounds)
    {
        _center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        _radius = Math.Max(0.01, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
    }
    private Rect3D? _pendingEditorBounds;
    private void Fit()
    {
        if(_pendingEditorBounds is { } bounds){SetCameraBounds(bounds);_pendingEditorBounds=null;}
        double aspect = Math.Max(0.1, _viewport.ActualWidth / Math.Max(1, _viewport.ActualHeight));
        double half = Math.Atan(Math.Tan(_camera.FieldOfView * Math.PI / 360) / Math.Max(1, aspect));
        _distance = _radius / Math.Sin(half) * 1.15; UpdateCamera();
    }
    private void UpdateCamera()
    {
        var offset = new Vector3D(Math.Sin(_yaw) * Math.Cos(_pitch), Math.Sin(_pitch), Math.Cos(_yaw) * Math.Cos(_pitch)) * _distance;
        _camera.Position = _center + offset; _camera.LookDirection = -offset;
        _camera.NearPlaneDistance = Math.Max(0.001, _radius / 1000); _camera.FarPlaneDistance = _distance + _radius * 50;
        DrawSkeleton();
        UpdateCameraReadout();
        UpdateLighting();
    }
}
