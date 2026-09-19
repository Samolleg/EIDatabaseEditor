using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog
{
    private readonly AnimationStoryboard _story = new();
    private readonly ScrollViewer _storyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly WrapPanel _storyTools = new();
    private readonly CheckBox _partLocked = new() { Content = "Заблокировать часть · Ctrl+L", Margin = new(4) };
    private readonly CheckBox _bendUnlocked = new() { Content = "Снять ограничение сгиба · Ctrl+U", Margin = new(4) };
    private readonly TextBlock _selectedInfo = new() { Margin = new(4), TextWrapping = TextWrapping.Wrap };
    private bool _updatingLocks;
    private bool _settingStoryboardSelection;
    private StackPanel? _storySidebar;
    private int _selectionAnchor;

    private string? SelectedLimb()
    {
        string part = _part.SelectedItem as string ?? "";
        if (part.Length < 2) return null;
        string end = part[..2] + "3";
        return end is "lh3" or "rh3" or "ll3" or "rl3" && _loadedClip is not null && FigureLimbIk.Supports(_editor.Preview(), _loadedClip, end) ? end : null;
    }
    private void RefreshPartLocks()
    {
        _updatingLocks = true;
        _partLocked.IsChecked = _part.SelectedItem is string part && _editor.LockedParts.Contains(part);
        string? limb = SelectedLimb(); _bendUnlocked.IsEnabled = limb is not null;
        _bendUnlocked.IsChecked = limb is not null && _editor.UnlockedLimbs.Contains(limb);
        _updatingLocks = false;
    }
    internal void SelectStoryboardFrame(int frame, bool extend = false)
    {
        if (_busy || _loadedClip is null) return;
        frame = Math.Clamp(frame, 0, _loadedClip.FrameCount - 1);
        if (!extend) _selectionAnchor = frame;
        int first = Math.Min(_selectionAnchor, frame), last = Math.Max(_selectionAnchor, frame);
        _first.Text = first.ToString(); _last.Text = last.ToString(); _length.Text = (last - first + 1).ToString();
        _frame.Text = frame.ToString(); _preview.PauseAnimation();
        _settingStoryboardSelection = true;
        try { ReadPose(); SyncStoryboardSelection(); }
        finally { _settingStoryboardSelection = false; }
    }
    private void SyncStoryboardSelection()
    {
        if (_loadedClip is null) return;
        var r = SelectedRange(); _story.First = _timeline.First = r.First; _story.Last = _timeline.Last = r.Last;
        if (r.First == r.Last) _selectionAnchor = r.First;
        _story.Current = CurrentFrame; _timeline.Frame = CurrentFrame;
        _selectedInfo.Text = r.First == r.Last ? $"Выбран кадр {r.First}. Вставка заменяет его позу." : $"Выбраны кадры {r.First}–{r.Last}. Замена начинается с {r.First}.";
        _timeline.InvalidateVisual(); _story.InvalidateVisual();
        EnsureStoryboardFrameVisible();
    }
    private void EnsureStoryboardFrameVisible()
    {
        if (_storyScroll.ViewportWidth <= 0) return;
        double x = CurrentFrame * AnimationStoryboard.CardWidth;
        if (x < _storyScroll.HorizontalOffset) _storyScroll.ScrollToHorizontalOffset(x);
        else if (x + AnimationStoryboard.CardWidth > _storyScroll.HorizontalOffset + _storyScroll.ViewportWidth)
            _storyScroll.ScrollToHorizontalOffset(x + AnimationStoryboard.CardWidth - _storyScroll.ViewportWidth);
    }
    private void RefreshStoryboard()
    {
        if (_loadedClip is null) return;
        _story.SetClip(_editor.Preview(), _loadedClip, EditableParts(_scene, _loadedClip)); SyncStoryboardSelection(); RefreshPartLocks();
    }
    private void EnsurePoseEditable()
    {
        if (_before.IsChecked == true) throw new InvalidOperationException("Выключите показ оригинала для редактирования.");
    }
    internal async Task CopySelectedFrames()
    {
        var r = SelectedRange(); _copiedFrames = await _editor.CopyFramesAsync(_name, r.First, r.Last);
        _message.Text = $"Скопировано: {r.First}–{r.Last}. Выберите место и нажмите «Заменить позу» (Ctrl+V).";
    }
    internal async Task ReplaceSelectedFrames()
    {
        EnsurePoseEditable();
        if (_copiedFrames is null) throw new InvalidOperationException("Сначала скопируйте позу: Ctrl+C.");
        int frame = SelectedRange().First;
        await _editor.ReplaceFramesAsync(_name, frame, _copiedFrames); await Refresh();
        _message.Text = $"Заменены кадры {frame}–{frame + _copiedFrames.Count - 1}. Длина клипа сохранена. Ctrl+Z — отменить.";
    }
    internal async Task DeleteSelectedFrames()
    {
        EnsurePoseEditable(); var r = SelectedRange(); await _editor.DeleteFramesAsync(_name, r.First, r.Last);
        _frame.Text = _first.Text = _last.Text = r.First.ToString(); await Refresh();
        _message.Text = $"Удалены кадры {r.First}–{r.Last}. Остальные сдвинуты. Ctrl+Z — отменить.";
    }
    private void InitializeStoryboard(StackPanel sidebar)
    {
        _storySidebar = sidebar;
        _storyScroll.Content = _story;
        _storyScroll.ScrollChanged += (_, e) => { _story.Offset = _storyScroll.HorizontalOffset; _story.Viewport = _storyScroll.ViewportWidth; _story.InvalidateVisual(); if (e.ViewportWidthChange != 0) EnsureStoryboardFrameVisible(); };
        _story.Selected += SelectStoryboardFrame;
        void Button(string label, Func<Task> action)
        {
            var b = ActionButton(label, () => { }); b.Click += async (_, _) => await Run(action); _storyTools.Children.Add(b);
        }
        Button("Копировать · Ctrl+C", CopySelectedFrames);
        Button("Заменить позу · Ctrl+V", ReplaceSelectedFrames);
        Button("Из предыдущего", async () =>
        {
            EnsurePoseEditable(); int frame = SelectedRange().First;
            if (frame == 0) throw new InvalidOperationException("Перед кадром 0 нет предыдущего кадра.");
            await _editor.ReplaceFramesAsync(_name, frame, await _editor.CopyFramesAsync(_name, frame - 1, frame - 1)); await Refresh();
            _message.Text = $"В кадр {frame} скопирована поза кадра {frame - 1}.";
        });
        Button("Дублировать после", async () => { EnsurePoseEditable(); var r = SelectedRange(); await _editor.DuplicateFramesAsync(_name, r.First, r.Last); _frame.Text = _first.Text = _last.Text = (r.Last + 1).ToString(); await Refresh(); _message.Text = "Копия добавлена после выделения."; });
        Button("Удалить · Del", DeleteSelectedFrames);
        Button("Отменить · Ctrl+Z", async () => { await _editor.NavigateHistoryAsync(_name, false); await Refresh(); _message.Text = "Операция отменена."; });
        var reset = new ComboBox { Width = 145, ItemsSource = new[] { "Сброс позы…", "В исходную позу кадра", "В Т-позу" }, SelectedIndex = 0, Margin = new(3) };
        reset.SelectionChanged += async (_, _) => { int mode = reset.SelectedIndex; if (mode == 0) return; reset.SelectedIndex = 0; await Run(async () => { EnsurePoseEditable(); await _editor.ResetFrameAsync(_name, CurrentFrame, mode == 2); await Refresh(); _message.Text = "Поза кадра сброшена. Ctrl+Z — отменить."; }); }; _storyTools.Children.Add(reset);

        // Keep the core workflow visible; existing specialist tools remain available together.
        sidebar.Children.Remove(_message);
        var advanced = new StackPanel(); var previous = sidebar.Children.Cast<UIElement>().ToArray(); sidebar.Children.Clear();
        _authoring.Children.Remove(_limits); // Per-limb control replaces the ambiguous global switch.
        foreach (var child in previous) advanced.Children.Add(child);
        var partGroup = _poseActions.Children[0]; _poseActions.Children.RemoveAt(0);
        sidebar.Children.Add(Paragraph("ВЫБРАННАЯ ЧАСТЬ")); sidebar.Children.Add(partGroup);
        sidebar.Children.Add(_partLocked); sidebar.Children.Add(_bendUnlocked);
        sidebar.Children.Add(Paragraph("Блокировка удерживает часть и при движении её родителей. Ограничение сгиба снимается только у выбранной руки или ноги."));
        sidebar.Children.Add(_selectedInfo);
        var frameGroup = _poseActions.Children[0]; _poseActions.Children.RemoveAt(0); sidebar.Children.Add(frameGroup);
        _frame.LostKeyboardFocus += (_, _) => SelectStoryboardFrame(CurrentFrame);
        var source = new ComboBox { ItemsSource = _scene.Animations!.Names, ItemTemplate = AnimationLabelConverter.Template(() => _scene.Model), SelectedItem = _name, Margin = new(3) };
        var sourceFrame = Number(); sourceFrame.Text = "0";
        var import = new StackPanel(); import.Children.Add(source); import.Children.Add(Paragraph("Номер кадра источника (с 0). Части без дорожки в источнике сохранят свою позу.")); import.Children.Add(sourceFrame);
        var transfer = ActionButton("Заменить выбранный кадр", () => { }); transfer.Click += async (_, _) => await Run(async () =>
        {
            EnsurePoseEditable();
            if (source.SelectedItem is not string clip || !int.TryParse(sourceFrame.Text, out int f)) throw new ArgumentException("Выберите анимацию и целый номер кадра.");
            await _editor.ReplaceFramesAsync(_name, CurrentFrame, await _editor.CopyFramesAsync(clip, f, f)); await Refresh();
            _message.Text = $"Поза из {clip}, кадр {f}, заменяет кадр {CurrentFrame}. Остальные кадры сохранены.";
        }); import.Children.Add(transfer);
        sidebar.Children.Add(new Expander { Header = "Поза из другой анимации", Content = import, Margin = new(4) });
        sidebar.Children.Add(new Expander { Header = "Точные параметры, монтаж и проект", Content = advanced, Margin = new(4) });
        var saving = new WrapPanel();
        foreach (string title in new[] { "Сохранить проект…", "Открыть проект…", "Экспорт ANM…" })
        {
            var parent = title == "Экспорт ANM…" ? _actions : _authoring;
            var button = parent.Children.OfType<Button>().Single(b => b.Content as string == title);
            parent.Children.Remove(button); saving.Children.Add(button);
        }
        sidebar.Children.Add(saving);
        sidebar.Children.Add(Paragraph("Миниатюры показывают схему позы. Щелчок — кадр, Shift — диапазон. Ctrl+Z / Ctrl+Y — отмена и повтор последних 32 операций."));
        var footer = (StackPanel)_storyScroll.Parent; footer.Children.Add(_message);
        _partLocked.Checked += (_, _) => SetPartLock(true); _partLocked.Unchecked += (_, _) => SetPartLock(false);
        _bendUnlocked.Checked += (_, _) => SetBendUnlock(true); _bendUnlocked.Unchecked += (_, _) => SetBendUnlock(false);
        _partLocked.ToolTip = "Запрещает менять положение и поворот части, в том числе через родительские суставы. Удаление и вставка новых кадров остаются доступны.";
        _bendUnlocked.ToolTip = "По умолчанию выключено: ограничения сгиба включены. Ctrl+U переключает только выбранную конечность.";
        PreviewKeyDown += async (_, e) =>
        {
            if (_busy || Keyboard.FocusedElement is TextBox || _loadedClip is null) return;
            var action = StoryboardKeyAction(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement is ComboBox);
            if (action is not null) { e.Handled = true; await Run(action); }
        };
        Compact(_storyTools);
    }
    internal Func<Task>? StoryboardKeyAction(Key key, ModifierKeys modifiers, bool comboFocused = false)
    {
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && key == Key.L) return () => { _partLocked.IsChecked = _partLocked.IsChecked != true; return Task.CompletedTask; };
        if (ctrl && key == Key.U && _bendUnlocked.IsEnabled) return () => { _bendUnlocked.IsChecked = _bendUnlocked.IsChecked != true; return Task.CompletedTask; };
        if (comboFocused) return null;
        if (ctrl && key == Key.C) return CopySelectedFrames;
        if (ctrl && key == Key.V) return ReplaceSelectedFrames;
        if (ctrl && key is Key.Z or Key.Y) return async () => { await _editor.NavigateHistoryAsync(_name, key == Key.Y); await Refresh(); };
        if (key == Key.Delete) return DeleteSelectedFrames;
        return null;
    }
    private void SetPartLock(bool value)
    {
        if (_updatingLocks || _part.SelectedItem is not string part) return;
        _preview.PauseAnimation();
        if (value) _editor.LockedParts.Add(part); else _editor.LockedParts.Remove(part);
        _message.Text = PartLabel(part) + (value ? " — заблокирована." : " — разблокирована.");
    }
    private void SetBendUnlock(bool value)
    {
        if (_updatingLocks || SelectedLimb() is not string limb) return;
        _preview.PauseAnimation();
        if (value) _editor.UnlockedLimbs.Add(limb); else _editor.UnlockedLimbs.Remove(limb);
        _message.Text = value ? "Ограничение сгиба выбранной конечности снято." : "Ограничение сгиба выбранной конечности включено.";
    }
}
