using System.Globalization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Models;

internal enum WorkspaceSection
{
    Overview,
    Resources,
    Records,
    Changes,
    Validation,
}

internal sealed class NavigationItemViewModel(
    WorkspaceSection section,
    string title,
    string glyph) : ObservableObject
{
    private bool _isSelected;
    private bool _isEnabled = true;
    private string? _badge;

    public WorkspaceSection Section { get; } = section;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string? Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }

    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
}

internal sealed record ResourceRowViewModel(
    int Index,
    string Name,
    string Kind,
    long Offset,
    long Length,
    string State);

internal sealed class DatabaseGroupViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isEnabled;

    public DatabaseGroupViewModel(
        string key,
        string title,
        string resourceName,
        DatabaseSupportState supportState,
        IReadOnlyList<DatabaseTableViewModel> tables)
    {
        Key = key;
        Title = title;
        ResourceName = resourceName;
        SupportState = supportState;
        Tables = tables;
        foreach (DatabaseTableViewModel table in tables)
        {
            table.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(DatabaseTableViewModel.RecordCount))
                {
                    OnPropertyChanged(nameof(RecordCount));
                }
            };
        }
    }

    public string Key { get; }

    public string Title { get; }

    public string ResourceName { get; }

    public DatabaseSupportState SupportState { get; }

    public IReadOnlyList<DatabaseTableViewModel> Tables { get; }

    public int TableCount => Tables.Count;

    public int RecordCount => Tables.Sum(table => table.RecordCount);

    public bool IsEditable => SupportState == DatabaseSupportState.Editable;

    public bool IsAvailable => SupportState != DatabaseSupportState.Unavailable;

    public string StatusText => SupportState switch
    {
        DatabaseSupportState.Editable => "Известные поля",
        DatabaseSupportState.ReadOnly => "Только просмотр",
        _ => "Недоступно",
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            foreach (var table in Tables)
            {
                table.IsEnabled = value && table.IsAvailable;
            }
        }
    }
}

