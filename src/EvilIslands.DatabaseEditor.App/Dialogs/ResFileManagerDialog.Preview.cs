using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ResFileManagerDialog
{
    private CancellationTokenSource? _managerPreviewCancellation;
    private readonly SemaphoreSlim _managerPreviewGate = new(1, 1);
    private ResManagerFileRow? _previewRow;
    private ResFileManagerArchive? _previewArchive;
    internal Task ManagerPreviewTask { get; private set; } = Task.CompletedTask;

    private void CancelManagerPreview()
    {
        _managerPreviewCancellation?.Cancel();
        _managerPreviewCancellation?.Dispose();
        _managerPreviewCancellation = null;
    }

    private void QueueManagerPreview(ResManagerFileRow? row)
    {
        if (ReferenceEquals(row, _previewRow) && ReferenceEquals(_archive, _previewArchive)) return;
        _previewRow = row; _previewArchive = _archive;
        CancelManagerPreview();
        ManagerPreviewImage.Bitmap = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        if (row is null || _archive is null || row.FileType != "MMP") return;
        PreviewPanel.Visibility = Visibility.Visible;
        if (row.State == "Будет удалён")
        {
            PreviewInformation.Text = "Ресурс помечен для удаления.";
            return;
        }
        var cancellation = _managerPreviewCancellation = new CancellationTokenSource();
        _edits.TryGetValue(row.ResourceName, out var edit);
        PreviewInformation.Text = "Загрузка…";
        ManagerPreviewTask = LoadManagerPreviewAsync(_archive, row.ResourceName, edit?.ImportedFile, cancellation.Token);
    }

    private async Task LoadManagerPreviewAsync(ResFileManagerArchive archive, string name, ResImportedFile? imported, CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
            await _managerPreviewGate.WaitAsync(token);
            MmpImage image;
            try { image = await archive.ReadPreviewAsync(name, imported, token); }
            finally { _managerPreviewGate.Release(); }
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
                image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bitmap.Freeze();
            ManagerPreviewImage.Bitmap = bitmap;
            PreviewInformation.Text = $"{image.Width} × {image.Height} · {image.Format}" +
                (image.Format == "PNT3" ? " · отдельный слой" : "") +
                (imported is null ? "" : " · подготовленная замена/добавление");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) PreviewInformation.Text = "Не удалось показать текстуру: " + ex.Message;
        }
    }

    private void PreviewZoom_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManagerPreviewImage is not null)
            ManagerPreviewImage.Zoom = PreviewZoom.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
    }
}
