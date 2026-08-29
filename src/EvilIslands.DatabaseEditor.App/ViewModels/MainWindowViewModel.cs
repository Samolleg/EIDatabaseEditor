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
    private RecordRowViewModel? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resourceSearchText = string.Empty;
    private string _textsSearchText = string.Empty;
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
        _recordsView = CollectionViewSource.GetDefaultView(Array.Empty<RecordRowViewModel>());
        _resourcesView = CollectionViewSource.GetDefaultView(Resources);
        _textsView = CollectionViewSource.GetDefaultView(TextResources);

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
            () => HasDocument && !HasChanges && !IsBusy);
        AttachSoundsCommand = new RelayCommand(
            () => AttachSoundsRequested?.Invoke(this, EventArgs.Empty),
            () => HasDocument && !HasChanges && !IsBusy);
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
    }

    public event EventHandler? OpenFileRequested;

    public event EventHandler? AttachTextsRequested;

    public event EventHandler? AttachSoundsRequested;

    public event EventHandler? SaveRequested;

    public event EventHandler? SaveCopyRequested;

    public event EventHandler? SearchFocusRequested;

    public event EventHandler<RecordFocusRequestedEventArgs>? RecordFocusRequested;

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel OverviewNavigationItem { get; }

    public NavigationItemViewModel TextsNavigationItem { get; }

    public ObservableCollection<NavigationItemViewModel> ServiceNavigationItems { get; }

    public RangeObservableCollection<ResourceRowViewModel> Resources { get; }

    public RangeObservableCollection<DatabaseGroupViewModel> Databases { get; }

    public RangeObservableCollection<LocalizationEntryViewModel> TextResources { get; }

    public ObservableCollection<ChangeEntryViewModel> Changes { get; }

    public RangeObservableCollection<ValidationMessageViewModel> Diagnostics { get; }

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

    public ICommand ToggleColumnModeCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public ICommand DismissNotificationCommand { get; }

    public ICommand RevertChangeCommand { get; }

    public ICommand NavigateToDiagnosticCommand { get; }

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

            if (SetProperty(ref _selectedRecord, value))
            {
                OnPropertyChanged(nameof(HasSelectedRecord));
                OnPropertyChanged(nameof(SelectedRecordHasTexts));
                OnPropertyChanged(nameof(SelectedRecordTextCount));
                OnPropertyChanged(nameof(SelectedRecordHasSoundReferences));
                OnPropertyChanged(nameof(SelectedRecordSoundCount));
                (AppendRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (AddMissingTextsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedRecord => SelectedRecord is not null;

    public bool SelectedRecordHasTexts => SelectedRecord?.HasRelatedTexts is true;

    public int SelectedRecordTextCount => SelectedRecord?.RelatedTextCount ?? 0;

    public bool SelectedRecordHasSoundReferences => SelectedRecord?.HasSoundReferences is true;

    public int SelectedRecordSoundCount => SelectedRecord?.ResolvedSoundCount ?? 0;

    public bool IsCurrentTableAddable => SelectedTable?.CanAppendRecords is true;

    public bool CurrentTableSupportsSounds => SelectedTable?.Key is
        "acks.answers" or "acks.cries" or "acks.others" or "quests.briefings";

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

    public async Task AttachTextsPathAsync(string path)
    {
        if (_workspace is null || HasChanges || IsBusy)
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
        if (_workspace is null || HasChanges || IsBusy || paths.Count == 0)
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
        _changesByPath.Clear();
        Changes.Clear();
        LastSavedPath = null;
        SearchText = string.Empty;
        ResourceSearchText = string.Empty;
        TextsSearchText = string.Empty;

        ReplaceCollection(Resources, workspace.Resources);
        ReplaceCollection(Databases, workspace.Databases);
        ReplaceCollection(TextResources, workspace.TextResources);
        ReplaceCollection(Diagnostics, workspace.Diagnostics);
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
        var fieldsWithSpecificProjections = columns
            .Where(column => column.Projection is not null &&
                             column.Projection.Kind != DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();
        var fieldsWithDecodedProjection = columns
            .Where(column => column.Projection?.Kind == DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();

        return columns
            .Where(column => column.Projection switch
            {
                null => !fieldsWithSpecificProjections.Contains(column.FieldId) &&
                        !fieldsWithDecodedProjection.Contains(column.FieldId),
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
            error = "В буфере обмена нет табличных значений.";
            ShowError(error);
            return false;
        }

        var seen = new HashSet<(
            RecordRowViewModel Record,
            int FieldId,
            DatabaseFieldProjectionKind? ProjectionKind,
            int ProjectionIndex)>();
        var pending = new List<(
            RecordRowViewModel Record,
            RecordFieldViewModel Field,
            DatabaseFieldProjection? Projection,
            string Value,
            DatabaseFieldState BeforeState)>();
        foreach (SpreadsheetPasteCell cell in cells)
        {
            var identity = (
                cell.Record,
                cell.FieldId,
                cell.Projection?.Kind,
                cell.Projection?.Index ?? -1);
            if (!SelectedTable.Records.Any(record => ReferenceEquals(record, cell.Record)) ||
                !seen.Add(identity))
            {
                error = "Диапазон вставки содержит неизвестную или повторяющуюся ячейку.";
                ShowError(error);
                return false;
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
        }
        catch (Exception exception)
        {
            foreach (var item in pending.AsEnumerable().Reverse())
            {
                item.Field.RestoreState(item.BeforeState);
            }

            RefreshChangedFields(pending.Select(item => (item.Record, item.Field)));
            error = $"Вставка отменена без частичных изменений: {UnwrapMessage(exception)}";
            ShowError(error);
            return false;
        }
        finally
        {
            _isApplyingSpreadsheetPaste = false;
        }

        if (edits.Count == 0)
        {
            StatusText = "Вставленные значения уже совпадают с содержимым выбранных ячеек.";
            return true;
        }

        RefreshChangedFields(edits.Select(edit => (edit.Record, edit.Field)));
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

            RefreshChangedFields(pending.Select(item => (item.Record, item.Field)));
            error = $"Вставка отменена без частичных изменений: {UnwrapMessage(exception)}";
            ShowError(error);
            return false;
        }

        WorkspaceEditFrame fieldFrame = edits.Count == 1 ? edits[0] : new FieldBatchEditFrame(edits.ToArray());
        WorkspaceEditFrame pasteFrame = automaticTexts.Count == 0
            ? fieldFrame
            : new CompositeEditFrame(
                [
                    fieldFrame,
                    new TextPresenceBatchEditFrame(automaticTexts, BeforePresent: false, AfterPresent: true),
                ],
                $"Отменена вставка и автотексты: {edits.Count:N0} ячеек.",
                $"Повторена вставка и автотексты: {edits.Count:N0} ячеек.");
        _undo.Push(pasteFrame);
        _redo.Clear();
        RefreshValidationDeferred();
        changedCellCount = edits.Count;
        StatusText = automaticTexts.Count == 0
            ? $"Вставлено из буфера: {changedCellCount:N0} ячеек."
            : $"Вставлено ячеек: {changedCellCount:N0}; создано записей texts.res: {automaticTexts.Count:N0}.";
        RaiseCommandStates();
        return true;
    }

    internal void ReportSpreadsheetCopy(int rowCount, int columnCount)
    {
        NotificationIsError = false;
        StatusText = $"Скопировано: {rowCount:N0} × {columnCount:N0} ячеек. Можно вставить в Excel или другую таблицу.";
    }

    internal void ReportSpreadsheetError(string message) => ShowError(message);

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

    private void RefreshLocalizationOwners(LocalizationEntryViewModel entry)
    {
        foreach (RecordRowViewModel record in Databases
                     .SelectMany(database => database.Tables)
                     .SelectMany(table => table.Records)
                     .Where(record => record.RelatedTexts.Contains(entry)))
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