internal sealed class DatabaseTableViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEnabled;

    public DatabaseTableViewModel(
        string groupKey,
        string groupTitle,
        string key,
        string title,
        string resourceName,
        byte sectionId,
        DatabaseSupportState supportState,
        bool canAppendRecords,
        IReadOnlyList<RecordRowViewModel> records,
        IReadOnlyDictionary<int, RangeObservableCollection<ReferenceChoiceViewModel>>? referenceChoices = null)
    {
        GroupKey = groupKey;
        GroupTitle = groupTitle;
        Key = key;
        Title = title;
        ResourceName = resourceName;
        SectionId = sectionId;
        SupportState = supportState;
        CanAppendRecords = canAppendRecords;
        Records = new ObservableCollection<RecordRowViewModel>(records);
        Records.CollectionChanged += Records_CollectionChanged;
        Columns = BuildColumns(
            records,
            supportState == DatabaseSupportState.Editable,
            referenceChoices ?? new Dictionary<int, RangeObservableCollection<ReferenceChoiceViewModel>>());
        ReindexRecords();
    }

    public string GroupKey { get; }

    public string GroupTitle { get; }

    public string Key { get; }

    public string Title { get; }

    public string ResourceName { get; }

    public byte SectionId { get; }

    public DatabaseSupportState SupportState { get; }

    public ObservableCollection<RecordRowViewModel> Records { get; }

    public IReadOnlyList<SpreadsheetColumnViewModel> Columns { get; }

    public bool CanAppendRecords { get; }

    public int RecordCount => Records.Count;

    public bool IsEditable => SupportState == DatabaseSupportState.Editable;

    public bool IsAvailable => SupportState != DatabaseSupportState.Unavailable;

    public bool IsReadOnly => SupportState == DatabaseSupportState.ReadOnly;

    internal void AppendRecord(RecordRowViewModel record) => Records.Add(record);

    internal void RemoveLastRecord(RecordRowViewModel record)
    {
        if (Records.Count == 0 || !ReferenceEquals(Records[^1], record))
        {
            throw new InvalidOperationException("Безопасно удалить можно только последнюю добавленную строку таблицы.");
        }

        Records.RemoveAt(Records.Count - 1);
    }

    public string StatusText => SupportState switch
    {
        DatabaseSupportState.Editable => "Известные поля",
        DatabaseSupportState.ReadOnly => "Только просмотр",
        _ => "Недоступно",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private void Records_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        ReindexRecords();
        OnPropertyChanged(nameof(RecordCount));
    }

    private void ReindexRecords()
    {
        for (int index = 0; index < Records.Count; index++)
        {
            Records[index].SetDisplayIndex(index + 1);
        }
    }

    private static IReadOnlyList<SpreadsheetColumnViewModel> BuildColumns(
        IReadOnlyList<RecordRowViewModel> records,
        bool tableIsEditable,
        IReadOnlyDictionary<int, RangeObservableCollection<ReferenceChoiceViewModel>> referenceChoices)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var byIdentity = new Dictionary<SpreadsheetColumnIdentity, SpreadsheetColumnAccumulator>();
        foreach (var record in records)
        {
            foreach (var field in record.FieldDefinitions)
            {
                if (field.Projections.Count == 0)
                {
                    AddOrMerge(field, null);
                    continue;
                }

                foreach (var projection in field.Projections)
                {
                    AddOrMerge(field, projection);
                }
            }
        }

        var nameFieldId = records[0].NameFieldId;
        return byIdentity.Values
            .Select(column => column.ToViewModel(column.FieldId == nameFieldId && column.Projection is null))
            .OrderBy(column => column.DisplayOrder)
            .ThenByDescending(column => column.IsNameField)
            .ThenBy(column => column.FieldId)
            .ThenBy(column => column.Projection?.Index ?? -1)
            .ToArray();

        void AddOrMerge(DatabaseField field, DatabaseFieldProjection? projection)
        {
            var identity = new SpreadsheetColumnIdentity(
                field.Id,
                projection?.Kind,
                projection?.Index ?? -1);
            if (!byIdentity.TryGetValue(identity, out var column))
            {
                byIdentity.Add(
                    identity,
                    new SpreadsheetColumnAccumulator(
                        field,
                        projection,
                        tableIsEditable && projection is null && field.IsEditable,
                        projection is null && referenceChoices.TryGetValue(field.Id, out var choices)
                            ? choices
                            : []));
                return;
            }

            column.Merge(field, projection, tableIsEditable);
        }
    }

    private readonly record struct SpreadsheetColumnIdentity(
        int FieldId,
        DatabaseFieldProjectionKind? ProjectionKind,
        int ProjectionIndex);

    private sealed class SpreadsheetColumnAccumulator
    {
        public SpreadsheetColumnAccumulator(
            DatabaseField field,
            DatabaseFieldProjection? projection,
            bool isEditable,
            RangeObservableCollection<ReferenceChoiceViewModel>? referenceChoices)
        {
            FieldId = field.Id;
            Projection = projection;
            Key = BuildKey(field, projection);
            Title = projection?.DisplayName ?? field.DisplayName;
            Kind = GetDisplayKind(field.Kind, projection);
            DisplayOrder = projection?.DisplayOrder ?? field.DisplayOrder;
            IsEditable = isEditable;
            ReferenceChoices = referenceChoices;
        }

        public int FieldId { get; }

        public DatabaseFieldProjection? Projection { get; }

        private string Key { get; }

        private string Title { get; }

        private string Kind { get; }

        private int DisplayOrder { get; }

        private bool IsEditable { get; set; }

        private RangeObservableCollection<ReferenceChoiceViewModel>? ReferenceChoices { get; }

        public void Merge(
            DatabaseField field,
            DatabaseFieldProjection? projection,
            bool tableIsEditable)
        {
            var metadataMatches = Equals(Projection, projection) &&
                                  string.Equals(Key, BuildKey(field, projection), StringComparison.Ordinal) &&
                                  string.Equals(Title, projection?.DisplayName ?? field.DisplayName, StringComparison.Ordinal) &&
                                  string.Equals(Kind, GetDisplayKind(field.Kind, projection), StringComparison.Ordinal);
            IsEditable = IsEditable && projection is null && tableIsEditable && field.IsEditable && metadataMatches;
        }

        public SpreadsheetColumnViewModel ToViewModel(bool isNameField) =>
            new(
                FieldId,
                Key,
                Title,
                Kind,
                IsEditable,
                isNameField,
                Projection,
                DisplayOrder,
                ReferenceChoices);

        private static string BuildKey(DatabaseField field, DatabaseFieldProjection? projection) =>
            projection is null ? field.Key : $"{field.Key}[{projection.Index}]";

        private static string GetDisplayKind(string sourceKind, DatabaseFieldProjection? projection) =>
            projection?.ValueKind ?? (projection?.Kind switch
            {
                DatabaseFieldProjectionKind.Bit => "Boolean",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "Float32List" => "Float32",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "UInt32List" => "UInt32",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "ByteList" => "Byte",
                _ => sourceKind,
            });
    }
}

