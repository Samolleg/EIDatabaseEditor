using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class DatabaseComparisonDialog : Window
{
    private readonly DatabaseComparisonDialogViewModel _viewModel;
    private readonly DatabaseComparisonResult _result;

    internal DatabaseComparisonDialog(DatabaseComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        InitializeComponent();
        _result = result;
        _viewModel = new DatabaseComparisonDialogViewModel(result);
        DataContext = _viewModel;
    }

    internal DatabaseDifferenceRowViewModel? SelectedDifference { get; private set; }

    internal DatabaseChangeProject? SelectedProject { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SearchText = SearchBox.Text;
        }
    }

    private void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && KindFilter.SelectedItem is ComboBoxItem item)
        {
            _viewModel.KindFilter = item.Tag?.ToString() ?? "All";
        }
    }

    private void OpenDifferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: DatabaseDifferenceRowViewModel row } && row.CanNavigate)
        {
            SelectAndClose(row);
        }
    }

    private void DifferencesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DifferencesGrid.SelectedItem is DatabaseDifferenceRowViewModel { CanNavigate: true } row)
        {
            SelectAndClose(row);
            e.Handled = true;
        }
    }

    private void SelectAndClose(DatabaseDifferenceRowViewModel row)
    {
        SelectedDifference = row;
        DialogResult = true;
    }

    private void SelectCompatibleButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectVisibleCompatible();

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.ClearSelection();

    private async void SavePlanButton_Click(object sender, RoutedEventArgs e)
    {
        DatabaseChangeProject project = BuildSelectedProject();
        if (project.Changes.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить план переноса",
            Filter = "План переноса Evil Islands (*.eidbpatch.json)|*.eidbpatch.json|JSON (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".eidbpatch.json",
            FileName = $"{Path.GetFileNameWithoutExtension(_result.ComparedPath)}-changes.eidbpatch.json",
            OverwritePrompt = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        try
        {
            await project.SaveAsync(dialog.FileName);
            _viewModel.StatusText = $"План сохранён: {Path.GetFileName(dialog.FileName)} · {project.Changes.Count:N0} полей.";
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"Не удалось сохранить план: {exception.Message}";
        }
    }

    private void ApplySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        DatabaseChangeProject project = BuildSelectedProject();
        if (project.Changes.Count == 0)
        {
            return;
        }

        var confirm = new ConfirmDialog(
            "Применить выбранные поля?",
            $"Из второй базы будет перенесено {project.Changes.Count:N0} полей существующих записей.",
            "Перед применением редактор повторно сверит каждое исходное значение. Пакет попадёт в журнал как одна операция Undo; файлы на диске не изменятся до отдельного сохранения.",
            "Применить поля",
            "≠")
        {
            Owner = this,
        };
        if (confirm.ShowDialog() is not true || !confirm.IsConfirmed)
        {
            return;
        }

        SelectedProject = project;
        DialogResult = true;
    }

    private DatabaseChangeProject BuildSelectedProject() =>
        DatabaseChangeProject.Create(
            _result,
            _viewModel.Differences.Where(row => row.IsSelected).Select(row => row.Difference));

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class DatabaseComparisonDialogViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private string _kindFilter = "All";
    private string _statusText = "Выберите совместимые изменённые поля для переноса. Новые записи и отсутствующие поля пока остаются только в отчёте.";

    public DatabaseComparisonDialogViewModel(DatabaseComparisonResult result)
    {
        CurrentPath = result.CurrentPath;
        ComparedPath = result.ComparedPath;
        CurrentFileName = Path.GetFileName(result.CurrentPath);
        ComparedFileName = Path.GetFileName(result.ComparedPath);
        OnlyInCurrentCount = result.OnlyInCurrentCount;
        OnlyInComparedCount = result.OnlyInComparedCount;
        ModifiedRecordCount = result.ModifiedRecordCount;
        ChangedFieldCount = result.ChangedFieldCount;
        Differences = new ObservableCollection<DatabaseDifferenceRowViewModel>(
            result.Differences.Select(difference => new DatabaseDifferenceRowViewModel(difference)));
        foreach (DatabaseDifferenceRowViewModel row in Differences)
        {
            row.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(DatabaseDifferenceRowViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(HasSelection));
                }
            };
        }
        DifferencesView = CollectionViewSource.GetDefaultView(Differences);
        DifferencesView.Filter = FilterDifference;
    }

    public string CurrentPath { get; }

    public string ComparedPath { get; }

    public string CurrentFileName { get; }

    public string ComparedFileName { get; }

    public int OnlyInCurrentCount { get; }

    public int OnlyInComparedCount { get; }

    public int ModifiedRecordCount { get; }

    public int ChangedFieldCount { get; }

    public ObservableCollection<DatabaseDifferenceRowViewModel> Differences { get; }

    public ICollectionView DifferencesView { get; }

    public Visibility EmptyStateVisibility => Differences.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int SelectedCount => Differences.Count(row => row.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public void SelectVisibleCompatible()
    {
        foreach (DatabaseDifferenceRowViewModel row in Differences)
        {
            row.IsSelected = row.CanApply && DifferencesView.Contains(row);
        }
    }

    public void ClearSelection()
    {
        foreach (DatabaseDifferenceRowViewModel row in Differences)
        {
            row.IsSelected = false;
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                DifferencesView.Refresh();
            }
        }
    }

    public string KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetProperty(ref _kindFilter, value))
            {
                DifferencesView.Refresh();
            }
        }
    }

    private bool FilterDifference(object item)
    {
        if (item is not DatabaseDifferenceRowViewModel row)
        {
            return false;
        }

        bool kindMatches = KindFilter switch
        {
            "Changed" => row.Kind is DatabaseDifferenceKind.FieldChanged or
                DatabaseDifferenceKind.FieldOnlyInCurrent or DatabaseDifferenceKind.FieldOnlyInCompared,
            "Current" => row.Kind is DatabaseDifferenceKind.OnlyInCurrent or
                DatabaseDifferenceKind.FieldOnlyInCurrent,
            "Compared" => row.Kind is DatabaseDifferenceKind.OnlyInCompared or
                DatabaseDifferenceKind.FieldOnlyInCompared,
            _ => true,
        };
        if (!kindMatches)
        {
            return false;
        }

        string query = SearchText.Trim();
        return query.Length == 0 ||
               row.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}

