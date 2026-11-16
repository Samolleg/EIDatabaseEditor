using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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

internal static class NativePakRegression
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; }
        Check(rejected, message);
    }
    internal static int Run(string database, string output, bool verifyNative = true)
    {
        Directory.CreateDirectory(output);
        try {
            if (verifyNative) {
                VerifyNative(output);
                Console.WriteLine("PASS native: exact payloads, CP1251, empty, prefix, concurrent reads, immutable snapshot, table/payload corruption, cancellation, texture diagnostics, sound tombstones");
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        var app = new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = VerifyUi(database, output);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
    private static void VerifyNative(string output)
    {
        string path = Path.Combine(output, "native.pak");
        byte[] payload = new byte[4096]; new Random(213).NextBytes(payload);
        PakArchive.Create(path, new() { ["payload.bin"] = payload, ["текст"] = [5, 6, 7], ["empty"] = [] });
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        using (var reader = PakArchive.Open(path)) {
            Check(reader.Entries.Count == 3 && reader.Read("ТЕКСТ").SequenceEqual(new byte[] { 5, 6, 7 }) && reader.Read("empty").Length == 0, "Name/empty entry mismatch");
            Check(reader.ReadPrefix("payload.bin", 76).SequenceEqual(payload.Take(76)), "Prefix bytes changed");
            Parallel.For(0, 24, _ => Check(reader.Read("payload.bin").SequenceEqual(payload), "Concurrent read changed bytes"));
            reader.VerifyContents();
            PakArchive.ApplyChanges([(path, hash, new() { ["текст"] = [9] })]);
            Check(reader.Read("текст").SequenceEqual(new byte[] { 5, 6, 7 }), "Open snapshot followed external replacement");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool stopped = false;
            try { reader.Read("payload.bin", cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Cancelled read continued");
        }
        Check(PakArchive.Read(path, "текст").SequenceEqual(new byte[] { 9 }), "Replacement missing");
        byte[] valid = File.ReadAllBytes(path);
        string bad = Path.Combine(output, "bad-table.pak");
        byte[] data = (byte[])valid.Clone(); data[^1] ^= 1; File.WriteAllBytes(bad, data);
        Reject(() => { using var reader = PakArchive.Open(bad); }, "Corrupt table accepted");
        bad = Path.Combine(output, "bad-payload.pak"); data = (byte[])valid.Clone();
        int toc = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24)));
        int count = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)));
        for (int i = 0; i < count; i++) {
            int nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(toc)));
            if (System.Text.Encoding.ASCII.GetString(data, toc + 48, nameLength) == "payload.bin") {
                int offset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(toc + 8)));
                int stored = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(toc + 16)));
                data[offset + stored - 1] ^= 1;
            }
            toc += 48 + nameLength;
        }
        File.WriteAllBytes(bad, data);
        Reject(() => { using var reader = PakArchive.Open(bad); reader.ReadPrefix("payload.bin", 76); }, "Prefix read did not verify payload tail");
        byte[] mmp = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(mmp, 0x00504d4d); BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(8), 1);
        string textures = Path.Combine(output, "textures.pak");
        PakArchive.Create(textures, new() { ["good.mmp"] = mmp, ["bad.mmp"] = new byte[80], ["short.mmp"] = [1] });
        var catalog = TextureResourceCatalog.OpenAsync(new([textures], [], true, false, [])).GetAwaiter().GetResult();
        Check(catalog.Check("good.mmp").State == TextureCheckState.Found && catalog.Check("bad.mmp").State == TextureCheckState.Corrupt && catalog.Check("short.mmp").State == TextureCheckState.Corrupt, "Texture header diagnostics changed");
        using (var cancelled = new CancellationTokenSource()) {
            cancelled.Cancel(); bool stopped = false;
            try { TextureResourceCatalog.OpenAsync(new([textures], [], true, false, []), cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Cancelled catalog completed");
        }
        string high = Path.Combine(output, "high.pak"), low = Path.Combine(output, "low.pak");
        PakArchive.Create(high, new() { ["hidden.wav"] = [1], ["present.wav"] = [2] });
        PakArchive.ApplyChanges([(high, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(high))), new() { ["hidden.wav"] = null })]);
        PakArchive.Create(low, new() { ["hidden.wav"] = [3] });
        var sound = SoundArchiveCatalog.Open([high, low]);
        Check(sound.Resources.Count == 1 && sound.Resources[0].ResourceName == "present.wav", "Sound tombstone priority changed");
    }
    private static async Task VerifyUi(string database, string output)
    {
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)));
        var prefs = new EditorPreferencesStore(Path.Combine(output, "ui.json")); prefs.RememberDatabasePath(database);
        var window = new MainWindow(prefs, new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        await window.HomeRefreshTask;
        var vm = (MainWindowViewModel)window.DataContext;
        var clock = Stopwatch.StartNew();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.BusyText)) Console.WriteLine($"UI {clock.Elapsed.TotalSeconds:F3}s: {vm.BusyText}"); };
        ((Button)window.FindName("OpenLastDatabaseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (vm.IsBusy && clock.Elapsed < TimeSpan.FromMinutes(2)) await Task.Delay(20);
        Check(!vm.IsBusy && vm.HasDocument && vm.RecordCount > 3000, $"Real UI failed: busy={vm.IsBusy}, document={vm.HasDocument}, records={vm.RecordCount}, stage={vm.BusyText}, status={vm.StatusText}, error={vm.NotificationMessage}");
        double seconds = clock.Elapsed.TotalSeconds;
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1500, 950)); root.Arrange(new Rect(0, 0, 1500, 950)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1500, 950, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "database.png"))) encoder.Save(file);
        Check(originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))), "Installed database changed");
        string result = $"PASS real database UI with installed companion resources: {seconds:F3} seconds, {vm.RecordCount} records, {vm.TableCount} tables";
        Console.WriteLine(result); File.WriteAllText(Path.Combine(output, "ui-result.txt"), result);
        vm.Dispose(); window.Close();
    }
}
