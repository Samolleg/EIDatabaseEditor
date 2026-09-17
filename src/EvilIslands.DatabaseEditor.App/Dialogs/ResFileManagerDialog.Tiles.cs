using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ResFileManagerDialog
{
    private int _tilePage;
    private const int TilePageSize = 60;
    private CancellationTokenSource _tileCancellation = new();
    private readonly SemaphoreSlim _tileGate = new(2);
    private bool _tilesRefreshQueued;

    private void TileMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TilesList is null || _filesView is null) return;
        bool tiles = TileMode.IsChecked == true;
        var selected = (tiles ? FilesGrid.SelectedItems : TilesList.SelectedItems).Cast<ResManagerFileRow>().ToArray();
        FilesGrid.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
        TilesPanel.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        if (tiles)
        {
            var rows = _filesView.Cast<ResManagerFileRow>().ToList();
            _tilePage = selected.Length > 0 ? Math.Max(0, rows.IndexOf(selected[0])) / TilePageSize : 0;
            RefreshTiles();
            foreach (var row in selected.Where(r => TilesList.Items.Contains(r))) TilesList.SelectedItems.Add(row);
        }
        else
        {
            _tileCancellation.Cancel();
            FilesGrid.SelectedItems.Clear();
            foreach (var row in selected) FilesGrid.SelectedItems.Add(row);
            if (selected.Length > 0) FilesGrid.ScrollIntoView(selected[0]);
        }
        UpdateUiState();
    }

    private void QueueTilesRefresh()
    {
        if (_tilesRefreshQueued) return;
        _tilesRefreshQueued = true;
        Dispatcher.BeginInvoke(() => { if (_tilesRefreshQueued) RefreshTiles(); }, DispatcherPriority.Background);
    }

    private void RefreshTiles()
    {
        _tilesRefreshQueued = false;
        if (TileMode.IsChecked != true) return;
        _tileCancellation.Cancel(); _tileCancellation.Dispose(); _tileCancellation = new();
        var rows = _filesView.Cast<ResManagerFileRow>().ToArray();
        int pages = Math.Max(1, (rows.Length + TilePageSize - 1) / TilePageSize);
        _tilePage = Math.Clamp(_tilePage, 0, pages - 1);
        TilesList.ItemsSource = null;
        TilesList.ItemsSource = rows.Skip(_tilePage * TilePageSize).Take(TilePageSize).ToArray();
        TilesPageText.Text = $"{_tilePage + 1} / {pages} · {rows.Length} файлов";
    }
    private void TilesPrevious_Click(object sender, RoutedEventArgs e) { _tilePage--; RefreshTiles(); }
    private void TilesNext_Click(object sender, RoutedEventArgs e) { _tilePage++; RefreshTiles(); }

    private async void TileImage_Loaded(object sender, RoutedEventArgs e)
    {
        var control = (Image)sender;
        control.Source = null;
        if (control.DataContext is not ResManagerFileRow row || row.FileType != "MMP" || row.State == "Будет удалён" || _archive is not { } archive) return;
        var token = _tileCancellation.Token;
        _edits.TryGetValue(row.ResourceName, out var edit);
        try
        {
            await _tileGate.WaitAsync(token);
            BitmapSource thumbnail;
            try
            {
                var decoded = await archive.ReadPreviewAsync(row.ResourceName, edit?.ImportedFile, token);
                token.ThrowIfCancellationRequested();
                thumbnail = await Task.Run(() =>
                {
                    var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, decoded.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4);
                    double scale = Math.Min(1, 142.0 / Math.Max(decoded.Width, decoded.Height));
                    var small = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)); small.Freeze(); return small;
                }, token);
            }
            finally { _tileGate.Release(); }
            if (!token.IsCancellationRequested && ReferenceEquals(control.DataContext, row))
            {
                control.Source = thumbnail;
                if (control.Parent is Grid panel && panel.Children[0] is TextBlock placeholder) placeholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) control.ToolTip = "Не удалось показать текстуру: " + ex.Message; }
    }
}
