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

    public bool CreateBackup => _withoutBackup?.IsChecked != true;
    private System.Windows.Controls.CheckBox? _withoutBackup;
    public void AllowSavingWithoutBackup()
    {
        _withoutBackup = new System.Windows.Controls.CheckBox { Content = "Сохранить без бэкапа", Margin = new Thickness(0,12,0,0), Foreground = MessageText.Foreground };
        var panel = new System.Windows.Controls.StackPanel();
        var original = DetailBorder.Child;
        DetailBorder.Child = null;
        panel.Children.Add(original);
        panel.Children.Add(_withoutBackup);
        DetailBorder.Child = panel;
        DetailBorder.Visibility = Visibility.Visible;
        _withoutBackup.Checked += (_, _) => ConfirmButton.Content = "Сохранить без бэкапа";
        _withoutBackup.Unchecked += (_, _) => ConfirmButton.Content = "Сохранить с резервной копией";
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