internal sealed class DatabaseDifferenceRowViewModel : ObservableObject
{
    private bool _isSelected;

    public DatabaseDifferenceRowViewModel(DatabaseDifference difference)
    {
        Kind = difference.Kind;
        Difference = difference;
        TableKey = difference.TableKey;
        TableTitle = difference.TableTitle;
        RecordName = difference.RecordName;
        FieldName = difference.FieldName;
        CurrentValue = difference.CurrentValue;
        ComparedValue = difference.ComparedValue;
        CurrentRecordIndex = difference.CurrentRecordIndex;
        FieldId = difference.FieldId;
        StatusText = difference.Kind switch
        {
            DatabaseDifferenceKind.OnlyInCurrent => "Только в текущей",
            DatabaseDifferenceKind.OnlyInCompared => "Только во второй",
            DatabaseDifferenceKind.FieldOnlyInCurrent => "Поле только в текущей",
            DatabaseDifferenceKind.FieldOnlyInCompared => "Поле только во второй",
            _ => "Изменено",
        };
        TechnicalPath = difference.FieldId is int fieldId
            ? $"{difference.TableKey}[{difference.RecordIdentity}] / field-{fieldId}"
            : $"{difference.TableKey}[{difference.RecordIdentity}]";
        SearchText = string.Join(
            '\n',
            StatusText,
            difference.TableKey,
            difference.TableTitle,
            difference.ResourceName,
            difference.RecordIdentity,
            difference.RecordName,
            difference.FieldName,
            difference.CurrentValue,
            difference.ComparedValue);
    }

    public DatabaseDifferenceKind Kind { get; }

    public DatabaseDifference Difference { get; }

    public string TableKey { get; }

    public string TableTitle { get; }

    public string RecordName { get; }

    public string FieldName { get; }

    public string CurrentValue { get; }

    public string ComparedValue { get; }

    public int? CurrentRecordIndex { get; }

    public int? FieldId { get; }

    public string StatusText { get; }

    public string TechnicalPath { get; }

    public string SearchText { get; }

    public bool CanNavigate => CurrentRecordIndex.HasValue;

    public bool CanApply => Difference.CanApplyFromCompared;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (CanApply)
            {
                SetProperty(ref _isSelected, value);
            }
        }
    }
}
