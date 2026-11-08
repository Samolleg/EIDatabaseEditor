using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class StructuredStringListEditorDialog
{
    private IReadOnlyList<EmbeddedMagicChoice> _shapeChoices = [];

    private void SetShapeChoices(IReadOnlyList<EmbeddedMagicChoice> choices, EmbeddedMagicChoice? selected)
    {
        _shapeChoices = choices;
        RefreshShapeSearch(selected);
    }

    private void RefreshShapeSearch(EmbeddedMagicChoice? selected)
    {
        if (MagicShapeComboBox is null) return;
        string query = MagicShapeComboBox.SearchText.Trim();
        var matches = _shapeChoices.Where(choice => choice.MatchesSearch(query)).ToArray();
        // WPF clears Selection when the selected item disappears from ItemsSource.
        // Keep it visible so searching cannot silently clear or replace a draft item.
        bool keepSelected = selected is not null && !matches.Contains(selected);
        IReadOnlyList<EmbeddedMagicChoice> visible = keepSelected ? [selected!, .. matches] : matches;
        bool wasSyncing = _syncingMagicConstructor;
        _syncingMagicConstructor = true;
        try
        {
            MagicShapeComboBox.ItemsSource = visible;
            MagicShapeComboBox.SelectedItem = selected;
        }
        finally { _syncingMagicConstructor = wasSyncing; }

        MagicShapeComboBox.FirstSearchResult = matches.FirstOrDefault();
        MagicShapeComboBox.SearchStatus = query.Length == 0 ? "" : matches.Length == 0
            ? "Совпадений нет." + (keepSelected ? " Текущая форма сохранена." : "")
            : $"Найдено: {matches.Length} из {_shapeChoices.Count}." + (keepSelected ? " Текущая форма показана отдельно." : "");
    }

    private void MagicShapeComboBox_SearchTextChanged(object sender, EventArgs e)
    {
        if (_syncingMagicConstructor) return;
        RefreshShapeSearch(MagicShapeComboBox?.SelectedItem as EmbeddedMagicChoice);
    }

}
