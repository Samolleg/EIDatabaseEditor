using System.IO;
using System.Numerics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog : LuaToolWindow
{
    private FigureAnimationEditor _editor;
    internal FigureAnimationEditor WorkingEditor => _editor;
    private UnitModelScene _scene;
    private string _name;
    private readonly UnitModelPreviewView _preview = new() { AllowAnimationEditing = false };
    private readonly TextBox _first = Number(), _last = Number(), _length = Number();
    private readonly TextBlock _message = Paragraph("");
    private readonly TextBlock _generationSummary = Paragraph("");
    private readonly WrapPanel _actions = new();
    private byte[] _exported;
    private bool _busy;
    internal bool EditingBusy => _busy;
    internal int EditingFrameCount => _loadedClip?.FrameCount ?? 0;
    private readonly WrapPanel _poseActions = new();
    private readonly ComboBox _part = new() { Width = 285, Margin = new(4) };
    private readonly TextBox _frame = Number(), _rx = Number(), _ry = Number(), _rz = Number(), _px = Number(), _py = Number(), _pz = Number();
    private FigureAnimationClip? _loadedClip;
    private static TextBox Number() => new() { Width = 75, Margin = new(4), TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };

    public AnimationEditorDialog(UnitModelScene scene, string name) : base("Редактирование анимации — " + name)
    {
        Width = Math.Min(1150, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(850, SystemParameters.WorkArea.Height - 32);
        _scene = scene; _name = name; _editor = new(scene.Animations!); _exported = _editor.ExportBytes();
        var partText = new FrameworkElementFactory(typeof(TextBlock));
        partText.SetBinding(TextBlock.TextProperty, new Binding { Converter = new PartLabelConverter() });
        _part.ItemTemplate = new DataTemplate { VisualTree = partText };
        _part.ToolTip = "Левая и правая стороны указаны относительно самой модели.";
        _preview.JointSelected += SelectJoint;
        _preview.LimbEdited += async (frame, rotations) => await Run(async () =>
        {
            await ApplyPose(frame, rotations, new Dictionary<string, Vector3>());
            await Refresh(); _frame.Text = frame.ToString(); ReadPose();
            _message.Text = "Перемещение сустава сохранено в кадре. Отменить возвращает весь жест.";
        });
        _preview.JointEdited += async (part, frame, rotation, position) => await Run(async () =>
        {
            await ApplyPose(frame, new Dictionary<string, Quaternion> { [part] = rotation }, position.HasValue ? new Dictionary<string, Vector3> { [part] = position.Value } : new Dictionary<string, Vector3>());
            await Refresh(); _frame.Text = frame.ToString(); ReadPose();
            _message.Text = "Изменение мышью сохранено в кадре. Отменить — возвращает весь жест.";
        });
        var note = Paragraph("Тяните голубую точку кисти или стопы: IK сгибает руку или ногу в плоскости экрана. Якорь «Сгиб» меняет направление локтя/колена при неподвижной кисти/стопе. Поверните камеру для другой плоскости. Длины сегментов сохраняются; далёкие цели ограничены досягаемостью. Белую точку тоже можно тянуть: поворачивается родительская кость вместе с дочерними частями. Точка корня перемещает всю модель. Кольца X/Y/Z вращаются вправо/влево; стрелки перемещают корень. Esc отменяет жест. Кадры с 0; игровой список и длительность действия редактируются отдельно в ADB.");
        DockPanel.SetDock(note, Dock.Top); Body.Children.Add(note);
        void Field(string label, TextBox box)
        {
            var group = new StackPanel { Width = 94 };
            group.Children.Add(Paragraph(label)); group.Children.Add(box); _actions.Children.Add(group);
        }
        Field("Первый кадр", _first); Field("Последний", _last); Field("Новая длина", _length);
        Button Action(string text, Func<Task> action)
        {
            var b = ActionButton(text, () => { }); b.Click += async (_, _) => await Run(action); _actions.Children.Add(b); return b;
        }
        Action("Применить", async () =>
        {
            if (!int.TryParse(_first.Text, out int first) || !int.TryParse(_last.Text, out int last) || !int.TryParse(_length.Text, out int frames))
                throw new FormatException("Введите целые номера кадров и длину.");
            await _editor.TransformAsync(_name, first, last, frames); await Refresh();
            _message.Text = "Изменение применено к копии. Другие клипы не изменены.";
        });
        Action("Отменить", async () => { await _editor.NavigateHistoryAsync(_name,false); await RefreshAfterHistory(); });
        Action("Повторить", async () => { await _editor.NavigateHistoryAsync(_name,true); await RefreshAfterHistory(); });
        Action("Экспорт ANM…", async () =>
        {
            if (!await CheckBeforeExport()) return;
            var picker = new SaveFileDialog { Filter = "Архив анимаций (*.anm)|*.anm", FileName = scene.Model + ".anm", OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return;
            if (File.Exists(picker.FileName)) throw new IOException("Выберите новое имя файла. Исходный ANM этим экспортом не перезаписывается.");
            _editor.NestFlatClip(_name);
            byte[] bytes = _editor.ExportBytes();
            using (var file = new FileStream(picker.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (!File.ReadAllBytes(picker.FileName).AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка записанного ANM.");
            _exported = bytes; _message.Text = "ANM экспортирован и проверен. Для установки замените ресурс " + scene.Model + ".anm в нужном RES через менеджер архивов.";
            return;
        });
        DockPanel.SetDock(_actions, Dock.Top); Body.Children.Add(_actions);
        void PoseField(string label, Control box)
        {
            var group = new StackPanel { Width = box == _part ? 282 : 94 };
            if (box == _part) box.Width = 274;
            group.Children.Add(Paragraph(label)); group.Children.Add(box); _poseActions.Children.Add(group);
        }
        PoseField("Часть", _part); PoseField("Кадр", _frame);
        var readPose = ActionButton("Взять кадр из просмотра", () => { _frame.Text = ((int)_preview.AnimationFrame).ToString(); ReadPose(); });
        _poseActions.Children.Add(readPose);
        PoseField("Доворот X°", _rx); PoseField("Y°", _ry); PoseField("Z°", _rz);
        PoseField("ANM X", _px); PoseField("ANM Y", _py); PoseField("ANM Z", _pz);
        foreach (var box in new[] { _px, _py, _pz }) box.ToolTip = "Сырые координаты корня ANM. В просмотре отдельно применяется ростовая поправка ADB; перемещение мышью переводится обратно перед записью.";
        var applyPose = ActionButton("Применить к кадру", () => { });
        applyPose.Click += async (_, _) => await Run(async () =>
        {
            if (_part.SelectedItem is not string part || !int.TryParse(_frame.Text, out int frame) || _loadedClip is null || frame < 0 || frame >= _loadedClip.FrameCount)
                throw new FormatException("Выберите часть и существующий кадр.");
            float Value(TextBox box) => float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? n : throw new FormatException("Введите конечное число с десятичной точкой.");
            float radians = MathF.PI / 180;
            var delta = Quaternion.CreateFromYawPitchRoll(Value(_ry) * radians, Value(_rx) * radians, Value(_rz) * radians);
            var rotation = Quaternion.Normalize(_loadedClip.Tracks[part].Rotation(frame) * delta);
            bool root = _scene.Animations!.Parents.TryGetValue(part, out var parent) && parent.Length == 0;
            Vector3? position = root ? new Vector3(Value(_px), Value(_py), Value(_pz)) : null;
            await ApplyPose(frame, new Dictionary<string, Quaternion> { [part] = rotation }, position.HasValue ? new Dictionary<string, Vector3> { [part] = position.Value } : new Dictionary<string, Vector3>());
            await Refresh(); _frame.Text = frame.ToString(); _preview.SetAnimationFrame(frame); ReadPose();
            _message.Text = "Поза изменена с выбранным радиусом сглаживания. Отменить возвращает всю операцию.";
        });
        _poseActions.Children.Add(applyPose);
        _part.SelectionChanged += (_, _) => ReadPose();
        _frame.LostKeyboardFocus += (_, _) => ReadPose();
        DockPanel.SetDock(_poseActions, Dock.Top); Body.Children.Add(_poseActions);
        DockPanel.SetDock(_message, Dock.Bottom); Body.Children.Add(_message); Body.Children.Add(_preview);
        Body.Children.Clear();
        var sidebar = new StackPanel { Margin = new Thickness(4, 0, 10, 0) };
        sidebar.Children.Add(Paragraph("КЛИП")); sidebar.Children.Add(_actions);
        var adb = ActionButton("Игровой список ADB…", () => { });
        adb.IsEnabled = scene.AnimationDatabase is not null;
        adb.ToolTip = adb.IsEnabled ? "Все клипы ANM и записи ADB этой модели: параметры выбора, события и сохранение игрового списка." : "Ресурс ADB этой модели не найден.";
        adb.Click += async (_, _) => { new AnimationDatabaseDialog(_scene, _editor.Preview()) { Owner = this }.ShowDialog(); await Run(Refresh); };
        sidebar.Children.Add(adb);
        sidebar.Children.Add(Paragraph("ПОЗА КАДРА")); sidebar.Children.Add(_poseActions);

        var help = new Expander { Header = "Управление мышью", Content = note, Margin = new Thickness(4, 8, 4, 4) };
        sidebar.Children.Add(help); sidebar.Children.Add(_message);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var scroll = new ScrollViewer { Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        columns.Children.Add(scroll); Grid.SetColumn(_preview, 1); columns.Children.Add(_preview); Body.Children.Add(columns);
        InitializeAuthoring(sidebar, columns);
        InitializeMotion(sidebar);
        InitializeCatalog(sidebar);
        InitializeStoryboard(sidebar);
        InitializeKimodo(sidebar);
        sidebar.Children.Add(_generationSummary);
        Compact(sidebar); Compact(_playback); _mapping.Height=90;
        Loaded += async (_, _) => await Run(Refresh);
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; return; }
            if (HasUnsavedWork() && MessageBox.Show(this, "Закрыть без сохранения проекта или экспорта анимации?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            if (!e.Cancel) _preview.PauseAnimation();
        };
    }
    private static void Compact(DependencyObject element)
    {
        if (element is Control c)
        {
            c.FontSize = 12;
            if (c is Button or TextBox or ComboBox)
            { c.MinHeight = 0; c.Height = 30; c.Padding = new Thickness(6, 2, 6, 2); c.Margin = new Thickness(3); }
        }
        if (element is TextBlock text) { text.FontSize = 12; text.Margin = new Thickness(3, 5, 3, 3); }
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) Compact(child);
    }
    private int _coordinateRevision;
    internal async Task Refresh()
    {
        int previousFrame = CurrentFrame;
        var set = _editor.Preview(); var clip = await set.LoadAsync(_name);
        if (_coordinateRevision != _editor.CoordinateRevision)
        {
            _coordinateRevision = _editor.CoordinateRevision;
            _loadingSettings = true;
            try { _pins.Clear(); _leftFoot.IsChecked = _rightFoot.IsChecked = false; }
            finally { _loadingSettings = false; }
            if (_editor.ProjectCoordinateWarning is { } warning) _message.Text = warning;
        }
        if(_loadedClip is null)
        { _preview.ShowScene(_scene with { Animations = set }); await _preview.SelectAnimationAsync(_name); }
        else await _preview.ReplaceEditedAnimationAsync(set,clip,previousFrame);
        _preview.RestrictToEditedClip();
        int first = int.TryParse(_first.Text, out int a) ? Math.Clamp(a, 0, clip.FrameCount - 1) : 0;
        int last = int.TryParse(_last.Text, out int b) ? Math.Clamp(b, first, clip.FrameCount - 1) : first;
        _first.Text = first.ToString(); _last.Text = last.ToString(); _length.Text = (last - first + 1).ToString();
        string? selected = _part.SelectedItem as string;
        _loadedClip = clip; _part.ItemsSource = EditableParts(_scene, clip);
        _part.SelectedItem = selected; if (_part.SelectedIndex < 0) _part.SelectedIndex = 0;
        _frame.Text = Math.Clamp(previousFrame, 0, clip.FrameCount - 1).ToString();
        ReadPose();
        await RefreshAuthoring();
        await RefreshMotion();
        RefreshStoryboard();
    }
    internal async Task RefreshAfterHistory()
    {
        if (!_editor.Preview().Names.Contains(_name, StringComparer.OrdinalIgnoreCase))
        {
            string fallback = _workingClips.FirstOrDefault(name => _editor.Preview().Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("История не содержит доступного клипа для просмотра.");
            await SwitchWorkingClip(fallback);
        }
        else await Refresh();
    }
    internal static string[] EditableParts(UnitModelScene scene, FigureAnimationClip clip)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in scene.Meshes)
        {
            string node = mesh.Name;
            // Invisible parents still drive displayed children; unrelated equipment does not.
            while (node.Length > 0 && used.Add(node))
            {
                if (scene.Animations is null || !scene.Animations.Parents.TryGetValue(node, out var parent)) break;
                node = parent;
            }
        }
        return clip.Tracks.Keys.Where(used.Contains).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static string PartLabel(string code)
    {
        string? name = code.ToLowerInvariant() switch
        {
            "bd" => "Туловище", "hd" => "Голова", "hp" => "Таз",
            "lh1" => "Левое плечо", "lh2" => "Левое предплечье", "lh3" => "Левая кисть",
            "rh1" => "Правое плечо", "rh2" => "Правое предплечье", "rh3" => "Правая кисть",
            "ll1" => "Левое бедро", "ll2" => "Левая голень", "ll3" => "Левая стопа",
            "rl1" => "Правое бедро", "rl2" => "Правая голень", "rl3" => "Правая стопа",
            "wings" => "Крылья", "chel ust" or "chelust" => "Челюсть",
            _ => null
        };
        return name is null ? code : code + " — " + name;
    }
    private sealed class PartLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => PartLabel(value as string ?? "");
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private void ReadPose()
    {
        if (_loadedClip is null || _part.SelectedItem is not string part || !int.TryParse(_frame.Text, out int frame) || frame < 0 || frame >= _loadedClip.FrameCount) return;
        _rx.Text = _ry.Text = _rz.Text = "0";
        bool root = _scene.Animations!.Parents.TryGetValue(part, out var parent) && parent.Length == 0;
        _px.IsEnabled = _py.IsEnabled = _pz.IsEnabled = root;
        _px.ToolTip = _py.ToolTip = _pz.ToolTip = root ? "Положение корня" : "Положение дочерней части задаётся BON";
        var track = _loadedClip.Tracks[part];
        var p = track.Positions.Length > 0 ? track.Position(frame) : _scene.Animations.Bones.GetValueOrDefault(part);
        _px.Text = p.X.ToString("R", CultureInfo.InvariantCulture); _py.Text = p.Y.ToString("R", CultureInfo.InvariantCulture); _pz.Text = p.Z.ToString("R", CultureInfo.InvariantCulture);
        _preview.SetAnimationFrame(frame);
        _preview.SetSkeleton(EditableParts(_scene, _loadedClip), part);
        RefreshPartLocks();
    }
    internal void SelectJoint(string part, int frame)
    { _frame.Text = frame.ToString(); _part.SelectedItem = part; SelectStoryboardFrame(frame); ReadPose(); }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _editor.MotionLimits = _limits.IsChecked == true; _editor.MotionPins = _pins;
        _busy = true; _workingClipPicker.IsEnabled = false; _actions.IsEnabled = _poseActions.IsEnabled = _motionTools.IsEnabled = _authoring.IsEnabled = _playback.IsEnabled = _timeline.IsEnabled = _preview.IsEnabled = false;
        try { if (_storySidebar is not null) _storySidebar.IsEnabled = false; _storyTools.IsEnabled = false; _story.IsEnabled = false; _preview.PauseAnimation(); await action(); }
        catch (Exception ex) { _message.Text = ex.Message; }
        finally { if (_storySidebar is not null) _storySidebar.IsEnabled = true; _storyTools.IsEnabled = _story.IsEnabled = true; _busy = false; _workingClipPicker.IsEnabled = true; _actions.IsEnabled = _motionTools.IsEnabled = _authoring.IsEnabled = _playback.IsEnabled = _timeline.IsEnabled = _preview.IsEnabled = true; _poseActions.IsEnabled = !_preview.ShowingOriginal; }
    }
}
