using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class StructuredStringListEditorDialog : Window, INotifyPropertyChanged
{
    private readonly Func<IReadOnlyList<string>, (bool Success, string? Error)> _tryApply;
    private readonly IReadOnlyList<string> _referenceSuggestions;
    private readonly IReadOnlyList<string> _initialValues;
    private readonly string _fieldName;
    private readonly bool _singleValueMode;
    private readonly EmbeddedMagicEditorContext? _magicContext;
    private readonly ObservableCollection<EmbeddedMagicChoice> _selectedMagicModifiers = [];
    private StructuredStringListItemViewModel? _selectedItem;
    private bool _syncingTabs;
    private bool _syncingMagicConstructor;
    private bool _isRawMode;

    internal StructuredStringListEditorDialog(
        string address,
        string fieldName,
        IReadOnlyList<string> initialValues,
        IReadOnlyList<string> referenceSuggestions,
        Func<IReadOnlyList<string>, (bool Success, string? Error)> tryApply,
        bool singleValueMode = false,
        EmbeddedMagicEditorContext? magicContext = null)
    {
        _tryApply = tryApply ?? throw new ArgumentNullException(nameof(tryApply));
        _referenceSuggestions = referenceSuggestions ?? [];
        _initialValues = initialValues.ToArray();
        _fieldName = fieldName ?? string.Empty;
        _singleValueMode = singleValueMode;
        _magicContext = magicContext;
        Items = new ObservableCollection<StructuredStringListItemViewModel>(
            initialValues.Select(CreateItem));
        foreach (StructuredStringListItemViewModel item in Items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }

        InitializeComponent();
        DataContext = this;
        ItemsGrid.ItemsSource = Items;
        MagicModifiersList.ItemsSource = _selectedMagicModifiers;
        if (_magicContext is not null)
        {
            MagicShapeComboBox.ItemsSource = _magicContext.Shapes;
            MagicSpellComboBox.ItemsSource = _magicContext.Spells;
            MagicModifierComboBox.ItemsSource = _magicContext.Modifiers;
        }
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

            if (_magicContext is not null &&
                (index >= _initialValues.Count ||
                 !values[index].Equals(_initialValues[index], StringComparison.Ordinal)))
            {
                StringListElementAnalysis analysis = StringListEditorCodec.AnalyzeElement(values[index], _fieldName);
                if (analysis.Kind == "Предмет с магией")
                {
                    EmbeddedMagicEvaluation evaluation = _magicContext.Catalog.Evaluate(
                        analysis.PrimaryValue,
                        analysis.SpellCode,
                        analysis.Modifiers);
                    if (!evaluation.IsAllowedForManualInsertion)
                    {
                        SetError(
                            $"Элемент {index + 1} не проходит ограничения ручной вставки:" +
                            Environment.NewLine + evaluation.StatusText);
                        SetEditorMode(useRawMode: false);
                        SelectedItem = Items[index];
                        ItemsGrid.ScrollIntoView(Items[index]);
                        return false;
                    }
                }
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
        bool useMagicConstructor = isItemMagic && _magicContext is not null;
        OpenMagicConstructorButton.Visibility = _magicContext is not null &&
            SelectedItem is not null && !isItemMagic
                ? Visibility.Visible
                : Visibility.Collapsed;
        GenericElementEditorPanel.Visibility = useMagicConstructor ? Visibility.Collapsed : Visibility.Visible;
        MagicConstructorPanel.Visibility = useMagicConstructor ? Visibility.Visible : Visibility.Collapsed;
        if (useMagicConstructor)
        {
            LoadMagicConstructor(analysis);
        }
        SetElementError(analysis.Error);
    }

    private void UpdateElementButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null)
        {
            return;
        }

        StringListElementAnalysis previous = StringListEditorCodec.AnalyzeElement(SelectedItem.Value, _fieldName);
        bool useMagicConstructor = previous.Kind == "Предмет с магией" && _magicContext is not null;
        string primary = useMagicConstructor
            ? BuildSelectedItemValue()
            : PrimaryValueBox.Text.Trim();
        string spell = useMagicConstructor
            ? (MagicSpellComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty
            : SpellCodeBox.Text.Trim();
        string[] modifiers = useMagicConstructor
            ? _selectedMagicModifiers.Select(choice => choice.Value).ToArray()
            : ModifiersBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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

        if (useMagicConstructor)
        {
            EmbeddedMagicEvaluation evaluation = _magicContext!.Catalog.Evaluate(primary, spell, modifiers);
            if (!evaluation.IsAllowedForManualInsertion)
            {
                SetElementError(evaluation.StatusText);
                RefreshMagicEvaluation();
                return;
            }
        }

        SelectedItem.Value = value;
        SetElementError(null);
    }

    private void OpenMagicConstructorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_magicContext is null || SelectedItem is null)
        {
            return;
        }

        EmbeddedMagicModifierOption? itemRune = _magicContext.Catalog.Modifiers.FirstOrDefault(modifier =>
            modifier.Value.Equals("it", StringComparison.OrdinalIgnoreCase) ||
            modifier.Value.Equals("ic", StringComparison.OrdinalIgnoreCase));
        if (itemRune is null)
        {
            SetElementError("В таблице модификаторов не найдена обязательная руна it или ic.");
            return;
        }
        foreach (EmbeddedMagicShapeOption shape in _magicContext.Catalog.Shapes)
        {
            foreach (EmbeddedMagicMaterialOption material in _magicContext.Catalog.GetCompatibleMaterials(shape.Value))
            {
                foreach (EmbeddedMagicSpellOption spell in _magicContext.Catalog.Spells)
                {
                    string[] modifiers = [itemRune.Value];
                    string item = $"{shape.Value}.{material.Value}";
                    EmbeddedMagicEvaluation evaluation = _magicContext.Catalog.Evaluate(item, spell.Value, modifiers);
                    if (!evaluation.IsAllowedForManualInsertion)
                    {
                        continue;
                    }

                    SelectedItem.Value = $"{item}[{BuildSpellValue(spell.Value, modifiers)}]";
                    return;
                }
            }
        }

        SetElementError(
            "В этой базе не нашлось готовой комбинации, проходящей ограничения ручной вставки. " +
            "Проверьте наличие формы, материала, заклинания и руны it/ic.");
    }

    private void LoadMagicConstructor(StringListElementAnalysis analysis)
    {
        if (_magicContext is null)
        {
            return;
        }

        _syncingMagicConstructor = true;
        try
        {
            SplitItemValue(analysis.PrimaryValue, out string shapeValue, out string materialValue);
            MagicShapeComboBox.SelectedItem = FindChoice(_magicContext.Shapes, shapeValue);
            RefreshMaterialChoices(materialValue);
            MagicSpellComboBox.SelectedItem = FindChoice(_magicContext.Spells, analysis.SpellCode);
            _selectedMagicModifiers.Clear();
            foreach (string modifier in analysis.Modifiers)
            {
                EmbeddedMagicChoice? choice = FindChoice(_magicContext.Modifiers, modifier);
                _selectedMagicModifiers.Add(choice ?? new EmbeddedMagicChoice(modifier, $"Не найден: {modifier}"));
            }
            MagicModifierComboBox.SelectedIndex = _magicContext.Modifiers.Count > 0 ? 0 : -1;
        }
        finally
        {
            _syncingMagicConstructor = false;
        }

        RefreshMagicEvaluation();
    }

    private void MagicShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMagicConstructor || _magicContext is null)
        {
            return;
        }

        _syncingMagicConstructor = true;
        try
        {
            RefreshMaterialChoices(null);
        }
        finally
        {
            _syncingMagicConstructor = false;
        }
        RefreshMagicEvaluation();
    }

    private void MagicSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingMagicConstructor)
        {
            RefreshMagicEvaluation();
        }
    }

    private void AddMagicModifierButton_Click(object sender, RoutedEventArgs e)
    {
        if (MagicModifierComboBox.SelectedItem is EmbeddedMagicChoice choice)
        {
            _selectedMagicModifiers.Add(choice);
            RefreshMagicEvaluation();
        }
    }

    private void RemoveMagicModifierButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: EmbeddedMagicChoice choice })
        {
            _selectedMagicModifiers.Remove(choice);
            RefreshMagicEvaluation();
        }
    }

    private void RefreshMaterialChoices(string? preferredValue)
    {
        if (_magicContext is null)
        {
            return;
        }

        string? shape = (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value;
        IReadOnlyList<EmbeddedMagicChoice> materials = _magicContext.GetMaterials(shape);
        MagicMaterialComboBox.ItemsSource = materials;
        MagicMaterialComboBox.SelectedItem = FindChoice(materials, preferredValue) ?? materials.FirstOrDefault();
    }

    private void RefreshMagicEvaluation()
    {
        if (_magicContext is null || MagicCapacityText is null || MagicValidationText is null)
        {
            return;
        }

        string itemValue = BuildSelectedItemValue();
        string spell = (MagicSpellComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
        string[] modifiers = _selectedMagicModifiers.Select(choice => choice.Value).ToArray();
        EmbeddedMagicEvaluation evaluation = _magicContext.Catalog.Evaluate(itemValue, spell, modifiers);
        MagicItemRussianNameText.Text = _magicContext.GetItemRussianName(
            (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value,
            (MagicMaterialComboBox.SelectedItem as EmbeddedMagicChoice)?.Value);
        MagicCapacityText.Text = evaluation.CanCalculate
            ? $"Сложность: {evaluation.RequiredComplexity} / {evaluation.AvailableComplexity} · " +
              $"энергия: {evaluation.RequiredMana:R} / {evaluation.AvailableMana:R}"
            : "Сложность и энергия будут рассчитаны после выбора всех частей.";
        MagicValidationText.Text = evaluation.StatusText;
        MagicValidationText.Foreground = evaluation.IsAllowedForManualInsertion
            ? (System.Windows.Media.Brush)FindResource("DatabaseAccentBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private string BuildSelectedItemValue()
    {
        string shape = (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
        string material = (MagicMaterialComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
        return shape.Length == 0 || material.Length == 0 ? shape : $"{shape}.{material}";
    }

    private static EmbeddedMagicChoice? FindChoice(
        IEnumerable<EmbeddedMagicChoice> choices,
        string? value) =>
        choices.FirstOrDefault(choice => choice.Value.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void SplitItemValue(string value, out string shape, out string material)
    {
        int separator = value.IndexOf('.');
        shape = separator > 0 ? value[..separator].Trim() : value.Trim();
        material = separator > 0 ? value[(separator + 1)..].Trim() : string.Empty;
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
