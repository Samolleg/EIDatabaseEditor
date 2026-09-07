using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed record TexturePreviewItem(string Label, string Name, string Information, string Source, BitmapSource? Image)
{
    public string SourceName => Path.GetFileName(Source);
}

internal sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _previewCancellation;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private int _previewRevision;
    private int _textureZoomIndex;
    public ObservableCollection<TexturePreviewItem> TexturePreviewItems { get; } = [];
    public int TextureZoomIndex
    {
        get => _textureZoomIndex;
        set
        {
            if (SetProperty(ref _textureZoomIndex, value)) OnPropertyChanged(nameof(TextureZoom));
        }
    }
    public double TextureZoom => TextureZoomIndex switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };

    public async Task RefreshTexturePreviewAsync()
    {
        if (_disposed) return;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancel = _previewCancellation = new CancellationTokenSource();
        var token = cancel.Token;
        int revision = ++_previewRevision;
        TexturePreviewItems.Clear();
        if (_disposed || SelectedTexture is not { ResourceName.Length: > 0 } row) return;
        string stem = row.ResourceName.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) ? row.ResourceName[..^4] : row.ResourceName;
        if (stem.EndsWith(".x2", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
        string[] names = [stem + ".mmp", stem + ".x2.mmp"];
        var catalog = _textureCatalog;
        for (int i = 0; i < 2; i++)
            TexturePreviewItems.Add(new(i == 0 ? "Обычная" : "Вариант .x2", names[i],
                catalog is null ? "Подключите ресурсы игры." : "Загрузка…", "", null));
        if (catalog is null) return;
        try
        {
            await Task.Delay(120, token);
            for (int i = 0; i < 2; i++)
            {
                TexturePreviewItem item;
                await _previewGate.WaitAsync(token);
                try
                {
                    var decoded = await catalog.ReadPreviewAsync(names[i], token);
                    token.ThrowIfCancellationRequested();
                    var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96,
                        decoded.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null,
                        decoded.Bgra, decoded.Width * 4);
                    bitmap.Freeze();
                    string source = catalog.Check(names[i]).Matches.FirstOrDefault()?.Source ?? "";
                    string info = $"{decoded.Width} × {decoded.Height} · {decoded.Format} · верхний уровень";
                    if (!catalog.Complete) info += " · из неполного набора";
                    if (decoded.Format == "PNT3") info += " · отдельный слой";
                    item = new(i == 0 ? "Обычная" : "Вариант .x2", names[i], info, source, bitmap);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
                {
                    item = new(i == 0 ? "Обычная" : "Вариант .x2", names[i], ex.Message, "", null);
                }
                finally { _previewGate.Release(); }
                if (revision != _previewRevision || token.IsCancellationRequested || _disposed) return;
                TexturePreviewItems[i] = item;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (revision == _previewRevision && !_disposed)
            {
                TexturePreviewItems.Clear();
                TexturePreviewItems.Add(new("Предпросмотр", row.ResourceName, "Не удалось показать текстуру: " + ex.Message, "", null));
            }
        }
    }
}
