using System.Windows;
using System.Windows.Input;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class DetachedDetailsWindow : Window
{
    public DetachedDetailsWindow()
    {
        InitializeComponent();
    }

    internal object? PaneContent
    {
        get => PaneHost.Content;
        set => PaneHost.Content = value;
    }

    private void DockButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
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
            MaximizeButton_Click(sender, e);
            return;
        }

        DragMove();
    }
}
