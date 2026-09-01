using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class StructuredStringListEditorDialog : Window, INotifyPropertyChanged
{
    private readonly Func<IReadOnlyList<string>, (bool Success, string? Error)> _tryApply;
    private readonly IReadOnlyList<string> _referenceSuggestions;
    private readonly string _fieldName;
    private readonly bool _singleValueMode;
    private StructuredStringListItemViewModel? _selectedItem;
    private bool _syncingTabs;
    private bool _isRawMode;

    internal StructuredStringListEditorDialog(
        string address,
        string fieldName,
        IReadOnlyList<string> initialValues,
        IReadOnlyList<string> referenceSuggestions,
        Func<IReadOnlyList<string>, (bool Success, string? Error)> tryApply,
        bool singleValueMode = false)
    {
        _tryApply = tryApply ?? throw new ArgumentNullException(nameof(tryApply));
        _referenceSuggestions = referenceSuggestions ?? [];
        _fieldName = fieldName ?? string.Empty;
        _singleValueMode = singleValueMode;
        Items = new ObservableCollection<StructuredStringListItemViewModel>(
            initialValues.Select(CreateItem));
        foreach (StructuredStringListItemViewModel item in Items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }

        InitializeComponent();
        DataContext = this;
        ItemsGrid.ItemsSource = Items;
        AddressText.Text = string.IsNullOrWhiteSpace(address) ? "—" : address;
        FieldNameText.Text = fieldName;
        if (_singleValueMode)
        {
            Title = "Структурный редактор значения";
            DialogTitleText.Text = "Структурный редактор значения";
            FieldDescriptionText.Text =
                "Поле хранит один составной объект RES. Оружие и вложенное заклинание применяются одной операцией Undo.";
            ListActionsBorder.Visibility = Visibility.Collapsed;
            AddReferenceButton.Content = "Подставить →";
            ApplyButton.Content = "Применить значение";
        }
        SynchronizeRawText();
        RenumberItems();
        RefreshReferenceResults();
        SelectedItem = Items.FirstOrDefault();
        SetEditorMode(useRawMode: false);
        Loaded += (_, _) => ItemsGrid.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<StructuredStringListItemViewModel> Items { get; }

    public StructuredStringListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            _selectedItem = value;
            OnPropertyChanged();
            if (ItemsGrid is not null && !ReferenceEquals(ItemsGrid.SelectedItem, value))
            {
                ItemsGrid.SelectedItem = value;
            }
            RefreshElementDetails();
        }
    }

    internal bool WasApplied { get; private set; }

    internal bool TryCommit()
    {
        ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_isRawMode && !TryLoadRawTextIntoItems())
        {
            return false;
        }

        string[] values = Items.Select(item => item.Value.Trim()).ToArray();
        if (_singleValueMode && values.Length != 1)
        {
            SetError("Это поле должно содержать ровно один составной объект.");
            return false;
        }
        for (int index = 0; index < values.Length; index++)
        {
            if (!StringListEditorCodec.TryValidateElement(values[index], out string? validationError))
            {
                SetError($"Элемент {index + 1}: {validationError}");
                SetEditorMode(useRawMode: false);
                SelectedItem = Items[index];
                ItemsGrid.ScrollIntoView(Items[index]);
                return false;
            }
        }

        (bool success, string? error) = _tryApply(values);
        if (!success)
        {
            SetError(error ?? (_singleValueMode ? "Значение не принято." : "Список не принят."));
            return false;
        }

        WasApplied = true;
        SetError(null);
        return true;
    }

    private StructuredStringListItemViewModel CreateItem(string value) => new(value, _fieldName);

    private void AddButton_Click(object sender, RoutedEventArgs e) => InsertValue(string.Empty);

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not null)
        {
            InsertValue(SelectedItem.Value);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null)
        {
            return;
        }

        int index = Items.IndexOf(SelectedItem);
        SelectedItem.PropertyChanged -= Item_PropertyChanged;
        Items.RemoveAt(index);
        RenumberItems();
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        SynchronizeRawText();
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (SelectedItem is null)
        {
            return;
        }

        int oldIndex = Items.IndexOf(SelectedItem);
        int newIndex = oldIndex + delta;
        if (newIndex < 0 || newIndex >= Items.Count)
        {
            return;
        }

        Items.Move(oldIndex, newIndex);
        RenumberItems();
        ItemsGrid.ScrollIntoView(SelectedItem);
        SynchronizeRawText();
    }

    private void InsertValue(string value)
    {
        var item = CreateItem(value);
        item.PropertyChanged += Item_PropertyChanged;
        int index = SelectedItem is null ? Items.Count : Items.IndexOf(SelectedItem) + 1;
        Items.Insert(index, item);
        RenumberItems();
        SelectedItem = item;
        ItemsGrid.ScrollIntoView(item);
        SynchronizeRawText();
        ItemsGrid.Focus();
        ItemsGrid.BeginEdit();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StructuredStringListItemViewModel.Value))
        {
            return;
        }

        SetError(null);
        SynchronizeRawText();
        if (ReferenceEquals(sender, SelectedItem))
        {
            RefreshElementDetails();
        }
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedItem = ItemsGrid.SelectedItem as StructuredStringListItemViewModel;
        RefreshElementDetails();
    }

    private void RefreshElementDetails()
    {
        StringListElementAnalysis analysis = StringListEditorCodec.AnalyzeElement(SelectedItem?.Value, _fieldName);
        bool isItemMagic = analysis.Kind == "Предмет с магией";
        bool isStandaloneSpell = analysis.Kind == "Заклинание";
        ElementKindText.Text = SelectedItem is null ? "Элемент не выбран" : analysis.Kind;
        ElementSummaryText.Text = SelectedItem is null ? "Выберите строку слева." : analysis.Summary;
        PrimaryValueLabel.Text = analysis.Kind switch
        {
            "Предмет с магией" => "Предмет / форма / материал",
            "Заклинание" => "Код заклинания",
            "Звук шага" => "Путь блока звука шага",
            "Путь к ресурсу" => "Путь к ресурсу",
            _ => "Значение элемента",
        };
        PrimaryValueBox.Text = SelectedItem is null ? string.Empty : analysis.PrimaryValue;
        SpellCodeBox.Text = analysis.SpellCode ?? string.Empty;
        ModifiersBox.Text = string.Join(";", analysis.Modifiers);
        PrimaryValueBox.IsEnabled = SelectedItem is not null;
        SpellCodePanel.Visibility = isItemMagic ? Visibility.Visible : Visibility.Collapsed;
        ModifiersPanel.Visibility = isItemMagic || isStandaloneSpell ? Visibility.Visible : Visibility.Collapsed;
        SpellCodeBox.IsEnabled = SelectedItem is not null && isItemMagic;
        ModifiersBox.IsEnabled = SelectedItem is not null && (isItemMagic || isStandaloneSpell);
        UpdateElementButton.IsEnabled = SelectedItem is not null;
        SetElementError(analysis.Error);
    }

    private void UpdateElementButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null)
        {
            return;
        }

        StringListElementAnalysis previous = StringListEditorCodec.AnalyzeElement(SelectedItem.Value, _fieldName);
        string primary = PrimaryValueBox.Text.Trim();
        string spell = SpellCodeBox.Text.Trim();
        string[] modifiers = ModifiersBox.Text
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string value = previous.Kind switch
        {
            "Предмет с магией" => $"{primary}[{BuildSpellValue(spell, modifiers)}]",
            "Заклинание" => $"{primary}{{{string.Join(";", modifiers)}}}",
            _ => primary,
        };

        if (!StringListEditorCodec.TryValidateElement(value, out string? error))
        {
            SetElementError(error);
            return;
        }

        SelectedItem.Value = value;
        SetElementError(null);
    }

    private void ReferenceSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshReferenceResults();

    private void RefreshReferenceResults()
    {
        if (ReferenceResultsList is null || ReferenceSearchBox is null)
        {
            return;
        }

        string query = ReferenceSearchBox.Text.Trim();
        ReferenceResultsList.ItemsSource = _referenceSuggestions
            .Where(value => query.Length == 0 || value.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(100)
            .ToArray();
    }

    private void AddReferenceButton_Click(object sender, RoutedEventArgs e) => AddSelectedReference();

    private void ReferenceResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedReference();

    private void AddSelectedReference()
    {
        string? value = ReferenceResultsList.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = ReferenceSearchBox.Text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (_singleValueMode)
            {
                if (SelectedItem is null)
                {
                    InsertValue(value);
                }
                else
                {
                    SelectedItem.Value = value;
                }
            }
            else
            {
                InsertValue(value);
            }
        }
    }

    private static string BuildSpellValue(string spell, IReadOnlyList<string> modifiers) =>
        modifiers.Count == 0 ? spell : $"{spell}{{{string.Join(";", modifiers)}}}";

    private void StructuredModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }

        if (_isRawMode && !TryLoadRawTextIntoItems())
        {
            SetEditorMode(useRawMode: true);
            return;
        }

        SetEditorMode(useRawMode: false);
    }

    private void RawModeButton_Click(object sender, RoutedEventArgs e) => SetEditorMode(useRawMode: true);

    private void SetEditorMode(bool useRawMode)
    {
        _isRawMode = useRawMode;
        if (StructuredPanel is null || RawPanel is null || StructuredModeButton is null || RawModeButton is null)
        {
            return;
        }

        _syncingTabs = true;
        StructuredPanel.Visibility = useRawMode ? Visibility.Collapsed : Visibility.Visible;
        RawPanel.Visibility = useRawMode ? Visibility.Visible : Visibility.Collapsed;
        StructuredModeButton.IsChecked = !useRawMode;
        RawModeButton.IsChecked = useRawMode;
        _syncingTabs = false;

        if (useRawMode)
        {
            RawEditorBox.Focus();
        }
        else
        {
            ItemsGrid.Focus();
        }
    }

    private bool TryLoadRawTextIntoItems()
    {
        IReadOnlyList<string> values;
        string? error = null;
        if (_singleValueMode)
        {
            values = [RawEditorBox.Text.Trim()];
            if (!StringListEditorCodec.TryValidateElement(values[0], out error))
            {
                SetError(error);
                return false;
            }
        }
        else if (!StringListEditorCodec.TryParseEditorText(RawEditorBox.Text, out values, out error))
        {
            SetError(error);
            return false;
        }

        if (Items.Select(item => item.Value).SequenceEqual(values, StringComparer.Ordinal))
        {
            SetError(null);
            return true;
        }

        foreach (StructuredStringListItemViewModel item in Items)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        Items.Clear();
        foreach (string value in values)
        {
            StructuredStringListItemViewModel item = CreateItem(value);
            item.PropertyChanged += Item_PropertyChanged;
            Items.Add(item);
        }

        RenumberItems();
        SelectedItem = Items.FirstOrDefault();
        SetError(null);
        return true;
    }

    private void RawEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingTabs)
        {
            SetError(null);
        }
    }

    private void SynchronizeRawText()
    {
        if (RawEditorBox is null)
        {
            return;
        }

        _syncingTabs = true;
        RawEditorBox.Text = _singleValueMode
            ? Items.FirstOrDefault()?.Value ?? string.Empty
            : StringListEditorCodec.FormatEditorText(Items.Select(item => item.Value).ToArray());
        _syncingTabs = false;
        StructuredStatisticsText.Text = _singleValueMode
            ? "1 составной объект · изменение применится одной операцией Undo"
            : $"{Items.Count:N0} элементов · изменения применятся одной операцией Undo";
    }

    private void RenumberItems()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].Number = index + 1;
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryCommit())
        {
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetError(string? error)
    {
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetElementError(string? error)
    {
        ElementErrorText.Text = error ?? string.Empty;
        ElementErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && TryCommit())
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class StructuredStringListItemViewModel : INotifyPropertyChanged
{
    private readonly string _fieldName;
    private int _number;
    private string _value;

    public StructuredStringListItemViewModel(string value, string fieldName = "")
    {
        _value = value;
        _fieldName = fieldName;
        RefreshAnalysis();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number
    {
        get => _number;
        set
        {
            if (_number == value)
            {
                return;
            }

            _number = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number)));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            RefreshAnalysis();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public string Summary { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    private void RefreshAnalysis()
    {
        StringListElementAnalysis analysis = StringListEditorCodec.AnalyzeElement(_value, _fieldName);
        Kind = analysis.Kind;
        Summary = analysis.Error is null ? analysis.Summary : $"⚠ {analysis.Error}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
}
