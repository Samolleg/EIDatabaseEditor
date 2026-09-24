using System.Globalization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Models;

internal enum WorkspaceSection
{
    Overview,
    Modules,
    Resources,
    Records,
    Texts,
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
        DatabaseSupportState.Editable => "Схема распознана",
        DatabaseSupportState.ReadOnly => "Только просмотр",
        _ => "Недоступно",
    };

    public string StatusToolTip => SupportState switch
    {
        DatabaseSupportState.Editable =>
            "Структура базы распознана: её физические поля можно редактировать и безопасно сохранять.",
        DatabaseSupportState.ReadOnly => "Структура доступна для просмотра, но запись пока не поддерживается.",
        _ => "Эта база не разобрана редактором.",
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
        IReadOnlyList<RecordRowViewModel> records)
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
        Columns = BuildColumns(records, supportState == DatabaseSupportState.Editable);
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
        if (!ReferenceEquals(Records.LastOrDefault(r => !r.IsTextOnlyBriefing), record))
        {
            throw new InvalidOperationException("Безопасно удалить можно только последнюю добавленную строку таблицы.");
        }

        Records.Remove(record);
    }

    public string StatusText => SupportState switch
    {
        DatabaseSupportState.Editable => "Схема распознана",
        DatabaseSupportState.ReadOnly => "Только просмотр",
        _ => "Недоступно",
    };

    public string StatusToolTip => SupportState switch
    {
        DatabaseSupportState.Editable =>
            "Структура таблицы распознана: её физические поля можно редактировать и безопасно сохранять.",
        DatabaseSupportState.ReadOnly => "Таблица доступна для просмотра, но запись пока не поддерживается.",
        _ => "Эта таблица не разобрана редактором.",
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
        bool tableIsEditable)
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
                AddOrMerge(field, null);
                if (field.Kind is "Hex" or "Nested" or "StructuredList" or "PackedStructure")
                {
                    AddOrMerge(
                        field,
                        new DatabaseFieldProjection(
                            DatabaseFieldProjectionKind.DecodedValue,
                            0,
                            field.Kind == "StructuredList"
                                ? field.DisplayName
                                : $"{field.DisplayName} · расшифровка",
                            "Читаемое представление исходных байтов.",
                            field.Kind == "StructuredList" ? "StructuredListDecoded" : "Decoded",
                            field.DisplayOrder));
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
                        tableIsEditable &&
                        (projection is null ? field.IsEditable : field.CanEditProjection(projection))));
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
            bool isEditable)
        {
            FieldId = field.Id;
            Projection = projection;
            Key = BuildKey(field, projection);
            Title = GetDisplayTitle(field, projection);
            Kind = GetDisplayKind(field.Kind, projection);
            DisplayOrder = projection?.DisplayOrder ?? field.DisplayOrder;
            IsEditable = isEditable;
            ShowInReadableView = field.ShowInReadableView;
        }

        public int FieldId { get; }

        public DatabaseFieldProjection? Projection { get; }

        private string Key { get; }

        private string Title { get; }

        private string Kind { get; }

        private int DisplayOrder { get; }

        private bool IsEditable { get; set; }

        private bool ShowInReadableView { get; }

        private bool MetadataMatches { get; set; } = true;

        public void Merge(
            DatabaseField field,
            DatabaseFieldProjection? projection,
            bool tableIsEditable)
        {
            var metadataMatches = Equals(Projection, projection) &&
                                  string.Equals(Key, BuildKey(field, projection), StringComparison.Ordinal) &&
                                  string.Equals(Title, GetDisplayTitle(field, projection), StringComparison.Ordinal) &&
                                  string.Equals(Kind, GetDisplayKind(field.Kind, projection), StringComparison.Ordinal) &&
                                  ShowInReadableView == field.ShowInReadableView;
            MetadataMatches &= metadataMatches;
            IsEditable |= tableIsEditable && IsColumnEditable(field, projection);
        }

        public SpreadsheetColumnViewModel ToViewModel(bool isNameField) =>
            new(
                FieldId,
                Key,
                Title,
                Kind,
                IsEditable && MetadataMatches,
                isNameField,
                Projection,
                DisplayOrder,
                ShowInReadableView);

        private static string BuildKey(DatabaseField field, DatabaseFieldProjection? projection) =>
            projection is null ? field.Key : $"{field.Key}[{projection.Index}]";

        private static string GetDisplayTitle(DatabaseField field, DatabaseFieldProjection? projection) =>
            projection is null && IsRawEditorKind(field.Kind)
                ? $"{field.DisplayName} · raw"
                : projection?.DisplayName ?? field.DisplayName;

        private static string GetDisplayKind(string sourceKind, DatabaseFieldProjection? projection) =>
            projection is null && IsRawEditorKind(sourceKind)
                ? "RawHex"
                : projection?.ValueKind ?? (projection?.Kind switch
            {
                DatabaseFieldProjectionKind.Bit => "Boolean",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "Float32List" => "Float32",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "UInt32List" => "UInt32",
                DatabaseFieldProjectionKind.ListElement when sourceKind == "ByteList" => "Byte",
                DatabaseFieldProjectionKind.EnumValue => "Enum",
                DatabaseFieldProjectionKind.DecodedValue when sourceKind == "StructuredList" =>
                    "StructuredListDecoded",
                DatabaseFieldProjectionKind.DecodedValue => "Decoded",
                _ => sourceKind,
            });

        private static bool IsRawEditorKind(string kind) =>
            kind is "Hex" or "Nested" or "StructuredList" or "PackedStructure";

        private static bool IsColumnEditable(DatabaseField field, DatabaseFieldProjection? projection) =>
            projection is null ? field.IsEditable : field.CanEditProjection(projection);
    }
}

