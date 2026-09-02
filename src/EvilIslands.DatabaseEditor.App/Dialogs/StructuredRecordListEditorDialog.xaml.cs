using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class StructuredRecordListEditorDialog : Window
{
    private readonly IReadOnlyList<StructuredListFieldDescriptor> _fields;
    private readonly Func<IReadOnlyList<StructuredListEntryValue>, (bool Success, string? Error)> _apply;

    internal StructuredRecordListEditorDialog(
        string address,
        string title,
        IReadOnlyList<StructuredListFieldDescriptor> fields,
        IReadOnlyList<StructuredListEntryValue> entries,
        Func<IReadOnlyList<StructuredListEntryValue>, (bool Success, string? Error)> apply)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(apply);
        InitializeComponent();

        _fields = fields;
        _apply = apply;
        AddressText.Text = address;
        FieldTitleText.Text = title;
        Entries = new ObservableCollection<StructuredRecordRow>(entries.Select(entry => new StructuredRecordRow(entry)));
        EntriesGrid.ItemsSource = Entries;
        EntriesGrid.SelectedItem = Entries.FirstOrDefault();

        bool hasConditions = fields.Any(field => field.Id == 10);
        ConditionsColumn.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
        ConditionsTextColumn.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
        ConditionsHelpText.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
        RefreshActionButtons();
    }

    public ObservableCollection<StructuredRecordRow> Entries { get; }

    public bool WasApplied { get; private set; }

    internal bool TryCommit()
    {
        EntriesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        EntriesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        IReadOnlyList<StructuredListEntryValue> values = Entries
            .Select((row, index) => row.ToValue(_fields, index))
            .ToArray();
        (bool success, string? error) = _apply(values);
        if (!success)
        {
            ErrorText.Text = error ?? "Изменения не приняты.";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }

        WasApplied = true;
        ErrorText.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryCommit())
        {
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddEntry();

    private void DuplicateButton_Click(object sender, RoutedEventArgs e) => DuplicateSelectedEntry();

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedEntry();

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelectedEntry(-1);

    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelectedEntry(1);

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshActionButtons();

    internal void AddEntry()
    {
        var fields = _fields.ToDictionary(field => field.Id, field => GetDefaultValue(field.Kind));
        InsertEntry(new StructuredRecordRow(new StructuredListEntryValue(-1, fields)));
    }

    internal void DuplicateSelectedEntry()
    {
        if (EntriesGrid.SelectedItem is StructuredRecordRow selected)
        {
            InsertEntry(selected.Clone());
        }
    }

    internal void DeleteSelectedEntry()
    {
        if (EntriesGrid.SelectedItem is not StructuredRecordRow selected)
        {
            return;
        }

        int index = Entries.IndexOf(selected);
        Entries.RemoveAt(index);
        RenumberEntries();
        EntriesGrid.SelectedItem = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
        RefreshActionButtons();
    }

    internal void MoveSelectedEntry(int delta)
    {
        if (EntriesGrid.SelectedItem is not StructuredRecordRow selected)
        {
            return;
        }

        int oldIndex = Entries.IndexOf(selected);
        int newIndex = oldIndex + delta;
        if (newIndex < 0 || newIndex >= Entries.Count)
        {
            return;
        }

        Entries.Move(oldIndex, newIndex);
        RenumberEntries();
        EntriesGrid.SelectedItem = selected;
        EntriesGrid.ScrollIntoView(selected);
        RefreshActionButtons();
    }

    private void InsertEntry(StructuredRecordRow row)
    {
        int index = EntriesGrid.SelectedItem is StructuredRecordRow selected
            ? Entries.IndexOf(selected) + 1
            : Entries.Count;
        Entries.Insert(index, row);
        RenumberEntries();
        EntriesGrid.SelectedItem = row;
        EntriesGrid.ScrollIntoView(row);
        EntriesGrid.CurrentCell = new DataGridCellInfo(row, FileNameColumn);
        EntriesGrid.Focus();
        EntriesGrid.BeginEdit();
        RefreshActionButtons();
    }

    private void RenumberEntries()
    {
        for (int index = 0; index < Entries.Count; index++)
        {
            Entries[index].Index = index;
        }
    }

    private void RefreshActionButtons()
    {
        if (DuplicateButton is null)
        {
            return;
        }

        int selectedIndex = EntriesGrid.SelectedItem is StructuredRecordRow selected
            ? Entries.IndexOf(selected)
            : -1;
        bool hasSelection = selectedIndex >= 0;
        DuplicateButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        MoveUpButton.IsEnabled = selectedIndex > 0;
        MoveDownButton.IsEnabled = selectedIndex >= 0 && selectedIndex < Entries.Count - 1;
    }

    private static string GetDefaultValue(string kind) => kind switch
    {
        "Int32" or "UInt32" or "Float32" or "Byte" => "0",
        "Boolean" => "false",
        _ => string.Empty,
    };

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    public sealed class StructuredRecordRow : INotifyPropertyChanged
    {
        private static readonly (uint Mask, string Name)[] ConditionNames =
        [
            (0x0001, "бег"), (0x0002, "шаг"), (0x0004, "крадётся"), (0x0008, "ползёт"),
            (0x0010, "Гипат"), (0x0020, "Ингос"), (0x0040, "Суслангер"),
            (0x0080, "день"), (0x0100, "ночь"), (0x0200, "снаружи"), (0x0400, "пещера"),
            (0x0800, "дождь"), (0x1000, "снег"), (0x2000, "ясно"),
        ];

        private readonly IReadOnlyDictionary<int, string> _originalFields;
        private readonly int? _sourceIndex;
        private int _index;
        private string _fileName;
        private string _probability;
        private string _description;
        private string _key;
        private string _conditions;

        internal StructuredRecordRow(StructuredListEntryValue entry)
        {
            _index = entry.Index;
            _sourceIndex = entry.SourceIndex ?? (entry.Index >= 0 ? entry.Index : null);
            _originalFields = new Dictionary<int, string>(entry.Fields);
            _fileName = GetValue(1);
            _probability = GetValue(2);
            _description = GetValue(3);
            _key = GetValue(4);
            _conditions = GetValue(10);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Index
        {
            get => _index;
            internal set
            {
                if (_index == value)
                {
                    return;
                }

                _index = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Number));
            }
        }

        public int Number => Index + 1;

        public string FileName
        {
            get => _fileName;
            set => SetField(ref _fileName, value);
        }

        public string Probability
        {
            get => _probability;
            set => SetField(ref _probability, value);
        }

        public string Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        public string Key
        {
            get => _key;
            set => SetField(ref _key, value);
        }

        public string Conditions
        {
            get => _conditions;
            set
            {
                if (SetField(ref _conditions, value))
                {
                    OnPropertyChanged(nameof(ConditionsText));
                }
            }
        }

        public string ConditionsText
        {
            get
            {
                if (!uint.TryParse(Conditions, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
                {
                    return string.IsNullOrWhiteSpace(Conditions) ? "—" : "Введите целое число";
                }

                if (value == 0)
                {
                    return "без дополнительных условий";
                }

                var names = ConditionNames
                    .Where(condition => (value & condition.Mask) != 0)
                    .Select(condition => condition.Name)
                    .ToList();
                uint knownMask = ConditionNames.Aggregate(0u, (mask, condition) => mask | condition.Mask);
                uint unknown = value & ~knownMask;
                if (unknown != 0)
                {
                    names.Add($"неизвестные биты 0x{unknown:X}");
                }

                return string.Join(", ", names);
            }
        }

        internal StructuredRecordRow Clone()
        {
            var values = new Dictionary<int, string>(_originalFields)
            {
                [1] = FileName,
                [2] = Probability,
                [3] = Description,
                [4] = Key,
            };
            if (values.ContainsKey(10))
            {
                values[10] = Conditions;
            }

            return new StructuredRecordRow(new StructuredListEntryValue(Index, values)
            {
                SourceIndex = _sourceIndex,
            });
        }

        internal StructuredListEntryValue ToValue(IReadOnlyList<StructuredListFieldDescriptor> fields, int index)
        {
            var values = new Dictionary<int, string>(_originalFields);
            foreach (StructuredListFieldDescriptor field in fields)
            {
                values[field.Id] = field.Id switch
                {
                    1 => FileName,
                    2 => Probability,
                    3 => Description,
                    4 => Key,
                    10 => Conditions,
                    _ => values.GetValueOrDefault(field.Id, string.Empty),
                };
            }

            return new StructuredListEntryValue(index, values)
            {
                SourceIndex = _sourceIndex,
            };
        }

        private string GetValue(int fieldId) => _originalFields.GetValueOrDefault(fieldId, string.Empty);

        private bool SetField(ref string field, string? value, [CallerMemberName] string? propertyName = null)
        {
            value ??= string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
