using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ReferenceUsagesDialog : Window
{
    private readonly ReferenceUsagesDialogViewModel _viewModel;

    internal ReferenceUsagesDialog(DatabaseReferenceSearchResult result)
    {
        InitializeComponent();
        _viewModel = new ReferenceUsagesDialogViewModel(result);
        DataContext = _viewModel;
    }

    internal DatabaseReferenceUsage? SelectedUsage { get; private set; }

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

    private void OpenUsageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ReferenceUsageRowViewModel row })
        {
            SelectAndClose(row.Usage);
        }
    }

    private void UsagesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UsagesGrid.SelectedItem is ReferenceUsageRowViewModel row)
        {
            SelectAndClose(row.Usage);
            e.Handled = true;
        }
    }

    private void SelectAndClose(DatabaseReferenceUsage usage)
    {
        SelectedUsage = usage;
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
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

internal sealed class ReferenceUsagesDialogViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private string _kindFilter = "All";

    public ReferenceUsagesDialogViewModel(DatabaseReferenceSearchResult result)
    {
        TargetTitle = $"{result.Target.RecordName} · {result.Target.RecordIdentity}";
        TargetPath = $"{result.Target.TableTitle} · строка {result.Target.RecordIndex + 1:N0}";
        ConfirmedCount = result.ConfirmedCount;
        StructuredCount = result.StructuredCount;
        Usages = new ObservableCollection<ReferenceUsageRowViewModel>(
            result.Usages.Select(usage => new ReferenceUsageRowViewModel(usage)));
        UsagesView = CollectionViewSource.GetDefaultView(Usages);
        UsagesView.Filter = FilterUsage;
    }

    public string TargetTitle { get; }
    public string TargetPath { get; }
    public int ConfirmedCount { get; }
    public int StructuredCount { get; }
    public ObservableCollection<ReferenceUsageRowViewModel> Usages { get; }
    public ICollectionView UsagesView { get; }
    public Visibility EmptyStateVisibility => Usages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) UsagesView.Refresh(); }
    }

    public string KindFilter
    {
        get => _kindFilter;
        set { if (SetProperty(ref _kindFilter, value)) UsagesView.Refresh(); }
    }

    private bool FilterUsage(object item)
    {
        if (item is not ReferenceUsageRowViewModel row)
        {
            return false;
        }

        bool kindMatches = KindFilter switch
        {
            "Confirmed" => row.Usage.MatchKind == DatabaseReferenceMatchKind.Confirmed,
            "Structured" => row.Usage.MatchKind == DatabaseReferenceMatchKind.StructuredValue,
            _ => true,
        };
        return kindMatches && (SearchText.Trim().Length == 0 ||
            row.SearchText.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }
}

internal sealed class ReferenceUsageRowViewModel
{
    public ReferenceUsageRowViewModel(DatabaseReferenceUsage usage)
    {
        Usage = usage;
    }

    public DatabaseReferenceUsage Usage { get; }
    public string KindText => Usage.MatchKind == DatabaseReferenceMatchKind.Confirmed
        ? "Точная ссылка"
        : "Составное значение";
    public string TableTitle => Usage.TableTitle;
    public string RecordName => Usage.RecordName;
    public string FieldName => Usage.FieldName;
    public string Value => Usage.Value;
    public string SearchText => string.Join('\n', KindText, TableTitle, RecordName, FieldName, Value);
}
