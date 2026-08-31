using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.App.Services;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class RecoverySessionDialog : Window
{
    internal RecoverySessionDialog(IReadOnlyList<RecoverySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        InitializeComponent();
        RecoveryList.ItemsSource = snapshots.Select(snapshot => new RecoveryChoice(snapshot)).ToArray();
        RecoveryList.SelectedIndex = snapshots.Count > 0 ? 0 : -1;
        UpdateOpenButton();
    }

    internal RecoverySnapshot? SelectedSnapshot { get; private set; }

    private void RecoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOpenButton();

    private void UpdateOpenButton() => OpenButton.IsEnabled = RecoveryList.SelectedItem is RecoveryChoice;

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not RecoveryChoice selected)
        {
            return;
        }

        SelectedSnapshot = selected.Snapshot;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private sealed class RecoveryChoice
    {
        public RecoveryChoice(RecoverySnapshot snapshot)
        {
            Snapshot = snapshot;
            SourcePath = snapshot.SourcePath;
            FileName = Path.GetFileName(snapshot.SourcePath);
            DirectoryName = Path.GetDirectoryName(snapshot.SourcePath) ?? snapshot.SourcePath;
            CreatedText = snapshot.CreatedUtc.ToLocalTime().ToString("g");
        }

        public RecoverySnapshot Snapshot { get; }

        public string SourcePath { get; }

        public string FileName { get; }

        public string DirectoryName { get; }

        public string CreatedText { get; }
    }
}
