using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App;

public partial class MainWindow : Window
{
    internal static TimeSpan RecoveryAutosaveInterval { get; } = TimeSpan.FromMinutes(5);

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const string LocalizationKeyColumnIdentity = "texts:key";
    private const string LocalizationTitleColumnIdentity = "texts:title";
    private const string LocalizationDescriptionColumnIdentity = "texts:description";
    private const string SoundSummaryColumnIdentity = "sound:summary";
    private const string SoundPathColumnIdentity = "sound:path";
    private static readonly double[] InterfaceScaleSteps = [1.0, 1.1, 1.25, 1.5];
    private static readonly IValueConverter ProjectionConverter = new SpreadsheetProjectionConverter();
    private readonly MainWindowViewModel _viewModel;
    private readonly EditorPreferencesStore _preferences;
    private readonly RecoverySnapshotStore _recoveryStore;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly Dictionary<DataGridColumn, SpreadsheetColumnViewModel> _spreadsheetColumns = [];
    private DataGridColumn? _localizationKeyColumn;
    private DataGridColumn? _localizationTitleColumn;
    private DataGridColumn? _localizationDescriptionColumn;
    private DataGridColumn? _soundSummaryColumn;
    private DataGridColumn? _soundPathColumn;
    private FormulaCellContext? _formulaCellContext;
    private FormulaCellContext? _textsFormulaCellContext;
    private MainWindowViewModel? _spreadsheetViewModel;
    private HwndSource? _windowSource;
    private string? _columnLayoutTableKey;
    private EditorLayoutState _layout;
    private bool _hasExplicitInterfaceScale;
    private bool _allowClose;
    private bool _closePromptPending;
    private bool _recoverySaveRunning;
    private bool _startupRecoveryChecked;
    private DetachedDetailsWindow? _detailsWindow;
    private bool _preserveDetailsDetachedState;
    private bool _detailsDetachScheduled;
    private readonly MediaPlayer _soundPreviewPlayer = new();
    private string? _soundPreviewPath;

    public MainWindow() : this(new EditorPreferencesStore(), new RecoverySnapshotStore())
    {
    }

