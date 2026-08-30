using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class LongTextEditorDialog : Window
{
    private readonly Func<string, (bool Success, string? Error)> _tryApply;

    internal LongTextEditorDialog(
        string address,
        string fieldName,
        string initialText,
        Func<string, (bool Success, string? Error)> tryApply)
    {
        _tryApply = tryApply ?? throw new ArgumentNullException(nameof(tryApply));
        InitializeComponent();
        AddressText.Text = string.IsNullOrWhiteSpace(address) ? "—" : address;
        FieldNameText.Text = fieldName;
        EditorBox.Text = initialText;
        Loaded += (_, _) =>
        {
            EditorBox.Focus();
            EditorBox.CaretIndex = EditorBox.Text.Length;
        };
    }

    internal string EditorText
    {
        get => EditorBox.Text;
        set => EditorBox.Text = value;
    }

    internal string Statistics => StatisticsText.Text;

    internal bool WasApplied { get; private set; }

    internal bool TryCommit()
    {
        (bool success, string? error) = _tryApply(EditorBox.Text);
        if (!success)
        {
            SetError(error ?? "Значение не принято.");
            EditorBox.Focus();
            return false;
        }

        WasApplied = true;
        SetError(null);
        return true;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryCommit())
        {
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void EditorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string text = EditorBox.Text;
        int lineCount = string.IsNullOrEmpty(text) ? 1 : text.Count(character => character == '\n') + 1;
        StatisticsText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} символов · {1:N0} строк",
            text.Length,
            lineCount);
        SetError(null);
    }

    private void SetError(string? error)
    {
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        EditorBox.BorderBrush = string.IsNullOrWhiteSpace(error)
            ? (System.Windows.Media.Brush)FindResource("BorderBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && TryCommit())
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }
}
