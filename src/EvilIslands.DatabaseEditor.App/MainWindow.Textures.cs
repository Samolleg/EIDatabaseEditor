using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.App;

public partial class MainWindow
{
    private bool _textureExpanded;

    private void TextureSources_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
    }

    private async void TextureNearby_Click(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.ConnectNearbyTexturesAsync(); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }

    private void TextureTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DetailsPane) && _layout is not null && TextureTab is not null)
            UpdateDetailsPaneLayout();
    }

    private void TextureExpand_Click(object sender, RoutedEventArgs e)
    {
        _textureExpanded = !_textureExpanded;
        UpdateDetailsPaneLayout();
    }

    private string? SelectTextureGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папка игры с каталогом Res", InitialDirectory = _viewModel.SourceDirectory };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private async void TextureGame_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        string? game = SelectTextureGameFolder();
        if (game is null) return;
        try { await _viewModel.ConnectTextureGameAsync(game); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }

    private async void TextureMod_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        string? game = _viewModel.TextureGameFolder ?? SelectTextureGameFolder();
        if (game is null) return;
        var dialog = new OpenFileDialog { Title = "Конфигурация выбранного мода", Filter = "Конфигурация мода|config.ini", InitialDirectory = _viewModel.SourceDirectory, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ConnectTextureGameAsync(game, dialog.FileName); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }

    private async void TextureArchives_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Title = "Текстурные архивы PAK/RES", Filter = "Архивы PAK/RES|*.pak;*.res", Multiselect = true, InitialDirectory = _viewModel.SourceDirectory, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ConnectTextureArchivesAsync(dialog.FileNames); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }

    private async void TextureReload_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        try { await _viewModel.ReloadTexturesAsync(); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }

    private void TextureNavigate_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToTexture(_viewModel.SelectedTexture);
        _textureExpanded = false;
        UpdateDetailsPaneLayout();
    }
}