internal sealed record ReferenceChoiceViewModel(
    string Value,
    string DisplayText,
    bool IsKnown);

internal sealed record SpreadsheetColumnViewModel(
    int FieldId,
    string Key,
    string Title,
    string Kind,
    bool IsEditable,
    bool IsNameField,
    DatabaseFieldProjection? Projection,
    int DisplayOrder,
    RangeObservableCollection<ReferenceChoiceViewModel>? ReferenceChoices = null)
{
    public string Subtitle => HasReferenceChoices ? $"{Key} · {Kind} · список" : $"{Key} · {Kind}";

    public string HeaderToolTip => $"{Title}\n{Subtitle}";

    public SpreadsheetColumnLayout Layout => SpreadsheetColumnLayout.For(this);

    public string Identity => Projection is null
        ? $"{FieldId}"
        : $"{FieldId}:{Projection.Kind}:{Projection.Index}";

    public bool IsSortable => Kind is not (
        "Hex" or "Nested" or "StructuredList" or "StringList" or "FixedStringList" or
        "UInt32List" or "Float32List" or "ByteList");

    public bool IsPrimitive => Kind is "String" or "FixedString" or "Int32" or "UInt32" or "Float32" or "Byte" or "Boolean";

    public bool HasReferenceChoices => ReferenceChoices is { Count: > 0 };
}

internal readonly record struct SpreadsheetColumnLayout(
    double PreferredWidth,
    double MinimumWidth,
    double MaximumWidth)
{
    public static SpreadsheetColumnLayout For(SpreadsheetColumnViewModel column)
    {
        if (column.IsNameField)
        {
            return new(150, 110, 360);
        }

        return column.Kind switch
        {
            "Boolean" => new(60, 46, 104),
            "Byte" => new(64, 50, 112),
            "Int32" or "UInt32" => new(70, 54, 150),
            "Float32" => new(76, 58, 170),
            "String" or "FixedString" when IsShortTextColumn(column) => new(88, 64, 220),
            "String" or "FixedString" => new(128, 80, 320),
            "Hex" => new(124, 84, 280),
            "StringList" or "FixedStringList" or "UInt32List" or "Float32List" or "ByteList" =>
                new(140, 96, 320),
            "Nested" or "StructuredList" => new(140, 96, 320),
            _ => new(112, 76, 280),
        };
    }

    private static bool IsShortTextColumn(SpreadsheetColumnViewModel column) =>
        column.Title.Length <= 10 ||
        column.Key.Contains("code", StringComparison.OrdinalIgnoreCase) ||
        column.Key.Contains("type", StringComparison.OrdinalIgnoreCase);
}

internal static class SpreadsheetProjectionValue
{
    public const string UnavailableText = "—";

