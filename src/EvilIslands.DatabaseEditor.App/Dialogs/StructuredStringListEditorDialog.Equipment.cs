using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class StructuredStringListEditorDialog
{
    private ModelRecord? _equipmentUnit;
    private ModelRecord[] _equipmentRecords = [];
    private string _equipmentPath = "";
    private int _equipmentField;
    private LuaDataWorkspace? _equipmentLua;
    private string? _equipmentLuaProblem;
    private CancellationTokenSource? _equipmentCancellation;
    private UnitModelPreviewView? _equipmentPreview;
    private readonly TextBlock _equipmentStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6) };
    private readonly StackPanel _equipmentStats = new() { Margin = new Thickness(10, 0, 4, 0) };
    private ComboBox? _equipmentSlot;
    private bool _changingEquipment;
    internal Task EquipmentLoadTask { get; private set; } = Task.CompletedTask;
    internal ModelRecord? EquipmentDraft { get; private set; }
    private static readonly string[] EquipmentSlots = ["Шлем", "Панцирь", "Поножи", "Рубашка", "Штаны", "Сапоги", "Перчатки"];

    internal void ConfigureEquipment(MainWindowViewModel vm, RecordRowViewModel row, int field)
    {
        _equipmentUnit = ModelRecord.Capture(row.TableKey, row.CoreRecord);
        _equipmentRecords = vm.Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r.CoreRecord))).ToArray();
        _equipmentPath = vm.SourcePath;
        _equipmentField = field;
        _equipmentLua = vm.LuaWorkspace;
        string? luaProblem = null;
        try
        {
            if (_equipmentLua is null && LuaDataWorkspace.FindNearby(vm.SourcePath) is { } luaFolder)
                _equipmentLua = LuaDataWorkspace.Open(luaFolder);
        }
        catch (Exception ex) { luaProblem = "Lua не прочитан: " + ex.Message; }
        _equipmentLuaProblem = luaProblem;
        Title = DialogTitleText.Text = "Редактор экипировки — " + row.CoreRecord.IdentityValue;
        Width = 1600; Height = 900; MinWidth = 1180;
        FieldDescriptionText.Text = "Изменения видны на модели сразу. Применить — записать весь список одной операцией Undo; Отмена — оставить исходный.";

        var original = (Grid)((Border)ItemsGrid.Parent).Parent;
        var list = (Border)ItemsGrid.Parent;
        var details = (Border)ElementDetailsScrollViewer.Parent;
        original.Children.Clear(); original.ColumnDefinitions.Clear();
        original.ColumnDefinitions.Add(new() { Width = new GridLength(490), MinWidth = 390 });
        original.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        original.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        var left = new Grid();
        left.RowDefinitions.Add(new() { Height = new GridLength(185) });
        left.RowDefinitions.Add(new() { Height = new GridLength(6) });
        left.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(list, 0); Grid.SetRow(list, 0); left.Children.Add(list);
        var horizontal = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(horizontal, 1); left.Children.Add(horizontal);
        Grid.SetColumn(details, 0); Grid.SetRow(details, 2); details.Padding = new Thickness(8); left.Children.Add(details);
        original.Children.Add(left);
        var splitter = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); original.Children.Add(splitter);
        var rightArea = new Grid(); Grid.SetColumn(rightArea, 2); original.Children.Add(rightArea);
        rightArea.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        rightArea.ColumnDefinitions.Add(new() { Width = new GridLength(330), MinWidth = 260 });
        var right = new DockPanel(); rightArea.Children.Add(right);
        DockPanel.SetDock(_equipmentStatus, Dock.Top); right.Children.Add(_equipmentStatus);
        var stats = new ScrollViewer { Content = _equipmentStats, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(stats, 1); rightArea.Children.Add(stats);
        RegisterName("EquipmentStatisticsPanel", _equipmentStats);
        _equipmentPreview = new UnitModelPreviewView { DataContext = null, ExternalScene = true }; right.Children.Add(_equipmentPreview);
        RegisterName("EquipmentPreview", _equipmentPreview);
        ((StackPanel)ElementKindText.Parent).Visibility = Visibility.Collapsed;
        ElementTypeDescriptionText.Visibility = Visibility.Collapsed;
        ItemsGrid.Columns[0].Width = 32;
        ItemsGrid.Columns[1].MinWidth = 100; ItemsGrid.Columns[1].Width = 150;
        ItemsGrid.Columns[2].Visibility = Visibility.Visible;
        ItemsGrid.Columns[2].Width = 270; ItemsGrid.Columns[2].MinWidth = 200;
        ItemsGrid.Columns[3].Visibility = Visibility.Collapsed;
        foreach (var column in ItemsGrid.Columns.OfType<DataGridTextColumn>())
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            column.ElementStyle = style;
        }
        if (field == 32)
        {
            _equipmentSlot = new ComboBox { ItemsSource = new[] { "Все семь типов" }.Concat(EquipmentSlots),
                SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 7) };
            RegisterName("EquipmentSlotCombo", _equipmentSlot);
            ItemConstructorPanel.Children.Insert(0, _equipmentSlot);
            _equipmentSlot.SelectionChanged += (_, _) => FilterEquipmentShapes();
        }
        foreach (var combo in new[] { ElementTypeComboBox, MagicShapeComboBox, MagicMaterialComboBox, MagicSpellComboBox })
        {
            combo.MinHeight = 30; combo.Padding = new Thickness(6, 3, 6, 3);
            combo.SelectionChanged += (_, _) => CommitEquipmentChoice();
        }
        Items.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (StructuredStringListItemViewModel item in e.OldItems) item.PropertyChanged -= EquipmentItemChanged;
            if (e.NewItems is not null) foreach (StructuredStringListItemViewModel item in e.NewItems) item.PropertyChanged += EquipmentItemChanged;
            QueueEquipmentPreview();
        };
        foreach (var item in Items) item.PropertyChanged += EquipmentItemChanged;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedItem)) QueueEquipmentPreview(); };
        Closed += (_, _) => { _equipmentCancellation?.Cancel(); _equipmentCancellation?.Dispose(); _equipmentPreview.PauseAnimation(); };
        Loaded += (_, _) => QueueEquipmentPreview();
        QueueEquipmentPreview();
    }

    private void FilterEquipmentShapes()
    {
        if (_magicContext is null || _equipmentSlot is null || _syncingMagicConstructor) return;
        string? previous = (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value;
        int slot = _equipmentSlot.SelectedIndex - 1;
        var shapes = _magicContext.Armors.Where(c => slot < 0 || _equipmentRecords.Any(r =>
            r.Table == "items.armors" && r.Name == c.Value && r.Integer(2, -1) == slot)).ToArray();
        _syncingMagicConstructor = true;
        try { SetShapeChoices(shapes, FindChoice(shapes, previous)); }
        finally { _syncingMagicConstructor = false; }
        RefreshMagicEvaluation();
    }

    private void CommitEquipmentChoice()
    {
        if (_equipmentUnit is null || _syncingMagicConstructor || _changingEquipment || SelectedItem is null) return;
        _changingEquipment = true;
        try { UpdateElementButton_Click(this, new RoutedEventArgs()); }
        finally { _changingEquipment = false; }
    }

    private void EquipmentItemChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(StructuredStringListItemViewModel.Value)) QueueEquipmentPreview(); }

    private void QueueEquipmentPreview()
    {
        if (_equipmentUnit is null) return;
        _equipmentCancellation?.Cancel(); _equipmentCancellation?.Dispose();
        var token = (_equipmentCancellation = new()).Token;
        if (_isRawMode) return;
        var fields = _equipmentUnit.Fields.ToDictionary();
        var lists = _equipmentUnit.Lists.ToDictionary();
        if (_equipmentField == 32) lists[32] = Items.Select(i => i.Value).ToArray();
        else fields[_equipmentField] = Items.FirstOrDefault()?.Value ?? "";
        EquipmentDraft = _equipmentUnit with { Fields = fields, Lists = lists };
        var previewDraft = EquipmentDraft;
        try
        {
            var report = EquipmentInspection.Inspect(previewDraft, _equipmentRecords, _equipmentLua);
            if (_equipmentLuaProblem is not null) report.Notes.Add(_equipmentLuaProblem);
            ShowEquipmentStatistics(report);
        }
        catch (Exception ex)
        {
            _equipmentStats.Children.Clear();
            _equipmentStats.Children.Add(new TextBlock { Text = "Не удалось рассчитать характеристики: " + ex.Message, TextWrapping = TextWrapping.Wrap });
        }
        _equipmentStatus.Text = "Обновление экипировки…";
        EquipmentLoadTask = LoadEquipmentPreviewAsync(previewDraft, token);
    }

    private void ShowEquipmentStatistics(EquipmentStatReport report)
    {
        var expanded = _equipmentStats.Children.OfType<Expander>().Where(e => e.IsExpanded).Select(e => e.Tag?.ToString()).ToHashSet();
        _equipmentStats.Children.Clear();
        _equipmentStats.Children.Add(new TextBlock { Text = "Общие характеристики", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 10) });
        _equipmentStats.Children.Add(StatisticsTable(report.General));
        _equipmentStats.Children.Add(new TextBlock { Text = "Исходный прототип · без временных эффектов", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), FontSize = 11 });
        foreach (var section in report.Details.Where(s => s.Rows.Count > 0))
            _equipmentStats.Children.Add(new Expander { Header = new TextBlock { Text = section.Title, TextWrapping = TextWrapping.Wrap },
                Tag = section.Title, IsExpanded = expanded.Contains(section.Title), Content = StatisticsTable(section.Rows), Margin = new Thickness(0, 4, 0, 4) });
        // Calculation limits remain visible; detailed values are collapsed by default.
        foreach (string note in report.Notes.Distinct())
            _equipmentStats.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush") });
    }

    private Grid StatisticsTable(IReadOnlyList<EquipmentStatRow> rows)
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new() { Width = new GridLength(1.5, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int column = 0; column < 2; column++)
            {
                var cell = new Border { Padding = new Thickness(6), BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                    Background = (System.Windows.Media.Brush)FindResource(i % 2 == 0 ? "SurfaceBrush" : "WindowBrush"),
                    Child = new TextBlock { Text = column == 0 ? rows[i].Name : rows[i].Value, TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = column == 1 ? FontWeights.SemiBold : FontWeights.Normal } };
                Grid.SetRow(cell, i); Grid.SetColumn(cell, column); table.Children.Add(cell);
            }
        }
        return table;
    }

    private async Task LoadEquipmentPreviewAsync(ModelRecord draft, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            var scene = await UnitModelPreview.LoadAsync(_equipmentPath, draft, _equipmentRecords, token, lua: _equipmentLua);
            token.ThrowIfCancellationRequested();
            _equipmentPreview!.Visibility = Visibility.Visible;
            await _equipmentPreview.ShowEquipmentSceneAsync(scene, token);
            token.ThrowIfCancellationRequested();
            _equipmentStatus.Text = draft.Name + " · " + scene.Model + " · вращение мышью, масштаб колёсиком";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _equipmentPreview!.Visibility = Visibility.Collapsed;
                _equipmentStatus.Text = "Не удалось обновить модель: " + ex.Message;
            }
        }
    }
}
