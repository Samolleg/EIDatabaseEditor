using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EvilIslands.DatabaseEditor.App.Controls;

// Search belongs to the popup's capture subtree, not to the editor behind it.
public sealed class SearchableComboBox : ComboBox
{
    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(SearchableComboBox),
        new FrameworkPropertyMetadata("", OnSearchTextChanged));

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public event EventHandler? SearchTextChanged;
    internal object? FirstSearchResult { get; set; }
    internal TextBox? SearchBox { get; private set; }
    internal Button? ClearSearchButton { get; private set; }
    internal TextBlock? SearchStatusText { get; private set; }
    internal Popup? SearchPopup { get; private set; }
    private TextBlock? _placeholder;
    private string _searchStatus = "";

    public string SearchStatus
    {
        get => _searchStatus;
        set
        {
            _searchStatus = value;
            if (SearchStatusText is null) return;
            SearchStatusText.Text = value;
            SearchStatusText.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public SearchableComboBox() => IsTextSearchEnabled = false;

    public override void OnApplyTemplate()
    {
        if (SearchBox is not null) SearchBox.PreviewKeyDown -= SearchBox_PreviewKeyDown;
        if (ClearSearchButton is not null) ClearSearchButton.Click -= ClearSearchButton_Click;
        base.OnApplyTemplate();
        SearchPopup = GetTemplateChild("PART_Popup") as Popup;
        if (SearchPopup?.Child is not Border border || border.Child is not ScrollViewer list) return;

        SearchBox = new TextBox
        {
            Style = (Style)FindResource("DarkTextBox"), Height = 28, MinHeight = 0,
            Padding = new Thickness(7, 2, 28, 2), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Название RU, имя формы или код модели. Несколько слов уточняют поиск. Ввод не меняет экипировку."
        };
        SearchBox.SetBinding(TextBox.TextProperty, new Binding(nameof(SearchText))
        {
            Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        ClearSearchButton = new Button
        {
            Content = "×", Style = (Style)FindResource("RoundedButton"),
            Height = 24, MinHeight = 0, Width = 24, Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 2, 2), HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Очистить поиск формы"
        };
        ClearSearchButton.Click += ClearSearchButton_Click;
        _placeholder = new TextBlock
        {
            Text = "Поиск: название RU, форма или модель", Margin = new Thickness(8, 0, 28, 0),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11, IsHitTestVisible = false,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var input = new Grid();
        input.Children.Add(SearchBox);
        input.Children.Add(_placeholder);
        input.Children.Add(ClearSearchButton);
        SearchStatusText = new TextBlock
        {
            FontSize = 10.5, Margin = new Thickness(1, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush")
        };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        header.Children.Add(input);
        header.Children.Add(SearchStatusText);
        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        // Keep the header fixed while scrolling through matches.
        border.Child = null;
        content.Children.Add(list);
        border.Child = content;
        RefreshSearchChrome();
        SearchStatus = _searchStatus;
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        // Run after ComboBox's own navigation to its selected item.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsDropDownOpen || SearchBox is null) return;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsSearchInput(e.OriginalSource)) return;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // In particular, Space/Home/End/arrow keys belong to the TextBox.
        if (IsSearchInput(e.OriginalSource)) return;
        if (IsDropDownOpen && e.Key == Key.Up && FirstSearchResult is not null &&
            ItemContainerGenerator.ContainerFromItem(FirstSearchResult) is ComboBoxItem first && first.IsKeyboardFocusWithin)
        {
            SearchBox?.Focus();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    internal bool IsSearchInput(object source) => source is DependencyObject child &&
        SearchBox is not null && (ReferenceEquals(child, SearchBox) || SearchBox.IsAncestorOf(child));

    private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SearchableComboBox)d;
        control.RefreshSearchChrome();
        control.SearchTextChanged?.Invoke(control, EventArgs.Empty);
    }

    private void RefreshSearchChrome()
    {
        if (_placeholder is not null) _placeholder.Visibility = SearchText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ClearSearchButton is not null) ClearSearchButton.IsEnabled = SearchText.Length > 0;
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(SearchTextProperty, "");
        SearchBox?.Focus();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (FirstSearchResult is not null)
            {
                SetCurrentValue(SelectedItemProperty, FirstSearchResult);
                CloseSearch();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None)
        {
            UpdateLayout();
            if (FirstSearchResult is not null && ItemContainerGenerator.ContainerFromItem(FirstSearchResult) is ComboBoxItem item)
                item.Focus();
            e.Handled = true;
        }
    }

    private void CloseSearch()
    {
        SetCurrentValue(IsDropDownOpenProperty, false);
        Focus();
    }
}
