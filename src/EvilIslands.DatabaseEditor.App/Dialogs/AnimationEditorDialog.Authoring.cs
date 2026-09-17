using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog
{
    private readonly AnimationTimeline _timeline = new();
    private readonly WrapPanel _authoring = new();
    private readonly WrapPanel _playback = new();
    private readonly TextBox _smooth = Number();
    private readonly CheckBox _limits = new() { Content = "Ограничения локтей и колен", IsChecked = true, Margin = new(4) };
    private readonly CheckBox _leftFoot = new() { Content = "Фиксировать левую стопу", Margin = new(4) };
    private readonly CheckBox _rightFoot = new() { Content = "Фиксировать правую стопу", Margin = new(4) };
    private readonly CheckBox _before = new() { Content = "Показать оригинал", Margin = new(4), ToolTip = "Сравнение по номеру кадра; за концом оригинала удерживается его последний кадр." };
    private readonly CheckBox _ghost = new() { Content = "Призрак оригинала", Margin = new(4) };
    private readonly Dictionary<string, Vector3> _pins = new();
    private AnimationFrameBlock? _copiedFrames;
    private FigureAnimationClip? _originalClip;
    private byte[]? _savedProject;
    private bool _loadingSettings;
    private int CurrentFrame => int.TryParse(_frame.Text, out int f) && _loadedClip is not null ? Math.Clamp(f, 0, _loadedClip.FrameCount - 1) : 0;
    private (int First, int Last) SelectedRange()
    {
        if (!int.TryParse(_first.Text, out int first) || !int.TryParse(_last.Text, out int last) || _loadedClip is null || first < 0 || last < first || last >= _loadedClip.FrameCount)
            throw new ArgumentException("Выберите диапазон на шкале или в полях первого и последнего кадра.");
        return (first, last);
    }
    private Task ApplyPose(int frame, IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Vector3> positions)
    {
        if (_before.IsChecked == true) throw new InvalidOperationException("Выключите показ оригинала для редактирования.");
        if (!int.TryParse(_smooth.Text, out int radius)) throw new ArgumentException("Введите целый радиус сглаживания: 0–120 кадров.");
        return _editor.ApplyAuthoringAsync(_name, frame, rotations, positions, radius, _limits.IsChecked == true, _pins);
    }
    private void InitializeAuthoring(StackPanel sidebar, Grid columns)
    {
        _smooth.Text = "0";
        _preview.PoseFilter = (frame, rotations, positions) => { var pins = _editor.ContactPins(frame); foreach(var p in _pins)pins[p.Key]=p.Value; return _loadedClip is null ? rotations : FigureAnimationEditor.ConstrainPreview(_scene.Animations!, _loadedClip, frame, rotations, positions, _limits.IsChecked == true, pins); };
        _preview.PoseRejected = message => _message.Text = message;
        _limits.ToolTip = "Для стандартных цепочек рук/ног: предел сгиба и запрет обратного сгиба относительно исходной позы кадра. Для нестандартных скелетов ограничения не назначаются.";
        _smooth.ToolTip = "0 — один кадр; N — плавно ослабить правку на N соседних кадров в обе стороны.";
        void Button(string title, Func<Task> action)
        {
            var button = ActionButton(title, () => { }); button.Click += async (_, _) => await Run(action); _authoring.Children.Add(button);
        }
        Button("Копировать кадры", async () => { var r = SelectedRange(); _copiedFrames = await _editor.CopyFramesAsync(_name, r.First, r.Last); _message.Text = $"Скопировано кадров: {_copiedFrames.Count}. Вставка — перед текущим кадром."; });
        Button("Вставить кадры", async () => { if (_copiedFrames is null) throw new InvalidOperationException("Сначала скопируйте кадр или диапазон."); await _editor.PasteFramesAsync(_name, CurrentFrame, _copiedFrames); await Refresh(); });
        Button("Дублировать", async () => { var r = SelectedRange(); await _editor.DuplicateFramesAsync(_name, r.First, r.Last); await Refresh(); });
        Button("Удалить кадры", async () => { var r = SelectedRange(); await _editor.DeleteFramesAsync(_name, r.First, r.Last); await Refresh(); });
        Button("Зеркальная поза", async () => { if (_before.IsChecked == true) throw new InvalidOperationException("Выключите показ оригинала."); await _editor.MirrorPoseAsync(_name, CurrentFrame, _limits.IsChecked == true, _pins); await Refresh(); });
        _authoring.Children.Add(Paragraph("Соседних кадров для сглаживания:")); _authoring.Children.Add(_smooth);
        _authoring.Children.Add(_limits); _authoring.Children.Add(_leftFoot); _authoring.Children.Add(_rightFoot);
        _authoring.Children.Add(_before); _authoring.Children.Add(_ghost);
        Button("Сохранить проект…", SaveProject);
        Button("Открыть проект…", OpenProject);
        sidebar.Children.Insert(0, _authoring); sidebar.Children.Insert(0, Paragraph("КАДРЫ И ПРОЕКТ"));
        columns.Children.Remove(_preview);
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(_preview); Grid.SetColumn(_preview, 0);
        var footer = new StackPanel(); footer.Children.Add(_timeline); _preview.MoveEditorPlaybackTo(_playback); footer.Children.Add(_playback);
        Grid.SetRow(footer, 1); right.Children.Add(footer); Grid.SetColumn(right, 1); columns.Children.Add(right);
        _timeline.Seek += frame => { if (_busy) return; _preview.PauseAnimation(); _frame.Text = frame.ToString(); ReadPose(); };
        _timeline.RangeChanged += (first, last) => { _first.Text = first.ToString(); _last.Text = last.ToString(); _length.Text = (last - first + 1).ToString(); };
        _preview.FrameDisplayed += frame => { _timeline.Frame = frame; if (!_busy) _frame.Text = ((int)frame).ToString(); _timeline.InvalidateVisual(); };
        foreach (var box in new[] { _first, _last }) box.LostKeyboardFocus += (_, _) => { try { var r = SelectedRange(); _timeline.First = r.First; _timeline.Last = r.Last; _timeline.InvalidateVisual(); } catch (ArgumentException) { } };
        void Pin(CheckBox check, string part)
        {
            check.Checked += (_, _) =>
            {
                if (_loadingSettings || _loadedClip is null) return;
                _preview.PauseAnimation();
                if (!FigureLimbIk.Supports(_editor.Preview(), _loadedClip, part)) { check.IsChecked = false; return; }
                _pins[part] = _editor.Preview().Pose(_loadedClip, CurrentFrame)[part].Position;
                _message.Text = "Стопа закреплена в текущей мировой позиции. Фиксация действует при последующих правках, включая соседние кадры.";
            };
            check.Unchecked += (_, _) => { if (!_loadingSettings) _pins.Remove(part); };
        }
        Pin(_leftFoot, "ll3"); Pin(_rightFoot, "rl3");
        foreach (var check in new[] { _before, _ghost })
        { check.Checked += (_, _) => UpdateComparison(); check.Unchecked += (_, _) => UpdateComparison(); }
    }
    private async Task RefreshAuthoring()
    {
        _originalClip ??= await _scene.Animations!.LoadAsync(_name);
        _timeline.Count = _loadedClip!.FrameCount; _timeline.First = 0; _timeline.Last = _loadedClip.FrameCount - 1; _timeline.InvalidateVisual();
        _leftFoot.IsEnabled = FigureLimbIk.Supports(_editor.Preview(), _loadedClip, "ll3");
        _rightFoot.IsEnabled = FigureLimbIk.Supports(_editor.Preview(), _loadedClip, "rl3");
        UpdateComparison();
    }
    private void UpdateComparison()
    {
        if (_originalClip is null || _loadingSettings) return;
        _preview.SetComparison(_scene.Animations!, _originalClip, _before.IsChecked == true, _ghost.IsChecked == true);
        _poseActions.IsEnabled = !_busy && _before.IsChecked != true;
    }
    private AnimationAuthoringSettings Settings() => new()
    {
        Frame = CurrentFrame, First = int.TryParse(_first.Text, out int first) ? first : 0,
        Last = int.TryParse(_last.Text, out int last) ? last : 0,
        SmoothRadius = int.TryParse(_smooth.Text, out int radius) ? radius : 0,
        Part = _part.SelectedItem as string ?? "", Limits = _limits.IsChecked == true,
        Contacts = _editor.Contacts.ToList(), Poses = _poses.ToList(), Trajectory = _trajectoryCheck.IsChecked == true, Onion = _onionCheck.IsChecked == true,
        Ghost = _ghost.IsChecked == true, Pins = new(_pins), Fps = _preview.EditorFps, Loop = _preview.EditorLoop
    };
    private byte[] ProjectBytes() => _editor.SaveProject(_scene.Model, _name, Settings());
    private bool HasUnsavedWork() => _savedProject is not null ? !ProjectSaved() : _poses.Count > 0 || _editor.Contacts.Count > 0 || !_editor.ExportBytes().AsSpan().SequenceEqual(_exported);
    private bool ProjectSaved() => _savedProject is not null && ProjectBytes().AsSpan().SequenceEqual(_savedProject);
    private Task SaveProject()
    {
        var picker = new SaveFileDialog { Filter = "Проект анимации (*.eianim)|*.eianim", FileName = _scene.Model + "-" + _name + ".eianim" };
        if (picker.ShowDialog(this) != true) return Task.CompletedTask;
        _ = SelectedRange();
        if (!int.TryParse(_smooth.Text, out int radius) || radius is < 0 or > 120) throw new ArgumentException("Радиус сглаживания: 0–120.");
        var bytes = ProjectBytes(); string temporary = picker.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка проекта.");
            if (File.Exists(picker.FileName)) File.Replace(temporary, picker.FileName, picker.FileName + ".bak", true);
            else File.Move(temporary, picker.FileName);
            _savedProject = bytes; _message.Text = "Проект сохранён вместе с правками, историей отмены, фиксацией стоп и настройками. ANM экспортируется отдельно.";
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Task.CompletedTask;
    }
    private async Task OpenProject()
    {
        var picker = new OpenFileDialog { Filter = "Проект анимации (*.eianim)|*.eianim" };
        if (picker.ShowDialog(this) != true) return;
        if (HasUnsavedWork() && MessageBox.Show(this, "Заменить текущие несохранённые правки проектом?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (new FileInfo(picker.FileName).Length > 256L * 1024 * 1024) throw new InvalidDataException("Проект превышает 256 МиБ.");
        var settings = await _editor.LoadProjectAsync(File.ReadAllBytes(picker.FileName), _scene.Model, _name);
        await Refresh(); _loadingSettings = true;
        try
        {
            _preview.EditorFps = settings.Fps; _preview.EditorLoop = settings.Loop;
            _poses.Clear(); _poses.AddRange(settings.Poses); RefreshLibrary(); _trajectoryCheck.IsChecked=settings.Trajectory; _onionCheck.IsChecked=settings.Onion;
            _smooth.Text = settings.SmoothRadius.ToString(); _limits.IsChecked = settings.Limits; _ghost.IsChecked = settings.Ghost; _before.IsChecked = false;
            _pins.Clear(); foreach (var pin in settings.Pins) _pins[pin.Key] = pin.Value;
            _leftFoot.IsChecked = _pins.ContainsKey("ll3"); _rightFoot.IsChecked = _pins.ContainsKey("rl3");
            _frame.Text = settings.Frame.ToString(); _part.SelectedItem = settings.Part;
            _first.Text = settings.First.ToString(); _last.Text = settings.Last.ToString(); _length.Text = (settings.Last - settings.First + 1).ToString();
            _timeline.First = settings.First; _timeline.Last = settings.Last; ReadPose();
        }
        finally { _loadingSettings = false; }
        UpdateComparison(); await RefreshMotion(); _savedProject = ProjectBytes(); _message.Text = "Проект открыт. История отмены и настройки восстановлены.";
    }
}
