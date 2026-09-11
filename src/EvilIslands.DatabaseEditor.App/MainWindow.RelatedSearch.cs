using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.App;

public partial class MainWindow
{
    private void RelatedTextSearch_Filter(object sender, FilterEventArgs e) =>
        e.Accepted = e.Item is LocalizationEntryViewModel row &&
            row.SearchText.Contains(RelatedTextSearch?.Text.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase);

    private void RelatedSoundSearch_Filter(object sender, FilterEventArgs e)
    {
        string query = RelatedSoundSearch?.Text.Trim() ?? "";
        e.Accepted = e.Item is SoundLinkViewModel row &&
            new[] { row.TranscriptText, row.Source, row.DisplayResourceName, row.ArchiveName, row.Reference }
                .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RelatedSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender == RelatedTextSearch)
        {
            if (!LocalizationGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !LocalizationGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            ((CollectionViewSource)Resources["RelatedTextSearchView"]).View?.Refresh();
        }
        else ((CollectionViewSource)Resources["RelatedSoundSearchView"]).View?.Refresh();
    }

    private void RelatedSoundPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SoundLinkViewModel { Resource: { } resource } row })
            ViewModel_PlayBriefingAudioRequested(this, new SoundPreviewRequestedEventArgs(resource, row.PhraseNumber ?? 0));
    }

    private void RelatedSoundStop_Click(object sender, RoutedEventArgs e) => StopSoundPreview();
}
