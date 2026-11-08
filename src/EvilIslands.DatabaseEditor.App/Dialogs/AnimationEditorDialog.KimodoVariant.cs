using System.Windows;
using System.Windows.Controls;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog
{
    internal static UnitModelPreviewView CreateGeneratedComparisonView() => new()
    {
        AllowAnimationEditing = false,
        // This window owns its prepared scene; database/visibility reloads must not clear it.
        ExternalScene = true
    };
    internal static async Task DisplayGeneratedComparisonAsync(UnitModelPreviewView view, UnitModelScene scene,
        FigureAnimationSet set, string name, bool loop)
    {
        view.PauseAnimation();
        int frame = (int)view.AnimationFrame;
        bool initial = view.ActiveAnimationName is null;
        if (initial) view.Visibility = Visibility.Hidden;
        var clip = await set.LoadAsync(name);
        if (initial) view.ShowScene(scene with { Animations = set });
        await view.ReplaceEditedAnimationAsync(set, clip, frame);
        view.RestrictToEditedClip(); view.EditorLoop = loop;
        if (initial) view.Visibility = Visibility.Visible;
        await view.PlayAnimationAsync();
    }

    private async Task ReviewKimodoVariant(FigureAnimationClip generated, string history, ulong usedSeed)
    {
        var original = _editor.Preview();
        var clip = await original.LoadAsync(_name);
        var window = new KimodoWindow { Owner = this, Title = "Сравнить и применить вариант — " + _name,
            Width = Math.Min(1150, SystemParameters.WorkArea.Width - 32), Height = Math.Min(850, SystemParameters.WorkArea.Height - 32) };
        var view = CreateGeneratedComparisonView();
        var status = Paragraph($"Seed: {usedSeed}. Исходная анимация. Настройте область и нажмите «Показать вариант».");
        var controls = new StackPanel { Margin = new Thickness(6) };
        controls.Children.Add(Paragraph($"Использованный seed: {usedSeed}. Введите его в генераторе, чтобы повторить результат."));
        var first = new TextBox { Text = "0" }; var last = new TextBox { Text = (clip.FrameCount - 1).ToString() };
        controls.Children.Add(Paragraph("Кадры (нумерация с 0)")); controls.Children.Add(first); controls.Children.Add(last);
        controls.Children.Add(ActionButton("Взять выделенные кадры", () => { var range = SelectedRange(); first.Text = range.First.ToString(); last.Text = range.Last.ToString(); }));
        var strength = new Slider { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
        var strengthText = Paragraph("Сила изменения: 100%"); controls.Children.Add(strengthText); controls.Children.Add(strength);
        controls.Children.Add(Paragraph("Выберите изменяемые части. Движение родителя влияет на дочерние части; блокировки и фиксация стоп проверяются перед применением."));
        var checks = EditableParts(_scene, clip).Where(generated.Tracks.ContainsKey).Select(p => new CheckBox
        { Tag = p, Content = PartLabel(p), IsChecked = !_editor.LockedParts.Contains(p), Margin = new Thickness(3) }).ToArray();
        foreach (var check in checks) controls.Children.Add(check);
        controls.Children.Add(Paragraph("Границы участка плавно смешиваются с исходником. Смешивание не гарантирует контакт стоп и бесшовный цикл — проверьте движение. Файл будет изменён только обычной кнопкой сохранения после принятия."));
        var apply = ActionButton("Принять в текущую анимацию", () => window.DialogResult = true); apply.IsEnabled = false;
        FigureAnimationEditor? candidate = null;
        bool busy = false;
        bool showingVariant = false;
        var ghost = new CheckBox { Content = "Тень оригинала", Margin = new Thickness(8),
            ToolTip = "Обычная модель — вариант, голубая полупрозрачная — оригинал. Обе позы показываются на одном кадре." };
        void Invalidate() { candidate = null; apply.IsEnabled = false; status.Text = "Настройки изменены. Нажмите «Показать вариант», чтобы пересчитать и проверить."; }
        first.TextChanged += (_, _) => Invalidate(); last.TextChanged += (_, _) => Invalidate();
        strength.ValueChanged += (_, _) => { strengthText.Text = $"Сила изменения: {strength.Value:0}%"; Invalidate(); };
        foreach (var check in checks) { check.Checked += (_, _) => Invalidate(); check.Unchecked += (_, _) => Invalidate(); }
        async Task Display(FigureAnimationSet set)
        {
            await DisplayGeneratedComparisonAsync(view, _scene, set, _name, _preview.EditorLoop);
        }
        var toolbar = new WrapPanel();
        var showOriginal = ActionButton("Показать оригинал", () => { });
        var showVariant = ActionButton("Показать вариант", () => { });
        async Task Show(bool variant)
        {
            if (busy) return;
            busy = true; apply.IsEnabled = false; controls.IsEnabled = false; toolbar.IsEnabled = false;
            try
            {
                if (!variant) { await Display(original); status.Text = "Оригинал — с вашими правками до генерации."; }
                else
                {
                    if (!int.TryParse(first.Text, out int a) || !int.TryParse(last.Text, out int b)) throw new ArgumentException("Введите целые номера кадров.");
                    var next = new FigureAnimationEditor(original) { MotionPins = _editor.MotionPins, Contacts = _editor.Contacts };
                    next.LockedParts.UnionWith(_editor.LockedParts);
                    await next.BlendGeneratedVariantAsync(_name, generated, a, b,
                        checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(), (float)strength.Value / 100);
                    var issues = await next.DiagnoseAsync(_name, _preview.EditorLoop);
                    await Display(next.Preview()); candidate = next;
                    status.Text = $"Вариант · замечаний проверки: {issues.Count}. Сравните с оригиналом перед принятием.";
                }
                showingVariant = variant;
                if (!variant) ghost.IsChecked = false;
                view.SetComparison(original, clip, false, variant && ghost.IsChecked == true);
            }
            catch (Exception ex) { candidate = null; status.Text = "Вариант не применён: " + ex.Message; }
            finally { busy = false; controls.IsEnabled = true; toolbar.IsEnabled = true; apply.IsEnabled = candidate is not null; }
        }
        showOriginal.Click += async (_, _) => await Show(false); showVariant.Click += async (_, _) => await Show(true);
        ghost.Checked += async (_, _) =>
        {
            if (!showingVariant || candidate is null) await Show(true);
            else view.SetComparison(original, clip, false, true);
        };
        ghost.Unchecked += (_, _) => view.SetComparison(original, clip, false, false);
        toolbar.Children.Add(showOriginal); toolbar.Children.Add(showVariant); toolbar.Children.Add(apply);
        toolbar.Children.Add(ghost);
        toolbar.Children.Add(ActionButton("Отмена", () => window.DialogResult = false));
        DockPanel.SetDock(toolbar, Dock.Bottom); window.Add(toolbar);
        DockPanel.SetDock(status, Dock.Bottom); window.Add(status);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Grid.SetColumn(view, 1); grid.Children.Add(view); window.Add(grid);
        window.Loaded += async (_, _) => await Show(false);
        window.Closing += (_, e) => { if (busy) e.Cancel = true; else view.PauseAnimation(); };
        if (window.ShowDialog() == true && candidate is not null)
        {
            _editor.AdoptSavedBytes(candidate.ExportBytes()); // In-memory acceptance only; saved baseline remains unchanged.
            await Refresh();
            _message.Text = "Вариант принят в «" + _name + "». Доступна отмена Ctrl+Z. Нажмите «Сохранить» для записи или «Создать копию…» для нового ID.";
            _generationSummary.Text = $"Seed: {usedSeed}. Исходные данные генерации: " + history;
        }
        else _message.Text = "Вариант отклонён. Открытая анимация не изменена.";
    }
}
