using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
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

        bool hasConditions = fields.Any(field => field.Id == 10);
        ConditionsColumn.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
        ConditionsTextColumn.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
        ConditionsHelpText.Visibility = hasConditions ? Visibility.Visible : Visibility.Collapsed;
    }

    public ObservableCollection<StructuredRecordRow> Entries { get; }

    public bool WasApplied { get; private set; }

    internal bool TryCommit()
    {
        EntriesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        EntriesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        IReadOnlyList<StructuredListEntryValue> values = Entries
            .Select(row => row.ToValue(_fields))
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
        private string _fileName;
        private string _probability;
        private string _description;
        private string _key;
        private string _conditions;

        internal StructuredRecordRow(StructuredListEntryValue entry)
        {
            Index = entry.Index;
            _originalFields = new Dictionary<int, string>(entry.Fields);
            _fileName = GetValue(1);
            _probability = GetValue(2);
            _description = GetValue(3);
            _key = GetValue(4);
            _conditions = GetValue(10);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Index { get; }

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

        internal StructuredListEntryValue ToValue(IReadOnlyList<StructuredListFieldDescriptor> fields)
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

            return new StructuredListEntryValue(Index, values);
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
