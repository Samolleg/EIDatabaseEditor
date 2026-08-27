using System.Windows;
using System.Windows.Input;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(
        string heading,
        string message,
        string detail,
        string confirmText,
        string icon = "▣")
    {
        InitializeComponent();
        HeadingText.Text = heading;
        MessageText.Text = message;
        DetailText.Text = detail;
        DetailBorder.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        IconText.Text = icon;
    }

    public bool IsConfirmed { get; private set; }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
