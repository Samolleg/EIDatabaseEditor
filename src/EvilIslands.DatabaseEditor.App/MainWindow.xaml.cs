using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private static readonly IValueConverter ProjectionConverter = new SpreadsheetProjectionConverter();
    private readonly MainWindowViewModel _viewModel;
    private readonly EditorPreferencesStore _preferences = new();
    private readonly Dictionary<DataGridColumn, SpreadsheetColumnViewModel> _spreadsheetColumns = [];
    private DataGridColumn? _localizationKeyColumn;
    private DataGridColumn? _localizationTitleColumn;
    private DataGridColumn? _localizationDescriptionColumn;
    private MainWindowViewModel? _spreadsheetViewModel;
    private bool _allowClose;
    private bool _closePromptPending;

    public MainWindow()
    {
        InitializeComponent();
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
        _viewModel.SaveRequested += ViewModel_SaveRequested;
        _viewModel.SaveCopyRequested += ViewModel_SaveCopyRequested;
        _viewModel.SearchFocusRequested += ViewModel_SearchFocusRequested;
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged -= SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel.RecordFocusRequested -= ViewModel_RecordFocusRequested;
        }

        _spreadsheetViewModel = e.NewValue as MainWindowViewModel;
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged += SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel.RecordFocusRequested += ViewModel_RecordFocusRequested;
        }

        RebuildSpreadsheetColumns();
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
        }
    }

    private void RebuildSpreadsheetColumns()
    {
        if (RecordsGrid is null)
        {
            return;
        }

        const int fixedColumnCount = 2;
        while (RecordsGrid.Columns.Count > fixedColumnCount)
        {
            RecordsGrid.Columns.RemoveAt(RecordsGrid.Columns.Count - 1);
        }

        _spreadsheetColumns.Clear();
        _localizationKeyColumn = null;
        _localizationTitleColumn = null;
        _localizationDescriptionColumn = null;
        if (_spreadsheetViewModel?.SelectedTable is null)
        {
            RecordsGrid.FrozenColumnCount = Math.Min(fixedColumnCount, RecordsGrid.Columns.Count);
            return;
        }

        var headerTemplate = (DataTemplate)FindResource("SpreadsheetColumnHeaderTemplate");
        var headerStyle = (Style)FindResource("SpreadsheetColumnHeaderStyle");
        var identityHeaderStyle = (Style)FindResource("DatabaseIdentityColumnHeaderStyle");
        var technicalHeaderStyle = (Style)FindResource("TechnicalColumnHeaderStyle");
        var elementStyle = (Style)FindResource("SpreadsheetCellText");
        var editingStyle = (Style)FindResource("SpreadsheetCellEditor");
        int nameFieldId = _spreadsheetViewModel.SelectedTable.Records.FirstOrDefault()?.NameFieldId ?? -1;
        var localizationColumnsAdded = false;
        foreach (var descriptor in _spreadsheetViewModel.VisibleSpreadsheetColumns)
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

            DataGridColumn column = new DataGridTextColumn
            {
                Binding = binding,
                ElementStyle = elementStyle,
                EditingElementStyle = editingStyle,
            };

            column.Header = descriptor;
            column.HeaderTemplate = headerTemplate;
            column.HeaderStyle = descriptor.Kind == "RawHex"
                ? technicalHeaderStyle
                : descriptor.IsNameField
                    ? identityHeaderStyle
                    : headerStyle;
            column.CellStyle = CreateSpreadsheetCellStyle(descriptor);
            column.Width = new DataGridLength(descriptor.Layout.PreferredWidth);
            column.MinWidth = descriptor.Layout.MinimumWidth;
            column.MaxWidth = descriptor.Layout.MaximumWidth;
            column.CanUserSort = descriptor.IsSortable;
            RecordsGrid.Columns.Add(column);
            _spreadsheetColumns.Add(column, descriptor);
            if (!localizationColumnsAdded &&
                descriptor.Projection is null &&
                descriptor.FieldId == nameFieldId &&
                _spreadsheetViewModel.HasTextsDocument)
            {
                AddLocalizationColumns(elementStyle, editingStyle);
                localizationColumnsAdded = true;
            }
        }

        if (!localizationColumnsAdded && _spreadsheetViewModel.HasTextsDocument)
        {
            AddLocalizationColumns(elementStyle, editingStyle);
        }

        RecordsGrid.FrozenColumnCount = Math.Min(
            _localizationTitleColumn is null ? 3 : 5,
            RecordsGrid.Columns.Count);
        UpdateSpreadsheetColumnReadOnlyStates();
    }

    private void AddLocalizationColumns(Style elementStyle, Style editingStyle)
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
            Width = new DataGridLength(250),
            MinWidth = 170,
            MaxWidth = 420,
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
            Width = new DataGridLength(240),
            MinWidth = 160,
            MaxWidth = 520,
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
            Width = new DataGridLength(320),
            MinWidth = 220,
            MaxWidth = 700,
        };
        RecordsGrid.Columns.Add(_localizationKeyColumn);
        RecordsGrid.Columns.Add(_localizationTitleColumn);
        RecordsGrid.Columns.Add(_localizationDescriptionColumn);
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
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(28, 32, 36))));
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
    }

    private void RecordsGrid_SelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        object? candidate = RecordsGrid.CurrentCell.Item;
        if (candidate is not RecordRowViewModel)
        {
            candidate = e.AddedCells.LastOrDefault().Item;
        }

        SelectSpreadsheetRecord(candidate);
    }

    private void SelectSpreadsheetRecord(object? candidate)
    {
        if (_spreadsheetViewModel is not null &&
            candidate is RecordRowViewModel record &&
            !ReferenceEquals(_spreadsheetViewModel.SelectedRecord, record))
        {
            _spreadsheetViewModel.SelectedRecord = record;
        }
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

            string text = SpreadsheetClipboardText.Serialize(matrix);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
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
                if (!TryGetPasteColumn(selected.Column, out SpreadsheetColumnViewModel? descriptor, out error))
                {
                    return false;
                }

                fillCells.Add(new SpreadsheetPasteCell(
                    (RecordRowViewModel)selected.Item,
                    descriptor.FieldId,
                    singleValue));
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
                if (!TryGetPasteColumn(column, out SpreadsheetColumnViewModel? descriptor, out error))
                {
                    return false;
                }

                string value = columnOffset < matrix[rowOffset].Count
                    ? matrix[rowOffset][columnOffset]
                    : string.Empty;
                result.Add(new SpreadsheetPasteCell(
                    rows[anchorRow + rowOffset],
                    descriptor.FieldId,
                    value));
            }
        }

        cells = result;
        return true;
    }

    private bool TryGetPasteColumn(
        DataGridColumn column,
        out SpreadsheetColumnViewModel descriptor,
        out string? error)
    {
        if (!_spreadsheetColumns.TryGetValue(column, out descriptor!) ||
            !descriptor.IsEditable ||
            descriptor.Projection is not null)
        {
            error = "Вставка возможна только в редактируемые физические поля; служебные и расшифрованные колонки доступны только для копирования.";
            return false;
        }

        error = null;
        return true;
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
        if (_viewModel.HasChanges)
        {
            var discardDialog = new ConfirmDialog(
                "Открыть другой файл?",
                "Несохранённые изменения текущей рабочей модели будут потеряны.",
                "Сначала нажмите «Сохранить» или создайте отдельную копию.",
                "Открыть без сохранения",
                "!")
            {
                Owner = this,
            };

            if (discardDialog.ShowDialog() is not true)
            {
                return;
            }
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
                _preferences.RememberDatabasePath(dialog.FileName);
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
                "EI Database Studio создаёт только новые файлы. Выберите другое имя.");
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
        var staging = Path.Combine(documents, "EI Database Studio", "staging");
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // XAML loading can fail before the view model is assigned. In that
        // case WPF still raises Closing for the partially constructed window.
        if (_viewModel is null)
        {
            return;
        }

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

        if (!_viewModel.HasChanges)
        {
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
            $"В рабочей памяти осталось изменений: {_viewModel.ChangeCount}.",
            "Несохранённые изменения будут потеряны. Для записи используйте «Сохранить».",
            "Закрыть без сохранения",
            "!")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() is true)
        {
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
        DataContextChanged -= MainWindow_DataContextChanged;
        if (_spreadsheetViewModel is not null)
        {
            _spreadsheetViewModel.PropertyChanged -= SpreadsheetViewModel_PropertyChanged;
            _spreadsheetViewModel = null;
        }

        _viewModel.OpenFileRequested -= ViewModel_OpenFileRequested;
        _viewModel.AttachTextsRequested -= ViewModel_AttachTextsRequested;
        _viewModel.SaveRequested -= ViewModel_SaveRequested;
        _viewModel.SaveCopyRequested -= ViewModel_SaveCopyRequested;
        _viewModel.SearchFocusRequested -= ViewModel_SearchFocusRequested;
        _viewModel.Dispose();
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