    internal MainWindow(EditorPreferencesStore preferences, RecoverySnapshotStore? recoveryStore = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _recoveryStore = recoveryStore ?? new RecoverySnapshotStore();
        InitializeComponent();
        _layout = _preferences.GetLayout();
        ApplyStoredLayout();
        double? storedInterfaceScale = _preferences.GetInterfaceScale();
        _hasExplicitInterfaceScale = storedInterfaceScale.HasValue;
        SetInterfaceScale(storedInterfaceScale ?? 1.0, persist: false);
        SourceInitialized += Window_SourceInitialized;
        ContentRendered += Window_ContentRendered;
        Closed += Window_Closed;
        RecordsGrid.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            RecordsGrid_CopyExecuted,
            RecordsGrid_CopyCanExecute));
        RecordsGrid.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            RecordsGrid_PasteExecuted,
            RecordsGrid_PasteCanExecute));
        DataContextChanged += MainWindow_DataContextChanged;
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.OpenFileRequested += ViewModel_OpenFileRequested;
        _viewModel.AttachTextsRequested += ViewModel_AttachTextsRequested;
        _viewModel.AttachSoundsRequested += ViewModel_AttachSoundsRequested;
        _viewModel.SaveRequested += ViewModel_SaveRequested;
        _viewModel.SaveCopyRequested += ViewModel_SaveCopyRequested;
        _viewModel.SearchFocusRequested += ViewModel_SearchFocusRequested;
        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RecoveryAutosaveInterval,
        };
        _recoveryTimer.Tick += RecoveryTimer_Tick;
        _recoveryTimer.Start();
    }

    internal bool IsDetailsPaneDetached => _detailsWindow is not null;

    private async void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_recoverySaveRunning || !_viewModel.HasUnsavedWork || _viewModel.IsBusy || !_viewModel.HasDocument)
        {
            return;
        }

        _recoverySaveRunning = true;
        RecoverySnapshotDraft? draft = null;
        try
        {
            string sourcePath = _viewModel.SourcePath;
            PendingSoundEditViewModel? pendingSound = _viewModel.PendingSoundEdit;
            draft = _recoveryStore.Prepare(sourcePath);
            SavePairCopyResult? result = await _viewModel.SaveRecoveryCopyPathAsync(draft.DatabasePath);
            if (result is null)
            {
                _recoveryStore.Abandon(draft);
                return;
            }

            _recoveryStore.Complete(sourcePath, draft, result, pendingSound);
        }
        catch (Exception exception)
        {
            if (draft is not null)
            {
                _recoveryStore.Abandon(draft);
            }

            _viewModel.ReportSpreadsheetError($"Аварийная копия не создана: {exception.Message}");
        }
        finally
        {
            _recoverySaveRunning = false;
        }
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_startupRecoveryChecked)
        {
            return;
        }

        _startupRecoveryChecked = true;
        IReadOnlyList<RecoverySnapshot> snapshots = _recoveryStore.FindAvailable();
        if (snapshots.Count == 0)
        {
            return;
        }

        var dialog = new RecoverySessionDialog(snapshots)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() is true && dialog.SelectedSnapshot is not null)
        {
            await OpenRecoverySnapshotAsync(dialog.SelectedSnapshot);
        }
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged -= SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel.RecordFocusRequested -= ViewModel_RecordFocusRequested;
            _spreadsheetViewModel.PlayBriefingAudioRequested -= ViewModel_PlayBriefingAudioRequested;
            _spreadsheetViewModel.StopBriefingAudioRequested -= ViewModel_StopBriefingAudioRequested;
            _spreadsheetViewModel.ImportBriefingAudioRequested -= ViewModel_ImportBriefingAudioRequested;
            _spreadsheetViewModel.SavePendingSoundCopyRequested -= ViewModel_SavePendingSoundCopyRequested;
        }

        _spreadsheetViewModel = e.NewValue as MainWindowViewModel;
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged += SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel.RecordFocusRequested += ViewModel_RecordFocusRequested;
            _spreadsheetViewModel.PlayBriefingAudioRequested += ViewModel_PlayBriefingAudioRequested;
            _spreadsheetViewModel.StopBriefingAudioRequested += ViewModel_StopBriefingAudioRequested;
            _spreadsheetViewModel.ImportBriefingAudioRequested += ViewModel_ImportBriefingAudioRequested;
            _spreadsheetViewModel.SavePendingSoundCopyRequested += ViewModel_SavePendingSoundCopyRequested;
        }

        RebuildSpreadsheetColumns();
    }

    private async void ViewModel_PlayBriefingAudioRequested(object? sender, SoundPreviewRequestedEventArgs e)
    {
        try
        {
            StopSoundPreview();
            byte[] bytes = await SoundArchiveCatalog.ReadResourceAsync(e.Resource);
            string directory = Path.Combine(
                Path.GetTempPath(),
                "EvilIslands.DatabaseEditor",
                "audio-preview");
            Directory.CreateDirectory(directory);
            string extension = "." + e.Resource.Format.ToLowerInvariant();
            string target = Path.Combine(directory, $"phrase-{e.PhraseNumber}-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(target, bytes);
            _soundPreviewPath = target;
            _soundPreviewPlayer.Open(new Uri(target, UriKind.Absolute));
            _soundPreviewPlayer.Play();
        }
        catch (Exception exception)
        {
            StopSoundPreview();
            (_spreadsheetViewModel ?? _viewModel).ReportSpreadsheetError(
                $"Не удалось воспроизвести звуковой файл: {exception.Message}");
        }
    }

    private void ViewModel_StopBriefingAudioRequested(object? sender, EventArgs e) => StopSoundPreview();

    private async void ViewModel_ImportBriefingAudioRequested(object? sender, EventArgs e)
    {
        MainWindowViewModel viewModel = _spreadsheetViewModel ?? _viewModel;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить или заменить звук выбранной реплики",
            Filter = "Звук Брифинга (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) is true)
        {
            await viewModel.ImportBriefingAudioPathAsync(dialog.FileName);
        }
    }

    private async void ViewModel_SavePendingSoundCopyRequested(object? sender, EventArgs e)
    {
        MainWindowViewModel viewModel = _spreadsheetViewModel ?? _viewModel;
        PendingSoundEditViewModel? edit = viewModel.PendingSoundEdit;
        if (edit is null)
        {
            return;
        }

        string sourceName = Path.GetFileNameWithoutExtension(edit.ArchiveName);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить проверенную копию звукового RES",
            Filter = "Evil Islands RES-контейнер (*.res)|*.res",
            DefaultExt = ".res",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false,
            CreatePrompt = false,
            RestoreDirectory = true,
            InitialDirectory = GetStagingDirectory(),
            FileName = $"{sourceName}.audio-edited.res",
        };
        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        string targetPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(targetPath, Path.GetFullPath(edit.ArchivePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowSafetyDialog(
                "Исходный звук защищён",
                "Первая версия записи звука не перезаписывает подключённый RES.",
                "Выберите новое имя в staging-папке. После проверки копию можно установить отдельно вручную.");
            return;
        }

        if (File.Exists(targetPath))
        {
            ShowSafetyDialog(
                "Перезапись запрещена",
                "Копия звукового RES с таким именем уже существует.",
                "Выберите новое имя; существующий файл редактор не заменяет.");
            return;
        }

        await viewModel.SavePendingSoundCopyPathAsync(targetPath);
    }

    private void StopSoundPreview()
    {
        _soundPreviewPlayer.Stop();
        _soundPreviewPlayer.Close();
        if (_soundPreviewPath is not { } path)
        {
            return;
        }

        _soundPreviewPath = null;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Windows Media Foundation may release the temporary file one dispatcher turn later.
        }
        catch (UnauthorizedAccessException)
        {
            // Preview cleanup must never interrupt editing or saving the RES documents.
        }
    }

    private void ViewModel_RecordFocusRequested(object? sender, RecordFocusRequestedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_spreadsheetViewModel?.SelectedTable?.Records.Contains(e.Record) is not true)
                {
                    return;
                }

                RecordsGrid.SelectedItem = e.Record;
                RecordsGrid.ScrollIntoView(e.Record);
                DataGridColumn? identityColumn = _spreadsheetColumns
                    .FirstOrDefault(pair => pair.Value.FieldId == e.FieldId)
                    .Key;
                if (identityColumn is null)
                {
                    return;
                }

                RecordsGrid.ScrollIntoView(e.Record, identityColumn);
                RecordsGrid.CurrentCell = new DataGridCellInfo(e.Record, identityColumn);
                RecordsGrid.Focus();
                if (e.BeginEdit)
                {
                    RecordsGrid.BeginEdit();
                }
            },
            DispatcherPriority.Loaded);
    }

    private async void ViewModel_AttachTextsRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Подключить texts.res",
            Filter = "Evil Islands texts.res|texts.res;*.res|RES-контейнеры (*.res)|*.res|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            InitialDirectory = _viewModel.HasDocument
                ? _viewModel.SourceDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) is true)
        {
            await _viewModel.AttachTextsPathAsync(dialog.FileName);
        }
    }

    private async void ViewModel_AttachSoundsRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Подключить RES со звуковыми файлами",
            Filter = "Звуковые RES (*.res)|*.res|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = true,
            RestoreDirectory = true,
            InitialDirectory = _viewModel.HasDocument
                ? _viewModel.SourceDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) is true)
        {
            await _viewModel.AttachSoundPathsAsync(dialog.FileNames);
        }
    }

    private void SpreadsheetViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedTable) or
            nameof(MainWindowViewModel.VisibleSpreadsheetColumns))
        {
            RebuildSpreadsheetColumns();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsEditMode) or nameof(MainWindowViewModel.IsBusy))
        {
            UpdateSpreadsheetColumnReadOnlyStates();
            RefreshFormulaBar();
            RefreshTextsFormulaBar();
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.ChangeCount) or nameof(MainWindowViewModel.HasChanges))
        {
            Dispatcher.BeginInvoke(RefreshFormulaBar, DispatcherPriority.DataBind);
            Dispatcher.BeginInvoke(RefreshTextsFormulaBar, DispatcherPriority.DataBind);
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentSection))
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    RefreshFormulaBar();
                    RefreshTextsFormulaBar();
                    UpdateDetailsPaneLayout();
                },
                DispatcherPriority.DataBind);
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRecord) or
            nameof(MainWindowViewModel.SelectedRecordHasSoundReferences))
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(SoundLinksGrid);
                    viewer?.ScrollToLeftEnd();
                    viewer?.ScrollToTop();
                    UpdateDetailsPaneLayout();
                },
                DispatcherPriority.DataBind);
        }
    }

    private void RebuildSpreadsheetColumns()
    {
        if (RecordsGrid is null)
        {
            return;
        }

        PersistCurrentColumnWidths();
        CaptureAndPersistLayout();
        ClearFormulaBar();
        RecordsGrid.FrozenColumnCount = 0;
        const int fixedColumnCount = 1;
        while (RecordsGrid.Columns.Count > fixedColumnCount)
        {
            RecordsGrid.Columns.RemoveAt(RecordsGrid.Columns.Count - 1);
        }

        _spreadsheetColumns.Clear();
        _localizationKeyColumn = null;
        _localizationTitleColumn = null;
        _localizationDescriptionColumn = null;
        _soundSummaryColumn = null;
        _soundPathColumn = null;
        if (_spreadsheetViewModel?.SelectedTable is null)
        {
            _columnLayoutTableKey = null;
            ClearFormulaBar();
            return;
        }

        string tableKey = _spreadsheetViewModel.SelectedTable.Key;
        var headerTemplate = (DataTemplate)FindResource("SpreadsheetColumnHeaderTemplate");
        var headerStyle = (Style)FindResource("SpreadsheetColumnHeaderStyle");
        var identityHeaderStyle = (Style)FindResource("DatabaseIdentityColumnHeaderStyle");
        var technicalHeaderStyle = (Style)FindResource("TechnicalColumnHeaderStyle");
        var elementStyle = (Style)FindResource("SpreadsheetCellText");
        var editingStyle = (Style)FindResource("SpreadsheetCellEditor");
        int nameFieldId = _spreadsheetViewModel.SelectedTable.Records.FirstOrDefault()?.NameFieldId ?? -1;
        var localizationColumnsAdded = false;
        DataGridColumn? nameColumn = null;
        foreach (var descriptor in _spreadsheetViewModel.VisibleSpreadsheetColumns)
        {
            Style columnElementStyle = CreateSpreadsheetElementStyle(descriptor, elementStyle);
            Style columnEditingStyle = CreateSpreadsheetEditingStyle(descriptor, editingStyle);
            DataGridColumn column;
            if (descriptor.Projection is not null && descriptor.IsEditable)
            {
                column = new DataGridTemplateColumn
                {
                    SortMemberPath = descriptor.Identity,
                    CellTemplate = CreateSpreadsheetTextTemplate(
                        typeof(TextBlock),
                        TextBlock.TextProperty,
                        CreateSpreadsheetBinding(descriptor),
                        columnElementStyle),
                    CellEditingTemplate = CreateSpreadsheetTextTemplate(
                        typeof(TextBox),
                        TextBox.TextProperty,
                        CreateSpreadsheetBinding(descriptor),
                        columnEditingStyle),
                };
            }
            else
            {
                column = new DataGridTextColumn
                {
                    Binding = CreateSpreadsheetBinding(descriptor),
                    ElementStyle = columnElementStyle,
                    EditingElementStyle = columnEditingStyle,
                };
            }

            column.Header = descriptor;
            column.HeaderTemplate = headerTemplate;
            column.HeaderStyle = descriptor.Kind == "RawHex"
                ? technicalHeaderStyle
                : descriptor.IsNameField
                    ? identityHeaderStyle
                    : headerStyle;
            column.CellStyle = CreateSpreadsheetCellStyle(descriptor);
            SpreadsheetColumnLayout layout = descriptor.Layout;
            column.MinWidth = layout.MinimumWidth;
            column.MaxWidth = layout.MaximumWidth;
            column.Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                descriptor.Identity,
                layout.PreferredWidth,
                layout.MinimumWidth,
                layout.MaximumWidth));
            column.CanUserSort = descriptor.IsSortable;
            RecordsGrid.Columns.Add(column);
            _spreadsheetColumns.Add(column, descriptor);
            if (descriptor.Projection is null && descriptor.FieldId == nameFieldId)
            {
                nameColumn = column;
            }

            if (!localizationColumnsAdded &&
                descriptor.Projection is null &&
                descriptor.FieldId == nameFieldId &&
                _spreadsheetViewModel.HasTextsDocument)
            {
                AddLocalizationColumns(elementStyle, editingStyle, tableKey);
                localizationColumnsAdded = true;
            }
        }

        if (!localizationColumnsAdded && _spreadsheetViewModel.HasTextsDocument)
        {
            AddLocalizationColumns(elementStyle, editingStyle, tableKey);
        }

        if (_spreadsheetViewModel.HasSoundArchives && _spreadsheetViewModel.CurrentTableSupportsSounds)
        {
            AddSoundColumns(elementStyle, tableKey);
        }

        _columnLayoutTableKey = tableKey;
        ApplyFrozenColumnLayout(nameColumn);
        UpdateSpreadsheetColumnReadOnlyStates();
        RefreshFormulaBar();
    }

    private static Binding CreateSpreadsheetBinding(SpreadsheetColumnViewModel descriptor)
    {
        var bindingPath = descriptor.Projection is null
            ? $"Cells[{descriptor.FieldId}].EditorText"
            : $"Cells[{descriptor.FieldId}].ProjectionSource";
        var binding = new Binding(bindingPath)
        {
            Mode = descriptor.Projection is null ? BindingMode.TwoWay : BindingMode.OneWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
        };
        if (descriptor.Projection is not null)
        {
            binding.Converter = ProjectionConverter;
            binding.ConverterParameter = descriptor.Projection;
        }

        return binding;
    }

    private static DataTemplate CreateSpreadsheetTextTemplate(
        Type elementType,
        DependencyProperty textProperty,
        Binding binding,
        Style style)
    {
        var factory = new FrameworkElementFactory(elementType);
        factory.SetBinding(textProperty, binding);
        factory.SetValue(FrameworkElement.StyleProperty, style);
        return new DataTemplate { VisualTree = factory };
    }

    private void AddLocalizationColumns(Style elementStyle, Style editingStyle, string tableKey)
    {
        var localizationHeaderStyle = (Style)FindResource("LocalizationColumnHeaderStyle");
        _localizationKeyColumn = new DataGridTextColumn
        {
            Header = "Ключ texts.res",
            Binding = new Binding(nameof(RecordRowViewModel.LocalizationKeyText)),
            ElementStyle = elementStyle,
            HeaderStyle = localizationHeaderStyle,
            CellStyle = CreateLocalizationCellStyle(nameof(RecordRowViewModel.LocalizationKeyText)),
            IsReadOnly = true,
            MinWidth = 170,
            MaxWidth = 420,
            Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                LocalizationKeyColumnIdentity,
                250,
                170,
                420)),
        };
        _localizationTitleColumn = new DataGridTextColumn
        {
            Header = "Название RU",
            Binding = new Binding(nameof(RecordRowViewModel.LocalizationTitle))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            },
            ElementStyle = elementStyle,
            EditingElementStyle = editingStyle,
            HeaderStyle = localizationHeaderStyle,
            CellStyle = CreateLocalizationCellStyle(nameof(RecordRowViewModel.LocalizationTitle)),
            MinWidth = 160,
            MaxWidth = 520,
            Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                LocalizationTitleColumnIdentity,
                240,
                160,
                520)),
        };
        _localizationDescriptionColumn = new DataGridTextColumn
        {
            Header = "Описание RU",
            Binding = new Binding(nameof(RecordRowViewModel.LocalizationDescription))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            },
            ElementStyle = elementStyle,
            EditingElementStyle = editingStyle,
            HeaderStyle = localizationHeaderStyle,
            CellStyle = CreateLocalizationCellStyle(nameof(RecordRowViewModel.LocalizationDescription)),
            MinWidth = 220,
            MaxWidth = 700,
            Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                LocalizationDescriptionColumnIdentity,
                320,
                220,
                700)),
        };
        RecordsGrid.Columns.Add(_localizationKeyColumn);
        RecordsGrid.Columns.Add(_localizationTitleColumn);
        RecordsGrid.Columns.Add(_localizationDescriptionColumn);
    }

    private void ApplyFrozenColumnLayout(DataGridColumn? nameColumn)
    {
        var frozenColumns = new List<DataGridColumn>(2);
        if (nameColumn is not null)
        {
            frozenColumns.Add(nameColumn);
        }

        if (_localizationTitleColumn is not null)
        {
            frozenColumns.Add(_localizationTitleColumn);
        }

        DataGridColumn[] orderedColumns = frozenColumns
            .Concat(RecordsGrid.Columns.Cast<DataGridColumn>().Where(column => !frozenColumns.Contains(column)))
            .ToArray();
        for (var displayIndex = 0; displayIndex < orderedColumns.Length; displayIndex++)
        {
            orderedColumns[displayIndex].DisplayIndex = displayIndex;
        }

        RecordsGrid.FrozenColumnCount = Math.Min(frozenColumns.Count, RecordsGrid.Columns.Count);
    }

    private void AddSoundColumns(Style elementStyle, string tableKey)
    {
        var soundHeaderStyle = (Style)FindResource("SoundColumnHeaderStyle");
        _soundSummaryColumn = new DataGridTextColumn
        {
            Header = "Аудио",
            Binding = new Binding(nameof(RecordRowViewModel.SoundSummary)),
            ElementStyle = elementStyle,
            HeaderStyle = soundHeaderStyle,
            CellStyle = CreateSoundCellStyle(nameof(RecordRowViewModel.SoundSummary)),
            IsReadOnly = true,
            MinWidth = 110,
            MaxWidth = 240,
            Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                SoundSummaryColumnIdentity,
                150,
                110,
                240)),
        };
        _soundPathColumn = new DataGridTextColumn
        {
            Header = "Файл в звуковом RES",
            Binding = new Binding(nameof(RecordRowViewModel.PrimarySoundPath)),
            ElementStyle = elementStyle,
            HeaderStyle = soundHeaderStyle,
            CellStyle = CreateSoundCellStyle(nameof(RecordRowViewModel.PrimarySoundPath)),
            IsReadOnly = true,
            MinWidth = 210,
            MaxWidth = 680,
            Width = new DataGridLength(GetRememberedColumnWidth(
                tableKey,
                SoundPathColumnIdentity,
                360,
                210,
                680)),
        };
        RecordsGrid.Columns.Add(_soundSummaryColumn);
        RecordsGrid.Columns.Add(_soundPathColumn);
    }

    private double GetRememberedColumnWidth(
        string tableKey,
        string columnIdentity,
        double preferredWidth,
        double minimumWidth,
        double maximumWidth)
    {
        double width = _preferences.GetColumnWidth(tableKey, columnIdentity) ?? preferredWidth;
        return Math.Clamp(width, minimumWidth, maximumWidth);
    }

    private void PersistCurrentColumnWidths()
    {
        if (RecordsGrid is null || string.IsNullOrWhiteSpace(_columnLayoutTableKey))
        {
            return;
        }

        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((DataGridColumn gridColumn, SpreadsheetColumnViewModel descriptor) in _spreadsheetColumns)
        {
            AddWidth(descriptor.Identity, gridColumn);
        }

        AddWidth(LocalizationKeyColumnIdentity, _localizationKeyColumn);
        AddWidth(LocalizationTitleColumnIdentity, _localizationTitleColumn);
        AddWidth(LocalizationDescriptionColumnIdentity, _localizationDescriptionColumn);
        AddWidth(SoundSummaryColumnIdentity, _soundSummaryColumn);
        AddWidth(SoundPathColumnIdentity, _soundPathColumn);
        if (widths.Count > 0)
        {
            _preferences.RememberColumnWidths(_columnLayoutTableKey, widths);
        }

        void AddWidth(string identity, DataGridColumn? column)
        {
            if (column is not null && double.IsFinite(column.ActualWidth) && column.ActualWidth > 0)
            {
                widths[identity] = column.ActualWidth;
            }
        }
    }

    private Style CreateLocalizationCellStyle(string toolTipProperty)
    {
        var baseStyle = (Style)Application.Current.FindResource(typeof(DataGridCell));
        var style = new Style(typeof(DataGridCell), baseStyle);
        style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("LocalizationSurfaceBrush")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("LocalizationTextBrush")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        style.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding(toolTipProperty)));

        var missingTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(RecordRowViewModel.HasMissingLocalization)),
            Value = true,
        };
        missingTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("WarningSurfaceBrush")));
        missingTrigger.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("WarningBrush")));
        missingTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("WarningBrush")));
        missingTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2, 0, 0, 0)));
        style.Triggers.Add(missingTrigger);

        var multipleTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(RecordRowViewModel.HasMultipleLocalizations)),
            Value = true,
        };
        multipleTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("LocalizationHeaderBrush")));
        multipleTrigger.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("LocalizationAccentBrush")));
        style.Triggers.Add(multipleTrigger);

        var modifiedTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(RecordRowViewModel.PrimaryLocalizationIsModified)),
            Value = true,
        };
        modifiedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("ModifiedSurfaceBrush")));
        modifiedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("AccentBrush")));
        modifiedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 2)));
        style.Triggers.Add(modifiedTrigger);

        var selectedTrigger = new Trigger
        {
            Property = DataGridCell.IsSelectedProperty,
            Value = true,
        };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SpreadsheetSelectionBrush")));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("AccentBrush")));
        selectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Triggers.Add(selectedTrigger);
        return style;
    }

    private Style CreateSoundCellStyle(string toolTipProperty)
    {
        var baseStyle = (Style)Application.Current.FindResource(typeof(DataGridCell));
        var style = new Style(typeof(DataGridCell), baseStyle);
        style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SoundSurfaceBrush")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("SoundTextBrush")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding(toolTipProperty)));

        var missingTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(RecordRowViewModel.HasMissingSounds)),
            Value = true,
        };
        missingTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("WarningSurfaceBrush")));
        missingTrigger.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("WarningBrush")));
        style.Triggers.Add(missingTrigger);
        return style;
    }

    private static Style CreateSpreadsheetElementStyle(
        SpreadsheetColumnViewModel descriptor,
        Style baseStyle)
    {
        var style = new Style(typeof(TextBlock), baseStyle);
        style.Setters.Add(new Setter(
            TextBlock.TextAlignmentProperty,
            GetSpreadsheetTextAlignment(descriptor)));
        return style;
    }

    private static Style CreateSpreadsheetEditingStyle(
        SpreadsheetColumnViewModel descriptor,
        Style baseStyle)
    {
        var style = new Style(typeof(TextBox), baseStyle);
        style.Setters.Add(new Setter(
            TextBox.TextAlignmentProperty,
            GetSpreadsheetTextAlignment(descriptor)));
        return style;
    }

    private static TextAlignment GetSpreadsheetTextAlignment(SpreadsheetColumnViewModel descriptor) =>
        descriptor.Kind switch
        {
            "Boolean" => TextAlignment.Center,
            "Byte" or "Int32" or "UInt32" or "Float32" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

    private Style CreateSpreadsheetCellStyle(SpreadsheetColumnViewModel descriptor)
    {
        var baseStyle = (Style)Application.Current.FindResource(typeof(DataGridCell));
        var style = new Style(typeof(DataGridCell), baseStyle);
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        style.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding($"Cells[{descriptor.FieldId}].Path")));
        if (descriptor.IsNameField)
        {
            style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("DatabaseSurfaceBrush")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("DatabaseTextBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("DatabaseBorderBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2, 0, 0, 0)));
        }
        else if (descriptor.Kind == "RawHex")
        {
            style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("TechnicalSurfaceBrush")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TechnicalAccentBrush")));
        }
        else if (descriptor.Projection is not null)
        {
            style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SurfaceBrush")));
        }

        if (!descriptor.IsEditable)
        {
            style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("MutedTextBrush")));
        }

        var modifiedTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].IsModified"),
            Value = true,
        };
        modifiedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("AccentBrush")));
        modifiedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 2)));
        modifiedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("ModifiedSurfaceBrush")));
        style.Triggers.Add(modifiedTrigger);

        var diagnosticTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].HasDiagnostic"),
            Value = true,
        };
        diagnosticTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("WarningBrush")));
        diagnosticTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        diagnosticTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("WarningSurfaceBrush")));
        diagnosticTrigger.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding($"Cells[{descriptor.FieldId}].DiagnosticText")));
        style.Triggers.Add(diagnosticTrigger);

        var blockingDiagnosticTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].HasBlockingDiagnostic"),
            Value = true,
        };
        blockingDiagnosticTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("DangerBrush")));
        blockingDiagnosticTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        blockingDiagnosticTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("DangerSurfaceBrush")));
        style.Triggers.Add(blockingDiagnosticTrigger);

        var errorTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].HasError"),
            Value = true,
        };
        errorTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("DangerBrush")));
        errorTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        errorTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("DangerSurfaceBrush")));
        errorTrigger.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding($"Cells[{descriptor.FieldId}].ErrorMessage")));
        style.Triggers.Add(errorTrigger);

        var selectedTrigger = new Trigger
        {
            Property = DataGridCell.IsSelectedProperty,
            Value = true,
        };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SpreadsheetSelectionBrush")));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("AccentBrush")));
        selectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Triggers.Add(selectedTrigger);
        return style;
    }

    private void UpdateSpreadsheetColumnReadOnlyStates()
    {
        var canEdit = _spreadsheetViewModel is { IsEditMode: true, IsBusy: false, IsCurrentTableEditable: true };
        foreach (var pair in _spreadsheetColumns)
        {
            pair.Key.IsReadOnly = !canEdit || !pair.Value.IsEditable;
        }

        if (_localizationTitleColumn is not null)
        {
            _localizationTitleColumn.IsReadOnly = !canEdit;
        }

        if (_localizationDescriptionColumn is not null)
        {
            _localizationDescriptionColumn.IsReadOnly = !canEdit;
        }
    }

    private void RecordsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is RecordRowViewModel localizationRecord &&
            (ReferenceEquals(e.Column, _localizationTitleColumn) ||
             ReferenceEquals(e.Column, _localizationDescriptionColumn)))
        {
            e.Cancel = _spreadsheetViewModel is not { IsEditMode: true, IsBusy: false } ||
                       !localizationRecord.CanEditPrimaryLocalization;
            return;
        }

        if (_spreadsheetViewModel is not { IsEditMode: true, IsBusy: false, IsCurrentTableEditable: true } ||
            e.Row.Item is not RecordRowViewModel record ||
            !_spreadsheetColumns.TryGetValue(e.Column, out var descriptor) ||
            !descriptor.IsEditable ||
            record.GetCell(descriptor.FieldId)?.CanEdit is not true)
        {
            e.Cancel = true;
        }
    }

    private void RecordsGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        SelectSpreadsheetRecord(RecordsGrid.CurrentCell.Item);
        RefreshFormulaBar();
    }

    private void RecordsGrid_SelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        object? candidate = RecordsGrid.CurrentCell.Item;
        if (candidate is not RecordRowViewModel)
        {
            candidate = e.AddedCells.LastOrDefault().Item;
        }

        SelectSpreadsheetRecord(candidate);
        RefreshFormulaBar();
    }

    private void RecordsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit &&
            e.Row.Item is RecordRowViewModel record &&
            _spreadsheetColumns.TryGetValue(e.Column, out SpreadsheetColumnViewModel? descriptor) &&
            descriptor.Projection is not null &&
            descriptor.IsEditable)
        {
            string? error = null;
            TextBox? editor = e.EditingElement as TextBox ?? FindVisualDescendant<TextBox>(e.EditingElement);
            RecordFieldViewModel? field = record.GetCell(descriptor.FieldId);
            if (editor is null || field is null ||
                !field.TrySetProjectedText(descriptor.Projection, editor.Text, out error))
            {
                e.Cancel = true;
                (_spreadsheetViewModel ?? _viewModel).ReportSpreadsheetError(
                    error ?? "Значение расшифрованной ячейки не принято.");
            }
        }

        Dispatcher.BeginInvoke(RefreshFormulaBar, DispatcherPriority.DataBind);
    }

    private void RecordsGrid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () => (_spreadsheetViewModel ?? _viewModel).CompletePendingRecordsViewRefresh(),
            DispatcherPriority.ContextIdle);
    }

    private static T? FindVisualDescendant<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void SelectSpreadsheetRecord(object? candidate)
    {
        if (_spreadsheetViewModel is not null &&
            candidate is RecordRowViewModel record &&
            _spreadsheetViewModel.SelectedTable?.Records.Contains(record) is true &&
            !ReferenceEquals(_spreadsheetViewModel.SelectedRecord, record))
        {
            _spreadsheetViewModel.SelectedRecord = record;
        }
    }

    private void TextsGrid_CurrentCellChanged(object? sender, EventArgs e) => RefreshTextsFormulaBar();

    private void TextsGrid_SelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e) =>
        RefreshTextsFormulaBar();

    private void TextsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            Dispatcher.BeginInvoke(RefreshTextsFormulaBar, DispatcherPriority.DataBind);
        }
    }

    private void RefreshTextsFormulaBar()
    {
        if (TextsFormulaAddressBox is null || TextsFormulaEditorBox is null)
        {
            return;
        }

        if (!TryCreateTextsFormulaCellContext(out FormulaCellContext context))
        {
            if (_textsFormulaCellContext is null ||
                _spreadsheetViewModel?.CurrentSection != WorkspaceSection.Texts)
            {
                ClearTextsFormulaBar();
            }

            return;
        }

        bool keepPendingInput = TextsFormulaEditorBox.IsKeyboardFocusWithin &&
                                _textsFormulaCellContext is not null &&
                                ReferenceEquals(_textsFormulaCellContext.Item, context.Item) &&
                                ReferenceEquals(_textsFormulaCellContext.Column, context.Column);
        string pendingInput = TextsFormulaEditorBox.Text;
        _textsFormulaCellContext = context;
        TextsFormulaAddressBox.Text = context.Address;
        TextsFormulaAddressBox.ToolTip = $"{context.Address} · {context.Label}";
        TextsFormulaEditorBox.Text = keepPendingInput ? pendingInput : context.ReadValue();
        TextsFormulaEditorBox.IsReadOnly = !context.IsEditable;
        TextsFormulaEditorBox.ToolTip = null;
        AutomationProperties.SetHelpText(
            TextsFormulaEditorBox,
            context.IsEditable
                ? $"{context.Label}. Enter — применить, Esc — отменить, Alt+Enter — перенос строки."
                : $"{context.Label}. Служебное поле доступно только для чтения.");
        TextsFormulaApplyButton.IsEnabled = context.IsEditable;
        TextsFormulaCancelButton.IsEnabled = context.IsEditable;
        TextsFormulaOpenEditorButton.IsEnabled = context.CanOpenLongTextEditor;
        SetTextsFormulaError(null);
    }

    private bool TryCreateTextsFormulaCellContext(out FormulaCellContext context)
    {
        context = null!;
        if (_spreadsheetViewModel?.CurrentSection != WorkspaceSection.Texts ||
            TextsGrid.CurrentCell is not { IsValid: true, Item: LocalizationEntryViewModel entry } currentCell)
        {
            return false;
        }

        DataGridColumn column = currentCell.Column;
        string address = GetSpreadsheetAddress(TextsGrid, entry, column);
        if (ReferenceEquals(column, TextsIndexColumn))
        {
            context = new FormulaCellContext(
                entry,
                column,
                address,
                "Номер строки",
                () => entry.Index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            return true;
        }

        if (ReferenceEquals(column, TextsPrefixColumn))
        {
            context = new FormulaCellContext(entry, column, address, "Тип", () => entry.Prefix);
            return true;
        }

        if (ReferenceEquals(column, TextsResourceColumn))
        {
            context = new FormulaCellContext(entry, column, address, "Ключ texts.res", () => entry.ResourceName);
            return true;
        }

        if (ReferenceEquals(column, TextsTitleColumn))
        {
            Action<string>? writeValue = CanEditTextResource()
                ? value => entry.Title = value
                : null;
            context = new FormulaCellContext(
                entry,
                column,
                address,
                "Название RU",
                () => entry.Title,
                writeValue,
                () => entry.ErrorMessage,
                SupportsLongTextEditor: true);
            return true;
        }

        if (ReferenceEquals(column, TextsDescriptionColumn))
        {
            Action<string>? writeValue = CanEditTextResource()
                ? value => entry.Description = value
                : null;
            context = new FormulaCellContext(
                entry,
                column,
                address,
                "Описание RU",
                () => entry.Description,
                writeValue,
                () => entry.ErrorMessage,
                SupportsLongTextEditor: true);
            return true;
        }

        return false;
    }

    private bool CanEditTextResource() =>
        _spreadsheetViewModel is { IsEditMode: true, IsBusy: false, HasTextsDocument: true };

    private void ClearTextsFormulaBar()
    {
        _textsFormulaCellContext = null;
        if (TextsFormulaAddressBox is null || TextsFormulaEditorBox is null)
        {
            return;
        }

        TextsFormulaAddressBox.Text = string.Empty;
        TextsFormulaAddressBox.ToolTip = "Адрес текущей ячейки texts.res";
        TextsFormulaEditorBox.Text = string.Empty;
        TextsFormulaEditorBox.IsReadOnly = true;
        TextsFormulaEditorBox.ToolTip = null;
        AutomationProperties.SetHelpText(TextsFormulaEditorBox, "Выберите ячейку texts.res.");
        TextsFormulaApplyButton.IsEnabled = false;
        TextsFormulaCancelButton.IsEnabled = false;
        TextsFormulaOpenEditorButton.IsEnabled = false;
        SetTextsFormulaError(null);
    }

    private void TextsFormulaEditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelTextsFormulaEdit();
            e.Handled = true;
            return;
        }

        bool isEnter = e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Enter);
        if (isEnter && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            ApplyTextsFormulaEdit();
            e.Handled = true;
        }
    }

    private void TextsFormulaEditorBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox editor || editor.IsReadOnly || editor.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (!TryFocusTextsFormulaEditor(selectAll: false))
        {
            return;
        }

        int characterIndex = editor.GetCharacterIndexFromPoint(e.GetPosition(editor), snapToText: true);
        editor.CaretIndex = characterIndex >= 0 ? characterIndex : editor.Text.Length;
    }

    internal bool TryFocusTextsFormulaEditor(bool selectAll)
    {
        if (_textsFormulaCellContext is null)
        {
            RefreshTextsFormulaBar();
        }

        if (_textsFormulaCellContext?.IsEditable is not true || TextsFormulaEditorBox.IsReadOnly)
        {
            return false;
        }

        bool focused = TextsFormulaEditorBox.Focus();
        if (focused && selectAll)
        {
            TextsFormulaEditorBox.SelectAll();
        }

        return focused && TextsFormulaEditorBox.IsKeyboardFocusWithin;
    }

    private void TextsFormulaCancelButton_Click(object sender, RoutedEventArgs e) => CancelTextsFormulaEdit();

    private void TextsFormulaApplyButton_Click(object sender, RoutedEventArgs e) => ApplyTextsFormulaEdit();

    private void TextsFormulaOpenEditorButton_Click(object sender, RoutedEventArgs e)
    {
        FormulaCellContext? context = _textsFormulaCellContext;
        if (context?.CanOpenLongTextEditor is not true)
        {
            return;
        }

        var dialog = new LongTextEditorDialog(
            context.Address,
            context.Label,
            TextsFormulaEditorBox.Text,
            value =>
            {
                bool success = TryApplyTextsFormulaValue(value, out string? error);
                return (success, error);
            })
        {
            Owner = this,
        };

        if (ShowLongTextEditorDialog(dialog) is true && dialog.WasApplied)
        {
            SetTextsFormulaError(null);
            TextsFormulaEditorBox.Text = context.ReadValue();
            TextsGrid.Focus();
        }
    }

    private void CancelTextsFormulaEdit()
    {
        if (_textsFormulaCellContext is not null)
        {
            TextsFormulaEditorBox.Text = _textsFormulaCellContext.ReadValue();
        }

        SetTextsFormulaError(null);
        TextsGrid.Focus();
    }

    private void ApplyTextsFormulaEdit()
    {
        if (!TryApplyTextsFormulaValue(TextsFormulaEditorBox.Text, out string? error))
        {
            SetTextsFormulaError(error ?? "Значение не принято.");
            TextsFormulaEditorBox.Focus();
            TextsFormulaEditorBox.SelectAll();
            return;
        }

        SetTextsFormulaError(null);
        TextsFormulaEditorBox.Text = _textsFormulaCellContext?.ReadValue() ?? TextsFormulaEditorBox.Text;
        TextsGrid.Focus();
    }

    internal bool TryApplyTextsFormulaValue(string value, out string? error)
    {
        error = null;
        FormulaCellContext? context = _textsFormulaCellContext;
        if (context?.WriteValue is null)
        {
            error = "Выбранная ячейка доступна только для чтения.";
            return false;
        }

        context.WriteValue(value);
        error = context.ReadError?.Invoke();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        TextsFormulaEditorBox.Text = context.ReadValue();
        return true;
    }

    private void SetTextsFormulaError(string? error)
    {
        TextsFormulaErrorText.Text = error ?? string.Empty;
        TextsFormulaErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        TextsFormulaEditorBox.BorderBrush = string.IsNullOrWhiteSpace(error)
            ? (Brush)FindResource("LocalizationBorderBrush")
            : (Brush)FindResource("DangerBrush");
    }

    private void RefreshFormulaBar()
    {
        if (FormulaAddressBox is null || FormulaEditorBox is null)
        {
            return;
        }

        if (!TryCreateFormulaCellContext(out FormulaCellContext context))
        {
            // Moving keyboard focus from the grid into the formula editor can make WPF
            // temporarily invalidate DataGrid.CurrentCell. Excel keeps the last active
            // cell in this situation, and so must we; otherwise the editor becomes
            // read-only between the mouse-down and the button/Enter command.
            if (_formulaCellContext is null)
            {
                ClearFormulaBar();
            }

            return;
        }

        bool keepPendingInput = FormulaEditorBox.IsKeyboardFocusWithin &&
                                _formulaCellContext is not null &&
                                ReferenceEquals(_formulaCellContext.Item, context.Item) &&
                                ReferenceEquals(_formulaCellContext.Column, context.Column);
        string pendingInput = FormulaEditorBox.Text;
        _formulaCellContext = context;
        FormulaAddressBox.Text = context.Address;
        FormulaAddressBox.ToolTip = $"{context.Address} · {context.Label}";
        FormulaEditorBox.Text = keepPendingInput ? pendingInput : context.ReadValue();
        FormulaEditorBox.IsReadOnly = !context.IsEditable;
        FormulaEditorBox.ToolTip = null;
        AutomationProperties.SetHelpText(
            FormulaEditorBox,
            context.IsEditable
                ? $"{context.Label}. Enter — применить, Esc — отменить, Alt+Enter — перенос строки."
                : $"{context.Label}. Вычисляемое или служебное поле доступно только для чтения.");
        FormulaApplyButton.IsEnabled = context.IsEditable;
        FormulaCancelButton.IsEnabled = context.IsEditable;
        FormulaOpenEditorButton.IsEnabled = context.CanOpenLongTextEditor;
        SetFormulaError(null);
    }

    private bool TryCreateFormulaCellContext(out FormulaCellContext context)
    {
        context = null!;
        if (RecordsGrid.CurrentCell is not { IsValid: true, Item: RecordRowViewModel record } currentCell)
        {
            return false;
        }

        DataGridColumn column = currentCell.Column;
        string address = GetSpreadsheetAddress(RecordsGrid, record, column);
        if (_spreadsheetColumns.TryGetValue(column, out SpreadsheetColumnViewModel? descriptor))
        {
            RecordFieldViewModel? field = record.GetCell(descriptor.FieldId);
            if (field is null)
            {
                return false;
            }

            if (descriptor.Projection is not null)
            {
                Action<string>? projectedWriteValue = descriptor.IsEditable && field.CanEditProjection(descriptor.Projection)
                    ? value => field.TrySetProjectedText(descriptor.Projection, value, out _)
                    : null;
                context = new FormulaCellContext(
                    record,
                    column,
                    address,
                    descriptor.Title,
                    () => field.GetProjectedText(descriptor.Projection),
                    projectedWriteValue,
                    () => field.ErrorMessage,
                    SupportsLongTextEditor: IsLongTextKind(descriptor.Kind));
                return true;
            }

            Action<string>? writeValue = descriptor.IsEditable && field.CanEdit
                ? value => field.EditorText = value
                : null;
            context = new FormulaCellContext(
                record,
                column,
                address,
                descriptor.Title,
                () => field.EditorText,
                writeValue,
                () => field.ErrorMessage,
                SupportsLongTextEditor: IsLongTextKind(descriptor.Kind));
            return true;
        }

        if (ReferenceEquals(column, _localizationKeyColumn))
        {
            context = new FormulaCellContext(
                record,
                column,
                address,
                "Ключ texts.res",
                () => record.LocalizationKeyText);
            return true;
        }

        if (ReferenceEquals(column, _localizationTitleColumn))
        {
            Action<string>? writeValue = CanEditLocalization(record)
                ? value => record.LocalizationTitle = value
                : null;
            context = new FormulaCellContext(
                record,
                column,
                address,
                "Название RU",
                () => record.LocalizationTitle,
                writeValue,
                () => record.PrimaryLocalizationErrorMessage,
                SupportsLongTextEditor: true);
            return true;
        }

        if (ReferenceEquals(column, _localizationDescriptionColumn))
        {
            Action<string>? writeValue = CanEditLocalization(record)
                ? value => record.LocalizationDescription = value
                : null;
            context = new FormulaCellContext(
                record,
                column,
                address,
                "Описание RU",
                () => record.LocalizationDescription,
                writeValue,
                () => record.PrimaryLocalizationErrorMessage,
                SupportsLongTextEditor: true);
            return true;
        }

        if (Equals(column.Header, "#"))
        {
            context = new FormulaCellContext(
                record,
                column,
                address,
                "Номер строки",
                () => record.Index.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    private bool CanEditLocalization(RecordRowViewModel record) =>
        _spreadsheetViewModel is { IsEditMode: true, IsBusy: false, IsCurrentTableEditable: true } &&
        record.CanEditPrimaryLocalization;

    private static bool IsLongTextKind(string kind) =>
        kind is "String" or "FixedString" or "StringList" or "FixedStringList";

    private static string GetSpreadsheetAddress(DataGrid grid, object item, DataGridColumn column)
    {
        DataGridColumn[] visibleColumns = grid.Columns
            .Where(candidate => candidate.Visibility == Visibility.Visible)
            .OrderBy(candidate => candidate.DisplayIndex)
            .ToArray();
        int columnIndex = Array.FindIndex(visibleColumns, candidate => ReferenceEquals(candidate, column));
        int rowIndex = grid.Items.IndexOf(item);
        if (columnIndex < 0 || rowIndex < 0)
        {
            return string.Empty;
        }

        return GetSpreadsheetColumnName(columnIndex + 1) + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string GetSpreadsheetColumnName(int oneBasedIndex)
    {
        var result = new Stack<char>();
        int value = oneBasedIndex;
        while (value > 0)
        {
            value--;
            result.Push((char)('A' + value % 26));
            value /= 26;
        }

        return new string(result.ToArray());
    }

    private void ClearFormulaBar()
    {
        _formulaCellContext = null;
        if (FormulaAddressBox is null || FormulaEditorBox is null)
        {
            return;
        }

        FormulaAddressBox.Text = string.Empty;
        FormulaAddressBox.ToolTip = "Адрес текущей ячейки в видимой таблице";
        FormulaEditorBox.Text = string.Empty;
        FormulaEditorBox.IsReadOnly = true;
        FormulaEditorBox.ToolTip = null;
        AutomationProperties.SetHelpText(FormulaEditorBox, "Выберите ячейку таблицы.");
        FormulaApplyButton.IsEnabled = false;
        FormulaCancelButton.IsEnabled = false;
        FormulaOpenEditorButton.IsEnabled = false;
        SetFormulaError(null);
    }

    private void FormulaEditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelFormulaEdit();
            e.Handled = true;
            return;
        }

        bool isEnter = e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Enter);
        if (isEnter && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            ApplyFormulaEdit();
            e.Handled = true;
        }
    }

    private void FormulaEditorBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox editor || editor.IsReadOnly || editor.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (!TryFocusFormulaEditor(selectAll: false))
        {
            return;
        }

        int characterIndex = editor.GetCharacterIndexFromPoint(e.GetPosition(editor), snapToText: true);
        editor.CaretIndex = characterIndex >= 0 ? characterIndex : editor.Text.Length;
    }

    internal bool TryFocusFormulaEditor(bool selectAll)
    {
        if (_formulaCellContext is null)
        {
            RefreshFormulaBar();
        }

        if (_formulaCellContext?.IsEditable is not true || FormulaEditorBox.IsReadOnly)
        {
            return false;
        }

        bool focused = FormulaEditorBox.Focus();
        if (focused && selectAll)
        {
            FormulaEditorBox.SelectAll();
        }

        return focused && FormulaEditorBox.IsKeyboardFocusWithin;
    }

    private void FormulaCancelButton_Click(object sender, RoutedEventArgs e) => CancelFormulaEdit();

    private void FormulaApplyButton_Click(object sender, RoutedEventArgs e) => ApplyFormulaEdit();

    private void FormulaOpenEditorButton_Click(object sender, RoutedEventArgs e)
    {
        FormulaCellContext? context = _formulaCellContext;
        if (context?.CanOpenLongTextEditor is not true)
        {
            return;
        }

        var dialog = new LongTextEditorDialog(
            context.Address,
            context.Label,
            FormulaEditorBox.Text,
            value =>
            {
                bool success = TryApplyFormulaValue(value, out string? error);
                return (success, error);
            })
        {
            Owner = this,
        };

        if (ShowLongTextEditorDialog(dialog) is true && dialog.WasApplied)
        {
            SetFormulaError(null);
            FormulaEditorBox.Text = context.ReadValue();
            RecordsGrid.Focus();
        }
    }

    private void CancelFormulaEdit()
    {
        if (_formulaCellContext is not null)
        {
            FormulaEditorBox.Text = _formulaCellContext.ReadValue();
        }

        SetFormulaError(null);
        RecordsGrid.Focus();
    }

    private void ApplyFormulaEdit()
    {
        if (!TryApplyFormulaValue(FormulaEditorBox.Text, out string? error))
        {
            SetFormulaError(error ?? "Значение не принято.");
            FormulaEditorBox.Focus();
            FormulaEditorBox.SelectAll();
            return;
        }

        SetFormulaError(null);
        FormulaEditorBox.Text = _formulaCellContext?.ReadValue() ?? FormulaEditorBox.Text;
        RecordsGrid.Focus();
    }

    internal bool TryApplyFormulaValue(string value, out string? error)
    {
        error = null;
        FormulaCellContext? context = _formulaCellContext;
        if (context?.WriteValue is null)
        {
            error = "Выбранная ячейка доступна только для чтения.";
            return false;
        }

        context.WriteValue(value);
        error = context.ReadError?.Invoke();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        FormulaEditorBox.Text = context.ReadValue();
        return true;
    }

    private void SetFormulaError(string? error)
    {
        FormulaErrorText.Text = error ?? string.Empty;
        FormulaErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        FormulaEditorBox.BorderBrush = string.IsNullOrWhiteSpace(error)
            ? (Brush)FindResource("StrongBorderBrush")
            : (Brush)FindResource("DangerBrush");
    }

    private void RecordsGrid_CopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _spreadsheetViewModel?.CurrentSection == WorkspaceSection.Records &&
                       (RecordsGrid.SelectedCells.Count > 0 || RecordsGrid.CurrentCell.IsValid);
        e.Handled = true;
    }

    private void RecordsGrid_CopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (!TryBuildClipboardMatrix(out IReadOnlyList<IReadOnlyList<string>> matrix) || matrix.Count == 0)
            {
                _viewModel.ReportSpreadsheetError("Выберите одну или несколько ячеек для копирования.");
                return;
            }

            DataObject data = CreateSpreadsheetClipboardDataObject(matrix);
            SetClipboardDataWithRetry(data);
            _viewModel.ReportSpreadsheetCopy(matrix.Count, matrix.Max(row => row.Count));
        }
        catch (ExternalException exception)
        {
            _viewModel.ReportSpreadsheetError($"Буфер обмена сейчас занят другим приложением: {exception.Message}");
        }
    }

    private void RecordsGrid_PasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _spreadsheetViewModel is
        {
            CurrentSection: WorkspaceSection.Records,
            IsEditMode: true,
            IsBusy: false,
            IsCurrentTableEditable: true,
        } &&
        RecordsGrid.CurrentCell.IsValid;

        e.Handled = true;
    }

    private void RecordsGrid_PasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            string text = GetClipboardTextWithRetry();
            IReadOnlyList<IReadOnlyList<string>> matrix = SpreadsheetClipboardText.Parse(text);
            if (!TryBuildPasteCells(matrix, out IReadOnlyList<SpreadsheetPasteCell> cells, out string? error))
            {
                _viewModel.ReportSpreadsheetError(error ?? "Не удалось определить диапазон вставки.");
                return;
            }

            _viewModel.TryPasteSpreadsheetCells(cells, out _, out _);
        }
        catch (Exception exception) when (exception is ExternalException or FormatException)
        {
            _viewModel.ReportSpreadsheetError($"Не удалось прочитать табличный буфер: {exception.Message}");
        }
    }

    internal bool TryBuildClipboardMatrix(out IReadOnlyList<IReadOnlyList<string>> matrix)
    {
        RecordRowViewModel[] rows = RecordsGrid.Items.Cast<object>().OfType<RecordRowViewModel>().ToArray();
        DataGridColumn[] columns = RecordsGrid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        var rowIndexes = rows.Select((row, index) => (row, index)).ToDictionary(item => item.row, item => item.index);
        var columnIndexes = columns.Select((column, index) => (column, index))
            .ToDictionary(item => item.column, item => item.index);
        var selected = RecordsGrid.SelectedCells
            .Where(cell => cell.Item is RecordRowViewModel && columnIndexes.ContainsKey(cell.Column))
            .Select(cell => ((RecordRowViewModel)cell.Item, cell.Column))
            .Distinct()
            .ToArray();
        if (selected.Length == 0 &&
            RecordsGrid.CurrentCell is { IsValid: true, Item: RecordRowViewModel currentRecord } &&
            columnIndexes.ContainsKey(RecordsGrid.CurrentCell.Column))
        {
            selected = [(currentRecord, RecordsGrid.CurrentCell.Column)];
        }

        if (selected.Length == 0)
        {
            matrix = [];
            return false;
        }

        int firstRow = selected.Min(cell => rowIndexes[cell.Item1]);
        int lastRow = selected.Max(cell => rowIndexes[cell.Item1]);
        int firstColumn = selected.Min(cell => columnIndexes[cell.Column]);
        int lastColumn = selected.Max(cell => columnIndexes[cell.Column]);
        var selectedSet = selected.ToHashSet();
        var result = new List<IReadOnlyList<string>>(lastRow - firstRow + 1);
        for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
        {
            var values = new string[lastColumn - firstColumn + 1];
            for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
            {
                DataGridColumn column = columns[columnIndex];
                RecordRowViewModel row = rows[rowIndex];
                values[columnIndex - firstColumn] = selectedSet.Contains((row, column))
                    ? GetClipboardCellText(row, column)
                    : string.Empty;
            }

            result.Add(values);
        }

        matrix = result;
        return true;
    }

    internal bool TryBuildPasteCells(
        IReadOnlyList<IReadOnlyList<string>> matrix,
        out IReadOnlyList<SpreadsheetPasteCell> cells,
        out string? error)
    {
        cells = [];
        error = null;
        if (matrix.Count == 0 || matrix.All(row => row.Count == 0))
        {
            error = "В буфере обмена нет табличных значений.";
            return false;
        }

        RecordRowViewModel[] rows = RecordsGrid.Items.Cast<object>().OfType<RecordRowViewModel>().ToArray();
        DataGridColumn[] columns = RecordsGrid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        int anchorRow = Array.FindIndex(rows, row => ReferenceEquals(row, RecordsGrid.CurrentCell.Item));
        int anchorColumn = Array.FindIndex(columns, column => ReferenceEquals(column, RecordsGrid.CurrentCell.Column));
        if (anchorRow < 0 || anchorColumn < 0)
        {
            error = "Выберите начальную ячейку для вставки.";
            return false;
        }

        string? singleValue = matrix.Count == 1 && matrix[0].Count == 1 ? matrix[0][0] : null;
        if (singleValue is not null && RecordsGrid.SelectedCells.Count > 1)
        {
            var selectedTargets = RecordsGrid.SelectedCells
                .Where(cell => cell.Item is RecordRowViewModel)
                .OrderBy(cell => Array.FindIndex(rows, row => ReferenceEquals(row, cell.Item)))
                .ThenBy(cell => cell.Column.DisplayIndex)
                .ToArray();
            var fillCells = new List<SpreadsheetPasteCell>(selectedTargets.Length);
            foreach (DataGridCellInfo selected in selectedTargets)
            {
                var record = (RecordRowViewModel)selected.Item;
                if (!TryCreatePasteCell(record, selected.Column, singleValue, out SpreadsheetPasteCell? cell, out error))
                {
                    return false;
                }

                if (cell is not null)
                {
                    fillCells.Add(cell);
                }
            }

            cells = fillCells;
            return true;
        }

        int width = matrix.Max(row => row.Count);
        if (anchorRow + matrix.Count > rows.Length || anchorColumn + width > columns.Length)
        {
            error = "Диапазон из буфера не помещается в оставшиеся строки или столбцы таблицы.";
            return false;
        }

        var result = new List<SpreadsheetPasteCell>(matrix.Count * width);
        for (var rowOffset = 0; rowOffset < matrix.Count; rowOffset++)
        {
            for (var columnOffset = 0; columnOffset < width; columnOffset++)
            {
                DataGridColumn column = columns[anchorColumn + columnOffset];
                string value = columnOffset < matrix[rowOffset].Count
                    ? matrix[rowOffset][columnOffset]
                    : string.Empty;
                RecordRowViewModel record = rows[anchorRow + rowOffset];
                if (!TryCreatePasteCell(record, column, value, out SpreadsheetPasteCell? cell, out error))
                {
                    return false;
                }

                if (cell is not null)
                {
                    result.Add(cell);
                }
            }
        }

        cells = result;
        return true;
    }

    private bool TryCreatePasteCell(
        RecordRowViewModel record,
        DataGridColumn column,
        string value,
        out SpreadsheetPasteCell? cell,
        out string? error)
    {
        if (_spreadsheetColumns.TryGetValue(column, out SpreadsheetColumnViewModel? descriptor) &&
            descriptor.IsEditable)
        {
            cell = new SpreadsheetPasteCell(
                record,
                descriptor.FieldId,
                value,
                descriptor.Projection);
            error = null;
            return true;
        }

        if (ReferenceEquals(column, _localizationTitleColumn) ||
            ReferenceEquals(column, _localizationDescriptionColumn))
        {
            if (record.CanEditPrimaryLocalization)
            {
                cell = new SpreadsheetPasteCell(
                    record,
                    -1,
                    value,
                    null,
                    ReferenceEquals(column, _localizationTitleColumn)
                        ? SpreadsheetPasteTarget.LocalizationTitle
                        : SpreadsheetPasteTarget.LocalizationDescription);
                error = null;
                return true;
            }

            if (AreClipboardValuesEquivalent(value, GetClipboardCellText(record, column), descriptor: null))
            {
                cell = null;
                error = null;
                return true;
            }

            cell = null;
            error = $"Для строки «{record.DisplayName}» нет связанной записи texts.res; сначала создайте тексты.";
            return false;
        }

        if (AreClipboardValuesEquivalent(value, GetClipboardCellText(record, column), descriptor))
        {
            cell = null;
            error = null;
            return true;
        }

        cell = null;
        string columnName = descriptor?.Title ?? column.Header as string ?? "служебный столбец";
        string address = GetSpreadsheetAddress(RecordsGrid, record, column);
        error = $"Ячейка {address}, столбец «{columnName}»: значение вычисляется редактором и не может быть изменено. " +
                "Если эта колонка попала в скопированный диапазон из Excel без изменений, вставка пропустит её автоматически.";
        return false;
    }

    private static bool AreClipboardValuesEquivalent(
        string incoming,
        string current,
        SpreadsheetColumnViewModel? descriptor)
    {
        if (string.Equals(incoming, current, StringComparison.Ordinal))
        {
            return true;
        }

        return descriptor?.Kind switch
        {
            "Float32" => TryParseSpreadsheetNumber(incoming, out double incomingNumber) &&
                         TryParseSpreadsheetNumber(current, out double currentNumber) &&
                         incomingNumber.Equals(currentNumber),
            "Int32" => int.TryParse(incoming.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int incomingInt) &&
                       int.TryParse(current.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int currentInt) &&
                       incomingInt == currentInt,
            "UInt32" or "Byte" => uint.TryParse(incoming.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out uint incomingUInt) &&
                                  uint.TryParse(current.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint currentUInt) &&
                                  incomingUInt == currentUInt,
            "Boolean" => TryParseSpreadsheetBoolean(incoming, out bool incomingBool) &&
                         TryParseSpreadsheetBoolean(current, out bool currentBool) &&
                         incomingBool == currentBool,
            _ => false,
        };
    }

    private static bool TryParseSpreadsheetNumber(string text, out double value)
    {
        string normalized = text.Trim().Replace('\u00A0', ' ');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return normalized.Contains(',') &&
               !normalized.Contains('.') &&
               double.TryParse(
                   normalized.Replace(',', '.'),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryParseSpreadsheetBoolean(string text, out bool value)
    {
        string normalized = text.Trim();
        if (normalized is "1" or "TRUE" or "True" or "true" or "ИСТИНА" or "Истина" or "истина")
        {
            value = true;
            return true;
        }

        if (normalized is "0" or "FALSE" or "False" or "false" or "ЛОЖЬ" or "Ложь" or "ложь")
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private string GetClipboardCellText(RecordRowViewModel record, DataGridColumn column)
    {
        if (_spreadsheetColumns.TryGetValue(column, out SpreadsheetColumnViewModel? descriptor))
        {
            RecordFieldViewModel? field = record.GetCell(descriptor.FieldId);
            if (field is null)
            {
                return string.Empty;
            }

            return descriptor.Projection is null
                ? field.EditorText
                : field.GetProjectedText(descriptor.Projection);
        }

        if (ReferenceEquals(column, _localizationKeyColumn))
        {
            return record.LocalizationKeyText;
        }

        if (ReferenceEquals(column, _localizationTitleColumn))
        {
            return record.LocalizationTitle;
        }

        if (ReferenceEquals(column, _localizationDescriptionColumn))
        {
            return record.LocalizationDescription;
        }

        return Equals(column.Header, "#")
            ? record.Index.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    internal static DataObject CreateSpreadsheetClipboardDataObject(
        IReadOnlyList<IReadOnlyList<string>> matrix)
    {
        SpreadsheetClipboardPayload payload = SpreadsheetClipboardText.CreatePayload(matrix);
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, payload.Text);
        data.SetData(DataFormats.Text, payload.Text);
        data.SetData(DataFormats.CommaSeparatedValue, payload.Csv);
        data.SetData(DataFormats.Html, payload.Html);
        return data;
    }

    private static void SetClipboardDataWithRetry(IDataObject data)
    {
        ExternalException? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (ExternalException exception)
            {
                lastError = exception;
                Thread.Sleep(15 * (attempt + 1));
            }
        }

        throw lastError ?? new ExternalException("Не удалось открыть буфер обмена.");
    }

    private static string GetClipboardTextWithRetry()
    {
        ExternalException? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : string.Empty;
            }
            catch (ExternalException exception)
            {
                lastError = exception;
                Thread.Sleep(15 * (attempt + 1));
            }
        }

        throw lastError ?? new ExternalException("Не удалось открыть буфер обмена.");
    }

    private void RecordsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_spreadsheetViewModel?.RecordsView is not ListCollectionView recordsView)
        {
            return;
        }

        SpreadsheetColumnViewModel? descriptor = null;
        if (_spreadsheetColumns.TryGetValue(e.Column, out var mapped))
        {
            descriptor = mapped;
            if (!descriptor.IsSortable)
            {
                e.Handled = true;
                return;
            }
        }
        else if (!Equals(e.Column.Header, "#"))
        {
            return;
        }

        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in RecordsGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction;
        recordsView.CustomSort = new SpreadsheetRecordComparer(descriptor, direction);
    }

    private void ValidationGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: ValidationMessageViewModel diagnostic })
        {
            return;
        }

        if (_viewModel.NavigateToDiagnosticCommand.CanExecute(diagnostic))
        {
            _viewModel.NavigateToDiagnosticCommand.Execute(diagnostic);
            e.Handled = true;
        }
    }

    private async void ViewModel_OpenFileRequested(object? sender, EventArgs e)
    {
        string? discardedSourcePath = null;
        if (_viewModel.HasUnsavedWork)
        {
            var discardDialog = new ConfirmDialog(
                "Открыть другой файл?",
                "Несохранённые изменения текущей рабочей модели или подготовленный звук будут потеряны.",
                "Сохраните database.res/texts.res либо отдельную копию звукового RES.",
                "Открыть без сохранения",
                "!")
            {
                Owner = this,
            };

            if (discardDialog.ShowDialog() is not true)
            {
                return;
            }

            discardedSourcePath = _viewModel.SourcePath;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Открыть database.res",
            Filter = "Evil Islands database.res|database.res;*.res|RES-контейнеры (*.res)|*.res|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            InitialDirectory = _preferences.GetLastDatabaseDirectory()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) is true)
        {
            await _viewModel.OpenPathAsync(dialog.FileName);
            if (_viewModel.HasDocument && string.Equals(
                    Path.GetFullPath(_viewModel.SourcePath),
                    Path.GetFullPath(dialog.FileName),
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(discardedSourcePath))
                {
                    _recoveryStore.Discard(discardedSourcePath);
                }
                _preferences.RememberDatabasePath(dialog.FileName);
                await OfferRecoveryAsync(dialog.FileName);
            }
        }
    }

    private async Task OfferRecoveryAsync(string sourcePath)
    {
        RecoverySnapshot? snapshot = _recoveryStore.FindLatest(sourcePath);
        if (snapshot is null)
        {
            return;
        }

        var dialog = new ConfirmDialog(
            "Найдена аварийная копия",
            $"Есть автоматически сохранённая версия от {snapshot.CreatedUtc.ToLocalTime():g}.",
            "Исходные игровые файлы не будут перезаписаны. Копия откроется отдельно; после проверки сохраните её под нужным именем.",
            "Открыть аварийную копию",
            "↻")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        await OpenRecoverySnapshotAsync(snapshot);
    }

    private async Task OpenRecoverySnapshotAsync(RecoverySnapshot snapshot)
    {
        await _viewModel.OpenPathAsync(snapshot.DatabasePath);
        if (_viewModel.HasDocument && string.Equals(
                Path.GetFullPath(_viewModel.SourcePath),
                Path.GetFullPath(snapshot.DatabasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            bool soundRestored = await _viewModel.RestorePendingSoundRecoveryAsync(snapshot);
            _viewModel.ReportRecoveryOpened(snapshot);
            if (!soundRestored)
            {
                _viewModel.ReportSpreadsheetError(
                    "database.res и texts.res восстановлены, но подготовленный звук не удалось автоматически вернуть. " +
                    $"Его файл сохранён здесь: {snapshot.PendingSoundAssetPath}");
            }
        }
    }

    private async void ViewModel_SaveRequested(object? sender, EventArgs e)
    {
        if (!CommitPendingCell())
        {
            return;
        }

        var allowGameInstallationPath = false;
        if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(_viewModel.SourcePath))
        {
            var dialog = new ConfirmDialog(
                "Сохранить database.res в каталоге игры?",
                "Открытый файл будет заменён проверенной версией.",
                "Перед заменой рядом будет создана резервная .bak-копия. Это явная запись в установленный мод.",
                "Сохранить с резервной копией",
                "!")
            {
                Owner = this,
            };

            if (dialog.ShowDialog() is not true)
            {
                return;
            }

            allowGameInstallationPath = true;
        }

        await _viewModel.SaveCurrentAsync(allowGameInstallationPath);
    }

    private async void ViewModel_SaveCopyRequested(object? sender, EventArgs e)
    {
        if (!CommitPendingCell())
        {
            return;
        }

        var stagingDirectory = GetStagingDirectory();
        var sourceName = Path.GetFileNameWithoutExtension(_viewModel.SourceFileName);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "database";
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить новую проверенную копию",
            Filter = "Evil Islands RES-контейнер (*.res)|*.res",
            DefaultExt = ".res",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false,
            CreatePrompt = false,
            RestoreDirectory = true,
            InitialDirectory = stagingDirectory,
            FileName = $"{sourceName}.edited.res",
        };

        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        var targetPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(targetPath, Path.GetFullPath(_viewModel.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowSafetyDialog(
                "Исходник заблокирован",
                "Нельзя сохранить поверх открытого database.res.",
                "Выберите новое имя в staging-папке или другом безопасном каталоге.");
            return;
        }

        if (File.Exists(targetPath))
        {
            ShowSafetyDialog(
                "Перезапись запрещена",
                "Файл назначения уже существует.",
                "Редактор БД от Атамана создаёт только новые файлы. Выберите другое имя.");
            return;
        }

        if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(targetPath))
        {
            ShowSafetyDialog(
                "Каталог игры защищён",
                "Preview-версия не сохраняет файлы в Evil Islands или Starter\\MODS.",
                "Сохраните копию в staging-папку. Установка в игру будет отдельной явной операцией.");
            return;
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(targetPath))
        {
            ShowSafetyDialog(
                "Небезопасная ссылка в пути",
                "Папка назначения проходит через junction или символическую ссылку.",
                "В Preview-версии такие пути запрещены: они могут незаметно вести в каталог игры.");
            return;
        }

        await _viewModel.SaveCopyPathAsync(targetPath);
    }

    private bool CommitPendingCell()
    {
        if (!RecordsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !LocalizationGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !LocalizationGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !TextsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !TextsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return false;
        }

        if (RecordsGrid.CurrentCell.Item is RecordRowViewModel record &&
            _spreadsheetColumns.TryGetValue(RecordsGrid.CurrentCell.Column, out var descriptor) &&
            record.GetCell(descriptor.FieldId)?.HasError is true)
        {
            RecordsGrid.Focus();
            RecordsGrid.BeginEdit();
            return false;
        }

        return true;
    }

    private void ViewModel_SearchFocusRequested(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentSection == WorkspaceSection.Resources)
        {
            ResourceSearchBox.Focus();
            ResourceSearchBox.SelectAll();
            return;
        }

        if (_viewModel.CurrentSection == WorkspaceSection.Texts)
        {
            TextsSearchBox.Focus();
            TextsSearchBox.SelectAll();
            return;
        }

        if (_viewModel.CurrentSection != WorkspaceSection.Records || _viewModel.SelectedTable is null)
        {
            var table = _viewModel.Databases
                .FirstOrDefault(database => database.Key == "units")?.Tables
                .FirstOrDefault(candidate => candidate.Key == "units.prototypes")
                ?? _viewModel.Databases.SelectMany(database => database.Tables).FirstOrDefault(candidate => candidate.IsEnabled);
            if (table is not null)
            {
                _viewModel.SelectTableCommand.Execute(table);
            }
        }

        RecordSearchBox.Focus();
        RecordSearchBox.SelectAll();
    }

    private static string GetStagingDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var staging = Path.Combine(documents, "Редактор БД от Атамана", "staging");
        try
        {
            Directory.CreateDirectory(staging);
            return staging;
        }
        catch (UnauthorizedAccessException)
        {
            return documents;
        }
        catch (IOException)
        {
            return documents;
        }
    }

    private void ShowSafetyDialog(string heading, string message, string detail)
    {
        var dialog = new ConfirmDialog(heading, message, detail, "Понятно", "▣")
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void ScaleDownButton_Click(object sender, RoutedEventArgs e) => ChangeInterfaceScale(-1);

    private void ScaleUpButton_Click(object sender, RoutedEventArgs e) => ChangeInterfaceScale(1);

    private void ChangeInterfaceScale(int direction)
    {
        double current = InterfaceScaleTransform.ScaleX;
        int currentIndex = Enumerable.Range(0, InterfaceScaleSteps.Length)
            .OrderBy(index => Math.Abs(InterfaceScaleSteps[index] - current))
            .First();
        int nextIndex = Math.Clamp(currentIndex + Math.Sign(direction), 0, InterfaceScaleSteps.Length - 1);
        _hasExplicitInterfaceScale = true;
        SetInterfaceScale(InterfaceScaleSteps[nextIndex], persist: true);
    }

    internal double CurrentInterfaceScale => InterfaceScaleTransform.ScaleX;

    internal void SetInterfaceScale(double scale, bool persist)
    {
        double normalized = InterfaceScaleSteps
            .OrderBy(candidate => Math.Abs(candidate - scale))
            .First();
        InterfaceScaleTransform.ScaleX = normalized;
        InterfaceScaleTransform.ScaleY = normalized;
        InterfaceScaleText.Text = $"{normalized * 100:0}%";
        ScaleDownButton.IsEnabled = normalized > InterfaceScaleSteps[0];
        ScaleUpButton.IsEnabled = normalized < InterfaceScaleSteps[^1];
        if (persist)
        {
            _preferences.RememberInterfaceScale(normalized);
        }
    }

    private void ApplyAutomaticInterfaceScale(IntPtr windowHandle)
    {
        if (_hasExplicitInterfaceScale)
        {
            return;
        }

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        IntPtr monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        int workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        int workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        double automaticScale = ChooseAutomaticInterfaceScale(dpiScale, workWidth, workHeight);
        SetInterfaceScale(automaticScale, persist: false);
    }

    internal static double ChooseAutomaticInterfaceScale(double dpiScale, int workWidth, int workHeight) =>
        dpiScale <= 1.1 && workWidth >= 2400 && workHeight >= 1300
            ? 1.25
            : 1.0;

    private void ApplyStoredLayout()
    {
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(_layout.MainWindowWidth, MinWidth, Math.Max(MinWidth, workArea.Width));
        Height = Math.Clamp(_layout.MainWindowHeight, MinHeight, Math.Max(MinHeight, workArea.Height));
        SidebarColumn.Width = new GridLength(_layout.SidebarWidth, GridUnitType.Pixel);
        HeaderSidebarColumn.Width = new GridLength(_layout.SidebarWidth + 7, GridUnitType.Pixel);
        FormulaPaneRow.Height = new GridLength(_layout.FormulaBarHeight, GridUnitType.Pixel);
        TextsFormulaPaneRow.Height = new GridLength(_layout.TextsFormulaBarHeight, GridUnitType.Pixel);
        BriefingScenesColumn.Width = new GridLength(_layout.BriefingScenesWidth, GridUnitType.Pixel);
        BriefingTimelineColumn.Width = new GridLength(_layout.BriefingTimelineWidth, GridUnitType.Pixel);
        if (_layout.MainWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool? ShowLongTextEditorDialog(LongTextEditorDialog dialog)
    {
        Rect workArea = SystemParameters.WorkArea;
        dialog.Width = Math.Clamp(_layout.LongTextEditorWidth, dialog.MinWidth, Math.Max(dialog.MinWidth, workArea.Width));
        dialog.Height = Math.Clamp(_layout.LongTextEditorHeight, dialog.MinHeight, Math.Max(dialog.MinHeight, workArea.Height));
        if (_layout.LongTextEditorMaximized)
        {
            dialog.Loaded += MaximizeLongTextEditorOnLoad;
        }

        bool? result = dialog.ShowDialog();
        dialog.Loaded -= MaximizeLongTextEditorOnLoad;
        Rect restoreBounds = dialog.RestoreBounds;
        if (dialog.WindowState == WindowState.Normal)
        {
            _layout.LongTextEditorWidth = dialog.ActualWidth;
            _layout.LongTextEditorHeight = dialog.ActualHeight;
        }
        else if (!restoreBounds.IsEmpty)
        {
            _layout.LongTextEditorWidth = restoreBounds.Width;
            _layout.LongTextEditorHeight = restoreBounds.Height;
        }

        _layout.LongTextEditorMaximized = dialog.WindowState == WindowState.Maximized;
        _preferences.RememberLayout(_layout);
        return result;
    }

    private static void MaximizeLongTextEditorOnLoad(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void CaptureAndPersistLayout()
    {
        if (SidebarColumn.ActualWidth > 0)
        {
            _layout.SidebarWidth = SidebarColumn.ActualWidth;
            HeaderSidebarColumn.Width = new GridLength(_layout.SidebarWidth + 7, GridUnitType.Pixel);
        }

        if (FormulaPaneRow.ActualHeight > 0)
        {
            _layout.FormulaBarHeight = FormulaPaneRow.ActualHeight;
        }

        if (TextsFormulaPaneRow.ActualHeight > 0)
        {
            _layout.TextsFormulaBarHeight = TextsFormulaPaneRow.ActualHeight;
        }

        if (BriefingScenesColumn.ActualWidth > 0)
        {
            _layout.BriefingScenesWidth = BriefingScenesColumn.ActualWidth;
        }

        if (BriefingTimelineColumn.ActualWidth > 0)
        {
            _layout.BriefingTimelineWidth = BriefingTimelineColumn.ActualWidth;
        }

        if (_detailsWindow is null &&
            DetailsPane.ActualHeight >= 180 &&
            _spreadsheetViewModel?.SelectedRecordHasDetails is true)
        {
            _layout.DetailsPaneHeight = DetailsPane.ActualHeight;
        }

        else if (_detailsWindow is not null)
        {
            CaptureDetachedDetailsWindowLayout(_detailsWindow);
        }

        bool hasLocalization = _spreadsheetViewModel?.SelectedRecord?.HasLocalizationReferences is true;
        bool hasSound = _spreadsheetViewModel?.SelectedRecordHasSoundReferences is true;
        if (hasLocalization && hasSound && LocalizationPanel.ActualHeight > 0 && SoundPanel.ActualHeight > 0)
        {
            double combined = LocalizationPanel.ActualHeight + SoundPanel.ActualHeight;
            _layout.LocalizationPaneRatio = LocalizationPanel.ActualHeight / combined;
        }

        if (WindowState == WindowState.Normal && ActualWidth >= MinWidth && ActualHeight >= MinHeight)
        {
            _layout.MainWindowWidth = ActualWidth;
            _layout.MainWindowHeight = ActualHeight;
        }

        _layout.MainWindowMaximized = WindowState == WindowState.Maximized;
        _preferences.RememberLayout(_layout);
    }

    private void LayoutSplitter_DragCompleted(object sender, DragCompletedEventArgs e) =>
        CaptureAndPersistLayout();

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (HeaderSidebarColumn is not null && SidebarColumn is not null)
        {
            HeaderSidebarColumn.Width = new GridLength(SidebarColumn.ActualWidth + 7, GridUnitType.Pixel);
        }
    }

    private void ResetLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog(
            "Сбросить раскладку?",
            "Размеры всех панелей и вспомогательных окон вернутся к стандартным.",
            "Данные database.res и texts.res, несохранённые изменения и ширина колонок таблиц не изменятся.",
            "Сбросить раскладку",
            "↺")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || !dialog.IsConfirmed)
        {
            return;
        }

        if (_detailsWindow is not null)
        {
            _preserveDetailsDetachedState = false;
            _detailsWindow.Close();
        }

        _preferences.ResetLayout();
        _layout = _preferences.GetLayout();
        WindowState = WindowState.Normal;
        ApplyStoredLayout();
        UpdateDetailsPaneLayout();
        _viewModel.ReportLayoutReset();
    }

    private void UpdateDetailsPaneLayout()
    {
        if (DetailsPaneRow is null || RecordsTableBorder is null)
        {
            return;
        }

        bool inRecords = _spreadsheetViewModel?.CurrentSection == WorkspaceSection.Records;
        bool hasLocalization = inRecords && _spreadsheetViewModel?.SelectedRecord?.HasLocalizationReferences is true;
        bool hasSound = inRecords && _spreadsheetViewModel?.SelectedRecordHasSoundReferences is true;
        ConfigureDetailsPaneContents(hasLocalization, hasSound);

        if (_detailsWindow is not null)
        {
            DetailsSplitterRow.Height = new GridLength(0);
            DetailsPaneRow.MinHeight = 0;
            DetailsPaneRow.Height = new GridLength(0);
            UpdateDetailsDetachButtons();
            return;
        }

        DetailsSplitterRow.Height = GridLength.Auto;
        UpdateDetailsDetachButtons();

        if (_layout.DetailsPaneDetached && (hasLocalization || hasSound) && !_detailsDetachScheduled && IsLoaded)
        {
            _detailsDetachScheduled = true;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    _detailsDetachScheduled = false;
                    if (_layout.DetailsPaneDetached && _detailsWindow is null)
                    {
                        DetachDetailsPane(persist: false);
                    }
                }));
        }

        if (!hasLocalization && !hasSound)
        {
            DetailsPaneRow.MinHeight = 0;
            DetailsPaneRow.Height = GridLength.Auto;
            LocalizationPaneRow.Height = GridLength.Auto;
            SoundPaneRow.Height = GridLength.Auto;
            LocalizationSoundSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        const double minimumDetailsHeight = 220;
        double maximumDetailsHeight = RecordsTableBorder.ActualHeight > 0
            ? Math.Max(minimumDetailsHeight, RecordsTableBorder.ActualHeight - 160)
            : 640;
        DetailsPaneRow.MinHeight = hasLocalization && hasSound ? 240 : minimumDetailsHeight;
        DetailsPaneRow.Height = new GridLength(
            Math.Clamp(_layout.DetailsPaneHeight, minimumDetailsHeight, maximumDetailsHeight),
            GridUnitType.Pixel);

    }

    private void ConfigureDetailsPaneContents(bool hasLocalization, bool hasSound)
    {
        if (hasLocalization && hasSound)
        {
            LocalizationPaneRow.MinHeight = 100;
            SoundPaneRow.MinHeight = 120;
            LocalizationPaneRow.Height = new GridLength(_layout.LocalizationPaneRatio, GridUnitType.Star);
            SoundPaneRow.Height = new GridLength(1 - _layout.LocalizationPaneRatio, GridUnitType.Star);
            LocalizationSoundSplitter.Visibility = Visibility.Visible;
        }
        else if (hasLocalization)
        {
            LocalizationPaneRow.MinHeight = 0;
            SoundPaneRow.MinHeight = 0;
            LocalizationPaneRow.Height = new GridLength(1, GridUnitType.Star);
            SoundPaneRow.Height = new GridLength(0);
            LocalizationSoundSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            LocalizationPaneRow.MinHeight = 0;
            SoundPaneRow.MinHeight = 0;
            LocalizationPaneRow.Height = new GridLength(0);
            SoundPaneRow.Height = new GridLength(1, GridUnitType.Star);
            LocalizationSoundSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void DetailsPaneDetachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsWindow is not null)
        {
            _preserveDetailsDetachedState = false;
            _detailsWindow.Close();
            return;
        }

        DetachDetailsPane(persist: true);
    }

    private void DetachDetailsPane(bool persist)
    {
        if (_detailsWindow is not null || DetailsPane.Parent is not Grid parent)
        {
            return;
        }

        if (persist)
        {
            CaptureAndPersistLayout();
        }

        parent.Children.Remove(DetailsPane);
        Rect workArea = SystemParameters.WorkArea;
        var window = new DetachedDetailsWindow
        {
            Owner = this,
            DataContext = DataContext,
            Width = Math.Clamp(_layout.DetailsWindowWidth, 720, Math.Max(720, workArea.Width)),
            Height = Math.Clamp(_layout.DetailsWindowHeight, 420, Math.Max(420, workArea.Height)),
        };
        window.PaneContent = DetailsPane;
        window.Closing += DetachedDetailsWindow_Closing;
        _detailsWindow = window;
        _layout.DetailsPaneDetached = true;
        UpdateDetailsPaneLayout();

        if (_layout.DetailsWindowMaximized)
        {
            window.Loaded += MaximizeDetachedDetailsWindowOnLoad;
        }

        window.Show();
        if (persist)
        {
            _preferences.RememberLayout(_layout);
        }
    }

    private static void MaximizeDetachedDetailsWindowOnLoad(object sender, RoutedEventArgs e)
    {
        if (sender is DetachedDetailsWindow window)
        {
            window.Loaded -= MaximizeDetachedDetailsWindowOnLoad;
            window.WindowState = WindowState.Maximized;
        }
    }

    private void DetachedDetailsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (sender is not DetachedDetailsWindow window || !ReferenceEquals(window, _detailsWindow))
        {
            return;
        }

        CaptureDetachedDetailsWindowLayout(window);
        window.Closing -= DetachedDetailsWindow_Closing;
        window.PaneContent = null;
        GenericRecordsWorkspace.Children.Add(DetailsPane);
        Grid.SetRow(DetailsPane, 2);
        _detailsWindow = null;
        _layout.DetailsPaneDetached = _preserveDetailsDetachedState;
        _preserveDetailsDetachedState = false;
        UpdateDetailsPaneLayout();
        _preferences.RememberLayout(_layout);
    }

    private void CaptureDetachedDetailsWindowLayout(DetachedDetailsWindow window)
    {
        Rect bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width >= window.MinWidth && bounds.Height >= window.MinHeight)
        {
            _layout.DetailsWindowWidth = bounds.Width;
            _layout.DetailsWindowHeight = bounds.Height;
        }

        _layout.DetailsWindowMaximized = window.WindowState == WindowState.Maximized;
        _layout.DetailsPaneDetached = true;
    }

    private void UpdateDetailsDetachButtons()
    {
        string label = _detailsWindow is null ? "↗ Открепить" : "↙ Закрепить";
        string toolTip = _detailsWindow is null
            ? "Открыть связанные тексты и звук в отдельном окне"
            : "Вернуть связанные тексты и звук под основную таблицу";
        LocalizationDetachButton.Content = label;
        LocalizationDetachButton.ToolTip = toolTip;
        SoundDetachButton.Content = label;
        SoundDetachButton.ToolTip = toolTip;
    }

    private void DetailsPaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_spreadsheetViewModel?.SelectedRecordHasDetails is not true ||
            !double.IsFinite(DetailsPane.ActualHeight) ||
            DetailsPane.ActualHeight < 180)
        {
            return;
        }

        _layout.DetailsPaneHeight = DetailsPane.ActualHeight;
        CaptureAndPersistLayout();
    }

    private void SoundLinksGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(SoundLinksGrid);
        if (viewer is null || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        double delta = -e.Delta / 120d * 42d;
        double target = Math.Clamp(viewer.VerticalOffset + delta, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < 0.1)
        {
            return;
        }

        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control &&
            e.Key is Key.C or Key.V &&
            _spreadsheetViewModel?.CurrentSection == WorkspaceSection.Records &&
            RecordsGrid.IsKeyboardFocusWithin)
        {
            RecordsGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            RecordsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            RoutedCommand clipboardCommand = e.Key == Key.C ? ApplicationCommands.Copy : ApplicationCommands.Paste;
            if (clipboardCommand.CanExecute(null, RecordsGrid))
            {
                clipboardCommand.Execute(null, RecordsGrid);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F2 &&
            _spreadsheetViewModel?.CurrentSection == WorkspaceSection.Records &&
            !FormulaEditorBox.IsKeyboardFocusWithin &&
            TryFocusFormulaEditor(selectAll: true))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 &&
            _spreadsheetViewModel?.CurrentSection == WorkspaceSection.Texts &&
            !TextsFormulaEditorBox.IsKeyboardFocusWithin &&
            TryFocusTextsFormulaEditor(selectAll: true))
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Z or Key.Y) ||
            Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        var activeViewModel = _spreadsheetViewModel ?? _viewModel;
        var command = e.Key == Key.Z ? activeViewModel.UndoCommand : activeViewModel.RedoCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        ApplyAutomaticInterfaceScale(handle);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
    }

    private static IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        NativeMinMaxInfo minMaxInfo = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // XAML loading can fail before the view model is assigned. In that
        // case WPF still raises Closing for the partially constructed window.
        if (_viewModel is null)
        {
            return;
        }

        PersistCurrentColumnWidths();

        if (_allowClose)
        {
            DetachAndDispose();
            return;
        }

        if (_closePromptPending)
        {
            e.Cancel = true;
            return;
        }

        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            ScheduleClosePrompt(() =>
                ShowSafetyDialog(
                    "Операция ещё выполняется",
                    "Дождитесь завершения текущей операции перед закрытием приложения.",
                    _viewModel.IsOperationCancelable
                        ? "Чтение можно отменить кнопкой «Отмена», после чего закрыть окно."
                        : "Атомарное сохранение уже началось и должно завершиться, чтобы не оставить неполный файл."));
            return;
        }

        if (!_viewModel.HasUnsavedWork)
        {
            if (_viewModel.HasDocument)
            {
                _recoveryStore.Discard(_viewModel.SourcePath);
            }
            DetachAndDispose();
            return;
        }

        e.Cancel = true;
        ScheduleClosePrompt(ShowUnsavedChangesClosePrompt);
    }

    private void ShowUnsavedChangesClosePrompt()
    {
        var dialog = new ConfirmDialog(
            "Закрыть без сохранения?",
            _viewModel.HasPendingSoundEdit
                ? $"В памяти осталось изменений database.res/texts.res: {_viewModel.ChangeCount}; также подготовлен звуковой RES."
                : $"В рабочей памяти осталось изменений: {_viewModel.ChangeCount}.",
            _viewModel.HasPendingSoundEdit
                ? "Подготовленный звук ещё не записан. Используйте «Сохранить копию RES» в карточке аудио."
                : "Несохранённые изменения будут потеряны. Для записи используйте «Сохранить».",
            "Закрыть без сохранения",
            "!")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() is true)
        {
            _recoveryStore.Discard(_viewModel.SourcePath);
            _allowClose = true;
            Close();
        }
    }

    private void ScheduleClosePrompt(Action showPrompt)
    {
        _closePromptPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                try
                {
                    if (!_allowClose)
                    {
                        showPrompt();
                    }
                }
                finally
                {
                    _closePromptPending = false;
                }
            }));
    }

    private void DetachAndDispose()
    {
        if (_detailsWindow is not null)
        {
            _preserveDetailsDetachedState = true;
            _detailsWindow.Close();
        }

        _recoveryTimer.Stop();
        _recoveryTimer.Tick -= RecoveryTimer_Tick;
        ContentRendered -= Window_ContentRendered;
        DataContextChanged -= MainWindow_DataContextChanged;
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged -= SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel.RecordFocusRequested -= ViewModel_RecordFocusRequested;
            _spreadsheetViewModel.PlayBriefingAudioRequested -= ViewModel_PlayBriefingAudioRequested;
            _spreadsheetViewModel.StopBriefingAudioRequested -= ViewModel_StopBriefingAudioRequested;
            _spreadsheetViewModel.ImportBriefingAudioRequested -= ViewModel_ImportBriefingAudioRequested;
            _spreadsheetViewModel.SavePendingSoundCopyRequested -= ViewModel_SavePendingSoundCopyRequested;
            _spreadsheetViewModel = null;
        }

        StopSoundPreview();

        _viewModel.OpenFileRequested -= ViewModel_OpenFileRequested;
        _viewModel.AttachTextsRequested -= ViewModel_AttachTextsRequested;
        _viewModel.AttachSoundsRequested -= ViewModel_AttachSoundsRequested;
        _viewModel.SaveRequested -= ViewModel_SaveRequested;
        _viewModel.SaveCopyRequested -= ViewModel_SaveCopyRequested;
        _viewModel.SearchFocusRequested -= ViewModel_SearchFocusRequested;
        _viewModel.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private sealed record FormulaCellContext(
        object Item,
        DataGridColumn Column,
        string Address,
        string Label,
        Func<string> ReadValue,
        Action<string>? WriteValue = null,
        Func<string?>? ReadError = null,
        bool SupportsLongTextEditor = false)
    {
        public bool IsEditable => WriteValue is not null;

        public bool CanOpenLongTextEditor => IsEditable && SupportsLongTextEditor;
    }

    private sealed class SpreadsheetRecordComparer(
        SpreadsheetColumnViewModel? column,
        ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is not RecordRowViewModel left)
            {
                return 1;
            }

            if (y is not RecordRowViewModel right)
            {
                return -1;
            }

            if (column is null)
            {
                return ApplyDirection(left.Index.CompareTo(right.Index));
            }

            var leftValue = left.GetSortValue(column);
            var rightValue = right.GetSortValue(column);
            if (leftValue is null)
            {
                return rightValue is null ? 0 : 1;
            }

            if (rightValue is null)
            {
                return -1;
            }

            return ApplyDirection(CompareValues(leftValue, rightValue));
        }

        private int ApplyDirection(int result) =>
            direction == ListSortDirection.Ascending ? result : -result;

        private static int CompareValues(object left, object right)
        {
            if (left is string leftText && right is string rightText)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(leftText, rightText);
            }

            if (left.GetType() == right.GetType() && left is IComparable comparable)
            {
                return comparable.CompareTo(right);
            }

            if (left is IConvertible && right is IConvertible)
            {
                try
                {
                    var leftNumber = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
                    var rightNumber = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
                    return leftNumber.CompareTo(rightNumber);
                }
                catch (FormatException)
                {
                    // Fall back to display text.
                }
                catch (InvalidCastException)
                {
                    // Fall back to display text.
                }
                catch (OverflowException)
                {
                    // Fall back to display text.
                }
            }

            return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
        }
    }
}