internal sealed record SpreadsheetColumnViewModel(
    int FieldId,
    string Key,
    string Title,
    string Kind,
    bool IsEditable,
    bool IsNameField,
    DatabaseFieldProjection? Projection,
    int DisplayOrder,
    bool ShowInReadableView)
{
    public string Subtitle => Kind == "StructuredListDecoded"
        ? "Варианты реплик · редактор ☷"
        : $"{Key} · {Kind}";

    public string HeaderToolTip => $"{Title}\n{Subtitle}";

    public SpreadsheetColumnLayout Layout => SpreadsheetColumnLayout.For(this);

    public string Identity => Projection is null
        ? $"{FieldId}"
        : $"{FieldId}:{Projection.Kind}:{Projection.Index}";

    public bool IsSortable => Kind is not (
        "Hex" or "RawHex" or "Nested" or "StructuredList" or "StringList" or "FixedStringList" or
        "StructuredListDecoded" or "UInt32List" or "Float32List" or "ByteList");

    public bool IsPrimitive => Kind is "String" or "FixedString" or "Int32" or "UInt32" or "Float32" or "Byte" or "Boolean";
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
            "Hex" or "RawHex" => new(124, 84, 280),
            "StringList" or "FixedStringList" or "UInt32List" or "Float32List" or "ByteList" =>
                new(140, 96, 320),
            "Nested" or "StructuredList" => new(140, 96, 320),
            "StructuredListDecoded" => new(190, 130, 420),
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
                            out float parsed))
                    {
                        return UnavailableText;
                    }

                    // Old fixed-size native arrays may retain NaN or denormal garbage in
                    // unused slots. Keep the raw bytes intact, but do not present that tail
                    // as a meaningful coordinate or coefficient in the readable table.
                    const float minimumNormal = 1.17549435E-38f;
                    if (projection.ValueKind == "Float32" &&
                        (!float.IsFinite(parsed) ||
                         (parsed != 0f && MathF.Abs(parsed) < minimumNormal)))
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
    private IReadOnlyList<LocalizationEntryViewModel> _relatedTexts = [];
    private IReadOnlyList<SoundLinkViewModel> _relatedSounds = [];
    private IReadOnlyList<BriefingPhraseViewModel> _briefingPhrases = [];
    private IReadOnlyList<string> _expectedTextKeys = [];
    private IReadOnlyList<string> _missingTextKeys = [];
    private LocalizationEntryViewModel? _primaryText;

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

    internal RecordRowViewModel? TextOnlyBriefingReference { get; set; }

    public bool IsTextOnlyBriefing => TableKey == "quests.briefings" && _record.ResourceName == "texts.res";

    public string BriefingSource => IsTextOnlyBriefing ? "texts.res (без записи БД)" : "quests.qdb";

    public int NameFieldId => _record.NameFieldId;

    public int IdentityFieldId => _record.IdentityFieldId;

    public string IdentityValue => _record.IdentityValue;

    public bool IsAdded => _record.IsNew;

    public IReadOnlyList<LocalizationEntryViewModel> RelatedTexts
    {
        get => _relatedTexts;
        private set
        {
            _relatedTexts = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRelatedTexts));
            OnPropertyChanged(nameof(RelatedTextCount));
        }
    }

    public bool HasRelatedTexts => RelatedTexts.Count > 0;

    public int RelatedTextCount => RelatedTexts.Count;

    public IReadOnlyList<SoundLinkViewModel> RelatedSounds
    {
        get => _relatedSounds;
        private set
        {
            _relatedSounds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSoundReferences));
            OnPropertyChanged(nameof(HasResolvedSounds));
            OnPropertyChanged(nameof(ResolvedSoundCount));
            OnPropertyChanged(nameof(MissingSoundCount));
            OnPropertyChanged(nameof(HasMissingSounds));
            OnPropertyChanged(nameof(SoundSummary));
            OnPropertyChanged(nameof(PrimarySoundPath));
        }
    }

    public bool HasSoundReferences => RelatedSounds.Count > 0;

    public bool HasResolvedSounds => ResolvedSoundCount > 0;

    public int ResolvedSoundCount => RelatedSounds.Count(sound => sound.IsResolved && sound.IsPreferred);

    public int MissingSoundCount => RelatedSounds.Count(sound => !sound.IsResolved);

    public bool HasMissingSounds => MissingSoundCount > 0;

    public string SoundSummary => (ResolvedSoundCount, MissingSoundCount) switch
    {
        (0, 0) => "—",
        (0, _) => "Не найдено",
        (var resolved, 0) => $"{resolved:N0} аудио",
        (var resolved, var missing) => $"{resolved:N0} аудио · {missing:N0} нет",
    };

    public string PrimarySoundPath => RelatedSounds
        .FirstOrDefault(sound => sound.IsResolved && sound.IsPreferred)?.ResourceName ??
        RelatedSounds.FirstOrDefault()?.Reference ?? string.Empty;

    public IReadOnlyList<BriefingPhraseViewModel> BriefingPhrases
    {
        get => _briefingPhrases;
        private set
        {
            _briefingPhrases = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BriefingPhraseCount));
            OnPropertyChanged(nameof(BriefingEventCount));
            OnPropertyChanged(nameof(BriefingProblemCount));
        }
    }

    public int BriefingPhraseCount => BriefingPhrases.Count(briefingEvent =>
        briefingEvent.IsTimelineEvent && briefingEvent.IsPhrase);

    public int BriefingEventCount => BriefingPhrases.Count(briefingEvent => briefingEvent.IsTimelineEvent);

    public int BriefingProblemCount => BriefingPhrases.Count(phrase => phrase.HasProblem);

    public bool CanEditPrimaryLocalization => _primaryText is not null;

    internal LocalizationEntryViewModel? PrimaryLocalization => _primaryText;

    internal string? PrimaryLocalizationErrorMessage => _primaryText?.ErrorMessage;

    public bool HasMissingLocalization => MissingLocalizationCount > 0;

    public bool HasLocalizationReferences => ExpectedTextKeys.Count > 0 || RelatedTexts.Count > 0;

    public IReadOnlyList<string> MissingLocalizationKeys => _missingTextKeys;

    public int MissingLocalizationCount => MissingLocalizationKeys.Count;

    public string LocalizationSummary => ExpectedTextKeys.Count > 0
        ? $"Найдено: {RelatedTexts.Count:N0} из {ExpectedTextKeys.Count:N0} · отсутствует: {MissingLocalizationCount:N0}"
        : $"Связано ресурсов: {RelatedTexts.Count:N0}";

    public bool HasMultipleLocalizations => RelatedTexts.Count > 1;

    public bool PrimaryLocalizationIsModified => _primaryText?.IsModified is true;

    public string LocalizationKeyText => _primaryText?.ResourceName ?? (RelatedTexts.Count, ExpectedTextKeys.Count) switch
    {
        (> 1, _) => $"Связано с texts.res: {RelatedTexts.Count:N0}",
        (0, 1) => $"{ExpectedTextKeys[0]} — нет ресурса",
        (0, > 1) => $"Нет ресурсов texts.res: {ExpectedTextKeys.Count:N0}",
        _ => "—",
    };

    public string LocalizationTitle
    {
        get => _primaryText?.Title ?? (RelatedTexts.Count, ExpectedTextKeys.Count) switch
        {
            (> 1, _) => "Связанные тексты — смотрите ниже",
            (0, > 1) => "Ожидаемые ключи — смотрите ниже",
            _ => string.Empty,
        };
        set
        {
            if (_primaryText is null)
            {
                return;
            }

            _primaryText.Title = value;
            OnPropertyChanged();
        }
    }

    public string LocalizationDescription
    {
        get => _primaryText?.Description ?? string.Empty;
        set
        {
            if (_primaryText is null)
            {
                return;
            }

            _primaryText.Description = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> ExpectedTextKeys => _expectedTextKeys;

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

    internal RecordFieldViewModel? GetFieldByOccurrence(int fieldId, int occurrence)
    {
        if (occurrence < 0)
        {
            return null;
        }

        DatabaseField? field = _record.Fields
            .Where(candidate => candidate.Id == fieldId)
            .Skip(occurrence)
            .FirstOrDefault();
        return field is null ? null : GetOrCreateField(field);
    }

    internal void SetRelatedTexts(
        IReadOnlyList<LocalizationEntryViewModel> texts,
        IReadOnlyList<string>? expectedKeys = null)
    {
        RelatedTexts = texts;
        if (expectedKeys is not null)
        {
            _expectedTextKeys = expectedKeys;
        }

        var relatedNames = texts
            .Select(text => text.ResourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _missingTextKeys = _expectedTextKeys
            .Where(key => !relatedNames.Contains(key))
            .ToArray();

        _primaryText = SelectPrimaryText(texts);
        OnPropertyChanged(nameof(ExpectedTextKeys));
        OnPropertyChanged(nameof(MissingLocalizationKeys));
        OnPropertyChanged(nameof(MissingLocalizationCount));
        OnPropertyChanged(nameof(HasLocalizationReferences));
        OnPropertyChanged(nameof(LocalizationSummary));
        OnPropertyChanged(nameof(CanEditPrimaryLocalization));
        OnPropertyChanged(nameof(HasMissingLocalization));
        OnPropertyChanged(nameof(HasMultipleLocalizations));
        OnPropertyChanged(nameof(PrimaryLocalizationIsModified));
        OnPropertyChanged(nameof(LocalizationKeyText));
        OnPropertyChanged(nameof(LocalizationTitle));
        OnPropertyChanged(nameof(LocalizationDescription));
        SearchText = BuildSearchText();
        OnPropertyChanged(nameof(SearchText));
    }

    internal void SetRelatedSounds(IReadOnlyList<SoundLinkViewModel> sounds)
    {
        RelatedSounds = sounds;
        SearchText = BuildSearchText();
        OnPropertyChanged(nameof(SearchText));
    }

    internal void SetBriefingPhrases(IReadOnlyList<BriefingPhraseViewModel> phrases)
    {
        BriefingPhrases = phrases;
    }

    internal void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(LocalizationKeyText));
        OnPropertyChanged(nameof(LocalizationTitle));
        OnPropertyChanged(nameof(LocalizationDescription));
        OnPropertyChanged(nameof(PrimaryLocalizationIsModified));
        SearchText = BuildSearchText();
        OnPropertyChanged(nameof(SearchText));
    }

    private LocalizationEntryViewModel? SelectPrimaryText(IReadOnlyList<LocalizationEntryViewModel> texts)
    {
        string? preferredPrefix = TableKey switch
        {
            "items.materials" => "MATERIAL",
            "items.loot_items" => "LITEM",
            _ => null,
        };
        if (preferredPrefix is not null)
        {
            LocalizationEntryViewModel? preferred = texts.FirstOrDefault(text =>
                text.Prefix.Equals(preferredPrefix, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return texts.Count == 1 ? texts[0] : null;
    }

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

        foreach (LocalizationEntryViewModel text in RelatedTexts)
        {
            builder.Append('\u001f').Append(text.ResourceName)
                .Append('\u001f').Append(text.Title)
                .Append('\u001f').Append(text.Description);
        }

        foreach (SoundLinkViewModel sound in RelatedSounds)
        {
            builder.Append('\u001f').Append(sound.Source)
                .Append('\u001f').Append(sound.Reference)
                .Append('\u001f').Append(sound.ResourceName)
                .Append('\u001f').Append(sound.ArchiveName)
                .Append('\u001f').Append(sound.TranscriptText);
        }

        return builder.ToString();
    }

    private static string NormalizeDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Без имени" : value;
}

internal sealed class SoundArchiveViewModel
{
    public SoundArchiveViewModel(SoundArchiveInfo archive)
    {
        SourcePath = archive.SourcePath;
        FileName = archive.FileName;
        FileLength = archive.FileLength;
        ArchiveEntryCount = archive.ArchiveEntryCount;
        SoundResourceCount = archive.SoundResourceCount;
        Priority = archive.Priority;
    }

    public int Priority { get; }

    public string SourcePath { get; }

    public string FileName { get; }

    public long FileLength { get; }

    public int ArchiveEntryCount { get; }

    public int SoundResourceCount { get; }
}

internal sealed class ManagedBackupViewModel
{
    public ManagedBackupViewModel(
        ManagedResKind kind,
        string targetPath,
        ManagedResBackupInfo? backup)
    {
        Kind = kind;
        TargetPath = targetPath;
        Backup = backup;
    }

    public ManagedResKind Kind { get; }

    public string TargetPath { get; }

    public ManagedResBackupInfo? Backup { get; }

    public bool HasBackup => Backup is not null;

    public string FileName => Path.GetFileName(TargetPath);

    public string TypeText => Kind switch
    {
        ManagedResKind.Database => "База данных",
        ManagedResKind.Texts => "Русские тексты",
        ManagedResKind.Sound => "Звуковой архив",
        _ => "RES",
    };

    public string BackupDetails => Backup is null
        ? "Резервных копий пока нет"
        : $"{Backup.LastWriteTimeUtc.ToLocalTime():g} · {FormatSize(Backup.FileLength)} · " +
          $"копий: {Backup.BackupCount:N0}";

    public string BackupToolTip => Backup is null
        ? $"После сохранения {FileName} здесь появится проверенная .bak-копия."
        : $"Текущий файл: {TargetPath}{Environment.NewLine}" +
          $"Будет восстановлен: {Backup.BackupPath}";

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }
}

internal sealed class PendingSoundEditViewModel
{
    public PendingSoundEditViewModel(
        string sceneKey,
        int phraseNumber,
        string speaker,
        BriefingSoundImportTarget target,
        SoundImportAsset asset)
    {
        SceneKey = sceneKey;
        PhraseNumber = phraseNumber;
        Speaker = speaker;
        Target = target;
        Asset = asset;
    }

    public string SceneKey { get; }

    public int PhraseNumber { get; }

    public string Speaker { get; }

    public string SourceFileName => Asset.FileName;

    public string Format => Asset.Format;

    public int Length => Asset.Data.Length;

    public string LengthText => Length < 1024
        ? $"{Length:N0} Б"
        : Length < 1024 * 1024
            ? $"{Length / 1024d:N1} КиБ"
            : $"{Length / 1024d / 1024d:N1} МиБ";

    public string ArchiveName => Target.Archive.FileName;

    public string ArchivePath => Target.Archive.SourcePath;

    public string ResourceName => Target.ResourceName;

    public bool IsReplacement => Target.IsReplacement;

    public string ActionText => IsReplacement ? "Замена существующего аудио" : "Добавление нового аудио";

    public string PhraseText => string.IsNullOrWhiteSpace(Speaker)
        ? $"{SceneKey} · реплика № {PhraseNumber:N0}"
        : $"{SceneKey} · реплика № {PhraseNumber:N0} · {Speaker}";

    internal BriefingSoundImportTarget Target { get; }

    internal SoundImportAsset Asset { get; }
}

internal sealed class SoundLinkViewModel
{
    public SoundLinkViewModel(
        DatabaseSoundLink link,
        BriefingPhrase? briefingPhrase = null,
        string? clipId = null)
    {
        Kind = link.Kind;
        Source = briefingPhrase is null
            ? link.Source
            : $"Реплика {clipId ?? briefingPhrase.Number.ToString()} · {briefingPhrase.Speaker}";
        Reference = link.Reference;
        IsResolved = link.IsResolved;
        IsPreferred = link.IsPreferred;
        ResourceName = link.Resource?.ResourceName ?? string.Empty;
        ArchiveName = link.Resource?.Archive.FileName ?? "—";
        ArchivePath = link.Resource?.Archive.SourcePath ?? string.Empty;
        Format = link.Resource?.Format ?? "—";
        Length = link.Resource?.Length;
        TranscriptText = briefingPhrase?.Text ?? string.Empty;
        PhraseNumber = briefingPhrase?.Number;
        Speaker = briefingPhrase?.Speaker ?? string.Empty;
        ClipId = clipId ?? string.Empty;
        Resource = link.Resource;
        if (PhraseNumber is null &&
            link.Kind == DatabaseSoundLinkKind.BriefingFolder &&
            BriefingTranscriptParser.TryGetClipIdentity(
                link.Resource?.ResourceName,
                out int parsedNumber,
                out string parsedClipId))
        {
            PhraseNumber = parsedNumber;
            ClipId = parsedClipId;
        }
    }

    public DatabaseSoundLinkKind Kind { get; }

    public string KindText => Kind == DatabaseSoundLinkKind.BriefingFolder
        ? "Брифинг"
        : "Реплика";

    public string Source { get; }

    public string Reference { get; }

    public string TranscriptText { get; }

    public int? PhraseNumber { get; }

    public string Speaker { get; }

    public string ClipId { get; }

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    public bool IsResolved { get; }

    public bool IsPreferred { get; }

    public string ResourceName { get; }

    public string DisplayResourceName => IsResolved ? ResourceName : Reference;

    public string ArchiveName { get; }

    public string ArchivePath { get; }

    public string Format { get; }

    public int? Length { get; }

    internal SoundResourceInfo? Resource { get; }

    public string LengthText => Length is int length
        ? length < 1024
            ? $"{length:N0} Б"
            : $"{length / 1024d:N1} КиБ"
        : "—";

    public string StatusText => IsResolved
        ? IsPreferred ? "Найден" : "Дубликат"
        : "Не найден";
}

internal sealed class BriefingPhraseViewModel
{
    public BriefingPhraseViewModel(
        BriefingTimelineEvent timelineEvent,
        IReadOnlyList<SoundLinkViewModel> sounds,
        IReadOnlyList<string>? scriptProblems = null,
        bool isTimelineEvent = true)
    {
        EventOrdinal = timelineEvent.Ordinal;
        Kind = timelineEvent.Kind;
        Directive = timelineEvent.Directive;
        Arguments = timelineEvent.Arguments;
        Number = timelineEvent.PhraseNumber;
        Speaker = timelineEvent.Speaker;
        Text = timelineEvent.Text;
        Sounds = sounds;
        ScriptProblems = scriptProblems ?? [];
        IsTimelineEvent = isTimelineEvent;
    }

    public int EventOrdinal { get; }

    public int SequenceNumber => EventOrdinal + 1;

    public bool IsTimelineEvent { get; }

    public BriefingEventKind Kind { get; }

    public string Directive { get; }

    public string Arguments { get; }

    public bool IsPhrase => Kind == BriefingEventKind.Phrase;

    public bool IsCommand => !IsPhrase;

    public int Number { get; }

    public string NumberText => $"№ {Number:N0}";

    public string Speaker { get; }

    public string Text { get; }

    public IReadOnlyList<SoundLinkViewModel> Sounds { get; }

    public SoundLinkViewModel? PrimarySound => Sounds.FirstOrDefault(sound => sound.IsResolved && sound.IsPreferred) ??
                                                     Sounds.FirstOrDefault(sound => sound.IsResolved);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool HasAudio => PrimarySound is not null;

    public bool HasDuplicateAudio => Sounds
        .Where(sound => sound.IsResolved)
        .GroupBy(sound => sound.ResourceName, StringComparer.OrdinalIgnoreCase)
        .Any(group => group.Count(sound => sound.IsPreferred) > 1);

    public int AudioPartCount => Sounds
        .Where(sound => sound.IsResolved && sound.IsPreferred)
        .Select(sound => sound.ResourceName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public IReadOnlyList<string> ScriptProblems { get; }

    public IReadOnlyList<string> ProblemMessages
    {
        get
        {
            var result = new List<string>(ScriptProblems);
            if (IsPhrase && !HasText)
            {
                result.Add(IsTimelineEvent
                    ? "У реплики отсутствует текст."
                    : $"Для аудио № {Number:N0} отсутствует команда #phrase.");
            }

            if (IsPhrase && !HasAudio && IsTimelineEvent)
            {
                result.Add($"Для реплики № {Number:N0} не найден звуковой файл.");
            }

            if (HasDuplicateAudio)
            {
                result.Add($"К реплике № {Number:N0} привязано несколько одинаковых аудиофайлов.");
            }

            return result.Distinct(StringComparer.CurrentCulture).ToArray();
        }
    }

    public bool HasProblem => ProblemMessages.Count > 0;

    public string ProblemSummary => string.Join(" • ", ProblemMessages);

    public string AudioName => PrimarySound is null
        ? "Аудио не найдено"
        : AudioPartCount > 1
            ? $"{PrimarySound.ResourceName} · ещё {AudioPartCount - 1:N0}"
            : PrimarySound.ResourceName;

    public string StatusText => ScriptProblems.Count > 0
        ? ScriptProblems.Count == 1 ? "Проверить сценарий" : $"Проблем: {ProblemMessages.Count:N0}"
        : !IsTimelineEvent ? "Нет #phrase"
        : (HasText, HasAudio, HasDuplicateAudio) switch
        {
            _ when !IsPhrase => EventTypeText,
            (false, false, _) => "Нет текста и звука",
            (false, true, _) => "Нет текста",
            (true, false, _) => "Нет звука",
            (_, _, true) => "Несколько аудиофайлов",
            _ => "Готово",
        };

    public string EventTypeText => Kind switch
    {
        BriefingEventKind.Phrase => $"Реплика № {Number:N0}",
        BriefingEventKind.Camera => "Камера",
        BriefingEventKind.Animation => "Анимация",
        BriefingEventKind.Thought => "Мысль",
        BriefingEventKind.Show => "Появление",
        BriefingEventKind.Hide => "Скрытие",
        BriefingEventKind.Description => "Режиссёрская пометка",
        _ => $"Команда #{Directive}",
    };

    public string DisplayTitle => Kind switch
    {
        BriefingEventKind.Phrase => Speaker,
        BriefingEventKind.Camera => "Положение камеры",
        BriefingEventKind.Animation => "Анимация персонажа",
        BriefingEventKind.Thought => "Мысль — без движения губ",
        BriefingEventKind.Show => "Показать участника",
        BriefingEventKind.Hide => "Скрыть участника",
        BriefingEventKind.Description => "Служебное описание",
        _ => $"#{Directive}",
    };

    public string DisplayText => Kind switch
    {
        BriefingEventKind.Phrase => Text,
        BriefingEventKind.Camera => $"Камера: {Arguments}",
        BriefingEventKind.Animation => $"Номер анимации: {Arguments}",
        BriefingEventKind.Thought => "Следующая реплика произносится в голове героя.",
        BriefingEventKind.Show => $"Участник: {Arguments}",
        BriefingEventKind.Hide => $"Участник: {Arguments}",
        BriefingEventKind.Description => Arguments,
        _ => Arguments,
    };

    public string DisplayDetails => !IsTimelineEvent
        ? $"Аудио № {Number:N0} не связано с текстовой репликой"
        : IsPhrase ? AudioName : $"#{Directive} {Arguments}".TrimEnd();
}

internal sealed class LocalizationEntryViewModel : ObservableObject
{
    private readonly TextResource _resource;
    private string? _errorMessage;

    public LocalizationEntryViewModel(TextResource resource)
    {
        _resource = resource;
    }

    public event EventHandler<LocalizationValueCommittedEventArgs>? ValueCommitted;

    public int? Index => _resource.ArchiveIndex is int archiveIndex ? archiveIndex + 1 : null;

    public string ResourceName => _resource.Name;

    public string Prefix => _resource.Prefix;

    public string SearchText => string.Join('\u001f', ResourceName, Prefix, Title, Description);

    public string Title
    {
        get => _resource.Title;
        set
        {
            TextResourceState before = _resource.CaptureState();
            if (!_resource.TrySetTitle(value, out string? error))
            {
                ErrorMessage = error;
                OnPropertyChanged();
                return;
            }

            ErrorMessage = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(SearchText));
            TextResourceState after = _resource.CaptureState();
            if (!before.IsEquivalentTo(after))
            {
                ValueCommitted?.Invoke(
                    this,
                    new LocalizationValueCommittedEventArgs(this, "Название RU", before, after));
            }
        }
    }

    public string Description
    {
        get => _resource.Description;
        set
        {
            TextResourceState before = _resource.CaptureState();
            if (!_resource.TrySetDescription(value, out string? error))
            {
                ErrorMessage = error;
                OnPropertyChanged();
                return;
            }

            ErrorMessage = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(SearchText));
            TextResourceState after = _resource.CaptureState();
            if (!before.IsEquivalentTo(after))
            {
                ValueCommitted?.Invoke(
                    this,
                    new LocalizationValueCommittedEventArgs(this, "Описание RU", before, after));
            }
        }
    }

    public string OriginalTitle => _resource.OriginalTitle;

    public string OriginalDescription => _resource.OriginalDescription;

    public bool IsNew => _resource.IsNew;

    public bool IsModified => _resource.IsModified;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    internal TextResource CoreResource => _resource;

    internal TextResourceState CaptureState() => _resource.CaptureState();

    internal void ResetToOriginal()
    {
        _resource.ResetToOriginal();
        RefreshState();
    }

    internal void RefreshState()
    {
        ErrorMessage = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(SearchText));
    }

    internal void RestoreState(TextResourceState state)
    {
        _resource.RestoreState(state);
        RefreshState();
    }
}

