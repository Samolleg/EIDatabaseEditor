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
    private RecordRowViewModel? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resourceSearchText = string.Empty;
    private string _statusText = "Готово. Откройте копию database.res для анализа.";
    private string? _notificationMessage;
    private bool _notificationIsError;
    private bool _isBusy;
    private bool _isOperationCancelable;
    private string _busyText = string.Empty;
    private bool _isEditMode;
    private bool _showAllColumns = true;
    private int _filteredRecordCount;
    private string? _lastSavedPath;
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
        Changes = [];
        Diagnostics = [];
        _recordsView = CollectionViewSource.GetDefaultView(Array.Empty<RecordRowViewModel>());
        _resourcesView = CollectionViewSource.GetDefaultView(Resources);

        NavigationItems =
        [
            new(WorkspaceSection.Overview, "Обзор", "⌂") { IsSelected = true },
            new(WorkspaceSection.Resources, "Ресурсы", "▦") { IsEnabled = false },
            new(WorkspaceSection.Changes, "Изменения", "≠") { IsEnabled = false },
            new(WorkspaceSection.Validation, "Проверка", "✓") { IsEnabled = false },
        ];
        OverviewNavigationItem = NavigationItems[0];
        ServiceNavigationItems = new ObservableCollection<NavigationItemViewModel>(NavigationItems.Skip(1));

        OpenCommand = new RelayCommand(() => OpenFileRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        SaveCopyCommand = new RelayCommand(() => SaveCopyRequested?.Invoke(this, EventArgs.Empty), () => HasDocument && !IsBusy);
        ToggleEditModeCommand = new RelayCommand(
            ToggleEditMode,
            () => HasDocument && !IsBusy && (IsEditMode || (IsRecordsSection && IsCurrentTableEditable)));
        UndoCommand = new RelayCommand(Undo, () => !IsBusy && _undo.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => !IsBusy && _redo.Count > 0);
        NavigateCommand = new RelayCommand<NavigationItemViewModel>(Navigate, item => item?.IsEnabled is true);
        SelectTableCommand = new RelayCommand<DatabaseTableViewModel>(SelectTable, table => table?.IsEnabled is true);
        AppendRecordCommand = new RelayCommand(
            AppendSelectedRecord,
            () => IsEditMode && IsRecordsSection && IsCurrentTableAddable && HasSelectedRecord && !IsBusy);
        RefreshValidationCommand = new RelayCommand(RefreshValidation, () => HasDocument && !IsBusy);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
        ClearResourceSearchCommand = new RelayCommand(() => ResourceSearchText = string.Empty, () => !string.IsNullOrEmpty(ResourceSearchText));
        ToggleColumnModeCommand = new RelayCommand(
            ToggleColumnMode,
            () => IsRecordsSection && SelectedTable is { Columns.Count: > 12 } && !IsBusy);
        FocusSearchCommand = new RelayCommand(() => SearchFocusRequested?.Invoke(this, EventArgs.Empty), () => HasDocument);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => IsBusy && IsOperationCancelable);
        DismissNotificationCommand = new RelayCommand(() => NotificationMessage = null, () => HasNotification);
        RevertChangeCommand = new RelayCommand<ChangeEntryViewModel>(
            RevertChange,
            change => change is { IsRecordAddition: false } && !IsBusy);
        NavigateToDiagnosticCommand = new RelayCommand<ValidationMessageViewModel>(
            NavigateToDiagnostic,
            diagnostic => diagnostic?.CanNavigate is true && HasDocument && !IsBusy);
    }

    public event EventHandler? OpenFileRequested;

    public event EventHandler? SaveCopyRequested;

    public event EventHandler? EditModeConfirmationRequested;

    public event EventHandler<CancelEventArgs>? EditModeEndingRequested;

    public event EventHandler? SearchFocusRequested;

    public event EventHandler<RecordFocusRequestedEventArgs>? RecordFocusRequested;

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel OverviewNavigationItem { get; }

    public ObservableCollection<NavigationItemViewModel> ServiceNavigationItems { get; }

    public RangeObservableCollection<ResourceRowViewModel> Resources { get; }

    public RangeObservableCollection<DatabaseGroupViewModel> Databases { get; }

    public ObservableCollection<ChangeEntryViewModel> Changes { get; }

    public RangeObservableCollection<ValidationMessageViewModel> Diagnostics { get; }

    public ICommand OpenCommand { get; }

    public ICommand SaveCopyCommand { get; }

    public ICommand ToggleEditModeCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand SelectTableCommand { get; }

    public ICommand AppendRecordCommand { get; }

    public ICommand RefreshValidationCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ClearResourceSearchCommand { get; }

    public ICommand ToggleColumnModeCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public ICommand DismissNotificationCommand { get; }

    public ICommand RevertChangeCommand { get; }

    public ICommand NavigateToDiagnosticCommand { get; }

    public ICollectionView RecordsView
    {
        get => _recordsView;
        private set => SetProperty(ref _recordsView, value);
    }

    public ICollectionView ResourcesView
    {
        get => _resourcesView;
        private set => SetProperty(ref _resourcesView, value);
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
            OnPropertyChanged(nameof(BreadcrumbText));
            OnPropertyChanged(nameof(EditModeButtonText));
            RaiseCommandStates();
        }
    }

    public string PageTitle => CurrentSection switch
    {
        WorkspaceSection.Overview => "Обзор базы",
        WorkspaceSection.Resources => "Ресурсы контейнера",
        WorkspaceSection.Records => SelectedTable?.Title ?? "Таблица базы",
        WorkspaceSection.Changes => "Изменения",
        WorkspaceSection.Validation => "Проверка целостности",
        _ => "EI Database Studio",
    };

    public string PageSubtitle => CurrentSection switch
    {
        WorkspaceSection.Overview => "Безопасное рабочее пространство: исходный файл всегда остаётся неизменным.",
        WorkspaceSection.Resources => "Содержимое RES-контейнера, смещения и размеры внутренних ресурсов.",
        WorkspaceSection.Records when SelectedTable is not null =>
            $"{SelectedTable.GroupTitle} · {SelectedTable.ResourceName} · строки — записи, столбцы — поля · {SelectedTable.StatusText.ToLowerInvariant()}.",
        WorkspaceSection.Changes => "Только отличия от открытого исходного файла.",
        WorkspaceSection.Validation => "Ошибки структуры и предупреждения по ссылочной целостности.",
        _ => string.Empty,
    };

    public bool IsRecordsSection => CurrentSection == WorkspaceSection.Records;

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
            OnPropertyChanged(nameof(CurrentTableRecordCount));
            OnPropertyChanged(nameof(IsCurrentTableReadOnly));
            OnPropertyChanged(nameof(IsCurrentTableEditable));
            OnPropertyChanged(nameof(IsCurrentTableAddable));
            OnPropertyChanged(nameof(EditModeButtonText));
            OnPropertyChanged(nameof(VisibleSpreadsheetColumns));
            OnPropertyChanged(nameof(SpreadsheetColumnCountText));
            OnPropertyChanged(nameof(ColumnModeButtonText));
            RaiseCommandStates();
        }
    }

    public string BreadcrumbText => CurrentSection == WorkspaceSection.Records && SelectedTable is not null
        ? $"database.res  ›  {SelectedTable.ResourceName}  ›  {SelectedTable.Title}"
        : $"database.res  ›  {PageTitle}";

    public string CurrentTableStatusText => SelectedTable?.StatusText ?? "—";

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
                (AppendRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedRecord => SelectedRecord is not null;

    public bool IsCurrentTableAddable => SelectedTable?.CanAppendRecords is true;

    public IReadOnlyList<SpreadsheetColumnViewModel> VisibleSpreadsheetColumns
    {
        get
        {
            if (SelectedTable is null || ShowAllColumns || SelectedTable.Columns.Count <= 12)
            {
                return SelectedTable?.Columns ?? [];
            }

            var result = new List<SpreadsheetColumnViewModel>(12);
            foreach (var column in SelectedTable.Columns)
            {
                if (column.IsNameField)
                {
                    result.Add(column);
                }
            }

            foreach (var column in SelectedTable.Columns)
            {
                if (result.Count >= 12)
                {
                    break;
                }

                if (!column.IsNameField && column.IsPrimitive &&
                    (column.IsEditable || column.Key.Contains("code", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(column);
                }
            }

            foreach (var column in SelectedTable.Columns)
            {
                if (result.Count >= 12)
                {
                    break;
                }

                if (!result.Contains(column))
                {
                    result.Add(column);
                }
            }

            return result;
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
        : $"{VisibleSpreadsheetColumns.Count:N0} из {SelectedTable.Columns.Count:N0} столбцов";

    public string ColumnModeButtonText => ShowAllColumns ? "Основные столбцы" : "Все столбцы";

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

    public int FilteredRecordCount
    {
        get => _filteredRecordCount;
        private set => SetProperty(ref _filteredRecordCount, value);
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
            OnPropertyChanged(nameof(EditModeButtonText));
            RaiseCommandStates();
        }
    }

    public string EditModeButtonText => IsRecordsSection && IsCurrentTableReadOnly && !IsEditMode
        ? "Только просмотр"
        : IsEditMode
            ? "Завершить редактирование"
            : "Режим редактирования";

    public bool HasDocument => _workspace is not null;

    public bool HasChanges => Changes.Count > 0;

    public string SourcePath => _workspace?.SourcePath ?? "Файл не открыт";

    public string SourceFileName => _workspace is null ? "database.res не открыт" : Path.GetFileName(_workspace.SourcePath);

    public string SourceDirectory => _workspace is null ? "Выберите файл вручную — путь к игре не открывается автоматически." : Path.GetDirectoryName(_workspace.SourcePath) ?? string.Empty;

    public long FileLength => _workspace?.FileLength ?? 0;

    public string Sha256 => _workspace?.Sha256 ?? "—";

    public string ShortSha256 => _workspace is null ? "—" : $"{_workspace.Sha256[..Math.Min(12, _workspace.Sha256.Length)]}…";

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
            LastSavedPath = result.TargetPath;
            NotificationIsError = false;
            NotificationMessage = $"Копия создана и проверена: {Path.GetFileName(result.TargetPath)} · SHA-256 {result.Sha256[..Math.Min(12, result.Sha256.Length)]}…";
            StatusText = $"Копия сохранена: {result.TargetPath}";
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

    public void ConfirmEditMode()
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        if (IsRecordsSection && !IsCurrentTableEditable)
        {
            ShowError("Выбранная таблица доступна только для просмотра. Редактирование появится после отдельной проверки её схемы.");
            return;
        }

        IsEditMode = true;
        StatusText = "Режим редактирования включён. Исходный файл по-прежнему заблокирован.";
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
    }

    private void ApplyWorkspace(WorkspaceSnapshot workspace)
    {
        ApplyTableEditMode(SelectedTable, false);
        UnsubscribeWorkspaceRecords(Databases);
        _workspace = workspace;
        IsEditMode = false;
        _undo.Clear();
        _redo.Clear();
        _changesByPath.Clear();
        Changes.Clear();
        LastSavedPath = null;
        SearchText = string.Empty;
        ResourceSearchText = string.Empty;

        ReplaceCollection(Resources, workspace.Resources);
        ReplaceCollection(Databases, workspace.Databases);
        ReplaceCollection(Diagnostics, workspace.Diagnostics);
        SubscribeWorkspaceRecords(Databases);
        ApplyDiagnosticsToRecords(Diagnostics);

        ResourcesView = CollectionViewSource.GetDefaultView(Resources);
        ResourcesView.Filter = FilterResource;
        foreach (var group in Databases)
        {
            group.IsExpanded = false;
        }

        CurrentSection = WorkspaceSection.Overview;
        SelectedTable = null;
        SelectedRecord = null;
        RecordsView = CollectionViewSource.GetDefaultView(Array.Empty<RecordRowViewModel>());
        FilteredRecordCount = 0;
        SetNavigationEnabled(true);
        RefreshAllCounts();
        NotifyWorkspaceProperties();
        RaiseCommandStates();
    }

    private void ToggleEditMode()
    {
        if (IsEditMode)
        {
            var endingEventArgs = new CancelEventArgs();
            EditModeEndingRequested?.Invoke(this, endingEventArgs);
            if (endingEventArgs.Cancel)
            {
                StatusText = "Сначала исправьте или отмените значение в активной клетке.";
                return;
            }

            IsEditMode = false;
            StatusText = HasChanges
                ? "Редактирование приостановлено. Сделанные изменения сохранены в рабочей памяти."
                : "Режим редактирования выключен.";
            return;
        }

        EditModeConfirmationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleColumnMode()
    {
        if (SelectedTable is null || SelectedTable.Columns.Count <= 12)
        {
            return;
        }

        ShowAllColumns = !ShowAllColumns;
        StatusText = ShowAllColumns
            ? $"Показаны все поля таблицы: {SelectedTable.Columns.Count:N0} столбцов."
            : $"Показаны основные поля: {VisibleSpreadsheetColumns.Count:N0} из {SelectedTable.Columns.Count:N0} столбцов.";
    }

    private void Navigate(NavigationItemViewModel? item)
    {
        if (item?.IsEnabled is not true)
        {
            return;
        }

        if (item.Section != WorkspaceSection.Records && IsEditMode)
        {
            IsEditMode = false;
        }

        CurrentSection = item.Section;
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

        if (IsEditMode && !table.IsEditable)
        {
            IsEditMode = false;
        }

        ApplyTableEditMode(SelectedTable, false);
        SearchText = string.Empty;
        ShowAllColumns = true;
        SelectedRecord = null;
        SelectedTable = table;
        ApplyCurrentTableEditMode();
        RecordsView = CollectionViewSource.GetDefaultView(table.Records);
        RecordsView.Filter = FilterRecord;
        CurrentSection = WorkspaceSection.Records;
        RefreshRecordFilter();
        StatusText = $"{table.ResourceName} · {table.Title} · {table.RecordCount:N0} записей · {table.StatusText.ToLowerInvariant()}";
    }

    private void AppendSelectedRecord()
    {
        DatabaseTableViewModel? table = SelectedTable;
        RecordRowViewModel? template = SelectedRecord;
        if (table is null || template is null || !IsEditMode || !table.CanAppendRecords)
        {
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
                         "до этого сохранение копии заблокировано.";
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
        if (sender is not RecordRowViewModel record)
        {
            return;
        }

        var field = eventArgs.Field;
        _undo.Push(new FieldEditFrame(record, field, eventArgs.BeforeState, eventArgs.AfterState));
        _redo.Clear();
        RefreshChangedField(record, field);
        RefreshValidationDeferred();
        StatusText = $"Изменено: {record.DisplayName} · {field.DisplayName}";
        RaiseCommandStates();
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

            default:
                error = "Неизвестный тип операции Undo/Redo.";
                return false;
        }
    }

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
        RefreshReferenceChoicesForTargetTable(table.Key);
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

    private void RevertChange(ChangeEntryViewModel? change)
    {
        if (change is null)
        {
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
        _undo.Push(new FieldEditFrame(change.Record, field, beforeState, afterState));
        _redo.Clear();
        RefreshChangedField(change.Record, field);
        RefreshValidationDeferred();
        StatusText = $"Поле возвращено к исходному значению: {change.FieldName}";
        RaiseCommandStates();
    }

    private void RefreshChangedField(RecordRowViewModel record, RecordFieldViewModel field)
    {
        record.RefreshState();
        RefreshReferenceChoicesForTargetTable(record.TableKey, field.Id, record.NameFieldId);
        if (record.IsAdded)
        {
            string key = GetRecordInsertionChangeKey(record);
            if (_changesByPath.TryGetValue(key, out ChangeEntryViewModel? insertion))
            {
                insertion.NewValue = record.DisplayName;
            }

            RefreshAllCounts();
            OnPropertyChanged(nameof(HasChanges));
            (SaveCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RecordsView.Refresh();
            return;
        }

        if (field.IsModified)
        {
            if (_changesByPath.TryGetValue(field.Path, out var existing))
            {
                existing.NewValue = field.EditorText;
            }
            else
            {
                var entry = new ChangeEntryViewModel(record, field);
                _changesByPath.Add(field.Path, entry);
                Changes.Add(entry);
            }
        }
        else if (_changesByPath.Remove(field.Path, out var removed))
        {
            Changes.Remove(removed);
        }

        RefreshAllCounts();
        OnPropertyChanged(nameof(HasChanges));
        (SaveCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RecordsView.Refresh();
    }

    private void RefreshReferenceChoicesForTargetTable(
        string targetTableKey,
        int? changedFieldId = null,
        int? changedNameFieldId = null)
    {
        foreach (DatabaseScalarReference reference in DatabaseReferenceCatalog.ScalarReferences.Where(rule =>
                     rule.TargetTableKey.Equals(targetTableKey, StringComparison.Ordinal) &&
                     (changedFieldId is null ||
                      changedFieldId == rule.TargetFieldId ||
                      changedFieldId == changedNameFieldId)))
        {
            DatabaseTableViewModel? source = Databases.SelectMany(database => database.Tables)
                .FirstOrDefault(table => table.Key.Equals(reference.SourceTableKey, StringComparison.Ordinal));
            DatabaseTableViewModel? target = Databases.SelectMany(database => database.Tables)
                .FirstOrDefault(table => table.Key.Equals(reference.TargetTableKey, StringComparison.Ordinal));
            RangeObservableCollection<ReferenceChoiceViewModel>? choices = source?.Columns
                .FirstOrDefault(column => column.FieldId == reference.SourceFieldId && column.Projection is null)
                ?.ReferenceChoices;
            if (source is null || target is null || choices is null)
            {
                continue;
            }

            var byValue = new Dictionary<string, ReferenceChoiceViewModel>(StringComparer.OrdinalIgnoreCase);
            if (reference.AllowEmpty)
            {
                byValue[string.Empty] = new ReferenceChoiceViewModel(string.Empty, "— не задано —", true);
            }

            if (!string.IsNullOrWhiteSpace(reference.Sentinel))
            {
                byValue[reference.Sentinel] = new ReferenceChoiceViewModel(
                    reference.Sentinel,
                    $"{reference.Sentinel} — служебное значение",
                    true);
            }

            foreach (RecordRowViewModel targetRecord in target.Records)
            {
                string value = GetRecordFieldText(targetRecord, reference.TargetFieldId);
                if (value.Length == 0 || byValue.ContainsKey(value))
                {
                    continue;
                }

                string displayName = targetRecord.DisplayName.Trim();
                byValue[value] = new ReferenceChoiceViewModel(
                    value,
                    displayName.Length == 0 || displayName.Equals(value, StringComparison.OrdinalIgnoreCase)
                        ? value
                        : $"{value} — {displayName}",
                    true);
            }

            foreach (RecordRowViewModel sourceRecord in source.Records)
            {
                string value = GetRecordFieldText(sourceRecord, reference.SourceFieldId);
                if (value.Length == 0 || byValue.ContainsKey(value))
                {
                    continue;
                }

                byValue[value] = new ReferenceChoiceViewModel(
                    value,
                    $"{value} — отсутствует в «{target.Title}»",
                    false);
            }

            ReferenceChoiceViewModel[] updated = byValue.Values
                .OrderBy(choice => choice.Value.Length == 0 ? 0 : choice.IsKnown ? 1 : 2)
                .ThenBy(choice => choice.DisplayText, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            choices.ReplaceWith(updated);
        }
    }

    private static string GetRecordFieldText(RecordRowViewModel record, int fieldId) =>
        record.FieldDefinitions.FirstOrDefault(field => field.Id == fieldId)?.Value.ToString()?.Trim() ?? string.Empty;

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
        OnPropertyChanged(nameof(HasChanges));
    }

    private void SetNavigationEnabled(bool enabled)
    {
        foreach (var item in NavigationItems)
        {
            item.IsEnabled = enabled || item.Section == WorkspaceSection.Overview;
        }

        foreach (var database in Databases)
        {
            database.IsEnabled = enabled && database.IsAvailable;
        }
    }

    private void RaiseCommandStates()
    {
        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleEditModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleColumnModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NavigateCommand as RelayCommand<NavigationItemViewModel>)?.RaiseCanExecuteChanged();
        (SelectTableCommand as RelayCommand<DatabaseTableViewModel>)?.RaiseCanExecuteChanged();
        (AppendRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    private sealed record RecordPresenceEditFrame(
        DatabaseTableViewModel Table,
        RecordRowViewModel Record,
        bool BeforePresent,
        bool AfterPresent) : WorkspaceEditFrame;
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