    public static string Format(string? sourceText, DatabaseFieldProjection projection)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || projection.Index < 0)
        {
            return UnavailableText;
        }

        switch (projection.Kind)
        {
            case DatabaseFieldProjectionKind.ListElement:
                {
                    string[] values = sourceText.Split(',', StringSplitOptions.TrimEntries);
                    if (projection.Index >= values.Length ||
                        !float.TryParse(
                            values[projection.Index],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out _))
                    {
                        return UnavailableText;
                    }

                    return values[projection.Index];
                }
            case DatabaseFieldProjectionKind.Bit:
                {
                    if (projection.Index >= 32 ||
                        !uint.TryParse(
                            sourceText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var mask))
                    {
                        return UnavailableText;
                    }

                    return ((mask >> projection.Index) & 1U).ToString(CultureInfo.InvariantCulture);
                }
            default:
                return UnavailableText;
        }
    }

    public static object? GetSortValue(object? sourceValue, DatabaseFieldProjection projection)
    {
        string? sourceText = sourceValue switch
        {
            null => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => sourceValue.ToString(),
        };
        string projectedText = Format(sourceText, projection);
        if (string.Equals(projectedText, UnavailableText, StringComparison.Ordinal))
        {
            return null;
        }

        if (projection.Kind == DatabaseFieldProjectionKind.Bit &&
            int.TryParse(projectedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit))
        {
            return bit;
        }

        return double.TryParse(projectedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : projectedText;
    }

    public static object? NormalizeSortValue(object? value)
    {
        string? text = value switch
        {
            null => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : text;
    }
}

internal sealed class RecordRowViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<ValidationMessageViewModel>> EmptyDiagnosticsByField =
        new Dictionary<int, IReadOnlyList<ValidationMessageViewModel>>();
    private readonly DatabaseRecord _record;
    private readonly bool _tableIsEditable;
    private IReadOnlyList<RecordFieldViewModel>? _fields;
    private Dictionary<int, RecordFieldViewModel>? _fieldCache;
    private string _displayName;
    private int _displayIndex;
    private bool _isModified;
    private bool _isEditMode;
    private IReadOnlyDictionary<int, IReadOnlyList<ValidationMessageViewModel>> _diagnosticsByFieldId =
        EmptyDiagnosticsByField;
    private int _diagnosticCount;
    private bool _hasBlockingDiagnostics;

    public RecordRowViewModel(
        DatabaseRecord record,
        string tableKey,
        string category,
        bool tableIsEditable)
    {
        ArgumentNullException.ThrowIfNull(record);
        _record = record;
        _tableIsEditable = tableIsEditable;
        _displayName = NormalizeDisplayName(record.DisplayName);
        _displayIndex = record.RecordIndex + 1;
        TableKey = tableKey;
        Category = category;
        Path = record.Path;
        _isModified = record.IsModified;
        Cells = new RecordCellCollectionViewModel(this);
        SearchText = BuildSearchText();
    }

    public event EventHandler<RecordFieldValueCommittedEventArgs>? FieldValueCommitted;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Category { get; }

    public string TableKey { get; }

    public RecordCellCollectionViewModel Cells { get; }

    public int Index => _displayIndex;

    public int SourceRecordIndex => _record.RecordIndex;

    public int NameFieldId => _record.NameFieldId;

    public int IdentityFieldId => _record.IdentityFieldId;

    public string IdentityValue => _record.IdentityValue;

    public bool IsAdded => _record.IsNew;

    public string Path { get; }

    public IReadOnlyList<RecordFieldViewModel> Fields
    {
        get
        {
            if (_fields is not null)
            {
                return _fields;
            }

            var fields = new RecordFieldViewModel[_record.Fields.Count];
            for (var index = 0; index < fields.Length; index++)
            {
                fields[index] = GetOrCreateField(_record.Fields[index]);
            }

            _fields = fields;
            SearchText = BuildSearchText();
            OnPropertyChanged(nameof(AreFieldsMaterialized));
            OnPropertyChanged(nameof(SearchText));
            return fields;
        }
    }

    public int FieldCount => _record.Fields.Count;

    public bool AreFieldsMaterialized => MaterializedFieldCount > 0;

    public int MaterializedFieldCount => _fieldCache?.Count ?? 0;

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (_isEditMode == value)
            {
                return;
            }

            _isEditMode = value;
            if (_fieldCache is null)
            {
                return;
            }

            foreach (var field in _fieldCache.Values)
            {
                field.IsEditMode = value;
            }
        }
    }

    public string SearchText { get; private set; }

    public bool IsModified
    {
        get => _isModified;
        private set => SetProperty(ref _isModified, value);
    }

    public int DiagnosticCount
    {
        get => _diagnosticCount;
        private set
        {
            if (SetProperty(ref _diagnosticCount, value))
            {
                OnPropertyChanged(nameof(HasDiagnostics));
            }
        }
    }

    public bool HasDiagnostics => DiagnosticCount > 0;

    public bool HasBlockingDiagnostics
    {
        get => _hasBlockingDiagnostics;
        private set => SetProperty(ref _hasBlockingDiagnostics, value);
    }

    public void RefreshState()
    {
        DisplayName = NormalizeDisplayName(_record.DisplayName);
        IsModified = _record.IsModified;
        SearchText = BuildSearchText();
        OnPropertyChanged(nameof(SearchText));
    }

    internal IReadOnlyList<DatabaseField> FieldDefinitions => _record.Fields;

    internal DatabaseRecord CoreRecord => _record;

    internal void SetDisplayIndex(int value)
    {
        if (_displayIndex != value)
        {
            _displayIndex = value;
            OnPropertyChanged(nameof(Index));
        }
    }

    internal void ApplyDiagnostics(IReadOnlyList<ValidationMessageViewModel> diagnostics)
    {
        DiagnosticCount = diagnostics.Count;
        HasBlockingDiagnostics = diagnostics.Any(item =>
            item.IsBlocking || item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        _diagnosticsByFieldId = diagnostics.Count == 0
            ? EmptyDiagnosticsByField
            : diagnostics
                .Where(item => item.FieldId is not null)
                .GroupBy(item => item.FieldId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ValidationMessageViewModel>)group.ToArray());

        if (_fieldCache is null)
        {
            return;
        }

        foreach ((int fieldId, RecordFieldViewModel field) in _fieldCache)
        {
            field.SetDiagnostics(_diagnosticsByFieldId.TryGetValue(fieldId, out var messages) ? messages : []);
        }
    }

    internal RecordFieldViewModel? GetCell(int fieldId)
    {
        if (_fieldCache is not null && _fieldCache.TryGetValue(fieldId, out var cached))
        {
            return cached;
        }

        var field = _record.Fields.FirstOrDefault(candidate => candidate.Id == fieldId);
        return field is null ? null : GetOrCreateField(field);
    }

    internal object? GetSortValue(SpreadsheetColumnViewModel column)
    {
        DatabaseField? field = _record.Fields.FirstOrDefault(candidate => candidate.Id == column.FieldId);
        if (field is null || column.Projection is null)
        {
            return field?.Value;
        }

        return SpreadsheetProjectionValue.NormalizeSortValue(field.GetProjectedValue(column.Projection));
    }

    private RecordFieldViewModel GetOrCreateField(DatabaseField field)
    {
        _fieldCache ??= [];
        if (_fieldCache.TryGetValue(field.Id, out var cached))
        {
            return cached;
        }

        var result = new RecordFieldViewModel(field, _tableIsEditable)
        {
            IsEditMode = IsEditMode,
        };
        result.SetDiagnostics(_diagnosticsByFieldId.TryGetValue(field.Id, out var messages) ? messages : []);
        result.ValueCommitted += OnFieldValueCommitted;
        _fieldCache.Add(field.Id, result);
        OnPropertyChanged(nameof(MaterializedFieldCount));
        OnPropertyChanged(nameof(AreFieldsMaterialized));
        return result;
    }

    private void OnFieldValueCommitted(object? sender, FieldValueCommittedEventArgs eventArgs)
    {
        if (sender is RecordFieldViewModel field)
        {
            FieldValueCommitted?.Invoke(
                this,
                new RecordFieldValueCommittedEventArgs(field, eventArgs.BeforeState, eventArgs.AfterState));
        }
    }

    private string BuildSearchText()
    {
        var materializedFields = _fields;
        var builder = new StringBuilder(
            DisplayName.Length + Category.Length + Path.Length + (materializedFields?.Count ?? 0) * 24);
        builder.Append(DisplayName).Append('\u001f').Append(Category).Append('\u001f').Append(Path);
        if (materializedFields is not null)
        {
            foreach (var field in materializedFields)
            {
                builder.Append('\u001f').Append(field.DisplayName).Append('\u001f').Append(field.EditorText);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Без имени" : value;
}

internal sealed class RecordCellCollectionViewModel(RecordRowViewModel owner)
{
    public RecordFieldViewModel? this[int fieldId] => owner.GetCell(fieldId);
}

internal sealed class RecordFieldValueCommittedEventArgs(
    RecordFieldViewModel field,
    DatabaseFieldState beforeState,
    DatabaseFieldState afterState) : EventArgs
{
    public RecordFieldViewModel Field { get; } = field;

    public DatabaseFieldState BeforeState { get; } = beforeState;

    public DatabaseFieldState AfterState { get; } = afterState;
}

internal sealed class FieldValueCommittedEventArgs(
    DatabaseFieldState beforeState,
    DatabaseFieldState afterState) : EventArgs
{
    public DatabaseFieldState BeforeState { get; } = beforeState;

    public DatabaseFieldState AfterState { get; } = afterState;
}

internal sealed class RecordFieldViewModel : ObservableObject
{
    private readonly DatabaseField _field;
    private readonly bool _tableIsEditable;
    private string _editorText;
    private bool _isEditMode;
    private string? _errorMessage;
    private int _diagnosticCount;
    private bool _hasBlockingDiagnostic;
    private string? _diagnosticText;

    public RecordFieldViewModel(DatabaseField field, bool tableIsEditable = true)
    {
        _field = field;
        _tableIsEditable = tableIsEditable;
        _editorText = FormatValue(field.Value);
        OriginalText = FormatValue(field.OriginalValue);
    }

    public event EventHandler<FieldValueCommittedEventArgs>? ValueCommitted;

    public int Id => _field.Id;

    public string Key => _field.Key;

    public string DisplayName => _field.DisplayName;

    public string Kind => _field.Kind;

    public string OriginalText { get; }

    public bool IsEditable => _tableIsEditable && _field.IsEditable;

    public string Path => _field.Path;

    public long Offset => _field.Offset;

    public long Length => _field.Length;

    public string GetProjectedText(DatabaseFieldProjection projection)
    {
        object? value = _field.GetProjectedValue(projection);
        return value is null ? SpreadsheetProjectionValue.UnavailableText : FormatValue(value);
    }

    public string EditorText
    {
        get => _editorText;
        set
        {
            if (string.Equals(_editorText, value, StringComparison.Ordinal))
            {
                ErrorMessage = null;
                OnPropertyChanged();
                return;
            }

            if (!CanEdit)
            {
                return;
            }

            var beforeState = _field.CaptureState();
            try
            {
                if (!_field.TrySetValue(value, out var error))
                {
                    ErrorMessage = error ?? "Значение не принято.";
                    OnPropertyChanged();
                    return;
                }
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
                OnPropertyChanged();
                return;
            }

            ErrorMessage = null;
            SetProperty(ref _editorText, FormatValue(_field.Value));
            OnPropertyChanged(nameof(IsModified));
            var afterState = _field.CaptureState();
            if (!beforeState.IsEquivalentTo(afterState))
            {
                ValueCommitted?.Invoke(this, new FieldValueCommittedEventArgs(beforeState, afterState));
            }
        }
    }

    public bool IsModified => _field.IsModified;

    public bool CanEdit => IsEditable && IsEditMode;

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (SetProperty(ref _isEditMode, value))
            {
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int DiagnosticCount
    {
        get => _diagnosticCount;
        private set
        {
            if (SetProperty(ref _diagnosticCount, value))
            {
                OnPropertyChanged(nameof(HasDiagnostic));
            }
        }
    }

    public bool HasDiagnostic => DiagnosticCount > 0;

    public bool HasBlockingDiagnostic
    {
        get => _hasBlockingDiagnostic;
        private set => SetProperty(ref _hasBlockingDiagnostic, value);
    }

    public string? DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    internal void SetDiagnostics(IReadOnlyList<ValidationMessageViewModel> diagnostics)
    {
        DiagnosticCount = diagnostics.Count;
        HasBlockingDiagnostic = diagnostics.Any(item =>
            item.IsBlocking || item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        DiagnosticText = diagnostics.Count == 0
            ? null
            : string.Join(
                Environment.NewLine,
                diagnostics.Take(6).Select(item => $"{item.SeverityText}: {item.Message}")) +
              (diagnostics.Count > 6 ? $"{Environment.NewLine}Ещё сообщений: {diagnostics.Count - 6}." : string.Empty);
    }

    public bool ResetToOriginal()
    {
        try
        {
            _field.ResetToOriginal();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, FormatValue(_field.Value), nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        return true;
    }

    public DatabaseFieldState CaptureState() => _field.CaptureState();

    public bool RestoreState(DatabaseFieldState state)
    {
        try
        {
            _field.RestoreState(state);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, FormatValue(_field.Value), nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        return true;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Convert.ToHexString(bytes),
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}

internal sealed class ChangeEntryViewModel : ObservableObject
{
    private string _newValue;

    public ChangeEntryViewModel(RecordRowViewModel record, RecordFieldViewModel field)
    {
        Record = record;
        Field = field;
        _newValue = field.EditorText;
    }

    private ChangeEntryViewModel(RecordRowViewModel record)
    {
        Record = record;
        IsRecordAddition = true;
        _newValue = record.DisplayName;
    }

    public static ChangeEntryViewModel ForAddedRecord(RecordRowViewModel record) => new(record);

    public RecordRowViewModel Record { get; }

    public RecordFieldViewModel? Field { get; }

    public bool IsRecordAddition { get; }

    public string RecordName => Record.DisplayName;

    public string FieldName => IsRecordAddition ? "Новая строка" : Field!.DisplayName;

    public string Path => IsRecordAddition ? Record.Path : Field!.Path;

    public string OriginalValue => IsRecordAddition ? "—" : Field!.OriginalText;

    public string ActionText => IsRecordAddition ? "Ctrl+Z" : "Вернуть";

    public string NewValue
    {
        get => _newValue;
        set => SetProperty(ref _newValue, value);
    }
}

internal sealed record ValidationMessageViewModel(
    string Severity,
    string SeverityText,
    string Domain,
    string DomainText,
    string Code,
    string Message,
    string Place,
    string Detail,
    string Path,
    long? Offset,
    bool IsBlocking,
    string? ResourceName,
    string? TableKey,
    int? RecordIndex,
    int? FieldId,
    string? FieldKey)
{
    public string BlockingText => IsBlocking ? "Блокирует" : "Не блокирует";

    public bool CanNavigate => !string.IsNullOrWhiteSpace(TableKey) && RecordIndex is >= 0;
}

internal sealed class WorkspaceSnapshot
{
    public required string SourcePath { get; init; }

    public required long FileLength { get; init; }

    public required string Sha256 { get; init; }

    public required IReadOnlyList<ResourceRowViewModel> Resources { get; init; }

    public IReadOnlyList<DatabaseGroupViewModel> Databases { get; init; } = [];

    public required IReadOnlyList<ValidationMessageViewModel> Diagnostics { get; init; }
}

internal sealed record SaveCopyResult(
    string TargetPath,
    long FileLength,
    string Sha256,
    bool ReopenedSuccessfully,
    int DiagnosticCount);
