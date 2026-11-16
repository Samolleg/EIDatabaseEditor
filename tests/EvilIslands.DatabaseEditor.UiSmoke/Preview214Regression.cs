using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class Preview214Regression
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static int Run(string database, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent();
        typeof(Application).GetField("_startupUri", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS background textures, document switch, source switch/errors/dispose, Base PAK, material and unit geometry, repeated model loading"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
    private static async Task Verify(string database, string output)
    {
        await VerifyCaches(output);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(database));
        using var vm = new MainWindowViewModel(); var clock = Stopwatch.StartNew();
        await vm.OpenPathAsync(database);
        Check(vm.HasDocument && !vm.IsBusy && vm.IsLoadingTextures, "Tables still wait for textures");
        Console.WriteLine($"DB ready {clock.Elapsed.TotalSeconds:F3}s; texture background={vm.IsLoadingTextures}");
        var materials = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "items.materials");
        vm.SelectTableCommand.Execute(materials); vm.SelectedRecord = materials.Records.Single(r => r.CoreRecord.IdentityValue == "troll black");
        Check(vm.SelectedRecord is not null && !vm.IsBusy, "Navigation blocked during texture scan");
        var records = vm.Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r.CoreRecord))).ToArray();
        var hero = records.Single(r => r.Table == "units.prototypes" && r.Name == "Human Hero");
        clock.Restart(); var first = await UnitModelPreview.LoadAsync(database, hero, records);
        Console.WriteLine($"Human Hero first {clock.Elapsed.TotalSeconds:F3}s, {first.Meshes.Count} meshes");
        clock.Restart(); var second = await UnitModelPreview.LoadAsync(database, hero, records);
        Console.WriteLine($"Human Hero repeat {clock.Elapsed.TotalSeconds:F3}s, {second.Meshes.Count} meshes");
        Check(first.Meshes.Count == second.Meshes.Count && first.Meshes.Zip(second.Meshes).All(p => p.First.Positions.SequenceEqual(p.Second.Positions) && p.First.UV.SequenceEqual(p.Second.UV)), "Cached model changed geometry/UV");
        await vm.TextureLoadTask;
        Check(!vm.IsBusy && !vm.IsLoadingTextures, "Texture task did not complete");
        var item = await ItemModelPreview.LoadAsync(database, records.Single(r => r.Table == "items.materials" && r.Name == "troll black"), records);
        Check(item.Meshes.Count > 0 && item.Textures.Count > 0, "Black troll material has no model/texture");
        Check(item.Sources.Any(s => s.Contains("Base", StringComparison.OrdinalIgnoreCase) && s.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)), "Base PAK not used");
        Render(item, Path.Combine(output, "troll-black.png"), true); Render(second, Path.Combine(output, "human-hero.png"), false);
        File.WriteAllLines(Path.Combine(output, "material-sources.txt"), item.Sources.Concat(item.Notes));
        foreach (string race in new[] { "Goblin", "Ogre" }) {
            var record = records.First(r => r.Table == "units.prototypes" && r.Text(1) == race && r.Integer(3) == 0);
            var scene = await UnitModelPreview.LoadAsync(database, record, records);
            Check(scene.Textures.ContainsKey(1), "Explicit race skin missing: " + race);
            Render(scene, Path.Combine(output, race + ".png"), false);
        }
        var axe = records.Single(r => r.Table == "items.weapons" && r.Name == "stone pick axe");
        var axeScene = await ItemModelPreview.LoadAsync(database, axe, records, choice: await ItemModelPreview.DefaultChoiceAsync(database, axe, records));
        Check(axeScene.Meshes.Count > 0 && axeScene.Notes.Any(n => n.Contains("цветовая маска")), "Damaged texture hid geometry or its diagnostic");
        var taskA = vm.ConnectNearbyTexturesAsync();
        string copy = Path.Combine(output, "database.pak"); File.Copy(database, copy);
        await vm.OpenPathAsync(copy); await Task.WhenAll(taskA, vm.TextureLoadTask);
        Check(vm.SourcePath == copy && vm.SpellPreviewTextureSources?.NearbyDatabasePath == copy, "Old background scan replaced new document sources");
        string missing = Path.Combine(output, "absent.pak");
        await vm.ConnectTextureSourcesAsync(new([missing], [], true, false, []));
        Check(!vm.IsBusy && !vm.IsLoadingTextures && vm.TextureSourcesDescription.Contains("absent.pak"), "Background failure blocked workspace or lost diagnostics");
        var pending = vm.ConnectNearbyTexturesAsync(); vm.Dispose(); await pending;
        Check(!vm.IsLoadingTextures && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "Dispose/source unchanged check failed");
    }
    private static async Task VerifyCaches(string output)
    {
        byte[] empty = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature); BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
        var blank = ResArchiveDocument.Parse("empty", empty);
        string file = Path.Combine(output, "cache-model.res");
        File.WriteAllBytes(file, ResArchiveWriter.Rebuild(blank, new Dictionary<int, byte[]>(), [new("part", [1])]));
        var first = PreviewModelArchiveCache.Parse(file, "model.mod", () => File.ReadAllBytes(file), default);
        var same = PreviewModelArchiveCache.Parse(file, "model.mod", () => throw new Exception("Cache re-read unchanged model"), default);
        Check(ReferenceEquals(first, same), "Model cache missed");
        File.WriteAllBytes(file, ResArchiveWriter.Rebuild(blank, new Dictionary<int, byte[]>(), [new("part", [2,3])]));
        var changed = PreviewModelArchiveCache.Parse(file, "model.mod", () => File.ReadAllBytes(file), default);
        Check(!ReferenceEquals(first, changed) && changed.GetResourceBytes("part").Span.SequenceEqual(new byte[]{2,3}) && first.GetResourceBytes("part").Span.SequenceEqual(new byte[]{1}), "Changed model cache remained stale");
        string pak = Path.Combine(output, "cache-texture.pak"); byte[] mmp = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(mmp, 0x00504d4d); BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(8), 1);
        PakArchive.Create(pak, new() { ["cache.mmp"] = mmp });
        var sources = new TextureSources([pak], [], true, false, []);
        var catalog = await TextureResourceCatalog.OpenCachedAsync(sources);
        Check(ReferenceEquals(catalog, await TextureResourceCatalog.OpenCachedAsync(sources)), "Catalog cache missed");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pak)));
        PakArchive.ApplyChanges([(pak,hash,new(){["cache.mmp"]=new byte[81]})]);
        var next = await TextureResourceCatalog.OpenCachedAsync(sources);
        Check(!ReferenceEquals(catalog, next), "Catalog cache ignored changed source");
        bool rejected = false; try { await next.ReadPreviewAsync("cache.mmp"); } catch(InvalidDataException) { rejected=true; }
        Check(rejected && (await TextureResourceCatalog.OpenAsync(sources)).Check("cache.mmp").State == TextureCheckState.Corrupt, "Preview/F5 corruption checks lost");
        PakDiscoveryRegression.Run();
        Console.WriteLine("PASS cache reuse/invalidation, immutable cached model, preview/F5 validation, Base PAK/RES UV classification and provider priority");
    }
    private static void Render(UnitModelScene scene, string path, bool inventory)
    {
        var view = new UnitModelPreviewView { Width = 800, Height = 800 };
        view.Measure(new Size(800, 800)); view.Arrange(new Rect(0, 0, 800, 800)); view.ShowScene(scene, inventory); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(800, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
