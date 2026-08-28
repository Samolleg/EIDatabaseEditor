using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private MainWindowViewModel? _spreadsheetViewModel;
    private bool _allowClose;
    private bool _closePromptPending;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.OpenFileRequested += ViewModel_OpenFileRequested;
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
        if (_spreadsheetViewModel?.SelectedTable is null)
        {
            RecordsGrid.FrozenColumnCount = Math.Min(fixedColumnCount, RecordsGrid.Columns.Count);
            return;
        }

        var headerTemplate = (DataTemplate)FindResource("SpreadsheetColumnHeaderTemplate");
        var headerStyle = (Style)FindResource("SpreadsheetColumnHeaderStyle");
        var elementStyle = (Style)FindResource("SpreadsheetCellText");
        var editingStyle = (Style)FindResource("SpreadsheetCellEditor");
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
            column.HeaderStyle = headerStyle;
            column.CellStyle = CreateSpreadsheetCellStyle(descriptor);
            column.Width = new DataGridLength(descriptor.Layout.PreferredWidth);
            column.MinWidth = descriptor.Layout.MinimumWidth;
            column.MaxWidth = descriptor.Layout.MaximumWidth;
            column.CanUserSort = descriptor.IsSortable;
            RecordsGrid.Columns.Add(column);
            _spreadsheetColumns.Add(column, descriptor);
        }

        RecordsGrid.FrozenColumnCount = Math.Min(3, RecordsGrid.Columns.Count);
        UpdateSpreadsheetColumnReadOnlyStates();
    }

    private Style CreateSpreadsheetCellStyle(SpreadsheetColumnViewModel descriptor)
    {
        var baseStyle = (Style)Application.Current.FindResource(typeof(DataGridCell));
        var style = new Style(typeof(DataGridCell), baseStyle);
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        style.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding($"Cells[{descriptor.FieldId}].Path")));
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
        style.Triggers.Add(modifiedTrigger);

        var diagnosticTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].HasDiagnostic"),
            Value = true,
        };
        diagnosticTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("WarningBrush")));
        diagnosticTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
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
        style.Triggers.Add(blockingDiagnosticTrigger);

        var errorTrigger = new DataTrigger
        {
            Binding = new Binding($"Cells[{descriptor.FieldId}].HasError"),
            Value = true,
        };
        errorTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("DangerBrush")));
        errorTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        errorTrigger.Setters.Add(new Setter(
            ToolTipService.ToolTipProperty,
            new Binding($"Cells[{descriptor.FieldId}].ErrorMessage")));
        style.Triggers.Add(errorTrigger);
        return style;
    }

    private void UpdateSpreadsheetColumnReadOnlyStates()
    {
        var canEdit = _spreadsheetViewModel is { IsEditMode: true, IsBusy: false, IsCurrentTableEditable: true };
        foreach (var pair in _spreadsheetColumns)
        {
            pair.Key.IsReadOnly = !canEdit || !pair.Value.IsEditable;
        }
    }

    private void RecordsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_spreadsheetViewModel is not { IsEditMode: true, IsBusy: false, IsCurrentTableEditable: true } ||
            e.Row.Item is not RecordRowViewModel record ||
            !_spreadsheetColumns.TryGetValue(e.Column, out var descriptor) ||
            !descriptor.IsEditable ||
            record.GetCell(descriptor.FieldId)?.CanEdit is not true)
        {
            e.Cancel = true;
        }
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
            !RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true))
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