internal sealed class LocalizationValueCommittedEventArgs(
    LocalizationEntryViewModel entry,
    string fieldName,
    TextResourceState beforeState,
    TextResourceState afterState) : EventArgs
{
    public LocalizationEntryViewModel Entry { get; } = entry;

    public string FieldName { get; } = fieldName;

    public TextResourceState BeforeState { get; } = beforeState;

    public TextResourceState AfterState { get; } = afterState;
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
    internal DatabaseField CoreField => _field;
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
        _editorText = field.EditorText;
        OriginalText = field.OriginalEditorText;
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
        if (projection.Kind == DatabaseFieldProjectionKind.ListElement)
        {
            return SpreadsheetProjectionValue.Format(_editorText, projection);
        }

        object? value = _field.GetProjectedValue(projection);
        return value is null ? SpreadsheetProjectionValue.UnavailableText : FormatValue(value);
    }

    public bool CanEditProjection(DatabaseFieldProjection projection) =>
        CanEdit && _field.CanEditProjection(projection);

    public bool CanEditStringList =>
        CanEdit && Kind is "StringList" or "FixedStringList";

    public bool CanEditStructuredList =>
        CanEdit && Kind == "StructuredList" && _field.CanEditStructuredList;

    public bool TryGetStringListValues(out IReadOnlyList<string> values) =>
        _field.TryGetStringListValues(out values);

    public bool TryGetStructuredListEntries(
        out IReadOnlyList<StructuredListFieldDescriptor> fields,
        out IReadOnlyList<StructuredListEntryValue> entries) =>
        _field.TryGetStructuredListEntries(out fields, out entries);

    public bool TrySetStructuredListEntries(
        IReadOnlyList<StructuredListEntryValue> entries,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(entries);
        error = null;
        if (!CanEditStructuredList)
        {
            error = "Этот составной список доступен только для чтения.";
            ErrorMessage = error;
            return false;
        }

        DatabaseFieldState beforeState = _field.CaptureState();
        try
        {
            if (!_field.TrySetStructuredListEntries(entries, out error))
            {
                ErrorMessage = error ?? "Составной список не принят.";
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            ErrorMessage = error;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
        DatabaseFieldState afterState = _field.CaptureState();
        if (!beforeState.IsEquivalentTo(afterState))
        {
            ValueCommitted?.Invoke(this, new FieldValueCommittedEventArgs(beforeState, afterState));
        }

        return true;
    }

    public bool TrySetStringListValues(
        IReadOnlyList<string> values,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(values);
        error = null;
        if (!CanEditStringList)
        {
            error = "Это поле списка доступно только для чтения.";
            ErrorMessage = error;
            return false;
        }

        DatabaseFieldState beforeState = _field.CaptureState();
        try
        {
            if (!_field.TrySetStringListValues(values, out error))
            {
                ErrorMessage = error ?? "Список не принят.";
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            ErrorMessage = error;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
        DatabaseFieldState afterState = _field.CaptureState();
        if (!beforeState.IsEquivalentTo(afterState))
        {
            ValueCommitted?.Invoke(this, new FieldValueCommittedEventArgs(beforeState, afterState));
        }

        return true;
    }

    public bool TrySetProjectedText(
        DatabaseFieldProjection projection,
        string value,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(projection);
        error = null;
        if (!CanEditProjection(projection))
        {
            error = "Эта расшифрованная ячейка доступна только для чтения.";
            ErrorMessage = error;
            return false;
        }

        DatabaseFieldState beforeState = _field.CaptureState();
        try
        {
            if (!_field.TrySetProjectedValue(projection, value, out error))
            {
                ErrorMessage = error ?? "Значение не принято.";
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            ErrorMessage = error;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
        DatabaseFieldState afterState = _field.CaptureState();
        if (!beforeState.IsEquivalentTo(afterState))
        {
            ValueCommitted?.Invoke(this, new FieldValueCommittedEventArgs(beforeState, afterState));
        }

        return true;
    }

    public RecordFieldViewModel ProjectionSource => this;

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
            SetProperty(ref _editorText, _field.EditorText);
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(ProjectionSource));
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
                OnPropertyChanged(nameof(CanEditStringList));
                OnPropertyChanged(nameof(CanEditStructuredList));
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
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
        return true;
    }

    public DatabaseFieldState CaptureState() => _field.CaptureState();

    internal bool TryApplyTransferredPayload(
        ReadOnlySpan<byte> expectedCurrentPayload,
        ReadOnlySpan<byte> transferredPayload,
        out string? error)
    {
        try
        {
            if (!_field.TryApplyTransferredPayload(
                    expectedCurrentPayload,
                    transferredPayload,
                    out error))
            {
                ErrorMessage = error;
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            ErrorMessage = error;
            return false;
        }

        ErrorMessage = null;
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
        return true;
    }

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
        SetProperty(ref _editorText, _field.EditorText, nameof(EditorText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ProjectionSource));
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

    public ChangeEntryViewModel(LocalizationEntryViewModel localization, string fieldName)
    {
        Localization = localization;
        LocalizationFieldName = fieldName;
        _newValue = fieldName == "Название RU" ? localization.Title : localization.Description;
    }

    private ChangeEntryViewModel(LocalizationEntryViewModel localization)
    {
        Localization = localization;
        IsTextAddition = true;
        _newValue = localization.ResourceName;
    }

    public static ChangeEntryViewModel ForAddedRecord(RecordRowViewModel record, bool removal = false) => new(record) { IsRemoval = removal };

    public static ChangeEntryViewModel ForAddedText(LocalizationEntryViewModel localization, bool removal = false) => new(localization) { IsRemoval = removal };
    public bool IsRemoval { get; private init; }

    public RecordRowViewModel? Record { get; }

    public RecordFieldViewModel? Field { get; }

    public LocalizationEntryViewModel? Localization { get; }

    public string? LocalizationFieldName { get; }

    public bool IsRecordAddition { get; }

    public bool IsTextAddition { get; }

    public bool IsLocalization => Localization is not null;

    public string RecordName => Record?.DisplayName ?? Localization?.ResourceName ?? "—";

    public string FieldName => IsRecordAddition
        ? IsRemoval ? "Отмена добавленной строки" : "Новая строка"
        : IsTextAddition
            ? IsRemoval ? "Отмена добавленного текста" : "Новый текст"
            : LocalizationFieldName ?? Field!.DisplayName;

    public string Path => Record is not null
        ? IsRecordAddition ? Record.Path : Field!.Path
        : $"texts.res/{Localization!.ResourceName}/{LocalizationFieldName ?? "$new-resource"}";

    public string OriginalValue => IsRecordAddition || IsTextAddition
        ? IsRemoval ? RecordName : "—"
        : Localization is null
            ? Field!.OriginalText
            : LocalizationFieldName == "Название RU"
                ? Localization.OriginalTitle
                : Localization.OriginalDescription;

    public string ActionText => IsRecordAddition || IsTextAddition ? "Ctrl+Z" : "Вернуть";

    public string NewValue
    {
        get => IsRemoval ? "—" : _newValue;
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
    string? FieldKey,
    string TableTitle,
    string RecordTitle,
    string FieldTitle)
{
    public string BlockingText => IsBlocking ? "Блокирует" : "Не блокирует";

    public string ImpactText => IsBlocking
        ? "Блокирует сохранение"
        : "Требует ручной проверки";

    public int SeverityRank => Severity.Equals("Error", StringComparison.OrdinalIgnoreCase) ? 2 :
        Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    public string CategoryText => Code switch
    {
        "DB_RESOURCE_MISSING" => "Не найден ресурс базы",
        "DB_PARSE_FAILED" => "Ресурс базы повреждён",
        "RES_RESOURCE_OVERLAP_DIRECTORY" => "Ресурс пересекается с каталогом",
        "RES_RESOURCE_OVERLAP" => "Ресурсы пересекаются",
        "RES_DUPLICATE_NAME" => "Повторяется имя ресурса",
        "SEM_ITEM_MATERIAL_TYPE_MISMATCH" => "Не подходит тип материала",
        "SEM_EMBEDDED_MAGIC_TOO_MANY_RUNES" => "Слишком много рун",
        "SEM_EMBEDDED_MAGIC_ITEM_RUNE_REQUIRED" => "Для предмета нужна предметная руна",
        "SEM_EMBEDDED_MAGIC_COMPLEXITY_EXCEEDED" => "Превышена сложность магии",
        "SEM_EMBEDDED_MAGIC_ENERGY_EXCEEDED" => "Не хватает энергии для магии",
        "SEM_REQUIRED_VALUE_MISSING" => "Не заполнено обязательное значение",
        "SEM_NONFINITE_FLOAT" => "Некорректное число",
        "SEM_VALUE_OUT_OF_RANGE" => "Значение вне допустимого диапазона",
        "SEM_STRING_LIST_JOINED" => "Элементы списка слиты",
        "SEM_REFERENCE_NOT_FOUND" => "Ссылка не найдена",
        "SEM_VALUE_FORMAT" => "Некорректный формат значения",
        "SEM_LIST_LENGTH" => "Некорректное число элементов",
        "SEM_STRUCTURE_MEMBER_MISSING" => "Не хватает части структуры",
        "SEM_UNKNOWN_MASK_BITS" => "Неизвестные флаги значения",
        "SEM_DUPLICATE_KEY" => "Повторяется ключ",
        "SEM_EMBEDDED_MAGIC_FORMAT" => "Некорректная запись встроенной магии",
        "SEM_EMBEDDED_MAGIC_MATERIAL_NOT_FOUND" => "Материал встроенной магии не найден",
        _ => DomainText == "Структура" ? "Структурная проблема" : "Семантическое предупреждение",
    };

    public string GroupTitle => $"{CategoryText} · {SeverityText}";

    public string SearchText => string.Join(
        ' ',
        CategoryText,
        Message,
        Place,
        TableTitle,
        RecordTitle,
        FieldTitle,
        Code);

    public bool CanNavigate => !string.IsNullOrWhiteSpace(TableKey) && RecordIndex is >= 0;
}

internal sealed record DiagnosticCategoryViewModel(
    string Key,
    string Title,
    string Severity,
    string SeverityText,
    int Count,
    bool IsAll = false);

internal sealed class WorkspaceSnapshot
{
    public required string SourcePath { get; init; }

    public required long FileLength { get; init; }

    public required string Sha256 { get; init; }

    public required IReadOnlyList<ResourceRowViewModel> Resources { get; init; }

    public IReadOnlyList<DatabaseGroupViewModel> Databases { get; init; } = [];

    public required IReadOnlyList<ValidationMessageViewModel> Diagnostics { get; init; }

    public string? TextsPath { get; init; }

    public long TextsFileLength { get; init; }

    public string? TextsSha256 { get; init; }

    public int TextResourceCount { get; init; }

    public IReadOnlyList<LocalizationEntryViewModel> TextResources { get; init; } = [];

    public IReadOnlyList<SoundArchiveViewModel> SoundArchives { get; init; } = [];

    public int SoundResourceCount { get; init; }

    public IReadOnlyList<ManagedBackupViewModel> ManagedBackups { get; init; } = [];
}

internal sealed record SaveCopyResult(
    string TargetPath,
    long FileLength,
    string Sha256,
    bool ReopenedSuccessfully,
    int DiagnosticCount);

internal sealed record SaveCurrentResult(
    SaveCopyResult Save,
    WorkspaceSnapshot Workspace,
    string? BackupPath,
    SaveCopyResult? TextsSave = null,
    string? TextsBackupPath = null);

internal sealed record SavePairCopyResult(
    SaveCopyResult Database,
    SaveCopyResult? Texts);

internal sealed record SaveSoundCurrentResult(
    SoundArchiveCopyResult Save,
    IReadOnlyList<SoundArchiveViewModel> SoundArchives,
    int SoundResourceCount,
    IReadOnlyList<ManagedBackupViewModel> ManagedBackups);

internal sealed record RestoreManagedBackupResult(
    ManagedResRestoreResult Restore,
    WorkspaceSnapshot Workspace);
