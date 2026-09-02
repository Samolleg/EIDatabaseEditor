using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseWorkspaceService _service;
    private readonly Stack<WorkspaceEditFrame> _undo = new();
    private readonly Stack<WorkspaceEditFrame> _redo = new();
    private readonly Dictionary<string, ChangeEntryViewModel> _changesByPath = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _validationCancellation;
    private WorkspaceSnapshot? _workspace;
    private WorkspaceSection _currentSection = WorkspaceSection.Overview;
    private DatabaseTableViewModel? _selectedTable;
    private ICollectionView _recordsView;
    private ICollectionView _resourcesView;
    private ICollectionView _textsView;
    private ICollectionView _diagnosticsView;
    private RecordRowViewModel? _selectedRecord;
    private BriefingPhraseViewModel? _selectedBriefingPhrase;
    private PendingSoundEditViewModel? _pendingSoundEdit;
    private bool _showOnlyBriefingProblems;
    private string _briefingDraftSpeaker = string.Empty;
    private string _briefingDraftText = string.Empty;
    private string _briefingDraftArguments = string.Empty;
    private int _briefingDraftNumber;
    private bool _isNewBriefingPhrase;
    private int? _newBriefingInsertAfterOrdinal;
    private int? _pendingBriefingPhraseNumber;
    private int? _pendingBriefingEventOrdinal;
    private string _searchText = string.Empty;
    private string _resourceSearchText = string.Empty;
    private string _textsSearchText = string.Empty;
    private string _diagnosticSearchText = string.Empty;
    private string _selectedDiagnosticSeverityFilter = "Все уровни";
    private string _selectedDiagnosticDomainFilter = "Все области";
    private bool _showOnlyBlockingDiagnostics;
    private DiagnosticCategoryViewModel? _selectedDiagnosticCategory;
    private bool _isUpdatingDiagnosticCategories;
    private ValidationMessageViewModel? _selectedDiagnostic;
    private int _filteredDiagnosticCount;
    private string _statusText = "Готово. Откройте database.res для редактирования.";
    private string? _notificationMessage;
    private bool _notificationIsError;
    private bool _isBusy;
    private bool _isOperationCancelable;
    private string _busyText = string.Empty;
    private bool _isEditMode;
    private bool _showAllColumns;
    private int _filteredRecordCount;
    private int _filteredTextResourceCount;
    private string? _lastSavedPath;
    private bool _isApplyingSpreadsheetPaste;
    private bool _recordsViewRefreshPending;
    private bool _textsViewRefreshPending;
    private bool _disposed;

    public MainWindowViewModel()
        : this(new CoreDatabaseWorkspaceService())
    {
    }

    internal MainWindowViewModel(IDatabaseWorkspaceService service)
    {
        _service = service;
        Resources = [];
        Databases = [];
        TextResources = [];
        Changes = [];
        Diagnostics = [];
        DiagnosticCategories = [];
        _recordsView = CollectionViewSource.GetDefaultView(Array.Empty<RecordRowViewModel>());
        _resourcesView = CollectionViewSource.GetDefaultView(Resources);
        _textsView = CollectionViewSource.GetDefaultView(TextResources);
        _diagnosticsView = CollectionViewSource.GetDefaultView(Diagnostics);
        _diagnosticsView.Filter = FilterDiagnostic;
        ConfigureDiagnosticSort();
        RebuildDiagnosticCategories();

        NavigationItems =
        [
            new(WorkspaceSection.Overview, "Обзор", "⌂") { IsSelected = true },
            new(WorkspaceSection.Texts, "Русские тексты", "АЯ") { IsEnabled = false },
            new(WorkspaceSection.Resources, "Ресурсы", "▦") { IsEnabled = false },
            new(WorkspaceSection.Changes, "Изменения", "≠") { IsEnabled = false },
            new(WorkspaceSection.Validation, "Проверка", "✓") { IsEnabled = false },
        ];
        OverviewNavigationItem = NavigationItems[0];
        TextsNavigationItem = NavigationItems[1];
        ServiceNavigationItems = new ObservableCollection<NavigationItemViewModel>(NavigationItems.Skip(2));

        OpenCommand = new RelayCommand(() => OpenFileRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        AttachTextsCommand = new RelayCommand(
            () => AttachTextsRequested?.Invoke(this, EventArgs.Empty),
            () => HasDocument && !HasUnsavedWork && !IsBusy);
        AttachSoundsCommand = new RelayCommand(
            () => AttachSoundsRequested?.Invoke(this, EventArgs.Empty),
            () => HasDocument && !HasUnsavedWork && !IsBusy);
        SaveCommand = new RelayCommand(
            () => SaveRequested?.Invoke(this, EventArgs.Empty),
            () => HasDocument && !IsBusy);
        SaveCopyCommand = new RelayCommand(() => SaveCopyRequested?.Invoke(this, EventArgs.Empty), () => HasDocument && !IsBusy);
        UndoCommand = new RelayCommand(Undo, () => !IsBusy && _undo.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => !IsBusy && _redo.Count > 0);
        NavigateCommand = new RelayCommand<NavigationItemViewModel>(Navigate, item => item?.IsEnabled is true);
        SelectTableCommand = new RelayCommand<DatabaseTableViewModel>(SelectTable, table => table?.IsEnabled is true);
        AppendRecordCommand = new RelayCommand(
            AppendSelectedRecord,
            () => IsEditMode && IsRecordsSection && IsCurrentTableAddable && !IsBusy);
        AddMissingTextsCommand = new RelayCommand(
            AddMissingTexts,
            () => IsEditMode && IsRecordsSection &&
                  SelectedRecord?.MissingLocalizationCount > 0 && HasTextsDocument && !IsBusy);
        RefreshValidationCommand = new RelayCommand(RefreshValidation, () => HasDocument && !IsBusy);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
        ClearResourceSearchCommand = new RelayCommand(() => ResourceSearchText = string.Empty, () => !string.IsNullOrEmpty(ResourceSearchText));
        ClearTextsSearchCommand = new RelayCommand(() => TextsSearchText = string.Empty, () => !string.IsNullOrEmpty(TextsSearchText));
        ClearDiagnosticFiltersCommand = new RelayCommand(
            ClearDiagnosticFilters,
            () => HasActiveDiagnosticFilters);
        ToggleColumnModeCommand = new RelayCommand(
            ToggleColumnMode,
            () => IsRecordsSection && SelectedTable is { Columns.Count: > 12 } && !IsBusy);
        FocusSearchCommand = new RelayCommand(() => SearchFocusRequested?.Invoke(this, EventArgs.Empty), () => HasDocument);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => IsBusy && IsOperationCancelable);
        DismissNotificationCommand = new RelayCommand(() => NotificationMessage = null, () => HasNotification);
        RevertChangeCommand = new RelayCommand<ChangeEntryViewModel>(
            RevertChange,
            change => change is { IsRecordAddition: false, IsTextAddition: false } && !IsBusy);
        NavigateToDiagnosticCommand = new RelayCommand<ValidationMessageViewModel>(
            NavigateToDiagnostic,
            diagnostic => diagnostic?.CanNavigate is true && HasDocument && !IsBusy);
        NewBriefingPhraseCommand = new RelayCommand(
            BeginNewBriefingPhrase,
            () => IsBriefingsTable && SelectedRecord is not null && HasTextsDocument && !IsBusy);
        AddBriefingEventCommand = new RelayCommand<string>(
            AddBriefingEvent,
            kind => IsBriefingsTable && SelectedRecord is not null &&
                    GetSelectedBriefingText() is not null &&
                    TryParseBriefingEventKind(kind, out _) && !IsBusy);
        DeleteBriefingEventCommand = new RelayCommand(
            DeleteBriefingEvent,
            () => IsBriefingsTable && SelectedBriefingPhrase?.IsTimelineEvent is true &&
                  !IsNewBriefingPhrase && !IsBusy);
        DuplicateBriefingEventCommand = new RelayCommand(
            DuplicateBriefingEvent,
            () => IsBriefingsTable && SelectedBriefingPhrase?.IsTimelineEvent is true &&
                  !IsNewBriefingPhrase && !IsBusy);
        MoveBriefingEventUpCommand = new RelayCommand(
            () => ReorderBriefingEvent(-1),
            () => CanReorderBriefingEvent(-1));
        MoveBriefingEventDownCommand = new RelayCommand(
            () => ReorderBriefingEvent(1),
            () => CanReorderBriefingEvent(1));
        ApplyBriefingPhraseCommand = new RelayCommand(
            ApplyBriefingPhrase,
            CanApplyBriefingDraft);
        PreviousBriefingPhraseCommand = new RelayCommand(
            () => MoveBriefingPhrase(-1),
            () => CanMoveBriefingPhrase(-1));
        NextBriefingPhraseCommand = new RelayCommand(
            () => MoveBriefingPhrase(1),
            () => CanMoveBriefingPhrase(1));
        PlayBriefingAudioCommand = new RelayCommand(
            PlayBriefingAudio,
            () => SelectedBriefingPhrase?.PrimarySound?.Resource is not null && !IsBusy);
        StopBriefingAudioCommand = new RelayCommand(
            () => StopBriefingAudioRequested?.Invoke(this, EventArgs.Empty),
            () => IsBriefingsTable);
        ImportBriefingAudioCommand = new RelayCommand(
            () => ImportBriefingAudioRequested?.Invoke(this, EventArgs.Empty),
            () => IsBriefingsTable && SelectedRecord is not null &&
                  SelectedBriefingPhrase?.IsPhrase is true && !IsNewBriefingPhrase &&
                  HasSoundArchives && !IsBusy);
        CancelPendingSoundEditCommand = new RelayCommand(
            CancelPendingSoundEdit,
            () => PendingSoundEdit is not null && !IsBusy);
        SavePendingSoundCopyCommand = new RelayCommand(
            () => SavePendingSoundCopyRequested?.Invoke(this, EventArgs.Empty),
            () => PendingSoundEdit is not null && !IsBusy);
        SavePendingSoundCurrentCommand = new RelayCommand(
            () => SavePendingSoundCurrentRequested?.Invoke(this, EventArgs.Empty),
            () => PendingSoundEdit is not null && !IsBusy);
        RestoreManagedBackupCommand = new RelayCommand<ManagedBackupViewModel>(
            backup =>
            {
                if (backup is not null)
                {
                    RestoreManagedBackupRequested?.Invoke(
                        this,
                        new ManagedBackupRestoreRequestedEventArgs(backup));
                }
            },
            backup => backup?.HasBackup is true && HasDocument && !HasUnsavedWork && !IsBusy);
    }

    public event EventHandler? OpenFileRequested;

    public event EventHandler? AttachTextsRequested;

    public event EventHandler? AttachSoundsRequested;

    public event EventHandler? SaveRequested;

    public event EventHandler? SaveCopyRequested;

    public event EventHandler? SearchFocusRequested;

    public event EventHandler<RecordFocusRequestedEventArgs>? RecordFocusRequested;

    public event EventHandler<SoundPreviewRequestedEventArgs>? PlayBriefingAudioRequested;

    public event EventHandler? StopBriefingAudioRequested;

    public event EventHandler? ImportBriefingAudioRequested;

    public event EventHandler? SavePendingSoundCopyRequested;

    public event EventHandler? SavePendingSoundCurrentRequested;

    public event EventHandler<ManagedBackupRestoreRequestedEventArgs>? RestoreManagedBackupRequested;

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel OverviewNavigationItem { get; }

    public NavigationItemViewModel TextsNavigationItem { get; }

    public ObservableCollection<NavigationItemViewModel> ServiceNavigationItems { get; }

    public RangeObservableCollection<ResourceRowViewModel> Resources { get; }

    public RangeObservableCollection<DatabaseGroupViewModel> Databases { get; }

    public RangeObservableCollection<LocalizationEntryViewModel> TextResources { get; }

    public ObservableCollection<ChangeEntryViewModel> Changes { get; }

    public RangeObservableCollection<ValidationMessageViewModel> Diagnostics { get; }

    public RangeObservableCollection<DiagnosticCategoryViewModel> DiagnosticCategories { get; }

    public IReadOnlyList<string> DiagnosticSeverityOptions { get; } =
        ["Все уровни", "Ошибки", "Предупреждения", "Информация"];

    public IReadOnlyList<string> DiagnosticDomainOptions { get; } =
        ["Все области", "Структура", "Семантика"];

    public ICommand OpenCommand { get; }

    public ICommand AttachTextsCommand { get; }

    public ICommand AttachSoundsCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand SaveCopyCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand SelectTableCommand { get; }

    public ICommand AppendRecordCommand { get; }

    public ICommand AddMissingTextsCommand { get; }

    public ICommand RefreshValidationCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ClearResourceSearchCommand { get; }

    public ICommand ClearTextsSearchCommand { get; }

    public ICommand ClearDiagnosticFiltersCommand { get; }

    public ICommand ToggleColumnModeCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public ICommand DismissNotificationCommand { get; }

    public ICommand RevertChangeCommand { get; }

    public ICommand NavigateToDiagnosticCommand { get; }

    public ICommand NewBriefingPhraseCommand { get; }

    public ICommand AddBriefingEventCommand { get; }

    public ICommand DeleteBriefingEventCommand { get; }

    public ICommand DuplicateBriefingEventCommand { get; }

    public ICommand MoveBriefingEventUpCommand { get; }

    public ICommand MoveBriefingEventDownCommand { get; }

    public ICommand ApplyBriefingPhraseCommand { get; }

    public ICommand PreviousBriefingPhraseCommand { get; }

    public ICommand NextBriefingPhraseCommand { get; }

    public ICommand PlayBriefingAudioCommand { get; }

    public ICommand StopBriefingAudioCommand { get; }

    public ICommand ImportBriefingAudioCommand { get; }

    public ICommand CancelPendingSoundEditCommand { get; }

    public ICommand SavePendingSoundCopyCommand { get; }

    public ICommand SavePendingSoundCurrentCommand { get; }

    public ICommand RestoreManagedBackupCommand { get; }

    public ICollectionView RecordsView
    {
        get => _recordsView;
        private set
        {
            _recordsViewRefreshPending = false;
            SetProperty(ref _recordsView, value);
        }
    }

    public ICollectionView ResourcesView
    {
        get => _resourcesView;
        private set => SetProperty(ref _resourcesView, value);
    }

    public ICollectionView TextsView
    {
        get => _textsView;
        private set => SetProperty(ref _textsView, value);
    }

    public ICollectionView DiagnosticsView => _diagnosticsView;

    public WorkspaceSection CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (!SetProperty(ref _currentSection, value))
            {
                return;
            }

            foreach (var item in NavigationItems)
            {
                item.IsSelected = item.Section == value;
            }

            if (SelectedTable is not null)
            {
                SelectedTable.IsSelected = value == WorkspaceSection.Records;
            }

            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(IsRecordsSection));
            OnPropertyChanged(nameof(IsTextsSection));
            OnPropertyChanged(nameof(IsBriefingsTable));
            OnPropertyChanged(nameof(BreadcrumbText));
            RaiseCommandStates();
        }
    }

    public string PageTitle => CurrentSection switch
    {
        WorkspaceSection.Overview => "Обзор базы",
        WorkspaceSection.Resources => "Ресурсы контейнера",
        WorkspaceSection.Records => SelectedTable?.Title ?? "Таблица базы",
        WorkspaceSection.Texts => "Русские тексты",
        WorkspaceSection.Changes => "Изменения",
        WorkspaceSection.Validation => "Проверка целостности",
        _ => "Редактор БД от Атамана",
    };

    public string PageSubtitle => CurrentSection switch
    {
        WorkspaceSection.Overview => "Редактирование включено; Ctrl+S сохраняет текущий файл с автоматической .bak-копией.",
        WorkspaceSection.Resources => "Содержимое RES-контейнера, смещения и размеры внутренних ресурсов.",
        WorkspaceSection.Records when SelectedTable is not null =>
            $"{SelectedTable.GroupTitle} · {SelectedTable.ResourceName} · строки — записи, столбцы — поля · {SelectedTable.StatusText.ToLowerInvariant()}.",
        WorkspaceSection.Texts => "Полное содержимое подключённого texts.res: ключи, русские названия и описания.",
        WorkspaceSection.Changes => "Только отличия от открытого исходного файла.",
        WorkspaceSection.Validation => "Ошибки структуры и предупреждения по ссылочной целостности.",
        _ => string.Empty,
    };

    public bool IsRecordsSection => CurrentSection == WorkspaceSection.Records;

    public bool IsTextsSection => CurrentSection == WorkspaceSection.Texts;

    public DatabaseTableViewModel? SelectedTable
    {
        get => _selectedTable;
        private set
        {
            if (ReferenceEquals(_selectedTable, value))
            {
                return;
            }

            if (_selectedTable is not null)
            {
                _selectedTable.IsSelected = false;
            }

            if (!SetProperty(ref _selectedTable, value))
            {
                return;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(BreadcrumbText));
            OnPropertyChanged(nameof(CurrentTableStatusText));
            OnPropertyChanged(nameof(CurrentTableStatusToolTip));
            OnPropertyChanged(nameof(CurrentTableRecordCount));
            OnPropertyChanged(nameof(IsCurrentTableReadOnly));
            OnPropertyChanged(nameof(IsCurrentTableEditable));
            OnPropertyChanged(nameof(IsCurrentTableAddable));
            OnPropertyChanged(nameof(CurrentTableSupportsSounds));
            OnPropertyChanged(nameof(IsBriefingsTable));
            OnPropertyChanged(nameof(VisibleSpreadsheetColumns));
            OnPropertyChanged(nameof(SpreadsheetColumnCountText));
            OnPropertyChanged(nameof(ColumnModeButtonText));
            RaiseCommandStates();
        }
    }

    public string BreadcrumbText => CurrentSection switch
    {
        WorkspaceSection.Records when SelectedTable is not null =>
            $"database.res  ›  {SelectedTable.ResourceName}  ›  {SelectedTable.Title}",
        WorkspaceSection.Texts => $"{TextsFileName}  ›  Все русские тексты",
        _ => $"database.res  ›  {PageTitle}",
    };

    public string CurrentTableStatusText => SelectedTable?.StatusText ?? "—";

    public string CurrentTableStatusToolTip =>
        SelectedTable?.StatusToolTip ?? "Откройте таблицу, чтобы увидеть состояние её схемы.";

    public int CurrentTableRecordCount => SelectedTable?.RecordCount ?? 0;

    public bool IsCurrentTableReadOnly => SelectedTable?.IsReadOnly is true;

    public bool IsCurrentTableEditable => SelectedTable?.IsEditable is true;

    public RecordRowViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (ReferenceEquals(_selectedRecord, value))
            {
                return;
            }

            if (_selectedRecord is not null)
            {
                _selectedRecord.PropertyChanged -= SelectedRecord_PropertyChanged;
            }

            if (SetProperty(ref _selectedRecord, value))
            {
                if (_selectedRecord is not null)
                {
                    _selectedRecord.PropertyChanged += SelectedRecord_PropertyChanged;
                }

                OnPropertyChanged(nameof(HasSelectedRecord));
                OnPropertyChanged(nameof(SelectedRecordHasTexts));
                OnPropertyChanged(nameof(SelectedRecordTextCount));
                OnPropertyChanged(nameof(SelectedRecordHasSoundReferences));
                OnPropertyChanged(nameof(SelectedRecordHasDetails));
                OnPropertyChanged(nameof(SelectedRecordSoundCount));
                RefreshBriefingPhraseSelection();
                (AppendRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (AddMissingTextsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedRecord => SelectedRecord is not null;

    public bool SelectedRecordHasTexts => SelectedRecord?.HasRelatedTexts is true;

    public int SelectedRecordTextCount => SelectedRecord?.RelatedTextCount ?? 0;

    public bool SelectedRecordHasSoundReferences => SelectedRecord?.HasSoundReferences is true;

    public bool SelectedRecordHasDetails =>
        SelectedRecord?.HasLocalizationReferences is true || SelectedRecordHasSoundReferences;

    public int SelectedRecordSoundCount => SelectedRecord?.ResolvedSoundCount ?? 0;

    public bool IsCurrentTableAddable => SelectedTable?.CanAppendRecords is true;

    public bool CurrentTableSupportsSounds => SelectedTable?.Key is
        "acks.answers" or "acks.cries" or "acks.others" or "quests.briefings";

    public bool IsBriefingsTable => IsRecordsSection &&
                                    SelectedTable?.Key.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase) is true;

    public IEnumerable<BriefingPhraseViewModel> FilteredBriefingPhrases =>
        (SelectedRecord?.BriefingPhrases ?? [])
        .Where(phrase => !ShowOnlyBriefingProblems || phrase.HasProblem);

    public BriefingPhraseViewModel? SelectedBriefingPhrase
    {
        get => _selectedBriefingPhrase;
        set
        {
            if (!SetProperty(ref _selectedBriefingPhrase, value))
            {
                return;
            }

            if (value is not null)
            {
                _isNewBriefingPhrase = false;
                _newBriefingInsertAfterOrdinal = null;
                BriefingDraftNumber = value.Number;
                BriefingDraftSpeaker = value.Speaker;
                BriefingDraftText = value.Text;
                BriefingDraftArguments = value.Arguments;
            }

            OnPropertyChanged(nameof(IsNewBriefingPhrase));
            OnPropertyChanged(nameof(IsPhraseBriefingEditor));
            OnPropertyChanged(nameof(IsCommandBriefingEditor));
            OnPropertyChanged(nameof(BriefingEditorTitle));
            OnPropertyChanged(nameof(BriefingCommandFieldLabel));
            OnPropertyChanged(nameof(BriefingCommandHelp));
            OnPropertyChanged(nameof(BriefingCommandHasArguments));
            RaiseBriefingCommandStates();
        }
    }

    public bool ShowOnlyBriefingProblems
    {
        get => _showOnlyBriefingProblems;
        set
        {
            if (!SetProperty(ref _showOnlyBriefingProblems, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FilteredBriefingPhrases));
            RefreshBriefingPhraseSelection();
        }
    }

    public int BriefingDraftNumber
    {
        get => _briefingDraftNumber;
        private set => SetProperty(ref _briefingDraftNumber, value);
    }

    public string BriefingDraftSpeaker
    {
        get => _briefingDraftSpeaker;
        set
        {
            if (SetProperty(ref _briefingDraftSpeaker, value))
            {
                RaiseBriefingCommandStates();
            }
        }
    }

    public string BriefingDraftText
    {
        get => _briefingDraftText;
        set
        {
            if (SetProperty(ref _briefingDraftText, value))
            {
                RaiseBriefingCommandStates();
            }
        }
    }

    public string BriefingDraftArguments
    {
        get => _briefingDraftArguments;
        set
        {
            if (SetProperty(ref _briefingDraftArguments, value))
            {
                RaiseBriefingCommandStates();
            }
        }
    }

    public bool IsNewBriefingPhrase => _isNewBriefingPhrase;

    public bool IsPhraseBriefingEditor => IsNewBriefingPhrase || SelectedBriefingPhrase?.IsPhrase is true;

    public bool IsCommandBriefingEditor => SelectedBriefingPhrase?.IsCommand is true && !IsNewBriefingPhrase;

    public string BriefingEditorTitle => IsNewBriefingPhrase
        ? $"Новая реплика № {BriefingDraftNumber:N0}"
        : SelectedBriefingPhrase is null
            ? "Выберите событие"
            : SelectedBriefingPhrase.IsPhrase
                ? $"Реплика № {SelectedBriefingPhrase.Number:N0}"
                : SelectedBriefingPhrase.DisplayTitle;

    public string BriefingCommandFieldLabel => SelectedBriefingPhrase?.Kind switch
    {
        BriefingEventKind.Camera => "Положение камеры",
        BriefingEventKind.Animation => "Номер анимации",
        BriefingEventKind.Show => "Кого показать",
        BriefingEventKind.Hide => "Кого скрыть",
        BriefingEventKind.Description => "Служебное описание",
        BriefingEventKind.Thought => "Параметры не требуются",
        _ => "Параметры команды",
    };

    public string BriefingCommandHelp => SelectedBriefingPhrase?.Kind switch
    {
        BriefingEventKind.Camera => "Формат игры: номер камеры либо номер, угол, дистанция и высота.",
        BriefingEventKind.Animation => "Целый номер анимации, которая запускается перед следующей репликой.",
        BriefingEventKind.Show => "Персонаж, его позиция и необязательный параметр weapon.",
        BriefingEventKind.Hide => "Имя участника, которого нужно скрыть из сцены.",
        BriefingEventKind.Description => "Пометка сценария #DESC. Она сохраняется в исходной последовательности.",
        BriefingEventKind.Thought => "#NOLIPS отключает движение губ для следующей реплики — герой думает в голове.",
        _ => "Исходная команда сохраняется на своём месте в BRIEFING-ресурсе texts.res.",
    };

    public bool BriefingCommandHasArguments => SelectedBriefingPhrase?.Kind != BriefingEventKind.Thought;

    public string BriefingWorkspaceSummary => SelectedRecord is null
        ? "Выберите брифинг слева"
        : $"{SelectedRecord.BriefingEventCount:N0} событий · {SelectedRecord.BriefingPhraseCount:N0} реплик · {SelectedRecord.BriefingProblemCount:N0} требуют внимания";

    public IReadOnlyList<SpreadsheetColumnViewModel> VisibleSpreadsheetColumns
    {
        get
        {
            if (SelectedTable is null || ShowAllColumns)
            {
                return SelectedTable?.Columns ?? [];
            }

            return BuildReadableColumns(SelectedTable.Columns);
        }
    }

    public bool ShowAllColumns
    {
        get => _showAllColumns;
        private set
        {
            if (!SetProperty(ref _showAllColumns, value))
            {
                return;
            }

            OnPropertyChanged(nameof(VisibleSpreadsheetColumns));
            OnPropertyChanged(nameof(SpreadsheetColumnCountText));
            OnPropertyChanged(nameof(ColumnModeButtonText));
            (ToggleColumnModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SpreadsheetColumnCountText => SelectedTable is null
        ? "0 столбцов"
        : $"{VisibleSpreadsheetColumns.Count:N0}/{SelectedTable.Columns.Count:N0} полей" +
          (HasTextsDocument ? " · +3 RU" : string.Empty) +
          (HasSoundArchives && CurrentTableSupportsSounds ? " · +2 звук" : string.Empty);

    public string ColumnModeButtonText => ShowAllColumns ? "Понятные поля" : "Технические поля";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
                ScheduleRecordSearch();
            }
        }
    }

    public string ResourceSearchText
    {
        get => _resourceSearchText;
        set
        {
            if (SetProperty(ref _resourceSearchText, value))
            {
                (ClearResourceSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
                ResourcesView.Refresh();
            }
        }
    }

    public string TextsSearchText
    {
        get => _textsSearchText;
        set
        {
            if (SetProperty(ref _textsSearchText, value))
            {
                (ClearTextsSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
                RefreshTextsFilter();
            }
        }
    }

    public string DiagnosticSearchText
    {
        get => _diagnosticSearchText;
        set
        {
            if (SetProperty(ref _diagnosticSearchText, value))
            {
                RefreshDiagnosticsView();
            }
        }
    }

    public string SelectedDiagnosticSeverityFilter
    {
        get => _selectedDiagnosticSeverityFilter;
        set
        {
            value ??= "Все уровни";
            if (SetProperty(ref _selectedDiagnosticSeverityFilter, value))
            {
                RefreshDiagnosticsView();
            }
        }
    }

    public string SelectedDiagnosticDomainFilter
    {
        get => _selectedDiagnosticDomainFilter;
        set
        {
            value ??= "Все области";
            if (SetProperty(ref _selectedDiagnosticDomainFilter, value))
            {
                RefreshDiagnosticsView();
            }
        }
    }

    public bool ShowOnlyBlockingDiagnostics
    {
        get => _showOnlyBlockingDiagnostics;
        set
        {
            if (SetProperty(ref _showOnlyBlockingDiagnostics, value))
            {
                RefreshDiagnosticsView();
            }
        }
    }

    public DiagnosticCategoryViewModel? SelectedDiagnosticCategory
    {
        get => _selectedDiagnosticCategory;
        set
        {
            if (SetProperty(ref _selectedDiagnosticCategory, value))
            {
                OnPropertyChanged(nameof(SelectedDiagnosticCategoryTitle));
                OnPropertyChanged(nameof(HasActiveDiagnosticFilters));
                if (!_isUpdatingDiagnosticCategories)
                {
                    RefreshDiagnosticsView(rebuildCategories: false);
                }
            }
        }
    }

    public string SelectedDiagnosticCategoryTitle =>
        SelectedDiagnosticCategory?.Title ?? "Все проблемы";

    public ValidationMessageViewModel? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set
        {
            if (SetProperty(ref _selectedDiagnostic, value))
            {
                OnPropertyChanged(nameof(HasSelectedDiagnostic));
            }
        }
    }

    public bool HasSelectedDiagnostic => SelectedDiagnostic is not null;

    public int FilteredDiagnosticCount
    {
        get => _filteredDiagnosticCount;
        private set
        {
            if (SetProperty(ref _filteredDiagnosticCount, value))
            {
                OnPropertyChanged(nameof(DiagnosticFilterSummary));
            }
        }
    }

    public string DiagnosticFilterSummary => FilteredDiagnosticCount == DiagnosticCount
        ? $"Показано {DiagnosticCount:N0}"
        : $"Показано {FilteredDiagnosticCount:N0} из {DiagnosticCount:N0}";

    public string DiagnosticEmptyTitle => DiagnosticCount == 0
        ? "Проблем не обнаружено"
        : "По выбранным условиям ничего не найдено";

    public string DiagnosticEmptyHint => DiagnosticCount == 0
        ? "Перед использованием изменённой базы всё равно проверьте её в игре."
        : "Сбросьте фильтры или запустите проверку снова.";

    public bool HasActiveDiagnosticFilters =>
        !string.IsNullOrWhiteSpace(DiagnosticSearchText) ||
        SelectedDiagnosticSeverityFilter != "Все уровни" ||
        SelectedDiagnosticDomainFilter != "Все области" ||
        ShowOnlyBlockingDiagnostics ||
        SelectedDiagnosticCategory is { IsAll: false };

    public int FilteredRecordCount
    {
        get => _filteredRecordCount;
        private set => SetProperty(ref _filteredRecordCount, value);
    }

    public int FilteredTextResourceCount
    {
        get => _filteredTextResourceCount;
        private set => SetProperty(ref _filteredTextResourceCount, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? NotificationMessage
    {
        get => _notificationMessage;
        private set
        {
            if (SetProperty(ref _notificationMessage, value))
            {
                OnPropertyChanged(nameof(HasNotification));
                (DismissNotificationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasNotification => !string.IsNullOrWhiteSpace(NotificationMessage);

    public bool NotificationIsError
    {
        get => _notificationIsError;
        private set => SetProperty(ref _notificationIsError, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ApplyCurrentTableEditMode();
                RaiseCommandStates();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool IsOperationCancelable
    {
        get => _isOperationCancelable;
        private set
        {
            if (SetProperty(ref _isOperationCancelable, value))
            {
                (CancelOperationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value))
            {
                return;
            }

            ApplyCurrentTableEditMode();
            RaiseCommandStates();
        }
    }

    public bool HasDocument => _workspace is not null;

    public bool HasChanges => Changes.Count > 0;

    public bool HasPendingSoundEdit => PendingSoundEdit is not null;

    public bool HasUnsavedWork => HasChanges || HasPendingSoundEdit;

    public PendingSoundEditViewModel? PendingSoundEdit
    {
        get => _pendingSoundEdit;
        private set
        {
            if (!SetProperty(ref _pendingSoundEdit, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPendingSoundEdit));
            OnPropertyChanged(nameof(HasUnsavedWork));
            (CancelPendingSoundEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SavePendingSoundCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SavePendingSoundCurrentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SourcePath => _workspace?.SourcePath ?? "Файл не открыт";

    public string SourceFileName => _workspace is null ? "database.res не открыт" : Path.GetFileName(_workspace.SourcePath);

    public string SourceDirectory => _workspace is null ? "Выберите файл вручную — путь к игре не открывается автоматически." : Path.GetDirectoryName(_workspace.SourcePath) ?? string.Empty;

    public long FileLength => _workspace?.FileLength ?? 0;

    public string Sha256 => _workspace?.Sha256 ?? "—";

    public string ShortSha256 => _workspace is null ? "—" : $"{_workspace.Sha256[..Math.Min(12, _workspace.Sha256.Length)]}…";

    public bool HasTextsDocument => !string.IsNullOrWhiteSpace(_workspace?.TextsPath);

    public string TextsFileName => _workspace?.TextsPath is null
        ? "texts.res не подключён"
        : Path.GetFileName(_workspace.TextsPath);

    public string TextsPath => _workspace?.TextsPath ?? "Рядом с database.res файл texts.res не найден.";

    public int TextResourceCount => TextResources.Count;

    public string TextsStatusText => HasTextsDocument
        ? $"Подключён {TextsFileName} · {TextResourceCount:N0} текстов"
        : "texts.res не подключён";

    public string AttachTextsButtonText => HasTextsDocument
        ? $"✓ {TextsFileName} · {TextResourceCount:N0}"
        : "＋ Подключить texts.res…";

    public bool HasSoundArchives => (_workspace?.SoundArchives.Count ?? 0) > 0;

    public int SoundArchiveCount => _workspace?.SoundArchives.Count ?? 0;

    public int SoundResourceCount => _workspace?.SoundResourceCount ?? 0;

    public string SoundArchivePaths => HasSoundArchives
        ? string.Join(Environment.NewLine, _workspace!.SoundArchives.Select(archive => archive.SourcePath))
        : "Подключите speech.res, sfx.res или другой RES со звуками.";

    public string AttachSoundsButtonText => HasSoundArchives
        ? $"♪ Звук · {SoundArchiveCount:N0} RES · {SoundResourceCount:N0} файлов"
        : "♪ Подключить звуковые RES…";

    public IReadOnlyList<ManagedBackupViewModel> ManagedBackups => _workspace?.ManagedBackups ?? [];

    public int ResourceCount => Resources.Count;

    public int DatabaseCount => Databases.Count;

    public int TableCount => Databases.Sum(group => group.TableCount);

    public int RecordCount => Databases.Sum(group => group.RecordCount);

    public int EditableTableCount => Databases.SelectMany(group => group.Tables).Count(table => table.IsEditable);

    public int ReadOnlyTableCount => Databases.SelectMany(group => group.Tables).Count(table => table.IsReadOnly);

    public int UnitCount => Databases.FirstOrDefault(group => group.Key == "units")?.RecordCount ?? 0;

    public int SpellCount => Databases.FirstOrDefault(group => group.Key == "spells")?.RecordCount ?? 0;

    public int ChangeCount => Changes.Count;

    public int DiagnosticCount => Diagnostics.Count;

    public int ErrorCount => Diagnostics.Count(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase) || item.Severity.Equals("Ошибка", StringComparison.OrdinalIgnoreCase));

    public int WarningCount => Diagnostics.Count(item => item.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) || item.Severity.Equals("Предупреждение", StringComparison.OrdinalIgnoreCase));

    public string? LastSavedPath
    {
        get => _lastSavedPath;
        private set => SetProperty(ref _lastSavedPath, value);
    }

    public async Task OpenPathAsync(string path)
    {
        CancelValidation();
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = true;
        BusyText = "Чтение и проверка RES-контейнера…";
        NotificationMessage = null;
        StatusText = "Открытие файла…";

        try
        {
            var workspace = await _service.OpenAsync(path, cancellationToken);
            ApplyWorkspace(workspace);
            StatusText = $"Открыто безопасно: {Path.GetFileName(workspace.SourcePath)} · {DatabaseCount} БД · {TableCount} таблиц · {RecordCount:N0} записей";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Операция отменена.";
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось открыть database.res: {UnwrapMessage(exception)}");
            StatusText = "Ошибка открытия.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    internal async Task<DatabaseComparisonResult?> ComparePathAsync(string path)
    {
        if (_workspace is null || IsBusy)
        {
            return null;
        }

        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = true;
        BusyText = "Чтение второй базы и построение отчёта…";
        NotificationMessage = null;
        StatusText = "Сравнение database.res…";

        try
        {
            DatabaseComparisonResult result = await _service.CompareAsync(path, cancellationToken);
            StatusText = result.AreEquivalent
                ? "Сравнение завершено: распознанные таблицы совпадают."
                : $"Сравнение завершено: {result.Differences.Count:N0} строк отчёта.";
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Сравнение отменено.";
            return null;
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось сравнить базы: {UnwrapMessage(exception)}");
            StatusText = "Ошибка сравнения.";
            return null;
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    public async Task AttachTextsPathAsync(string path)
    {
        if (_workspace is null || HasUnsavedWork || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "Подключение и индексация texts.res…";
        NotificationMessage = null;
        try
        {
            WorkspaceSnapshot workspace = await _service.AttachTextsAsync(path, CancellationToken.None);
            ApplyWorkspace(workspace);
            NotificationIsError = false;
            NotificationMessage = $"Подключён {Path.GetFileName(workspace.TextsPath)} · {workspace.TextResourceCount:N0} текстов.";
            StatusText = $"Связанный texts.res открыт: {workspace.TextsPath}";
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось подключить texts.res: {UnwrapMessage(exception)}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    public async Task AttachSoundPathsAsync(IReadOnlyList<string> paths)
    {
        if (_workspace is null || HasUnsavedWork || IsBusy || paths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        BusyText = "Чтение каталогов звуковых RES…";
        NotificationMessage = null;
        try
        {
            WorkspaceSnapshot workspace = await _service.AttachSoundsAsync(paths, CancellationToken.None);
            ApplyWorkspace(workspace);
            NotificationIsError = false;
            NotificationMessage =
                $"Подключено звуковых RES: {workspace.SoundArchives.Count:N0}; найдено WAV/MP3/OGG: {workspace.SoundResourceCount:N0}.";
            StatusText = "Связи реплик и брифингов со звуком обновлены.";
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось подключить звуковой RES: {UnwrapMessage(exception)}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    public async Task ImportBriefingAudioPathAsync(string path)
    {
        RecordRowViewModel? record = SelectedRecord;
        BriefingPhraseViewModel? phrase = SelectedBriefingPhrase;
        if (record is null || phrase?.IsPhrase is not true || IsBusy)
        {
            return;
        }

        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = true;
        BusyText = "Проверка MP3/WAV и подготовка пути внутри RES…";
        NotificationMessage = null;
        try
        {
            PendingSoundEditViewModel prepared = await _service.PrepareBriefingSoundImportAsync(
                record,
                phrase,
                path,
                cancellationToken);
            PendingSoundEditViewModel? previous = PendingSoundEdit;
            PendingSoundEdit = prepared;
            _undo.Push(new SoundStageEditFrame(previous, prepared));
            _redo.Clear();
            NotificationIsError = false;
            NotificationMessage = prepared.IsReplacement
                ? $"Подготовлена замена {prepared.ResourceName}. Исходный {prepared.ArchiveName} пока не изменён."
                : $"Подготовлен новый ресурс {prepared.ResourceName}. Исходный {prepared.ArchiveName} пока не изменён.";
            StatusText = "Звук находится только в памяти. Сохраните текущий RES с резервной копией, создайте отдельную копию или отмените подготовку.";
            OnPropertyChanged(nameof(HasUnsavedWork));
            RaiseCommandStates();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Импорт звука отменён.";
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось подготовить звук: {UnwrapMessage(exception)}");
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    public async Task SavePendingSoundCopyPathAsync(string targetPath)
    {
        PendingSoundEditViewModel? edit = PendingSoundEdit;
        if (edit is null || IsBusy)
        {
            return;
        }

        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = "Потоковая сборка и повторная проверка звукового RES…";
        NotificationMessage = null;
        try
        {
            SoundArchiveCopyResult result = await _service.SaveSoundCopyAsync(
                edit,
                targetPath,
                cancellationToken);
            PendingSoundEdit = null;
            LastSavedPath = result.TargetPath;
            NotificationIsError = false;
            NotificationMessage = result.IsReplacement
                ? $"Создана проверенная копия {Path.GetFileName(result.TargetPath)}; аудио заменено только в копии."
                : $"Создана проверенная копия {Path.GetFileName(result.TargetPath)}; новое аудио добавлено только в копию.";
            StatusText = $"Копия звукового RES сохранена: {result.TargetPath}";
            RemoveSoundFramesFromHistory();
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            ShowError($"Копия звукового RES не сохранена: {UnwrapMessage(exception)}");
            StatusText = "Ошибка сохранения звукового RES.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    public async Task SavePendingSoundCurrentAsync(bool allowGameInstallationPath)
    {
        PendingSoundEditViewModel? edit = PendingSoundEdit;
        RecordRowViewModel? selectedRecord = SelectedRecord;
        int selectedPhraseNumber = SelectedBriefingPhrase?.Number ?? edit?.PhraseNumber ?? -1;
        if (edit is null || IsBusy)
        {
            return;
        }

        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = $"Проверенная сборка и безопасная замена {edit.ArchiveName}…";
        NotificationMessage = null;
        try
        {
            SaveSoundCurrentResult result = await _service.SaveSoundCurrentAsync(
                edit,
                allowGameInstallationPath,
                cancellationToken);
            if (_workspace is not null)
            {
                _workspace = new WorkspaceSnapshot
                {
                    SourcePath = _workspace.SourcePath,
                    FileLength = _workspace.FileLength,
                    Sha256 = _workspace.Sha256,
                    Resources = _workspace.Resources,
                    Databases = _workspace.Databases,
                    Diagnostics = _workspace.Diagnostics,
                    TextsPath = _workspace.TextsPath,
                    TextsFileLength = _workspace.TextsFileLength,
                    TextsSha256 = _workspace.TextsSha256,
                    TextResourceCount = _workspace.TextResourceCount,
                    TextResources = _workspace.TextResources,
                    SoundArchives = result.SoundArchives,
                    SoundResourceCount = result.SoundResourceCount,
                    ManagedBackups = result.ManagedBackups,
                };
                OnPropertyChanged(nameof(SoundArchiveCount));
                OnPropertyChanged(nameof(SoundResourceCount));
                OnPropertyChanged(nameof(SoundArchivePaths));
                OnPropertyChanged(nameof(AttachSoundsButtonText));
                OnPropertyChanged(nameof(ManagedBackups));
            }

            PendingSoundEdit = null;
            if (selectedRecord is not null)
            {
                SelectedBriefingPhrase = selectedRecord.BriefingPhrases.FirstOrDefault(phrase =>
                    phrase.IsPhrase && phrase.Number == selectedPhraseNumber);
            }

            LastSavedPath = result.Save.TargetPath;
            NotificationIsError = false;
            NotificationMessage = result.Save.IsReplacement
                ? $"{edit.ArchiveName} сохранён и проверен; аудио заменено. Резервная копия: {Path.GetFileName(result.Save.BackupPath)}"
                : $"{edit.ArchiveName} сохранён и проверен; новое аудио добавлено. Резервная копия: {Path.GetFileName(result.Save.BackupPath)}";
            StatusText = $"Звуковой RES сохранён с резервной копией: {result.Save.TargetPath}";
            RemoveSoundFramesFromHistory();
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            ShowError($"Текущий звуковой RES не сохранён: {UnwrapMessage(exception)}");
            StatusText = "Ошибка сохранения звукового RES. Подготовленный звук сохранён в памяти.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void CancelPendingSoundEdit()
    {
        PendingSoundEditViewModel? previous = PendingSoundEdit;
        if (previous is null)
        {
            return;
        }

        PendingSoundEdit = null;
        _undo.Push(new SoundStageEditFrame(previous, null));
        _redo.Clear();
        NotificationIsError = false;
        NotificationMessage = "Подготовка звукового RES отменена. Исходный архив не изменялся.";
        StatusText = "Импорт звука отменён.";
        RaiseCommandStates();
    }

    private void RemoveSoundFramesFromHistory()
    {
        RemoveSoundFrames(_undo);
        RemoveSoundFrames(_redo);
    }

    private static void RemoveSoundFrames(Stack<WorkspaceEditFrame> stack)
    {
        WorkspaceEditFrame[] retained = stack
            .Where(frame => frame is not SoundStageEditFrame)
            .Reverse()
            .ToArray();
        stack.Clear();
        foreach (WorkspaceEditFrame frame in retained)
        {
            stack.Push(frame);
        }
    }

    public async Task SaveCopyPathAsync(string targetPath)
    {
        if (_workspace is null)
        {
            return;
        }

        var fullTarget = Path.GetFullPath(targetPath);
        if (string.Equals(fullTarget, Path.GetFullPath(_workspace.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Исходный файл заблокирован. Приложение сохраняет только новую копию.");
            return;
        }

        if (File.Exists(fullTarget))
        {
            ShowError("Файл назначения уже существует. Для защиты данных перезапись запрещена — выберите новое имя.");
            return;
        }

        CancelValidation();
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = "Атомарная сборка и побайтовая проверка; после старта транзакция не прерывается…";
        NotificationMessage = null;
        StatusText = "Сохранение проверяемой копии…";

        try
        {
            var result = await _service.SaveCopyAsync(fullTarget, cancellationToken);
            LastSavedPath = result.Database.TargetPath;
            NotificationIsError = false;
            NotificationMessage = result.Texts is null
                ? $"Копия создана и проверена: {Path.GetFileName(result.Database.TargetPath)}."
                : $"Связанная пара создана и проверена: {Path.GetFileName(result.Database.TargetPath)} + {Path.GetFileName(result.Texts.TargetPath)}.";
            StatusText = $"Копия сохранена: {result.Database.TargetPath}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Сохранение отменено.";
        }
        catch (Exception exception)
        {
            ShowError($"Копия не сохранена: {UnwrapMessage(exception)}");
            StatusText = "Ошибка сохранения.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    internal async Task<SavePairCopyResult?> SaveRecoveryCopyPathAsync(string targetPath)
    {
        if (_workspace is null || !HasUnsavedWork || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = "Создание аварийной копии…";
        try
        {
            SavePairCopyResult result = await _service.SaveCopyAsync(
                Path.GetFullPath(targetPath),
                CancellationToken.None);
            StatusText = $"Аварийная копия обновлена: {DateTime.Now:HH:mm:ss}";
            return result;
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось создать аварийную копию: {UnwrapMessage(exception)}");
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    internal void ReportRecoveryOpened(RecoverySnapshot snapshot)
    {
        NotificationIsError = false;
        NotificationMessage = snapshot.PendingSoundAssetPath is null
            ? "Открыта аварийная копия database.res + texts.res. Сохраните её под нужным именем после проверки."
            : "Открыта аварийная копия database.res + texts.res; импортированный звук также сохранён рядом с копией.";
        StatusText = $"Восстановлена аварийная копия от {snapshot.CreatedUtc.ToLocalTime():g}.";
    }

    internal async Task<bool> RestorePendingSoundRecoveryAsync(RecoverySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.PendingSoundAssetPath))
        {
            return true;
        }

        if (!File.Exists(snapshot.PendingSoundAssetPath) ||
            string.IsNullOrWhiteSpace(snapshot.PendingSoundArchivePath) ||
            !File.Exists(snapshot.PendingSoundArchivePath) ||
            string.IsNullOrWhiteSpace(snapshot.PendingSoundSceneKey) ||
            snapshot.PendingSoundPhraseNumber is not int phraseNumber)
        {
            return false;
        }

        await AttachSoundPathsAsync([snapshot.PendingSoundArchivePath]);
        DatabaseTableViewModel? table = Databases
            .SelectMany(database => database.Tables)
            .FirstOrDefault(candidate => candidate.Key.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase));
        RecordRowViewModel? record = table?.Records.FirstOrDefault(candidate =>
            candidate.IdentityValue.Equals(snapshot.PendingSoundSceneKey, StringComparison.OrdinalIgnoreCase) ||
            candidate.DisplayName.Equals(snapshot.PendingSoundSceneKey, StringComparison.OrdinalIgnoreCase));
        BriefingPhraseViewModel? phrase = record?.BriefingPhrases.FirstOrDefault(candidate =>
            candidate.IsPhrase && candidate.Number == phraseNumber);
        if (table is null || record is null || phrase is null)
        {
            return false;
        }

        SelectTableCommand.Execute(table);
        SelectedRecord = record;
        SelectedBriefingPhrase = phrase;
        await ImportBriefingAudioPathAsync(snapshot.PendingSoundAssetPath);
        return PendingSoundEdit is not null;
    }

    public async Task SaveCurrentAsync(bool allowGameInstallationPath)
    {
        if (_workspace is null || !HasChanges || IsBusy)
        {
            return;
        }

        var selectedTableKey = SelectedTable?.Key;
        var selectedRecordIndex = SelectedRecord?.SourceRecordIndex;
        CancelValidation();
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = "Проверка, резервная копия и атомарное сохранение…";
        NotificationMessage = null;
        StatusText = "Сохранение database.res…";

        try
        {
            var result = await _service.SaveCurrentAsync(allowGameInstallationPath, cancellationToken);
            ApplyWorkspace(result.Workspace);
            if (selectedTableKey is not null)
            {
                var table = Databases.SelectMany(database => database.Tables)
                    .FirstOrDefault(candidate => candidate.Key == selectedTableKey);
                if (table is not null)
                {
                    SelectTable(table);
                    if (selectedRecordIndex is int recordIndex)
                    {
                        SelectedRecord = table.Records.FirstOrDefault(
                            record => record.SourceRecordIndex == recordIndex);
                    }
                }
            }

            LastSavedPath = result.Save.TargetPath;
            NotificationIsError = false;
            var backups = new[] { result.BackupPath, result.TextsBackupPath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFileName(path))
                .ToArray();
            NotificationMessage = result.TextsSave is null
                ? $"Сохранено и проверено: {Path.GetFileName(result.Save.TargetPath)}. Резервная копия: {string.Join(", ", backups)}"
                : $"Сохранена и проверена связанная пара database.res + texts.res. Резервные копии: {string.Join(", ", backups)}";
            StatusText = $"Сохранено: {result.Save.TargetPath}";
        }
        catch (Exception exception)
        {
            ShowError($"Файл не сохранён: {UnwrapMessage(exception)}");
            StatusText = "Ошибка сохранения.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    public async Task RestoreManagedBackupAsync(
        ManagedBackupViewModel backup,
        bool allowGameInstallationPath)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (_workspace is null || !backup.HasBackup || HasUnsavedWork || IsBusy)
        {
            return;
        }

        CancelValidation();
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsOperationCancelable = false;
        BusyText = $"Проверка и восстановление {backup.FileName}…";
        NotificationMessage = null;
        StatusText = $"Восстановление {backup.FileName} из резервной копии…";
        try
        {
            RestoreManagedBackupResult result = await _service.RestoreManagedBackupAsync(
                backup,
                allowGameInstallationPath,
                cancellationToken);
            ApplyWorkspace(result.Workspace);
            LastSavedPath = result.Restore.TargetPath;
            NotificationIsError = false;
            NotificationMessage =
                $"{backup.FileName} восстановлен и проверен. Состояние до отката сохранено в " +
                $"{Path.GetFileName(result.Restore.CurrentStateBackupPath)}.";
            StatusText = $"Восстановлено из резервной копии: {result.Restore.RestoredFromPath}";
        }
        catch (Exception exception)
        {
            ShowError($"Резервная копия не восстановлена: {UnwrapMessage(exception)}");
            StatusText = "Ошибка восстановления резервной копии.";
        }
        finally
        {
            IsOperationCancelable = false;
            IsBusy = false;
            BusyText = string.Empty;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        CancelValidation();
        ApplyTableEditMode(SelectedTable, false);
        UnsubscribeWorkspaceRecords(Databases);
        UnsubscribeWorkspaceTexts(TextResources);
    }

    private void ApplyWorkspace(WorkspaceSnapshot workspace)
    {
        ApplyTableEditMode(SelectedTable, false);
        UnsubscribeWorkspaceRecords(Databases);
        UnsubscribeWorkspaceTexts(TextResources);
        _workspace = workspace;
        IsEditMode = true;
        _undo.Clear();
        _redo.Clear();
        PendingSoundEdit = null;
        _changesByPath.Clear();
        Changes.Clear();
        LastSavedPath = null;
        SearchText = string.Empty;
        ResourceSearchText = string.Empty;
        TextsSearchText = string.Empty;
        ClearDiagnosticFilters();

        ReplaceCollection(Resources, workspace.Resources);
        ReplaceCollection(Databases, workspace.Databases);
        ReplaceCollection(TextResources, workspace.TextResources);
        ReplaceCollection(Diagnostics, workspace.Diagnostics);
        RefreshDiagnosticsView();
        SubscribeWorkspaceRecords(Databases);
        SubscribeWorkspaceTexts(TextResources);
        ApplyDiagnosticsToRecords(Diagnostics);

        ResourcesView = CollectionViewSource.GetDefaultView(Resources);
        ResourcesView.Filter = FilterResource;
        TextsView = CollectionViewSource.GetDefaultView(TextResources);
        TextsView.Filter = FilterTextResource;
        foreach (var group in Databases)
        {
            group.IsExpanded = false;
        }

        CurrentSection = WorkspaceSection.Overview;
        SelectedTable = null;
        SelectedRecord = null;
        RecordsView = CollectionViewSource.GetDefaultView(Array.Empty<RecordRowViewModel>());
        FilteredRecordCount = 0;
        RefreshTextsFilter();
        SetNavigationEnabled(true);
        RefreshAllCounts();
        NotifyWorkspaceProperties();
        RaiseCommandStates();
    }

    private void ToggleColumnMode()
    {
        if (SelectedTable is null)
        {
            return;
        }

        ShowAllColumns = !ShowAllColumns;
        StatusText = ShowAllColumns
            ? $"Показаны технические исходные поля и их расшифровки: {SelectedTable.Columns.Count:N0} столбцов."
            : $"Показаны понятные значения без дублирующих raw-полей: {VisibleSpreadsheetColumns.Count:N0} из {SelectedTable.Columns.Count:N0} столбцов.";
    }

    private static IReadOnlyList<SpreadsheetColumnViewModel> BuildReadableColumns(
        IReadOnlyList<SpreadsheetColumnViewModel> columns)
    {
        SpreadsheetColumnViewModel[] readableCandidates = columns
            .Where(column => column.ShowInReadableView)
            .ToArray();
        var fieldsWithSpecificProjections = readableCandidates
            .Where(column => column.Projection is not null &&
                             column.Projection.Kind != DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();
        var fieldsWithDecodedProjection = readableCandidates
            .Where(column => column.Projection?.Kind == DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();
        var fieldsWithEditableDecodedProjection = readableCandidates
            .Where(column => column.Projection?.Kind == DatabaseFieldProjectionKind.DecodedValue &&
                             (column.IsEditable || column.Kind == "StructuredListDecoded"))
            .Select(column => column.FieldId)
            .ToHashSet();

        return readableCandidates
            .Where(column => column.Projection switch
            {
                null => !fieldsWithSpecificProjections.Contains(column.FieldId) &&
                        (!fieldsWithDecodedProjection.Contains(column.FieldId) ||
                         !fieldsWithEditableDecodedProjection.Contains(column.FieldId)),
                { Kind: DatabaseFieldProjectionKind.DecodedValue } =>
                    !fieldsWithSpecificProjections.Contains(column.FieldId),
                _ => true,
            })
            .ToArray();
    }

    private void Navigate(NavigationItemViewModel? item)
    {
        if (item?.IsEnabled is not true)
        {
            return;
        }

        CurrentSection = item.Section;
        if (item.Section == WorkspaceSection.Texts)
        {
            RefreshTextsFilter();
            StatusText = $"{TextsFileName} · {FilteredTextResourceCount:N0} из {TextResourceCount:N0} ресурсов.";
        }
    }

    private void SelectTable(DatabaseTableViewModel? table)
    {
        if (table?.IsEnabled is not true)
        {
            return;
        }

        var group = Databases.FirstOrDefault(candidate => candidate.Key == table.GroupKey);
        if (group is not null)
        {
            group.IsExpanded = true;
        }

        ApplyTableEditMode(SelectedTable, false);
        SearchText = string.Empty;
        ShowAllColumns = false;
        SelectedRecord = null;
        SelectedTable = table;
        CurrentSection = WorkspaceSection.Records;
        ApplyCurrentTableEditMode();
        RecordsView = CollectionViewSource.GetDefaultView(table.Records);
        RecordsView.Filter = FilterRecord;
        RefreshRecordFilter();
        StatusText = $"{table.ResourceName} · {table.Title} · {table.RecordCount:N0} записей · {table.StatusText.ToLowerInvariant()}";
    }

    private void AppendSelectedRecord()
    {
        DatabaseTableViewModel? table = SelectedTable;
        RecordRowViewModel? template = SelectedRecord;
        if (table is null || !IsEditMode || !table.CanAppendRecords)
        {
            return;
        }

        if (template is null)
        {
            ShowError(
                "Сначала выберите в таблице строку, наиболее похожую на новую. " +
                "Затем нажмите «Новая строка»: редактор безопасно скопирует её структуру в конец таблицы.");
            return;
        }

        try
        {
            SearchText = string.Empty;
            RecordRowViewModel record = _service.AppendRecordClone(table, template);
            record.FieldValueCommitted += OnFieldValueCommitted;
            record.IsEditMode = true;
            table.AppendRecord(record);
            AddRecordInsertionChange(record);
            _undo.Push(new RecordPresenceEditFrame(table, record, BeforePresent: false, AfterPresent: true));
            _redo.Clear();
            RecordsView.Refresh();
            FilteredRecordCount = RecordsView.Cast<object>().Count();
            SelectedRecord = record;
            RecordFocusRequested?.Invoke(
                this,
                new RecordFocusRequestedEventArgs(record, record.IdentityFieldId, beginEdit: true));
            RefreshAllCounts();
            OnPropertyChanged(nameof(CurrentTableRecordCount));
            OnPropertyChanged(nameof(HasChanges));
            RefreshValidationDeferred();
            StatusText = $"Добавлена строка по образцу «{template.DisplayName}». " +
                         $"Измените уникальный ключ в поле {record.GetCell(record.IdentityFieldId)?.DisplayName ?? "идентификатора"}; " +
                         "до этого сохранение заблокировано.";
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            ShowError(UnwrapMessage(exception));
        }
    }

    private void AddMissingTexts()
    {
        if (SelectedRecord is not { } record || !HasTextsDocument || IsBusy)
        {
            return;
        }

        try
        {
            IReadOnlyList<LocalizationEntryViewModel> added = _service.AddMissingTexts(record);
            RegisterAddedTexts(added);

            _undo.Push(new TextPresenceBatchEditFrame(added, BeforePresent: false, AfterPresent: true));
            _redo.Clear();
            StatusText = $"Добавлено записей texts.res: {added.Count:N0}. Заполните русские названия и описания.";
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            ShowError(UnwrapMessage(exception));
        }
    }

    private void BeginNewBriefingPhrase()
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        if (text is null)
        {
            ShowError("Для выбранного брифинга не найден ресурс BRIEFING в texts.res. Сначала создайте отсутствующий текст.");
            return;
        }

        _newBriefingInsertAfterOrdinal = GetBriefingInsertionOrdinal(text.Description);
        _isNewBriefingPhrase = true;
        _selectedBriefingPhrase = null;
        OnPropertyChanged(nameof(SelectedBriefingPhrase));
        BriefingDraftNumber = Math.Max(
            BriefingTranscriptParser.GetNextPhraseNumber(text.Description),
            (SelectedRecord?.BriefingPhrases
                 .Where(briefingEvent => briefingEvent.IsPhrase)
                 .Select(phrase => phrase.Number)
                 .DefaultIfEmpty(0)
                 .Max() ?? 0) + 1);
        BriefingDraftSpeaker = SelectedRecord?.BriefingPhrases.LastOrDefault(briefingEvent => briefingEvent.IsPhrase)?.Speaker is { Length: > 0 } speaker
            ? speaker
            : "Hero";
        BriefingDraftText = string.Empty;
        BriefingDraftArguments = string.Empty;
        OnPropertyChanged(nameof(IsNewBriefingPhrase));
        OnPropertyChanged(nameof(IsPhraseBriefingEditor));
        OnPropertyChanged(nameof(IsCommandBriefingEditor));
        OnPropertyChanged(nameof(BriefingEditorTitle));
        RaiseBriefingCommandStates();
        StatusText = $"Подготовлена новая реплика № {BriefingDraftNumber:N0}. Укажите персонажа и текст, затем нажмите «Применить».";
    }

    private void ApplyBriefingPhrase()
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        if (text is null)
        {
            ShowError("Ресурс BRIEFING в texts.res не найден.");
            return;
        }

        try
        {
            int number = BriefingDraftNumber;
            bool isNewPhrase = IsNewBriefingPhrase;
            BriefingPhraseViewModel? selected = SelectedBriefingPhrase;
            string updated = isNewPhrase
                ? BriefingTranscriptParser.InsertTimelineEvent(
                    text.Description,
                    _newBriefingInsertAfterOrdinal,
                    BriefingEventKind.Phrase,
                    phraseNumber: number,
                    speaker: BriefingDraftSpeaker,
                    text: BriefingDraftText)
                : BriefingTranscriptParser.UpdateTimelineEvent(
                    text.Description,
                    selected?.EventOrdinal ?? throw new InvalidOperationException("Выберите событие брифинга."),
                    BriefingDraftArguments,
                    number,
                    BriefingDraftSpeaker,
                    BriefingDraftText);
            if (isNewPhrase)
            {
                _pendingBriefingEventOrdinal = _newBriefingInsertAfterOrdinal is int afterOrdinal
                    ? afterOrdinal + 1
                    : BriefingTranscriptParser.ParseTimeline(updated).Count - 1;
            }
            else
            {
                _pendingBriefingEventOrdinal = selected!.EventOrdinal;
            }

            text.Description = updated;
            if (text.HasError)
            {
                _pendingBriefingPhraseNumber = null;
                _pendingBriefingEventOrdinal = null;
                _newBriefingInsertAfterOrdinal = null;
                ShowError(text.ErrorMessage ?? "Не удалось обновить реплику.");
                return;
            }

            _isNewBriefingPhrase = false;
            _newBriefingInsertAfterOrdinal = null;
            OnPropertyChanged(nameof(IsNewBriefingPhrase));
            OnPropertyChanged(nameof(IsPhraseBriefingEditor));
            OnPropertyChanged(nameof(IsCommandBriefingEditor));
            OnPropertyChanged(nameof(BriefingEditorTitle));
            StatusText = selected?.IsCommand is true
                ? $"Событие «{selected.DisplayTitle}» записано в {text.ResourceName}."
                : $"Реплика № {number:N0} записана в {text.ResourceName}.";
            RaiseBriefingCommandStates();
        }
        catch (Exception exception)
        {
            _pendingBriefingPhraseNumber = null;
            _pendingBriefingEventOrdinal = null;
            _newBriefingInsertAfterOrdinal = null;
            ShowError(UnwrapMessage(exception));
        }
    }

    private bool CanApplyBriefingDraft()
    {
        if (!IsBriefingsTable || SelectedRecord is null || IsBusy ||
            (!IsNewBriefingPhrase && SelectedBriefingPhrase?.IsTimelineEvent is not true))
        {
            return false;
        }

        if (IsNewBriefingPhrase || SelectedBriefingPhrase?.IsPhrase is true)
        {
            return BriefingDraftNumber > 0 &&
                   !string.IsNullOrWhiteSpace(BriefingDraftSpeaker) &&
                   !string.IsNullOrWhiteSpace(BriefingDraftText);
        }

        BriefingPhraseViewModel? selected = SelectedBriefingPhrase;
        if (selected is null)
        {
            return false;
        }

        try
        {
            _ = BriefingTranscriptParser.UpdateTimelineEvent(
                $"#{selected.Directive} {selected.Arguments}".TrimEnd(),
                0,
                BriefingDraftArguments,
                0,
                string.Empty,
                string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void AddBriefingEvent(string? kindName)
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        if (text is null || !TryParseBriefingEventKind(kindName, out BriefingEventKind kind))
        {
            ShowError("Для выбранного брифинга не найден связанный текст или неизвестен тип нового события.");
            return;
        }

        int? afterOrdinal = GetBriefingInsertionOrdinal(text.Description);
        try
        {
            ShowOnlyBriefingProblems = false;
            string arguments = kind switch
            {
                BriefingEventKind.Camera => "1",
                BriefingEventKind.Animation => "0",
                BriefingEventKind.Show => "Hero 0",
                BriefingEventKind.Hide => "Hero",
                BriefingEventKind.Description => "+",
                _ => string.Empty,
            };
            string updated = BriefingTranscriptParser.InsertTimelineEvent(
                text.Description,
                afterOrdinal,
                kind,
                arguments);
            _pendingBriefingEventOrdinal = afterOrdinal is int selectedOrdinal
                ? selectedOrdinal + 1
                : BriefingTranscriptParser.ParseTimeline(updated).Count - 1;
            text.Description = updated;
            if (text.HasError)
            {
                _pendingBriefingEventOrdinal = null;
                ShowError(text.ErrorMessage ?? "Не удалось добавить событие.");
                return;
            }

            StatusText = $"Добавлено событие «{GetBriefingEventKindTitle(kind)}» после выбранного шага. При необходимости измените параметры справа.";
            RaiseBriefingCommandStates();
        }
        catch (Exception exception)
        {
            _pendingBriefingEventOrdinal = null;
            ShowError(UnwrapMessage(exception));
        }
    }

    private void DeleteBriefingEvent()
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        BriefingPhraseViewModel? selected = SelectedBriefingPhrase;
        if (text is null || selected is null)
        {
            return;
        }

        try
        {
            int eventCount = BriefingTranscriptParser.ParseTimeline(text.Description).Count;
            int nextOrdinal = Math.Min(selected.EventOrdinal, eventCount - 2);
            _pendingBriefingEventOrdinal = nextOrdinal >= 0 ? nextOrdinal : null;
            string updated = BriefingTranscriptParser.RemoveTimelineEvent(
                text.Description,
                selected.EventOrdinal);
            text.Description = updated;
            if (text.HasError)
            {
                _pendingBriefingEventOrdinal = null;
                ShowError(text.ErrorMessage ?? "Не удалось удалить событие.");
                return;
            }

            StatusText = $"Удалено событие «{selected.DisplayTitle}». Ctrl+Z вернёт его вместе с исходным текстом.";
            RaiseBriefingCommandStates();
        }
        catch (Exception exception)
        {
            _pendingBriefingEventOrdinal = null;
            ShowError(UnwrapMessage(exception));
        }
    }

    private void DuplicateBriefingEvent()
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        BriefingPhraseViewModel? selected = SelectedBriefingPhrase;
        if (text is null || selected?.IsTimelineEvent is not true)
        {
            return;
        }

        try
        {
            string updated = BriefingTranscriptParser.DuplicateTimelineEvent(
                text.Description,
                selected.EventOrdinal);
            _pendingBriefingEventOrdinal = selected.EventOrdinal + 1;
            text.Description = updated;
            if (text.HasError)
            {
                _pendingBriefingEventOrdinal = null;
                ShowError(text.ErrorMessage ?? "Не удалось дублировать событие.");
                return;
            }

            StatusText = selected.IsPhrase
                ? "Реплика продублирована со следующим свободным номером. Ctrl+Z отменит действие."
                : $"Событие «{selected.DisplayTitle}» продублировано сразу после исходного.";
            RaiseBriefingCommandStates();
        }
        catch (Exception exception)
        {
            _pendingBriefingEventOrdinal = null;
            ShowError(UnwrapMessage(exception));
        }
    }

    private bool CanReorderBriefingEvent(int delta)
    {
        int ordinal = SelectedBriefingPhrase?.EventOrdinal ?? -1;
        int count = SelectedRecord?.BriefingEventCount ?? 0;
        int target = ordinal + delta;
        return IsBriefingsTable && !IsNewBriefingPhrase && !IsBusy &&
               SelectedBriefingPhrase?.IsTimelineEvent is true &&
               ordinal >= 0 && target >= 0 && target < count;
    }

    private void ReorderBriefingEvent(int delta)
    {
        LocalizationEntryViewModel? text = GetSelectedBriefingText();
        BriefingPhraseViewModel? selected = SelectedBriefingPhrase;
        if (text is null || selected is null)
        {
            return;
        }

        try
        {
            int targetOrdinal = selected.EventOrdinal + delta;
            string updated = BriefingTranscriptParser.MoveTimelineEvent(
                text.Description,
                selected.EventOrdinal,
                delta);
            _pendingBriefingEventOrdinal = targetOrdinal;
            text.Description = updated;
            if (text.HasError)
            {
                _pendingBriefingEventOrdinal = null;
                ShowError(text.ErrorMessage ?? "Не удалось переместить событие.");
                return;
            }

            StatusText = delta < 0
                ? $"Событие «{selected.DisplayTitle}» перемещено на один шаг выше."
                : $"Событие «{selected.DisplayTitle}» перемещено на один шаг ниже.";
            RaiseBriefingCommandStates();
        }
        catch (Exception exception)
        {
            _pendingBriefingEventOrdinal = null;
            ShowError(UnwrapMessage(exception));
        }
    }

    private static bool TryParseBriefingEventKind(string? value, out BriefingEventKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind) &&
        kind is BriefingEventKind.Camera or BriefingEventKind.Animation or
            BriefingEventKind.Thought or BriefingEventKind.Show or
            BriefingEventKind.Hide or BriefingEventKind.Description;

    private static string GetBriefingEventKindTitle(BriefingEventKind kind) => kind switch
    {
        BriefingEventKind.Camera => "Камера",
        BriefingEventKind.Animation => "Анимация",
        BriefingEventKind.Thought => "Мысль",
        BriefingEventKind.Show => "Появление участника",
        BriefingEventKind.Hide => "Скрытие участника",
        BriefingEventKind.Description => "Режиссёрская пометка",
        _ => "Событие",
    };

    private LocalizationEntryViewModel? GetSelectedBriefingText() =>
        SelectedRecord?.RelatedTexts.FirstOrDefault(text =>
            text.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase));

    private int? GetBriefingInsertionOrdinal(string description)
    {
        if (SelectedBriefingPhrase?.IsTimelineEvent is true)
        {
            return SelectedBriefingPhrase.EventOrdinal;
        }

        int count = BriefingTranscriptParser.ParseTimeline(description).Count;
        return count > 0 ? count - 1 : null;
    }

    private void SelectedRecord_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(RecordRowViewModel.BriefingPhrases))
        {
            return;
        }

        OnPropertyChanged(nameof(FilteredBriefingPhrases));
        OnPropertyChanged(nameof(BriefingWorkspaceSummary));
        RefreshBriefingPhraseSelection();
    }

    private void RefreshBriefingPhraseSelection()
    {
        OnPropertyChanged(nameof(FilteredBriefingPhrases));
        OnPropertyChanged(nameof(BriefingWorkspaceSummary));
        if (!IsBriefingsTable || SelectedRecord is null)
        {
            SelectedBriefingPhrase = null;
            BriefingDraftNumber = 0;
            BriefingDraftSpeaker = string.Empty;
            BriefingDraftText = string.Empty;
            BriefingDraftArguments = string.Empty;
            return;
        }

        int? preferredOrdinal = _pendingBriefingEventOrdinal ?? SelectedBriefingPhrase?.EventOrdinal;
        int? preferredNumber = _pendingBriefingPhraseNumber;
        _pendingBriefingEventOrdinal = null;
        _pendingBriefingPhraseNumber = null;
        BriefingPhraseViewModel[] visible = FilteredBriefingPhrases.ToArray();
        BriefingPhraseViewModel? selected = preferredNumber is int number
            ? visible.FirstOrDefault(briefingEvent => briefingEvent.IsPhrase && briefingEvent.Number == number)
            : preferredOrdinal is int ordinal
                ? visible.FirstOrDefault(briefingEvent => briefingEvent.EventOrdinal == ordinal)
                : null;
        SelectedBriefingPhrase = selected ?? visible.FirstOrDefault();
    }

    private bool CanMoveBriefingPhrase(int delta)
    {
        BriefingPhraseViewModel[] visible = FilteredBriefingPhrases.ToArray();
        int index = SelectedBriefingPhrase is null ? -1 : Array.IndexOf(visible, SelectedBriefingPhrase);
        int target = index + delta;
        return IsBriefingsTable && target >= 0 && target < visible.Length;
    }

    private void MoveBriefingPhrase(int delta)
    {
        BriefingPhraseViewModel[] visible = FilteredBriefingPhrases.ToArray();
        int index = SelectedBriefingPhrase is null ? -1 : Array.IndexOf(visible, SelectedBriefingPhrase);
        int target = index + delta;
        if (target >= 0 && target < visible.Length)
        {
            SelectedBriefingPhrase = visible[target];
        }
    }

    private void PlayBriefingAudio()
    {
        SoundResourceInfo? resource = SelectedBriefingPhrase?.PrimarySound?.Resource;
        if (resource is null)
        {
            return;
        }

        PlayBriefingAudioRequested?.Invoke(
            this,
            new SoundPreviewRequestedEventArgs(resource, SelectedBriefingPhrase!.Number));
    }

    private void RaiseBriefingCommandStates()
    {
        (NewBriefingPhraseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddBriefingEventCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
        (DeleteBriefingEventCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateBriefingEventCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveBriefingEventUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveBriefingEventDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyBriefingPhraseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousBriefingPhraseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextBriefingPhraseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayBriefingAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopBriefingAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ImportBriefingAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool FilterRecord(object item)
    {
        if (item is not RecordRowViewModel record || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return record.SearchText.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private bool FilterResource(object item)
    {
        if (item is not ResourceRowViewModel resource || string.IsNullOrWhiteSpace(ResourceSearchText))
        {
            return true;
        }

        var query = ResourceSearchText.Trim();
        return resource.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               resource.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool FilterTextResource(object item)
    {
        if (item is not LocalizationEntryViewModel text || string.IsNullOrWhiteSpace(TextsSearchText))
        {
            return true;
        }

        return text.SearchText.Contains(
            TextsSearchText.Trim(),
            StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshTextsFilter()
    {
        TextsView.Refresh();
        FilteredTextResourceCount = TextsView.Cast<object>().Count();
    }

    private async void ScheduleRecordSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;
        try
        {
            await Task.Delay(180, cancellationToken);
            RefreshRecordFilter();
        }
        catch (OperationCanceledException)
        {
            // A newer query superseded this one.
        }
    }

    private void RefreshRecordFilter()
    {
        RecordsView.Refresh();
        FilteredRecordCount = RecordsView.Cast<object>().Count();
    }

    private void OnFieldValueCommitted(object? sender, RecordFieldValueCommittedEventArgs eventArgs)
    {
        if (_isApplyingSpreadsheetPaste || sender is not RecordRowViewModel record)
        {
            return;
        }

        var field = eventArgs.Field;
        var fieldFrame = new FieldEditFrame(record, field, eventArgs.BeforeState, eventArgs.AfterState);
        _undo.Push(fieldFrame);
        _redo.Clear();
        RefreshChangedField(record, field);
        IReadOnlyList<LocalizationEntryViewModel> automaticTexts = [];
        string? automaticTextError = null;
        try
        {
            automaticTexts = TryCreateMissingTextsForAddedRecord(record);
            RegisterAddedTexts(automaticTexts);
        }
        catch (Exception exception)
        {
            automaticTextError = UnwrapMessage(exception);
        }

        if (automaticTexts.Count > 0)
        {
            WorkspaceEditFrame current = _undo.Pop();
            _undo.Push(new CompositeEditFrame(
                [
                    current,
                    new TextPresenceBatchEditFrame(automaticTexts, BeforePresent: false, AfterPresent: true),
                ],
                $"Отменены ключ и автотексты: {record.DisplayName}",
                $"Повторены ключ и автотексты: {record.DisplayName}"));
        }

        RefreshValidationDeferred();
        StatusText = automaticTexts.Count == 0
            ? $"Изменено: {record.DisplayName} · {field.DisplayName}"
            : $"Изменён ключ и создано записей texts.res: {automaticTexts.Count:N0}.";
        if (automaticTextError is not null)
        {
            ShowError($"Поле изменено, но автотексты не созданы: {automaticTextError}");
        }
        RaiseCommandStates();
    }

    private IReadOnlyList<LocalizationEntryViewModel> TryCreateMissingTextsForAddedRecord(
        RecordRowViewModel record)
    {
        if (!record.IsAdded || !HasTextsDocument || record.ExpectedTextKeys.Count == 0 ||
            record.ExpectedTextKeys.Any(key => string.IsNullOrWhiteSpace(key) || char.IsWhiteSpace(key[^1])))
        {
            return [];
        }

        DatabaseTableViewModel? table = Databases
            .SelectMany(database => database.Tables)
            .FirstOrDefault(candidate => candidate.Key == record.TableKey);
        string identity = record.IdentityValue.Trim();
        if (table is null || identity.Length == 0 || table.Records.Any(candidate =>
                !ReferenceEquals(candidate, record) &&
                candidate.IdentityValue.Trim().Equals(identity, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var relatedNames = record.RelatedTexts
            .Select(entry => entry.ResourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (record.ExpectedTextKeys.All(relatedNames.Contains))
        {
            return [];
        }

        return _service.AddMissingTexts(record);
    }

    private void RegisterAddedTexts(IReadOnlyList<LocalizationEntryViewModel> added)
    {
        if (added.Count == 0)
        {
            return;
        }

        foreach (LocalizationEntryViewModel entry in added)
        {
            entry.ValueCommitted -= OnLocalizationValueCommitted;
            entry.ValueCommitted += OnLocalizationValueCommitted;
            if (!TextResources.Contains(entry))
            {
                TextResources.Add(entry);
            }

            AddTextInsertionChange(entry);
        }

        OnPropertyChanged(nameof(SelectedRecordHasTexts));
        OnPropertyChanged(nameof(SelectedRecordTextCount));
        OnPropertyChanged(nameof(HasChanges));
        RefreshTextsFilter();
        RefreshAllCounts();
        NotifyWorkspaceProperties();
    }

    private void OnLocalizationValueCommitted(object? sender, LocalizationValueCommittedEventArgs eventArgs)
    {
        if (sender is not LocalizationEntryViewModel entry)
        {
            return;
        }

        if (_isApplyingSpreadsheetPaste)
        {
            return;
        }

        _undo.Push(new TextEditFrame(entry, eventArgs.BeforeState, eventArgs.AfterState));
        _redo.Clear();
        RefreshLocalizationChanges(entry);
        RefreshLocalizationOwners(entry);
        RefreshViewsAfterLocalizationEdit();
        StatusText = $"Изменён {entry.ResourceName} · {eventArgs.FieldName}";
        RaiseCommandStates();
    }

    internal bool TryPasteSpreadsheetCells(
        IReadOnlyList<SpreadsheetPasteCell> cells,
        out int changedCellCount,
        out string? error)
    {
        changedCellCount = 0;
        error = null;
        if (!IsEditMode || IsBusy || !IsCurrentTableEditable || SelectedTable is null)
        {
            error = "Текущая таблица недоступна для вставки.";
            ShowError(error);
            return false;
        }

        if (cells.Count == 0)
        {
            StatusText = "Вставка не требуется: служебные значения уже совпадают с таблицей.";
            return true;
        }

        var seen = new HashSet<(
            RecordRowViewModel Record,
            int FieldId,
            DatabaseFieldProjectionKind? ProjectionKind,
            int ProjectionIndex,
            SpreadsheetPasteTarget Target)>();
        var pending = new List<(
            RecordRowViewModel Record,
            RecordFieldViewModel Field,
            DatabaseFieldProjection? Projection,
            string Value,
            DatabaseFieldState BeforeState)>();
        var pendingTexts = new List<(
            RecordRowViewModel Record,
            LocalizationEntryViewModel Entry,
            SpreadsheetPasteTarget Target,
            string Value)>();
        var textBeforeStates = new Dictionary<LocalizationEntryViewModel, TextResourceState>();
        foreach (SpreadsheetPasteCell cell in cells)
        {
            var identity = (
                cell.Record,
                cell.FieldId,
                cell.Projection?.Kind,
                cell.Projection?.Index ?? -1,
                cell.Target);
            if (!SelectedTable.Records.Any(record => ReferenceEquals(record, cell.Record)) ||
                !seen.Add(identity))
            {
                error = "Диапазон вставки содержит неизвестную или повторяющуюся ячейку.";
                ShowError(error);
                return false;
            }

            if (cell.Target is SpreadsheetPasteTarget.LocalizationTitle or
                SpreadsheetPasteTarget.LocalizationDescription)
            {
                LocalizationEntryViewModel? entry = cell.Record.PrimaryLocalization;
                if (entry is null)
                {
                    error = $"Для строки «{cell.Record.DisplayName}» нет редактируемой записи texts.res.";
                    ShowError(error);
                    return false;
                }

                textBeforeStates.TryAdd(entry, entry.CaptureState());
                pendingTexts.Add((cell.Record, entry, cell.Target, cell.Value));
                continue;
            }

            RecordFieldViewModel? field = cell.Record.GetCell(cell.FieldId);
            bool canEdit = field is not null && (cell.Projection is null
                ? field.CanEdit
                : field.CanEditProjection(cell.Projection));
            if (!canEdit)
            {
                error = $"Ячейка «{cell.Record.DisplayName} · поле {cell.FieldId}» доступна только для чтения.";
                ShowError(error);
                return false;
            }

            pending.Add((cell.Record, field!, cell.Projection, cell.Value, field!.CaptureState()));
        }

        var edits = new List<FieldEditFrame>(pending.Count);
        var changedTextTargets = new HashSet<(LocalizationEntryViewModel Entry, SpreadsheetPasteTarget Target)>();
        _isApplyingSpreadsheetPaste = true;
        try
        {
            foreach (var item in pending)
            {
                bool accepted;
                string? editError = null;
                if (item.Projection is null)
                {
                    item.Field.EditorText = item.Value;
                    accepted = !item.Field.HasError;
                    editError = item.Field.ErrorMessage;
                }
                else
                {
                    accepted = item.Field.TrySetProjectedText(item.Projection, item.Value, out editError);
                }

                if (!accepted)
                {
                    throw new InvalidOperationException(
                        $"{item.Record.DisplayName} · {item.Field.DisplayName}: {editError ?? item.Field.ErrorMessage}");
                }

                DatabaseFieldState afterState = item.Field.CaptureState();
                if (!item.BeforeState.IsEquivalentTo(afterState))
                {
                    edits.Add(new FieldEditFrame(item.Record, item.Field, item.BeforeState, afterState));
                }
            }

            foreach (var item in pendingTexts)
            {
                string beforeValue = item.Target == SpreadsheetPasteTarget.LocalizationTitle
                    ? item.Entry.Title
                    : item.Entry.Description;
                if (item.Target == SpreadsheetPasteTarget.LocalizationTitle)
                {
                    item.Entry.Title = item.Value;
                }
                else
                {
                    item.Entry.Description = item.Value;
                }

                if (item.Entry.HasError)
                {
                    throw new InvalidOperationException(
                        $"{item.Record.DisplayName} · {(item.Target == SpreadsheetPasteTarget.LocalizationTitle ? "Название RU" : "Описание RU")}: {item.Entry.ErrorMessage}");
                }

                if (!string.Equals(beforeValue, item.Value, StringComparison.Ordinal))
                {
                    changedTextTargets.Add((item.Entry, item.Target));
                }
            }
        }
        catch (Exception exception)
        {
            foreach (var item in pending.AsEnumerable().Reverse())
            {
                item.Field.RestoreState(item.BeforeState);
            }

            foreach ((LocalizationEntryViewModel entry, TextResourceState state) in textBeforeStates)
            {
                entry.RestoreState(state);
            }

            RefreshChangedFields(pending.Select(item => (item.Record, item.Field)));
            foreach (LocalizationEntryViewModel entry in textBeforeStates.Keys)
            {
                RefreshLocalizationOwners(entry);
            }
            RefreshViewsAfterLocalizationEdit();
            error = $"Вставка отменена без частичных изменений: {UnwrapMessage(exception)}";
            ShowError(error);
            return false;
        }
        finally
        {
            _isApplyingSpreadsheetPaste = false;
        }

        TextEditFrame[] textEdits = textBeforeStates
            .Select(pair => new TextEditFrame(pair.Key, pair.Value, pair.Key.CaptureState()))
            .Where(edit => !edit.BeforeState.IsEquivalentTo(edit.AfterState))
            .ToArray();
        if (edits.Count == 0 && textEdits.Length == 0)
        {
            StatusText = "Вставленные значения уже совпадают с содержимым выбранных ячеек.";
            return true;
        }

        RefreshChangedFields(edits.Select(edit => (edit.Record, edit.Field)));
        foreach (TextEditFrame textEdit in textEdits)
        {
            RefreshLocalizationChanges(textEdit.Entry);
        }
        RefreshLocalizationOwners(textEdits.Select(textEdit => textEdit.Entry));
        if (textEdits.Length > 0)
        {
            RefreshViewsAfterLocalizationEdit();
        }
        var automaticTexts = new List<LocalizationEntryViewModel>();
        try
        {
            foreach (RecordRowViewModel record in edits.Select(edit => edit.Record).Distinct())
            {
                automaticTexts.AddRange(TryCreateMissingTextsForAddedRecord(record));
            }

            RegisterAddedTexts(automaticTexts);
        }
        catch (Exception exception)
        {
            foreach (LocalizationEntryViewModel entry in automaticTexts.AsEnumerable().Reverse())
            {
                SetAddedTextPresence(entry, shouldBePresent: false);
            }

            foreach (var item in pending.AsEnumerable().Reverse())
            {
                item.Field.RestoreState(item.BeforeState);
            }

            foreach ((LocalizationEntryViewModel entry, TextResourceState state) in textBeforeStates)
            {
                entry.RestoreState(state);
                RefreshLocalizationChanges(entry);
                RefreshLocalizationOwners(entry);
            }

            RefreshChangedFields(pending.Select(item => (item.Record, item.Field)));
            RefreshViewsAfterLocalizationEdit();
            error = $"Вставка отменена без частичных изменений: {UnwrapMessage(exception)}";
            ShowError(error);
            return false;
        }

        var pasteFrames = new List<WorkspaceEditFrame>();
        if (edits.Count > 0)
        {
            pasteFrames.Add(edits.Count == 1 ? edits[0] : new FieldBatchEditFrame(edits.ToArray()));
        }
        pasteFrames.AddRange(textEdits);
        if (automaticTexts.Count > 0)
        {
            pasteFrames.Add(new TextPresenceBatchEditFrame(automaticTexts, BeforePresent: false, AfterPresent: true));
        }

        int totalChangedCells = edits.Count + changedTextTargets.Count;
        WorkspaceEditFrame pasteFrame = pasteFrames.Count == 1
            ? pasteFrames[0]
            : new CompositeEditFrame(
                pasteFrames,
                $"Отменена вставка: {totalChangedCells:N0} ячеек.",
                $"Повторена вставка: {totalChangedCells:N0} ячеек.");
        _undo.Push(pasteFrame);
        _redo.Clear();
        RefreshValidationDeferred();
        changedCellCount = totalChangedCells;
        StatusText = automaticTexts.Count == 0
            ? $"Вставлено из буфера: {changedCellCount:N0} ячеек."
            : $"Вставлено ячеек: {changedCellCount:N0}; создано записей texts.res: {automaticTexts.Count:N0}.";
        RaiseCommandStates();
        return true;
    }

    internal bool TryPasteLocalizationSpreadsheetCells(
        IReadOnlyList<LocalizationSpreadsheetPasteCell> cells,
        out int changedCellCount,
        out string? error)
    {
        changedCellCount = 0;
        error = null;
        if (!IsEditMode || IsBusy || !HasTextsDocument)
        {
            error = "Подключённый texts.res сейчас недоступен для вставки.";
            ShowError(error);
            return false;
        }

        if (cells.Count == 0)
        {
            StatusText = "Вставка не требуется: служебные значения уже совпадают с таблицей.";
            return true;
        }

        var seen = new HashSet<(LocalizationEntryViewModel Entry, SpreadsheetPasteTarget Target)>();
        HashSet<LocalizationEntryViewModel> knownEntries = TextResources.ToHashSet();
        var beforeStates = new Dictionary<LocalizationEntryViewModel, TextResourceState>();
        var beforeValues = new Dictionary<(LocalizationEntryViewModel Entry, SpreadsheetPasteTarget Target), string>();
        foreach (LocalizationSpreadsheetPasteCell cell in cells)
        {
            if (cell.Target is not (SpreadsheetPasteTarget.LocalizationTitle or SpreadsheetPasteTarget.LocalizationDescription) ||
                !knownEntries.Contains(cell.Entry) ||
                !seen.Add((cell.Entry, cell.Target)))
            {
                error = "Диапазон вставки содержит неизвестную, служебную или повторяющуюся ячейку texts.res.";
                ShowError(error);
                return false;
            }

            beforeStates.TryAdd(cell.Entry, cell.Entry.CaptureState());
            beforeValues.Add(
                (cell.Entry, cell.Target),
                cell.Target == SpreadsheetPasteTarget.LocalizationTitle
                    ? cell.Entry.Title
                    : cell.Entry.Description);
        }

        _isApplyingSpreadsheetPaste = true;
        try
        {
            foreach (LocalizationSpreadsheetPasteCell cell in cells)
            {
                if (cell.Target == SpreadsheetPasteTarget.LocalizationTitle)
                {
                    cell.Entry.Title = cell.Value;
                }
                else
                {
                    cell.Entry.Description = cell.Value;
                }

                if (cell.Entry.HasError)
                {
                    throw new InvalidOperationException(
                        $"{cell.Entry.ResourceName} · " +
                        $"{(cell.Target == SpreadsheetPasteTarget.LocalizationTitle ? "Название RU" : "Описание RU")}: " +
                        cell.Entry.ErrorMessage);
                }
            }
        }
        catch (Exception exception)
        {
            foreach ((LocalizationEntryViewModel entry, TextResourceState state) in beforeStates)
            {
                entry.RestoreState(state);
            }

            RefreshLocalizationOwners(beforeStates.Keys);
            RefreshViewsAfterLocalizationEdit();
            error = $"Вставка отменена без частичных изменений: {UnwrapMessage(exception)}";
            ShowError(error);
            return false;
        }
        finally
        {
            _isApplyingSpreadsheetPaste = false;
        }

        TextEditFrame[] edits = beforeStates
            .Select(pair => new TextEditFrame(pair.Key, pair.Value, pair.Key.CaptureState()))
            .Where(edit => !edit.BeforeState.IsEquivalentTo(edit.AfterState))
            .ToArray();
        changedCellCount = cells.Count(cell =>
        {
            string after = cell.Target == SpreadsheetPasteTarget.LocalizationTitle
                ? cell.Entry.Title
                : cell.Entry.Description;
            return !string.Equals(beforeValues[(cell.Entry, cell.Target)], after, StringComparison.Ordinal);
        });
        if (edits.Length == 0)
        {
            StatusText = "Вставленные значения уже совпадают с содержимым выбранных ячеек.";
            return true;
        }

        foreach (TextEditFrame edit in edits)
        {
            RefreshLocalizationChanges(edit.Entry);
        }
        RefreshLocalizationOwners(edits.Select(edit => edit.Entry));
        RefreshViewsAfterLocalizationEdit();

        WorkspaceEditFrame frame = edits.Length == 1
            ? edits[0]
            : new CompositeEditFrame(
                edits,
                $"Отменена вставка texts.res: {changedCellCount:N0} ячеек.",
                $"Повторена вставка texts.res: {changedCellCount:N0} ячеек.");
        _undo.Push(frame);
        _redo.Clear();
        RefreshValidationDeferred();
        StatusText = $"Вставлено из Excel в texts.res: {changedCellCount:N0} ячеек.";
        RaiseCommandStates();
        return true;
    }

    internal void ReportSpreadsheetCopy(int rowCount, int columnCount)
    {
        NotificationIsError = false;
        StatusText = $"Скопировано: {rowCount:N0} × {columnCount:N0} ячеек. Можно вставить в Excel или другую таблицу.";
    }

    internal void ReportSpreadsheetError(string message) => ShowError(message);

    internal void NavigateToComparisonDifference(
        string tableKey,
        int currentRecordIndex,
        int? fieldId)
    {
        DatabaseTableViewModel? table = Databases
            .SelectMany(database => database.Tables)
            .FirstOrDefault(candidate => candidate.Key.Equals(tableKey, StringComparison.OrdinalIgnoreCase));
        RecordRowViewModel? record = table?.Records
            .FirstOrDefault(candidate => candidate.SourceRecordIndex == currentRecordIndex);
        if (table is null || record is null)
        {
            ShowError("Не удалось найти выбранную строку в текущей базе. Повторите сравнение.");
            return;
        }

        SelectTable(table);
        SelectedRecord = record;
        RecordFocusRequested?.Invoke(
            this,
            new RecordFocusRequestedEventArgs(record, fieldId ?? record.NameFieldId, beginEdit: false));
        StatusText = fieldId is int selectedFieldId
            ? $"Открыто различие: {table.Title} · #{record.Index:N0} · поле {selectedFieldId}."
            : $"Открыта отличающаяся запись: {table.Title} · #{record.Index:N0} {record.DisplayName}.";
    }

    internal void ReportLayoutReset()
    {
        NotificationMessage = "Стандартная раскладка восстановлена.";
        StatusText = "Размеры панелей и вспомогательных окон сброшены.";
    }

    private void NavigateToDiagnostic(ValidationMessageViewModel? diagnostic)
    {
        if (diagnostic?.CanNavigate is not true || diagnostic.RecordIndex is not int recordIndex)
        {
            return;
        }

        DatabaseTableViewModel? table = Databases
            .SelectMany(database => database.Tables)
            .FirstOrDefault(candidate => candidate.Key.Equals(diagnostic.TableKey, StringComparison.Ordinal));
        RecordRowViewModel? record = table?.Records
            .FirstOrDefault(candidate => candidate.SourceRecordIndex == recordIndex);
        if (table is null || record is null)
        {
            ShowError("Не удалось найти строку, указанную сообщением проверки. Повторите проверку через F5.");
            return;
        }

        SelectTable(table);
        SelectedRecord = record;
        int fieldId = diagnostic.FieldId ?? record.NameFieldId;
        RecordFocusRequested?.Invoke(
            this,
            new RecordFocusRequestedEventArgs(record, fieldId, beginEdit: false));
        StatusText = diagnostic.FieldId is null
            ? $"Открыта строка проверки: {table.Title} · #{record.Index:N0} {record.DisplayName}."
            : $"Открыта ячейка проверки: {table.Title} · #{record.Index:N0} · поле {diagnostic.FieldId}.";
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        WorkspaceEditFrame frame = _undo.Pop();
        if (!TryApplyEditFrame(frame, undo: true, out string status, out string? error))
        {
            _undo.Push(frame);
            ShowError(error ?? "Не удалось отменить изменение.");
            return;
        }

        _redo.Push(frame);
        RefreshValidationDeferred();
        StatusText = status;
        RaiseCommandStates();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        WorkspaceEditFrame frame = _redo.Pop();
        if (!TryApplyEditFrame(frame, undo: false, out string status, out string? error))
        {
            _redo.Push(frame);
            ShowError(error ?? "Не удалось повторить изменение.");
            return;
        }

        _undo.Push(frame);
        RefreshValidationDeferred();
        StatusText = status;
        RaiseCommandStates();
    }

    private bool TryApplyEditFrame(
        WorkspaceEditFrame frame,
        bool undo,
        out string status,
        out string? error)
    {
        status = string.Empty;
        error = null;
        switch (frame)
        {
            case CompositeEditFrame compositeFrame:
                WorkspaceEditFrame[] orderedFrames = undo
                    ? compositeFrame.Frames.Reverse().ToArray()
                    : compositeFrame.Frames.ToArray();
                var appliedFrames = new List<WorkspaceEditFrame>(orderedFrames.Length);
                foreach (WorkspaceEditFrame child in orderedFrames)
                {
                    if (TryApplyEditFrame(child, undo, out _, out error))
                    {
                        appliedFrames.Add(child);
                        continue;
                    }

                    foreach (WorkspaceEditFrame applied in appliedFrames.AsEnumerable().Reverse())
                    {
                        TryApplyEditFrame(applied, !undo, out _, out _);
                    }

                    error ??= "Не удалось атомарно восстановить связанные изменения database.res и texts.res.";
                    return false;
                }

                status = undo ? compositeFrame.UndoStatus : compositeFrame.RedoStatus;
                return true;

            case FieldEditFrame fieldFrame:
                DatabaseFieldState state = undo ? fieldFrame.BeforeState : fieldFrame.AfterState;
                if (!fieldFrame.Field.RestoreState(state))
                {
                    error = fieldFrame.Field.ErrorMessage;
                    return false;
                }

                RefreshChangedField(fieldFrame.Record, fieldFrame.Field);
                status = undo
                    ? $"Отменено: {fieldFrame.Record.DisplayName} · {fieldFrame.Field.DisplayName}"
                    : $"Повторено: {fieldFrame.Record.DisplayName} · {fieldFrame.Field.DisplayName}";
                return true;

            case FieldBatchEditFrame batchFrame:
                FieldEditFrame[] orderedEdits = undo
                    ? batchFrame.Edits.Reverse().ToArray()
                    : batchFrame.Edits.ToArray();
                var rollback = orderedEdits
                    .Select(edit => (Edit: edit, State: edit.Field.CaptureState()))
                    .ToArray();
                for (var index = 0; index < orderedEdits.Length; index++)
                {
                    FieldEditFrame edit = orderedEdits[index];
                    DatabaseFieldState batchState = undo ? edit.BeforeState : edit.AfterState;
                    if (edit.Field.RestoreState(batchState))
                    {
                        continue;
                    }

                    for (var rollbackIndex = index; rollbackIndex >= 0; rollbackIndex--)
                    {
                        rollback[rollbackIndex].Edit.Field.RestoreState(rollback[rollbackIndex].State);
                    }

                    error = edit.Field.ErrorMessage ?? "Не удалось восстановить ячейку пакетной операции.";
                    return false;
                }

                RefreshChangedFields(batchFrame.Edits.Select(edit => (edit.Record, edit.Field)));
                status = undo
                    ? $"Отменена вставка в {batchFrame.Edits.Count:N0} ячеек."
                    : $"Повторена вставка в {batchFrame.Edits.Count:N0} ячеек.";
                return true;

            case RecordPresenceEditFrame recordFrame:
                bool shouldBePresent = undo ? recordFrame.BeforePresent : recordFrame.AfterPresent;
                try
                {
                    SetAddedRecordPresence(recordFrame.Table, recordFrame.Record, shouldBePresent);
                    status = shouldBePresent
                        ? $"Возвращена новая строка: {recordFrame.Record.DisplayName}"
                        : $"Отменено добавление строки: {recordFrame.Record.DisplayName}";
                    return true;
                }
                catch (Exception exception)
                {
                    error = UnwrapMessage(exception);
                    return false;
                }

            case TextEditFrame textFrame:
                TextResourceState textState = undo ? textFrame.BeforeState : textFrame.AfterState;
                textFrame.Entry.RestoreState(textState);
                RefreshLocalizationChanges(textFrame.Entry);
                RefreshLocalizationOwners(textFrame.Entry);
                RefreshViewsAfterLocalizationEdit();
                status = undo
                    ? $"Отменено: {textFrame.Entry.ResourceName}"
                    : $"Повторено: {textFrame.Entry.ResourceName}";
                return true;

            case TextPresenceBatchEditFrame textPresenceFrame:
                bool textsShouldBePresent = undo
                    ? textPresenceFrame.BeforePresent
                    : textPresenceFrame.AfterPresent;
                try
                {
                    foreach (LocalizationEntryViewModel entry in textPresenceFrame.Entries)
                    {
                        SetAddedTextPresence(entry, textsShouldBePresent);
                    }

                    OnPropertyChanged(nameof(SelectedRecordHasTexts));
                    OnPropertyChanged(nameof(SelectedRecordTextCount));
                    status = textsShouldBePresent
                        ? $"Возвращены новые тексты: {textPresenceFrame.Entries.Count:N0}"
                        : $"Отменено добавление текстов: {textPresenceFrame.Entries.Count:N0}";
                    return true;
                }
                catch (Exception exception)
                {
                    error = UnwrapMessage(exception);
                    return false;
                }

            case SoundStageEditFrame soundFrame:
                PendingSoundEdit = undo ? soundFrame.Before : soundFrame.After;
                status = PendingSoundEdit is null
                    ? "Отменена подготовка звука; исходный RES не изменён."
                    : $"Возвращена подготовка звука: {PendingSoundEdit.ResourceName}.";
                return true;

            default:
                error = "Неизвестный тип операции Undo/Redo.";
                return false;
        }
    }

    private void SetAddedTextPresence(LocalizationEntryViewModel entry, bool shouldBePresent)
    {
        if (shouldBePresent)
        {
            _service.RestoreAddedText(entry);
            entry.ValueCommitted -= OnLocalizationValueCommitted;
            entry.ValueCommitted += OnLocalizationValueCommitted;
            if (!TextResources.Contains(entry))
            {
                TextResources.Add(entry);
            }
            AddTextInsertionChange(entry);
        }
        else
        {
            _service.RemoveAddedText(entry);
            entry.ValueCommitted -= OnLocalizationValueCommitted;
            TextResources.Remove(entry);
            RemoveTextChanges(entry);
        }

        SelectedRecord?.RefreshState();
        RecordsView.Refresh();
        RefreshTextsFilter();
        RefreshAllCounts();
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshLocalizationOwners(LocalizationEntryViewModel entry) =>
        RefreshLocalizationOwners([entry]);

    private void RefreshLocalizationOwners(IEnumerable<LocalizationEntryViewModel> entries)
    {
        HashSet<LocalizationEntryViewModel> entrySet = entries.ToHashSet();
        if (entrySet.Count == 0)
        {
            return;
        }

        foreach (RecordRowViewModel record in Databases
                     .SelectMany(database => database.Tables)
                     .SelectMany(table => table.Records)
                     .Where(record => record.RelatedTexts.Any(entrySet.Contains)))
        {
            record.RefreshLocalizationState();
            _service.RefreshRelatedSounds(record);
        }
    }

    private void RefreshViewsAfterLocalizationEdit()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            RefreshRecordsViewSafely();
        }

        if (!string.IsNullOrWhiteSpace(TextsSearchText))
        {
            ScheduleTextsViewRefresh();
        }
    }

    private void ScheduleTextsViewRefresh()
    {
        if (_textsViewRefreshPending)
        {
            return;
        }

        _textsViewRefreshPending = true;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            () =>
            {
                _textsViewRefreshPending = false;
                if (!string.IsNullOrWhiteSpace(TextsSearchText))
                {
                    RefreshTextsFilter();
                }
            },
            DispatcherPriority.ContextIdle);
    }

    internal bool HasPendingTextsViewRefresh => _textsViewRefreshPending;

    private void SetAddedRecordPresence(
        DatabaseTableViewModel table,
        RecordRowViewModel record,
        bool shouldBePresent)
    {
        bool isPresent = table.Records.Any(candidate => ReferenceEquals(candidate, record));
        if (isPresent == shouldBePresent)
        {
            return;
        }

        if (shouldBePresent)
        {
            _service.RestoreAddedRecord(record);
            record.FieldValueCommitted += OnFieldValueCommitted;
            record.IsEditMode = IsEditMode && ReferenceEquals(SelectedTable, table) && !IsBusy;
            table.AppendRecord(record);
            AddRecordInsertionChange(record);
            if (ReferenceEquals(SelectedTable, table))
            {
                SelectedRecord = record;
            }
        }
        else
        {
            _service.RemoveAddedRecord(record);
            if (ReferenceEquals(SelectedRecord, record))
            {
                SelectedRecord = null;
            }

            record.FieldValueCommitted -= OnFieldValueCommitted;
            record.IsEditMode = false;
            table.RemoveLastRecord(record);
            RemoveRecordInsertionChange(record);
        }

        if (ReferenceEquals(SelectedTable, table))
        {
            RecordsView.Refresh();
            FilteredRecordCount = RecordsView.Cast<object>().Count();
            OnPropertyChanged(nameof(CurrentTableRecordCount));
        }

        RefreshAllCounts();
        OnPropertyChanged(nameof(HasChanges));
    }

    private void AddRecordInsertionChange(RecordRowViewModel record)
    {
        string key = GetRecordInsertionChangeKey(record);
        if (_changesByPath.ContainsKey(key))
        {
            return;
        }

        var entry = ChangeEntryViewModel.ForAddedRecord(record);
        _changesByPath.Add(key, entry);
        Changes.Add(entry);
    }

    private void RemoveRecordInsertionChange(RecordRowViewModel record)
    {
        string key = GetRecordInsertionChangeKey(record);
        if (_changesByPath.Remove(key, out ChangeEntryViewModel? entry))
        {
            Changes.Remove(entry);
        }
    }

    private static string GetRecordInsertionChangeKey(RecordRowViewModel record) =>
        $"{record.Path}\u001f$new-record";

    private void AddTextInsertionChange(LocalizationEntryViewModel entry)
    {
        string key = GetTextChangeKey(entry, "$new-resource");
        if (_changesByPath.ContainsKey(key))
        {
            return;
        }

        var change = ChangeEntryViewModel.ForAddedText(entry);
        _changesByPath.Add(key, change);
        Changes.Add(change);
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshLocalizationChanges(LocalizationEntryViewModel entry)
    {
        if (entry.IsNew)
        {
            AddTextInsertionChange(entry);
            return;
        }

        RefreshTextFieldChange(
            entry,
            "Название RU",
            entry.Title,
            entry.OriginalTitle,
            GetTextChangeKey(entry, "title"));
        RefreshTextFieldChange(
            entry,
            "Описание RU",
            entry.Description,
            entry.OriginalDescription,
            GetTextChangeKey(entry, "description"));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshTextFieldChange(
        LocalizationEntryViewModel entry,
        string fieldName,
        string currentValue,
        string originalValue,
        string key)
    {
        if (!string.Equals(currentValue, originalValue, StringComparison.Ordinal))
        {
            if (_changesByPath.TryGetValue(key, out ChangeEntryViewModel? existing))
            {
                existing.NewValue = currentValue;
            }
            else
            {
                var change = new ChangeEntryViewModel(entry, fieldName);
                _changesByPath.Add(key, change);
                Changes.Add(change);
            }
        }
        else if (_changesByPath.Remove(key, out ChangeEntryViewModel? removed))
        {
            Changes.Remove(removed);
        }
    }

    private void RemoveTextChanges(LocalizationEntryViewModel entry)
    {
        string prefix = $"texts.res/{entry.ResourceName}/";
        foreach (string key in _changesByPath.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
        {
            if (_changesByPath.Remove(key, out ChangeEntryViewModel? change))
            {
                Changes.Remove(change);
            }
        }
    }

    private static string GetTextChangeKey(LocalizationEntryViewModel entry, string field) =>
        $"texts.res/{entry.ResourceName}/{field}";

    private void RevertChange(ChangeEntryViewModel? change)
    {
        if (change is null)
        {
            return;
        }

        if (change.Localization is { } localization)
        {
            TextResourceState beforeTextState = localization.CaptureState();
            localization.ResetToOriginal();
            TextResourceState afterTextState = localization.CaptureState();
            _undo.Push(new TextEditFrame(localization, beforeTextState, afterTextState));
            _redo.Clear();
            RefreshLocalizationChanges(localization);
            RefreshLocalizationOwners(localization);
            RefreshViewsAfterLocalizationEdit();
            StatusText = $"Текст возвращён к исходному значению: {localization.ResourceName}";
            RaiseCommandStates();
            return;
        }

        RecordFieldViewModel field = change.Field!;
        var beforeState = field.CaptureState();
        if (!field.ResetToOriginal())
        {
            ShowError(field.ErrorMessage ?? "Не удалось вернуть исходное значение.");
            return;
        }

        var afterState = field.CaptureState();
        _undo.Push(new FieldEditFrame(change.Record!, field, beforeState, afterState));
        _redo.Clear();
        RefreshChangedField(change.Record!, field);
        RefreshValidationDeferred();
        StatusText = $"Поле возвращено к исходному значению: {change.FieldName}";
        RaiseCommandStates();
    }

    private void RefreshChangedField(RecordRowViewModel record, RecordFieldViewModel field)
    {
        RefreshChangedFields([(record, field)]);
    }

    private void RefreshChangedFields(
        IEnumerable<(RecordRowViewModel Record, RecordFieldViewModel Field)> changedFields)
    {
        var fields = changedFields
            .DistinctBy(item => (item.Record, item.Field.Id))
            .ToArray();
        foreach (RecordRowViewModel record in fields.Select(item => item.Record).Distinct())
        {
            record.RefreshState();
            _service.RefreshRelatedTexts(record);
            _service.RefreshRelatedSounds(record);
            if (record.IsAdded)
            {
                string key = GetRecordInsertionChangeKey(record);
                if (_changesByPath.TryGetValue(key, out ChangeEntryViewModel? insertion))
                {
                    insertion.NewValue = record.DisplayName;
                }
            }
        }

        foreach (var item in fields.Where(item => !item.Record.IsAdded))
        {
            RecordFieldViewModel field = item.Field;
            if (field.IsModified)
            {
                if (_changesByPath.TryGetValue(field.Path, out var existing))
                {
                    existing.NewValue = field.EditorText;
                }
                else
                {
                    var entry = new ChangeEntryViewModel(item.Record, field);
                    _changesByPath.Add(field.Path, entry);
                    Changes.Add(entry);
                }
            }
            else if (_changesByPath.Remove(field.Path, out var removed))
            {
                Changes.Remove(removed);
            }
        }

        RefreshAllCounts();
        OnPropertyChanged(nameof(HasChanges));
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RefreshRecordsViewSafely();
    }

    private void RefreshRecordsViewSafely()
    {
        if (RecordsView is IEditableCollectionView editableView &&
            (editableView.IsAddingNew || editableView.IsEditingItem))
        {
            _recordsViewRefreshPending = true;
            return;
        }

        _recordsViewRefreshPending = false;
        RecordsView.Refresh();
    }

    internal bool HasPendingRecordsViewRefresh => _recordsViewRefreshPending;

    internal void CompletePendingRecordsViewRefresh()
    {
        if (_recordsViewRefreshPending)
        {
            RefreshRecordsViewSafely();
        }
    }

    private async void RefreshValidationDeferred() =>
        await RefreshValidationAsync(deferred: true);

    private async void RefreshValidation() =>
        await RefreshValidationAsync(deferred: false);

    private async Task RefreshValidationAsync(bool deferred)
    {
        CancelValidation();
        var validation = new CancellationTokenSource();
        _validationCancellation = validation;
        CancellationToken cancellationToken = validation.Token;
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        try
        {
            if (deferred)
            {
                await Task.Delay(220, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!HasDocument || IsBusy)
            {
                return;
            }

            IReadOnlyList<ValidationMessageViewModel> diagnostics =
                await _service.ValidateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_validationCancellation, validation))
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                dispatcher,
                () =>
                {
                    ReplaceCollection(Diagnostics, diagnostics);
                    RefreshDiagnosticsView();
                    ApplyDiagnosticsToRecords(Diagnostics);
                    RefreshAllCounts();
                    StatusText = Diagnostics.Count == 0
                        ? "Проверка завершена: проблем не обнаружено."
                        : $"Проверка завершена: {ErrorCount} ошибок, {WarningCount} предупреждений.";
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer edit superseded this validation pass.
        }
        catch (ObjectDisposedException)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            await InvokeOnDispatcherAsync(
                dispatcher,
                () => ShowError($"Проверка не выполнена: {UnwrapMessage(exception)}"),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_validationCancellation, validation))
            {
                _validationCancellation = null;
                validation.Dispose();
            }
        }
    }

    private static async Task InvokeOnDispatcherAsync(
        Dispatcher? dispatcher,
        Action action,
        CancellationToken cancellationToken)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Background,
            cancellationToken);
    }

    private void RefreshAllCounts()
    {
        NavigationItems.First(item => item.Section == WorkspaceSection.Texts).Badge = HasTextsDocument
            ? TextResourceCount.ToString("N0")
            : null;
        NavigationItems.First(item => item.Section == WorkspaceSection.Resources).Badge = Resources.Count.ToString("N0");
        NavigationItems.First(item => item.Section == WorkspaceSection.Changes).Badge = Changes.Count == 0 ? null : Changes.Count.ToString("N0");
        NavigationItems.First(item => item.Section == WorkspaceSection.Validation).Badge = Diagnostics.Count == 0 ? "✓" : Diagnostics.Count.ToString("N0");
        OnPropertyChanged(nameof(ResourceCount));
        OnPropertyChanged(nameof(DatabaseCount));
        OnPropertyChanged(nameof(TableCount));
        OnPropertyChanged(nameof(RecordCount));
        OnPropertyChanged(nameof(EditableTableCount));
        OnPropertyChanged(nameof(ReadOnlyTableCount));
        OnPropertyChanged(nameof(UnitCount));
        OnPropertyChanged(nameof(SpellCount));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(DiagnosticCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(DiagnosticFilterSummary));
        OnPropertyChanged(nameof(DiagnosticEmptyTitle));
        OnPropertyChanged(nameof(DiagnosticEmptyHint));
        OnPropertyChanged(nameof(FilteredTextResourceCount));
    }

    private void ApplyDiagnosticsToRecords(IEnumerable<ValidationMessageViewModel> diagnostics)
    {
        var byRecord = diagnostics
            .Where(item => item.CanNavigate && item.RecordIndex is not null)
            .GroupBy(item => (item.TableKey!, item.RecordIndex!.Value))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ValidationMessageViewModel>)group.ToArray());

        foreach (DatabaseTableViewModel table in Databases.SelectMany(database => database.Tables))
        {
            foreach (RecordRowViewModel record in table.Records)
            {
                record.ApplyDiagnostics(
                    byRecord.TryGetValue((table.Key, record.SourceRecordIndex), out var messages)
                        ? messages
                        : []);
            }
        }
    }

    private bool FilterDiagnostic(object item)
    {
        if (item is not ValidationMessageViewModel diagnostic)
        {
            return false;
        }

        if (!MatchesDiagnosticBaseFilters(diagnostic))
        {
            return false;
        }

        return SelectedDiagnosticCategory is not { IsAll: false } category ||
               string.Equals(category.Key, diagnostic.GroupTitle, StringComparison.Ordinal);
    }

    private bool MatchesDiagnosticBaseFilters(ValidationMessageViewModel diagnostic)
    {

        if (ShowOnlyBlockingDiagnostics && !diagnostic.IsBlocking)
        {
            return false;
        }

        if (SelectedDiagnosticSeverityFilter != "Все уровни" &&
            !string.Equals(
                SelectedDiagnosticSeverityFilter,
                diagnostic.SeverityText switch
                {
                    "Ошибка" => "Ошибки",
                    "Предупреждение" => "Предупреждения",
                    _ => "Информация",
                },
                StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedDiagnosticDomainFilter != "Все области" &&
            !string.Equals(SelectedDiagnosticDomainFilter, diagnostic.DomainText, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(DiagnosticSearchText) ||
               diagnostic.SearchText.Contains(
                   DiagnosticSearchText.Trim(),
                   StringComparison.CurrentCultureIgnoreCase);
    }

    private void ConfigureDiagnosticSort()
    {
        using IDisposable defer = DiagnosticsView.DeferRefresh();
        DiagnosticsView.GroupDescriptions.Clear();
        DiagnosticsView.SortDescriptions.Clear();
        DiagnosticsView.SortDescriptions.Add(
            new SortDescription(nameof(ValidationMessageViewModel.SeverityRank), ListSortDirection.Descending));
        DiagnosticsView.SortDescriptions.Add(
            new SortDescription(nameof(ValidationMessageViewModel.CategoryText), ListSortDirection.Ascending));
        DiagnosticsView.SortDescriptions.Add(
            new SortDescription(nameof(ValidationMessageViewModel.Place), ListSortDirection.Ascending));
    }

    private void RefreshDiagnosticsView(bool rebuildCategories = true)
    {
        if (rebuildCategories)
        {
            RebuildDiagnosticCategories();
        }

        DiagnosticsView.Refresh();
        FilteredDiagnosticCount = DiagnosticsView.Cast<object>().Count();
        if (SelectedDiagnostic is null ||
            !DiagnosticsView.Cast<ValidationMessageViewModel>()
                .Any(item => ReferenceEquals(item, SelectedDiagnostic)))
        {
            SelectedDiagnostic = null;
        }

        OnPropertyChanged(nameof(HasActiveDiagnosticFilters));
        (ClearDiagnosticFiltersCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RebuildDiagnosticCategories()
    {
        string? selectedKey = SelectedDiagnosticCategory?.Key;
        ValidationMessageViewModel[] visibleByGlobalFilters = Diagnostics
            .Where(MatchesDiagnosticBaseFilters)
            .ToArray();
        var categories = new List<DiagnosticCategoryViewModel>
        {
            new("*", "Все проблемы", "Info", "Все уровни", visibleByGlobalFilters.Length, IsAll: true),
        };
        categories.AddRange(
            visibleByGlobalFilters
                .GroupBy(item => item.GroupTitle, StringComparer.Ordinal)
                .OrderByDescending(group => group.Max(item => item.SeverityRank))
                .ThenBy(group => group.First().CategoryText, StringComparer.CurrentCultureIgnoreCase)
                .Select(group =>
                {
                    ValidationMessageViewModel sample = group.First();
                    return new DiagnosticCategoryViewModel(
                        group.Key,
                        sample.CategoryText,
                        sample.Severity,
                        sample.SeverityText,
                        group.Count());
                }));

        _isUpdatingDiagnosticCategories = true;
        try
        {
            ReplaceCollection(DiagnosticCategories, categories);
            _selectedDiagnosticCategory = categories.FirstOrDefault(item => item.Key == selectedKey) ?? categories[0];
            OnPropertyChanged(nameof(SelectedDiagnosticCategory));
            OnPropertyChanged(nameof(SelectedDiagnosticCategoryTitle));
        }
        finally
        {
            _isUpdatingDiagnosticCategories = false;
        }
    }

    private void ClearDiagnosticFilters()
    {
        _diagnosticSearchText = string.Empty;
        _selectedDiagnosticSeverityFilter = "Все уровни";
        _selectedDiagnosticDomainFilter = "Все области";
        _showOnlyBlockingDiagnostics = false;
        _selectedDiagnosticCategory = null;
        OnPropertyChanged(nameof(DiagnosticSearchText));
        OnPropertyChanged(nameof(SelectedDiagnosticSeverityFilter));
        OnPropertyChanged(nameof(SelectedDiagnosticDomainFilter));
        OnPropertyChanged(nameof(ShowOnlyBlockingDiagnostics));
        OnPropertyChanged(nameof(SelectedDiagnosticCategory));
        OnPropertyChanged(nameof(SelectedDiagnosticCategoryTitle));
        RefreshDiagnosticsView();
    }

    private void NotifyWorkspaceProperties()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(SourceFileName));
        OnPropertyChanged(nameof(SourceDirectory));
        OnPropertyChanged(nameof(FileLength));
        OnPropertyChanged(nameof(Sha256));
        OnPropertyChanged(nameof(ShortSha256));
        OnPropertyChanged(nameof(HasTextsDocument));
        OnPropertyChanged(nameof(TextsFileName));
        OnPropertyChanged(nameof(TextsPath));
        OnPropertyChanged(nameof(TextResourceCount));
        OnPropertyChanged(nameof(TextsStatusText));
        OnPropertyChanged(nameof(AttachTextsButtonText));
        OnPropertyChanged(nameof(HasSoundArchives));
        OnPropertyChanged(nameof(SoundArchiveCount));
        OnPropertyChanged(nameof(SoundResourceCount));
        OnPropertyChanged(nameof(SoundArchivePaths));
        OnPropertyChanged(nameof(AttachSoundsButtonText));
        OnPropertyChanged(nameof(ManagedBackups));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void SetNavigationEnabled(bool enabled)
    {
        foreach (var item in NavigationItems)
        {
            item.IsEnabled = item.Section switch
            {
                WorkspaceSection.Overview => true,
                WorkspaceSection.Texts => enabled && HasTextsDocument,
                _ => enabled,
            };
        }

        foreach (var database in Databases)
        {
            database.IsEnabled = enabled && database.IsAvailable;
        }
    }

    private void RaiseCommandStates()
    {
        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AttachTextsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AttachSoundsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleColumnModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NavigateCommand as RelayCommand<NavigationItemViewModel>)?.RaiseCanExecuteChanged();
        (SelectTableCommand as RelayCommand<DatabaseTableViewModel>)?.RaiseCanExecuteChanged();
        (AppendRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddMissingTextsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearTextsSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshValidationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelOperationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (FocusSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NavigateToDiagnosticCommand as RelayCommand<ValidationMessageViewModel>)?.RaiseCanExecuteChanged();
        (ImportBriefingAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelPendingSoundEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SavePendingSoundCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SavePendingSoundCurrentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreManagedBackupCommand as RelayCommand<ManagedBackupViewModel>)?.RaiseCanExecuteChanged();
        RaiseBriefingCommandStates();
    }

    private void CancelOperation()
    {
        if (_operationCancellation is { IsCancellationRequested: false })
        {
            _operationCancellation.Cancel();
        }
    }

    private void CancelValidation()
    {
        CancellationTokenSource? validation = Interlocked.Exchange(
            ref _validationCancellation,
            null);
        validation?.Cancel();
        validation?.Dispose();
    }

    private void ShowError(string message)
    {
        NotificationIsError = true;
        NotificationMessage = message;
    }

    private void SubscribeWorkspaceRecords(IEnumerable<DatabaseGroupViewModel> databases)
    {
        foreach (var record in databases.SelectMany(database => database.Tables).SelectMany(table => table.Records))
        {
            record.FieldValueCommitted += OnFieldValueCommitted;
        }
    }

    private void UnsubscribeWorkspaceRecords(IEnumerable<DatabaseGroupViewModel> databases)
    {
        foreach (var record in databases.SelectMany(database => database.Tables).SelectMany(table => table.Records))
        {
            record.FieldValueCommitted -= OnFieldValueCommitted;
            record.IsEditMode = false;
        }
    }

    private void SubscribeWorkspaceTexts(IEnumerable<LocalizationEntryViewModel> texts)
    {
        foreach (LocalizationEntryViewModel text in texts.Distinct())
        {
            text.ValueCommitted += OnLocalizationValueCommitted;
        }
    }

    private void UnsubscribeWorkspaceTexts(IEnumerable<LocalizationEntryViewModel> texts)
    {
        foreach (LocalizationEntryViewModel text in texts.Distinct())
        {
            text.ValueCommitted -= OnLocalizationValueCommitted;
        }
    }

    private void ApplyCurrentTableEditMode() =>
        ApplyTableEditMode(SelectedTable, IsEditMode && !IsBusy && IsRecordsSection && IsCurrentTableEditable);

    private static void ApplyTableEditMode(DatabaseTableViewModel? table, bool isEnabled)
    {
        if (table is null)
        {
            return;
        }

        foreach (var record in table.Records)
        {
            record.IsEditMode = isEnabled;
        }
    }

    private static void ReplaceCollection<T>(RangeObservableCollection<T> target, IEnumerable<T> source) =>
        target.ReplaceWith(source);

    private static string UnwrapMessage(Exception exception)
    {
        while (exception is TargetInvocationException or AggregateException && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception.Message;
    }

    private abstract record WorkspaceEditFrame;

    private sealed record FieldEditFrame(
        RecordRowViewModel Record,
        RecordFieldViewModel Field,
        DatabaseFieldState BeforeState,
        DatabaseFieldState AfterState) : WorkspaceEditFrame;

    private sealed record FieldBatchEditFrame(
        IReadOnlyList<FieldEditFrame> Edits) : WorkspaceEditFrame;

    private sealed record RecordPresenceEditFrame(
        DatabaseTableViewModel Table,
        RecordRowViewModel Record,
        bool BeforePresent,
        bool AfterPresent) : WorkspaceEditFrame;

    private sealed record TextEditFrame(
        LocalizationEntryViewModel Entry,
        TextResourceState BeforeState,
        TextResourceState AfterState) : WorkspaceEditFrame;

    private sealed record TextPresenceBatchEditFrame(
        IReadOnlyList<LocalizationEntryViewModel> Entries,
        bool BeforePresent,
        bool AfterPresent) : WorkspaceEditFrame;

    private sealed record CompositeEditFrame(
        IReadOnlyList<WorkspaceEditFrame> Frames,
        string UndoStatus,
        string RedoStatus) : WorkspaceEditFrame;

    private sealed record SoundStageEditFrame(
        PendingSoundEditViewModel? Before,
        PendingSoundEditViewModel? After) : WorkspaceEditFrame;
}

internal sealed class RecordFocusRequestedEventArgs(
    RecordRowViewModel record,
    int fieldId,
    bool beginEdit) : EventArgs
{
    public RecordRowViewModel Record { get; } = record;

    public int FieldId { get; } = fieldId;

    public bool BeginEdit { get; } = beginEdit;
}

internal sealed class SoundPreviewRequestedEventArgs(
    SoundResourceInfo resource,
    int phraseNumber) : EventArgs
{
    public SoundResourceInfo Resource { get; } = resource;

    public int PhraseNumber { get; } = phraseNumber;
}

internal sealed class ManagedBackupRestoreRequestedEventArgs(
    ManagedBackupViewModel backup) : EventArgs
{
    public ManagedBackupViewModel Backup { get; } = backup;
}
