using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class MmpPreviewRegression
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Put(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
    private static byte[] Header(int w, int h, uint format, byte[] pixels, uint skip = 0)
    {
        byte[] result = new byte[76 + skip + pixels.Length];
        Put(result, 0, 0x00504D4D); Put(result, 4, (uint)w); Put(result, 8, (uint)h);
        Put(result, 12, format == 0x33544E50 ? (uint)pixels.Length : 1);
        Put(result, 16, format);
        Put(result, 20, format switch { 0x8888 or 0x33544E50 => 32, 0x5650 or 0x5551 or 0x4444 => 16, 0x31545844 => 4, _ => 8 });
        uint[] masks = format switch
        {
            0x5650 => [0, 0xF800, 0x7E0, 0x1F], 0x5551 => [0x8000, 0x7C00, 0x3E0, 0x1F],
            0x4444 => [0xF000, 0xF00, 0xF0, 0xF], _ => [0xFF000000, 0xFF0000, 0xFF00, 0xFF],
        };
        for (int i = 0; i < 4; i++) Put(result, 24 + i * 12, masks[i]);
        Put(result, 72, skip);
        pixels.CopyTo(result, 76 + (int)skip);
        return result;
    }

    internal static int Run(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Use a new output folder.");
        Directory.CreateDirectory(root);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(Path.GetFullPath(args[0]), root, args);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("MMP preview: all requested checks PASS."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Reject(byte[] data, string reason)
    {
        try { MmpPreviewDecoder.Decode(data); throw new InvalidOperationException("Accepted " + reason); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
    }

    private static async Task Check(string source, string root, string[] args)
    {
        Require(MmpPreviewDecoder.Decode(Header(1, 1, 0x8888, [3, 7, 11, 128], 12)).Bgra.SequenceEqual(new byte[] { 3, 7, 11, 128 }), "8888/channel order/user data offset.");
        Require(MmpPreviewDecoder.Decode(Header(1, 1, 0x5650, [0, 0xF8])).Bgra.SequenceEqual(new byte[] { 0, 0, 255, 255 }), "565.");
        Require(MmpPreviewDecoder.Decode(Header(1, 1, 0x5551, [0, 0xFC])).Bgra.SequenceEqual(new byte[] { 0, 0, 255, 255 }), "1555.");
        Require(MmpPreviewDecoder.Decode(Header(1, 1, 0x4444, [0, 0x8F])).Bgra.SequenceEqual(new byte[] { 0, 0, 255, 136 }), "4444.");
        Console.WriteLine("PASS rgba-formats-and-offset");

        byte[] red = [0, 0xF8, 0, 0, 0, 0, 0, 0];
        Require(MmpPreviewDecoder.Decode(Header(4, 4, 0x31545844, red)).Bgra.Chunk(4).All(p => p.SequenceEqual(new byte[] { 0, 0, 255, 255 })), "DXT1 red.");
        byte[] transparent = [0, 0, 255, 255, 255, 255, 255, 255];
        Require(MmpPreviewDecoder.Decode(Header(4, 4, 0x31545844, transparent)).Bgra.All(x => x == 0), "DXT1 transparency.");
        byte[] dxt3 = Enumerable.Repeat((byte)0x88, 8).Concat(transparent).ToArray();
        var explicitImage = MmpPreviewDecoder.Decode(Header(4, 4, 0x33545844, dxt3));
        Require(explicitImage.Bgra.Chunk(4).All(p => p.SequenceEqual(new byte[] { 170, 170, 170, 136 })), "DXT3 must force four colors and explicit alpha.");
        byte[] dxt5 = new byte[16]; dxt5[0] = 255; dxt5[1] = 0;
        ulong alphaIndices = 0; for (int i = 0; i < 16; i++) alphaIndices |= 2ul << (3 * i);
        for (int i = 0; i < 6; i++) dxt5[i + 2] = (byte)(alphaIndices >> (8 * i));
        red.CopyTo(dxt5, 8);
        Require(MmpPreviewDecoder.Decode(Header(4, 4, 0x35545844, dxt5)).Bgra[3] == 218, "DXT5 seven-step alpha.");
        dxt5[0] = 0; dxt5[1] = 128; for (int i = 2; i < 8; i++) dxt5[i] = 255;
        Require(MmpPreviewDecoder.Decode(Header(4, 4, 0x35545844, dxt5)).Bgra[3] == 255, "DXT5 short palette.");
        Require(MmpPreviewDecoder.Decode(Header(4, 4, 0x32545844, dxt3)).PremultipliedAlpha &&
            MmpPreviewDecoder.Decode(Header(4, 4, 0x34545844, dxt5)).PremultipliedAlpha, "DXT2/4 premultiplied.");
        Require(MmpPreviewDecoder.Decode(Header(5, 3, 0x31545844, red.Concat(red).ToArray())).Bgra.Length == 60, "Partial edge blocks.");
        Console.WriteLine("PASS dxt1-to-5-alpha-and-edge-blocks");

        byte[] pnt = [16, 0, 0, 0, 0, 0, 80, 128, 0, 70, 0, 128, 60, 0, 0, 128, 0, 0, 0, 0];
        var layer = MmpPreviewDecoder.Decode(Header(8, 1, 0x33544E50, pnt));
        Require(layer.PremultipliedAlpha && layer.Bgra.Take(16).All(b => b == 0) && layer.Bgra.Skip(16).SequenceEqual(pnt.Skip(4)), "PNT3 skip/literal/premultiplied.");
        Reject(Header(8, 1, 0x33544E50, [4, 0, 0, 0]), "PNT3 unaligned skip");
        Reject(Header(4, 4, 0x31545844, [0]), "truncated DXT");
        Reject(Header(1, 1, 0x8888, [0]), "truncated raw");
        Reject(Header(8192, 8192, 0x8888, []), "excessive output");
        Reject(Header(1, 1, 0x12345678, []), "unknown format");
        var badOffset = Header(1, 1, 0x8888, [0, 0, 0, 0]); Put(badOffset, 72, uint.MaxValue);
        Reject(badOffset, "overflow offset");
        var badMask = Header(1, 1, 0x8888, [0, 0, 0, 0]); Put(badMask, 36, 255);
        Reject(badMask, "overlapping masks");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { MmpPreviewDecoder.Decode(Header(1, 1, 0x8888, [0, 0, 0, 0]), cancel.Token); throw new Exception("Ignored cancellation"); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS pnt3-bounds-corruption-and-cancellation");

        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(root, "settings.json")), new RecoverySnapshotStore(Path.Combine(root, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(source);
        var spells = vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == "spells.prototypes");
        vm.SelectTableCommand.Execute(spells);
        vm.SelectedRecord = spells.Records.First(r => int.TryParse(r.GetCell(18)?.EditorText, out int n) && n >= 0 && n < 1000);
        int index = int.Parse(vm.SelectedRecord.GetCell(18)!.EditorText, CultureInfo.InvariantCulture);
        string name = $"spell{index:D4}";
        byte[] icon = Icon(32), large = Icon(64);
        string archivePath = Path.Combine(root, "icons.res");
        WriteRes(archivePath, (name + ".mmp", icon), (name + ".x2.mmp", large), (name + ".x4.mmp", Icon(128)), ("only.x4.mmp", Icon(128)), ("bad.mmp", [0]));
        await vm.ConnectTextureSourcesAsync(new([archivePath], [], true, false, []));
        await vm.RecheckTexturesAsync();
        await vm.RefreshTexturePreviewAsync();
        Require(vm.TexturePreviewItems.Count == 3 && vm.TexturePreviewItems[0].Image?.PixelWidth == 32 &&
            vm.TexturePreviewItems[1].Image?.PixelWidth == 64 && vm.TexturePreviewItems[2].Image?.PixelWidth == 128 && vm.TexturePreviewItems.All(p => p.Image?.IsFrozen == true), "ViewModel normal/x2/x4 variants.");
        vm.TextureZoomIndex = 3; Require(vm.TextureZoom == 4, "Zoom mapping.");
        vm.TextureZoomIndex = 0;
        var selected = vm.SelectedTexture!;
        foreach (string resource in new[] { "only.mmp", "only.x2.mmp", "only.X4.MMP" })
        {
            vm.SelectedTexture = selected with { ResourceName = resource };
            await vm.RefreshTexturePreviewAsync();
            Require(vm.TexturePreviewItems.Count == 3 && vm.TexturePreviewItems[0].Image is null &&
                vm.TexturePreviewItems[1].Image is null && vm.TexturePreviewItems[2].Image?.PixelWidth == 128 &&
                vm.TexturePreviewItems[2].Name == "only.x4.mmp" && vm.TexturePreviewItems[2].Source == archivePath,
                "x4-only preview and explicit suffix normalization.");
        }
        vm.SelectedTexture = selected;
        Console.WriteLine("PASS x4-only-preview-and-suffix-normalization");
        vm.SelectedTexture = selected with { ResourceName = "absent.mmp" };
        var stale = vm.RefreshTexturePreviewAsync();
        vm.SelectedTexture = selected;
        await vm.RefreshTexturePreviewAsync(); await stale;
        Require(vm.TexturePreviewItems.All(p => p.Name.StartsWith(name)) && vm.TexturePreviewItems[0].Image is not null, "Stale preview overwrote selection.");
        var catalog = await TextureResourceCatalog.OpenAsync(new([archivePath], [], true, false, []));
        try { await catalog.ReadPreviewAsync("bad.mmp"); throw new Exception("Bad header accepted"); } catch (InvalidDataException) { }
        string originalHash = ResArchiveDocument.Open(archivePath).Sha256;
        await catalog.ReadPreviewAsync(name + ".mmp");
        Require(originalHash == ResArchiveDocument.Open(archivePath).Sha256, "Preview changed archive.");
        Console.WriteLine("PASS pair-zoom-stale-result-readonly");

        var tab = (TabItem)window.FindName("TextureTab"); tab.IsSelected = true;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1600, 1000)); content.Arrange(new Rect(0, 0, 1600, 1000)); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await vm.RecheckTexturesAsync(); await vm.RefreshTexturePreviewAsync();
        var browser = (Grid)window.FindName("TextureBrowser");
        tab.Content = null; content.UpdateLayout(); browser.DataContext = vm;
        var host = new Border { Child = browser, Width = 1180, Height = 740, Background = Brushes.DarkSlateGray };
        host.Measure(new Size(1180, 740)); host.Arrange(new Rect(0, 0, 1180, 740)); host.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); host.UpdateLayout();
        var views = Descendants(host).OfType<TextureImageView>().ToArray();
        Require(views.Length == 3 && views.All(v => v.Bitmap is not null), "Preview bindings not rendered.");
        SaveRender(host, Path.Combine(root, "preview-panel.png"), 1180, 740);
        vm.TextureZoomIndex = 3;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); host.UpdateLayout();
        Require(views.All(v => v.Zoom == 4), "Zoom binding not applied.");
        SaveRender(host, Path.Combine(root, "preview-400.png"), 1180, 740);
        Require(!window.IsVisible, "Test displayed a window.");
        Console.WriteLine("PASS offscreen-preview-and-zoom-bindings");

        File.SetLastWriteTimeUtc(archivePath, File.GetLastWriteTimeUtc(archivePath).AddMinutes(1));
        try { await catalog.ReadPreviewAsync(name + ".mmp"); throw new Exception("Stale source accepted"); }
        catch (IOException) { }
        await vm.RefreshTexturePreviewAsync();
        Require(vm.TexturePreviewItems.All(p => p.Image is null && p.Information.Contains("изменились")), "Changed archive retained stale image.");
        vm.SelectedTexture = null;
        Require(vm.TexturePreviewItems.Count == 0, "Empty selection retained image.");
        Console.WriteLine("PASS stale-source-and-cleared-selection");
        vm.Dispose(); window.Close();

        if (args.Length >= 4)
        {
            var actual = await TextureResourceCatalog.OpenAsync(TextureSources.FromGame(args[2], args[3]));
            var formats = new HashSet<uint>();
            foreach (string path in actual.Sources.Archives)
            {
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                foreach (var entry in ResFileManagerArchive.ReadCatalog(path, default))
                {
                    if (!entry.Name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) || entry.Size < 76 ||
                        actual.Check(entry.Name).Matches.FirstOrDefault()?.Source != path) continue;
                    byte[] header = new byte[76]; stream.Position = entry.Offset; stream.ReadExactly(header);
                    uint format = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                    if (formats.Contains(format)) continue;
                    var decoded = await actual.ReadPreviewAsync(entry.Name);
                    formats.Add(format);
                    Console.WriteLine($"REAL {decoded.Format}: {decoded.Width}x{decoded.Height}; {entry.Name}");
                    SaveImage(decoded, Path.Combine(root, $"real-{format:X8}.png"));
                }
            }
            Require(formats.Contains(0x33544E50) && formats.Contains(0x31545844) && formats.Contains(0x33545844) && formats.Contains(0x8888), "Real format coverage incomplete.");
            Console.WriteLine($"PASS real-format-samples: {formats.Count}");
        }
    }

    private static byte[] Icon(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            if (Math.Pow(x - size / 2, 2) + Math.Pow(y - size / 2, 2) > size * size / 5) continue;
            int p = (y * size + x) * 4;
            pixels[p] = 30; pixels[p + 1] = (byte)(x * 255 / size); pixels[p + 2] = 220; pixels[p + 3] = (byte)(y < size / 2 ? 128 : 255);
        }
        return Header(size, size, 0x8888, pixels);
    }

    private static void WriteRes(string path, params (string Name, byte[] Data)[] entries)
    {
        byte[] empty = new byte[16]; Put(empty, 0, ResArchiveDocument.Signature); Put(empty, 8, 16);
        var archive = ResArchiveDocument.Parse("fixture", empty);
        File.WriteAllBytes(path, ResArchiveWriter.Rebuild(archive, new Dictionary<int, byte[]>(),
            entries.Select(e => new AddedResResource(e.Name, e.Data)).ToArray()));
        _ = ResArchiveDocument.Open(path);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void SaveRender(Visual visual, string path, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); WritePng(image, path);
    }
    private static void SaveImage(MmpImage image, string path) => WritePng(BitmapSource.Create(image.Width, image.Height, 96, 96,
        image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), path);
    private static void WritePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}

