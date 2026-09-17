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
    private static readonly IReadOnlyList<StructuredElementKindChoice> AllElementKinds =
    [
        new(StructuredElementKind.RawValue, "Ссылка / значение", "Точная строка RES без автоматической сборки."),
        new(StructuredElementKind.Spell, "Заклинание / боевой навык", "Выбор прототипа заклинания и отдельных рун."),
        new(StructuredElementKind.Weapon, "Оружие", "Форма оружия, совместимый материал и необязательное вложенное заклинание."),
        new(StructuredElementKind.Armor, "Броня", "Форма брони, совместимый материал и необязательное вложенное заклинание."),
    ];
    private readonly IReadOnlyList<StructuredElementKindChoice> _elementKinds;
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
        EmbeddedMagicEditorContext? magicContext = null,
        IReadOnlyList<StructuredElementKind>? allowedElementKinds = null)
    {
        _tryApply = tryApply ?? throw new ArgumentNullException(nameof(tryApply));
        _referenceSuggestions = referenceSuggestions ?? [];
        _initialValues = initialValues.ToArray();
        _fieldName = fieldName ?? string.Empty;
        _singleValueMode = singleValueMode;
        _magicContext = magicContext;
        HashSet<StructuredElementKind>? allowedKinds = allowedElementKinds?.ToHashSet();
        _elementKinds = allowedKinds is null
            ? AllElementKinds
            : AllElementKinds.Where(choice => allowedKinds.Contains(choice.Kind)).ToArray();
        if (_elementKinds.Count == 0 || _elementKinds.All(choice => choice.Kind != StructuredElementKind.RawValue))
        {
            throw new ArgumentException("Набор типов конструктора должен содержать точное значение RES.", nameof(allowedElementKinds));
        }
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
            ElementTypeComboBox.ItemsSource = _elementKinds;
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
            ClearSlotButton.Visibility = Visibility.Visible;
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
            if (_singleValueMode && values[index].Length == 0) continue;
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

    private StructuredStringListItemViewModel CreateItem(string value) => new(value, _fieldName, _magicContext);

    private void ClearSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_singleValueMode) return;
        foreach (var item in Items) item.PropertyChanged -= Item_PropertyChanged;
        Items.Clear();
        var empty = CreateItem(string.Empty);
        empty.PropertyChanged += Item_PropertyChanged;
        Items.Add(empty);
        SelectedItem = empty;
        RenumberItems();
        SynchronizeRawText();
        SetEditorMode(useRawMode: false);
        SetError(null);

    }

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
        ElementKindText.Text = SelectedItem is null ? "Элемент не выбран" : analysis.Kind;
        ElementSummaryText.Text = SelectedItem is null ? "Выберите строку слева." : analysis.Summary;
        UpdateElementButton.IsEnabled = SelectedItem is not null;

        if (_magicContext is not null)
        {
            ElementTypePanel.Visibility = SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
            StructuredElementKind kind = InferElementKind(analysis);
            if (_elementKinds.All(choice => choice.Kind != kind))
            {
                kind = StructuredElementKind.RawValue;
            }
            _syncingMagicConstructor = true;
            try
            {
                ElementTypeComboBox.SelectedItem = FindElementKindChoice(kind);
                ConfigureContentBuilder(kind, analysis);
            }
            finally
            {
                _syncingMagicConstructor = false;
            }

            RefreshMagicEvaluation();
            SetElementError(analysis.Error);
            return;
        }

        ElementTypePanel.Visibility = Visibility.Collapsed;
        GenericElementEditorPanel.Visibility = Visibility.Visible;
        MagicConstructorPanel.Visibility = Visibility.Collapsed;
        bool isItemMagic = analysis.Kind == "Предмет с магией";
        bool isStandaloneSpell = analysis.Kind == "Заклинание";
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
        SetElementError(analysis.Error);
    }

    private void UpdateElementButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null)
        {
            return;
        }

        string value;
        if (_magicContext is not null &&
            ElementTypeComboBox.SelectedItem is StructuredElementKindChoice selectedType)
        {
            string spell = (MagicSpellComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
            string[] modifiers = _selectedMagicModifiers.Select(choice => choice.Value).ToArray();
            switch (selectedType.Kind)
            {
                case StructuredElementKind.Spell:
                    if (spell.Length == 0)
                    {
                        SetElementError("Выберите заклинание или боевой навык.");
                        return;
                    }
                    if (!_magicContext.Catalog.Spells.Any(option =>
                            option.Value.Equals(spell, StringComparison.OrdinalIgnoreCase)))
                    {
                        SetElementError($"Заклинание или боевой навык «{spell}» не найден в database.res.");
                        return;
                    }
                    if (TryFindUnknownModifier(modifiers, out string? unknownSpellModifier))
                    {
                        SetElementError($"Руна «{unknownSpellModifier}» не найдена в database.res.");
                        return;
                    }
                    value = BuildSpellValue(spell, modifiers);
                    break;

                case StructuredElementKind.Weapon:
                case StructuredElementKind.Armor:
                    string itemValue = BuildSelectedItemValue();
                    if (!TryValidateSelectedItem(selectedType.Kind, out string? itemError))
                    {
                        SetElementError(itemError);
                        return;
                    }
                    if (EmbedSpellCheckBox.IsChecked == true)
                    {
                        EmbeddedMagicEvaluation evaluation = _magicContext.Catalog.Evaluate(itemValue, spell, modifiers);
                        if (!evaluation.IsAllowedForManualInsertion)
                        {
                            SetElementError(evaluation.StatusText);
                            RefreshMagicEvaluation();
                            return;
                        }
                        value = $"{itemValue}[{BuildSpellValue(spell, modifiers)}]";
                    }
                    else
                    {
                        value = itemValue;
                    }
                    break;

                default:
                    value = PrimaryValueBox.Text.Trim();
                    break;
            }
        }
        else
        {
            StringListElementAnalysis previous = StringListEditorCodec.AnalyzeElement(SelectedItem.Value, _fieldName);
            string primary = PrimaryValueBox.Text.Trim();
            string spell = SpellCodeBox.Text.Trim();
            string[] modifiers = ModifiersBox.Text.Split(
                ';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            value = previous.Kind switch
            {
                "Предмет с магией" => $"{primary}[{BuildSpellValue(spell, modifiers)}]",
                "Заклинание" => BuildSpellValue(primary, modifiers),
                _ => primary,
            };
        }

        if (!StringListEditorCodec.TryValidateElement(value, out string? error))
        {
            SetElementError(error);
            return;
        }

        SelectedItem.Value = value;
        SetElementError(null);
    }

    private StructuredElementKind InferElementKind(StringListElementAnalysis analysis)
    {
        if (_magicContext is null || SelectedItem is null)
        {
            return StructuredElementKind.RawValue;
        }

        if (analysis.Kind == "Предмет с магией" &&
            _magicContext.GetItemKind(analysis.PrimaryValue) is StructuredElementKind embeddedItemKind)
        {
            return embeddedItemKind;
        }

        if (_magicContext.GetItemKind(analysis.PrimaryValue) is StructuredElementKind plainItemKind)
        {
            return plainItemKind;
        }

        string possibleSpell = analysis.SpellCode ?? analysis.PrimaryValue;
        if (analysis.Kind == "Заклинание" ||
            _magicContext.Spells.Any(choice => choice.Value.Equals(
                possibleSpell, StringComparison.OrdinalIgnoreCase)))
        {
            return StructuredElementKind.Spell;
        }

        if (analysis.Kind == "Ссылка / значение" &&
            analysis.PrimaryValue.Length > 0)
        {
            return StructuredElementKind.RawValue;
        }

        if (_fieldName.Contains("доспех", StringComparison.CurrentCultureIgnoreCase) ||
            _fieldName.Contains("брон", StringComparison.CurrentCultureIgnoreCase))
        {
            return StructuredElementKind.Armor;
        }
        if (_fieldName.Contains("оруж", StringComparison.CurrentCultureIgnoreCase))
        {
            return StructuredElementKind.Weapon;
        }
        if (_fieldName.Contains("заклин", StringComparison.CurrentCultureIgnoreCase) ||
            _fieldName.Contains("маг", StringComparison.CurrentCultureIgnoreCase))
        {
            return StructuredElementKind.Spell;
        }

        return StructuredElementKind.RawValue;
    }

    private void ConfigureContentBuilder(StructuredElementKind kind, StringListElementAnalysis analysis)
    {
        if (_magicContext is null)
        {
            return;
        }

        StructuredElementKindChoice choice = FindElementKindChoice(kind);
        ElementTypeDescriptionText.Text = choice.Description;
        GenericElementEditorPanel.Visibility = kind == StructuredElementKind.RawValue
            ? Visibility.Visible
            : Visibility.Collapsed;
        MagicConstructorPanel.Visibility = kind == StructuredElementKind.RawValue
            ? Visibility.Collapsed
            : Visibility.Visible;
        PrimaryValueLabel.Text = "Значение RES";
        PrimaryValueBox.Text = SelectedItem?.Value ?? string.Empty;
        PrimaryValueBox.IsEnabled = SelectedItem is not null;
        SpellCodePanel.Visibility = Visibility.Collapsed;
        ModifiersPanel.Visibility = Visibility.Collapsed;

        if (kind == StructuredElementKind.RawValue)
        {
            return;
        }

        bool isItem = kind is StructuredElementKind.Weapon or StructuredElementKind.Armor;
        ItemConstructorPanel.Visibility = isItem ? Visibility.Visible : Visibility.Collapsed;
        MagicShapeLabelText.Text = kind == StructuredElementKind.Weapon
            ? "Форма оружия"
            : "Форма брони";

        string preferredSpell = analysis.Kind == "Заклинание"
            ? analysis.PrimaryValue
            : analysis.SpellCode ?? string.Empty;
        bool hasEmbeddedSpell = isItem && analysis.Kind == "Предмет с магией";
        if (isItem)
        {
            IReadOnlyList<EmbeddedMagicChoice> shapes = _magicContext.GetShapes(kind);
            SplitItemValue(analysis.PrimaryValue, out string shapeValue, out string materialValue);
            if (_equipmentSlot is not null && kind == StructuredElementKind.Armor)
            {
                var armor = _equipmentRecords.FirstOrDefault(r => r.Table == "items.armors" && r.Name == shapeValue);
                if (armor is not null) _equipmentSlot.SelectedIndex = armor.Integer(2, -1) + 1;
                int slot = _equipmentSlot.SelectedIndex - 1;
                if (slot >= 0) shapes = shapes.Where(c => _equipmentRecords.Any(r => r.Table == "items.armors" && r.Name == c.Value && r.Integer(2, -1) == slot)).ToArray();
            }
            EmbeddedMagicChoice? shapeChoice = FindChoice(shapes, shapeValue);
            if (shapeChoice is null && shapeValue.Length > 0)
            {
                shapeChoice = new EmbeddedMagicChoice(shapeValue, $"Не найдена форма: {shapeValue}");
                shapes = [shapeChoice, .. shapes];
            }
            MagicShapeComboBox.ItemsSource = shapes;
            MagicShapeComboBox.SelectedItem = shapeChoice ?? shapes.FirstOrDefault();
            RefreshMaterialChoices(materialValue);
            EmbedSpellCheckBox.IsChecked = hasEmbeddedSpell;
        }
        else
        {
            EmbedSpellCheckBox.IsChecked = false;
        }

        SpellConstructorPanel.Visibility = !isItem || hasEmbeddedSpell
            ? Visibility.Visible
            : Visibility.Collapsed;
        MagicSpellLabelText.Text = isItem
            ? "Вложенное заклинание / боевой навык"
            : "Заклинание / боевой навык";
        RefreshSpellChoices(preferredSpell);
        LoadSelectedModifiers(
            analysis.Kind is "Заклинание" or "Предмет с магией"
                ? analysis.Modifiers
                : []);
        MagicModifierComboBox.SelectedIndex = _magicContext.Modifiers.Count > 0 ? 0 : -1;
    }

    private void ElementTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMagicConstructor || _magicContext is null || SelectedItem is null ||
            ElementTypeComboBox.SelectedItem is not StructuredElementKindChoice choice)
        {
            return;
        }

        _syncingMagicConstructor = true;
        try
        {
            ConfigureContentBuilder(
                choice.Kind,
                StringListEditorCodec.AnalyzeElement(SelectedItem.Value, _fieldName));
        }
        finally
        {
            _syncingMagicConstructor = false;
        }

        RefreshMagicEvaluation();
        SetElementError(null);
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

    private void EmbedSpellCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMagicConstructor)
        {
            return;
        }

        bool enabled = EmbedSpellCheckBox.IsChecked == true;
        SpellConstructorPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            AddDefaultItemRuneIfMissing();
        }
        RefreshMagicEvaluation();
    }

    private void RefreshMaterialChoices(string? preferredValue)
    {
        if (_magicContext is null)
        {
            return;
        }

        string? shape = (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value;
        IReadOnlyList<EmbeddedMagicChoice> materials = _magicContext.GetMaterials(shape);
        EmbeddedMagicChoice? selected = FindChoice(materials, preferredValue);
        if (selected is null && !string.IsNullOrWhiteSpace(preferredValue))
        {
            selected = new EmbeddedMagicChoice(
                preferredValue.Trim(),
                $"Не найден материал: {preferredValue.Trim()}");
            materials = [selected, .. materials];
        }
        MagicMaterialComboBox.ItemsSource = materials;
        MagicMaterialComboBox.SelectedItem = selected ?? materials.FirstOrDefault();
    }

    private void RefreshSpellChoices(string? preferredValue)
    {
        if (_magicContext is null)
        {
            return;
        }

        IReadOnlyList<EmbeddedMagicChoice> spells = _magicContext.Spells;
        EmbeddedMagicChoice? selected = FindChoice(spells, preferredValue);
        if (selected is null && !string.IsNullOrWhiteSpace(preferredValue))
        {
            selected = new EmbeddedMagicChoice(
                preferredValue.Trim(),
                $"Не найдено заклинание: {preferredValue.Trim()}");
            spells = [selected, .. spells];
        }
        MagicSpellComboBox.ItemsSource = spells;
        MagicSpellComboBox.SelectedItem = selected ?? spells.FirstOrDefault();
    }

    private void RefreshMagicEvaluation()
    {
        if (_magicContext is null || MagicCapacityText is null || MagicValidationText is null)
        {
            return;
        }

        if (ElementTypeComboBox.SelectedItem is not StructuredElementKindChoice choice ||
            choice.Kind == StructuredElementKind.RawValue)
        {
            return;
        }

        string[] modifiers = _selectedMagicModifiers.Select(item => item.Value).ToArray();
        if (choice.Kind == StructuredElementKind.Spell)
        {
            string spell = (MagicSpellComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
            bool hasUnknownModifier = TryFindUnknownModifier(modifiers, out string? unknownModifier);
            bool hasUnknownSpell = spell.Length > 0 && !_magicContext.Catalog.Spells.Any(option =>
                option.Value.Equals(spell, StringComparison.OrdinalIgnoreCase));
            MagicCapacityText.Text = spell.Length == 0
                ? "Выберите заклинание или боевой навык."
                : $"Будет записано: {BuildSpellValue(spell, modifiers)}";
            MagicValidationText.Text = hasUnknownSpell
                ? $"Заклинание или боевой навык «{spell}» не найден в database.res."
                : hasUnknownModifier
                    ? $"Руна «{unknownModifier}» не найдена в database.res."
                    : "Руны будут сохранены в том же порядке.";
            MagicValidationText.Foreground = hasUnknownSpell || hasUnknownModifier
                ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                : (System.Windows.Media.Brush)FindResource("DatabaseAccentBrush");
            return;
        }

        var existingShapes = MagicShapeComboBox.Items.Cast<EmbeddedMagicChoice>().ToArray();
        string? selectedMaterial = (MagicMaterialComboBox.SelectedItem as EmbeddedMagicChoice)?.Value;
        var localizedShapes = existingShapes.Select(s => _magicContext.LocalizeShapeChoice(s, selectedMaterial)).ToArray();
        if (!existingShapes.SequenceEqual(localizedShapes))
        {
            string? selectedShape = (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value;
            bool wasSyncing = _syncingMagicConstructor;
            _syncingMagicConstructor = true;
            try
            {
                MagicShapeComboBox.ItemsSource = localizedShapes;
                MagicShapeComboBox.SelectedItem = FindChoice(localizedShapes, selectedShape);
            }
            finally { _syncingMagicConstructor = wasSyncing; }
        }
        MagicItemRussianNameText.Text = _magicContext.GetItemRussianName(
            (MagicShapeComboBox.SelectedItem as EmbeddedMagicChoice)?.Value,
            (MagicMaterialComboBox.SelectedItem as EmbeddedMagicChoice)?.Value);
        if (!TryValidateSelectedItem(choice.Kind, out string? itemError))
        {
            MagicCapacityText.Text = "Предмет ещё не собран.";
            MagicValidationText.Text = itemError;
            MagicValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }

        if (EmbedSpellCheckBox.IsChecked != true)
        {
            MagicCapacityText.Text = $"Будет записано: {BuildSelectedItemValue()}";
            MagicValidationText.Text = "Предмет без вложенной магии.";
            MagicValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DatabaseAccentBrush");
            return;
        }

        string itemValue = BuildSelectedItemValue();
        string embeddedSpell = (MagicSpellComboBox.SelectedItem as EmbeddedMagicChoice)?.Value ?? string.Empty;
        EmbeddedMagicEvaluation evaluation = _magicContext.Catalog.Evaluate(itemValue, embeddedSpell, modifiers);
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

    private StructuredElementKindChoice FindElementKindChoice(StructuredElementKind kind) =>
        _elementKinds.FirstOrDefault(choice => choice.Kind == kind) ??
        _elementKinds.First(choice => choice.Kind == StructuredElementKind.RawValue);

    private void LoadSelectedModifiers(IEnumerable<string> modifiers)
    {
        if (_magicContext is null)
        {
            return;
        }

        _selectedMagicModifiers.Clear();
        foreach (string modifier in modifiers)
        {
            EmbeddedMagicChoice? choice = FindChoice(_magicContext.Modifiers, modifier);
            _selectedMagicModifiers.Add(choice ?? new EmbeddedMagicChoice(modifier, $"Не найдена: {modifier}"));
        }
    }

    private void AddDefaultItemRuneIfMissing()
    {
        if (_magicContext is null || _selectedMagicModifiers.Any(choice =>
                choice.Value.Equals("it", StringComparison.OrdinalIgnoreCase) ||
                choice.Value.Equals("ic", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        EmbeddedMagicChoice? itemRune = FindChoice(_magicContext.Modifiers, "it") ??
            FindChoice(_magicContext.Modifiers, "ic");
        if (itemRune is not null)
        {
            _selectedMagicModifiers.Insert(0, itemRune);
        }
    }

    private bool TryFindUnknownModifier(IEnumerable<string> modifiers, out string? unknownModifier)
    {
        if (_magicContext is not null)
        {
            foreach (string modifier in modifiers)
            {
                if (modifier.Equals("xa0", StringComparison.OrdinalIgnoreCase) ||
                    _magicContext.Catalog.Modifiers.Any(option =>
                        option.Value.Equals(modifier, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                unknownModifier = modifier;
                return true;
            }
        }

        unknownModifier = null;
        return false;
    }

    private bool TryValidateSelectedItem(StructuredElementKind kind, out string? error)
    {
        if (_magicContext is null ||
            MagicShapeComboBox.SelectedItem is not EmbeddedMagicChoice shapeChoice)
        {
            error = kind == StructuredElementKind.Weapon
                ? "Выберите форму оружия."
                : "Выберите форму брони.";
            return false;
        }

        EmbeddedMagicShapeOption? shape = _magicContext.Catalog.Shapes.FirstOrDefault(option =>
            option.Value.Equals(shapeChoice.Value, StringComparison.OrdinalIgnoreCase));
        string expectedTable = kind == StructuredElementKind.Weapon ? "items.weapons" : "items.armors";
        if (shape is null || !shape.TableKey.Equals(expectedTable, StringComparison.Ordinal))
        {
            error = "Выбранная форма не соответствует типу элемента.";
            return false;
        }

        if (MagicMaterialComboBox.SelectedItem is not EmbeddedMagicChoice materialChoice)
        {
            error = "Выберите материал предмета.";
            return false;
        }

        bool compatible = _magicContext.Catalog.GetCompatibleMaterials(shape.Value).Any(material =>
            material.Value.Equals(materialChoice.Value, StringComparison.OrdinalIgnoreCase));
        if (!compatible)
        {
            error = $"Материал «{materialChoice.Value}» несовместим с формой «{shape.Value}».";
            return false;
        }

        error = null;
        return true;
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
            .Select(value => _magicContext?.ReferenceChoice(value) ?? new EmbeddedMagicChoice(value, value))
            .Where(choice => query.Length == 0 || choice.DisplayText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(100)
            .ToArray();
    }

    private void AddReferenceButton_Click(object sender, RoutedEventArgs e) => AddSelectedReference();

    private void ReferenceResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedReference();

    private void AddSelectedReference()
    {
        string? value = (ReferenceResultsList.SelectedItem as EmbeddedMagicChoice)?.Value;
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
        if (_equipmentUnit is not null)
        {
            if (useRawMode) { _equipmentCancellation?.Cancel(); _equipmentPreview?.PauseAnimation(); }
            else QueueEquipmentPreview();
        }

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
    private readonly EmbeddedMagicEditorContext? _magicContext;
    private int _number;
    private string _value;

    public StructuredStringListItemViewModel(string value, string fieldName = "")
        : this(value, fieldName, null)
    {
    }

    internal StructuredStringListItemViewModel(
        string value,
        string fieldName,
        EmbeddedMagicEditorContext? magicContext)
    {
        _value = value;
        _fieldName = fieldName;
        _magicContext = magicContext;
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

    public string RussianName { get; private set; } = string.Empty;

    private void RefreshAnalysis()
    {
        StringListElementAnalysis analysis = StringListEditorCodec.AnalyzeElement(_value, _fieldName);
        int dot = analysis.PrimaryValue.IndexOf('.');
        RussianName = dot > 0 ? _magicContext?.GetItemRussianName(
            analysis.PrimaryValue[..dot], analysis.PrimaryValue[(dot + 1)..]) ?? "" : "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RussianName)));
        StructuredElementKind? itemKind = analysis.Kind is "Ссылка / значение" or "Предмет с магией"
            ? _magicContext?.GetItemKind(analysis.PrimaryValue)
            : null;
        bool hasEmbeddedMagic = analysis.Kind == "Предмет с магией";
        Kind = itemKind switch
        {
            StructuredElementKind.Weapon => hasEmbeddedMagic ? "Оружие с магией" : "Оружие",
            StructuredElementKind.Armor => hasEmbeddedMagic ? "Броня с магией" : "Броня",
            _ => analysis.Kind,
        };
        Summary = analysis.Error is not null
            ? $"⚠ {analysis.Error}"
            : itemKind switch
            {
                StructuredElementKind.Weapon => hasEmbeddedMagic
                    ? $"Оружие: {analysis.PrimaryValue} · магия: {analysis.SpellCode}"
                    : $"Оружие: {analysis.PrimaryValue}",
                StructuredElementKind.Armor => hasEmbeddedMagic
                    ? $"Броня: {analysis.PrimaryValue} · магия: {analysis.SpellCode}"
                    : $"Броня: {analysis.PrimaryValue}",
                _ => analysis.Summary,
            };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
}
