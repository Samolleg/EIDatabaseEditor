using System.IO;
using System.Numerics;
using System.Text.Json;
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

internal static class AnimationPreviewRegression
{
    private static ModelRecord[] Records(string path) => DatabaseDocument.Open(path).Databases.SelectMany(g => g.Tables)
        .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    public static int Audit(string database, string previousAudit, string output)
    {
        Directory.CreateDirectory(output);
        var records = Records(database);
        var names = File.ReadLines(previousAudit).Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .Where(r => r.GetProperty("Error").ValueKind == JsonValueKind.Null)
            .GroupBy(r => r.GetProperty("Model").GetString()).Select(g => g.First().GetProperty("Name").GetString()!).ToArray();
        using var writer = new StreamWriter(Path.Combine(output, "clips.jsonl")) { AutoFlush = true };
        int total = 0, errors = 0, missing = 0, morphs = 0, skipped = 0;
        foreach (string name in names)
        {
            var record = records.First(r => r.Table is "units.prototypes" or "units.npcs" && r.Name == name);
            var scene = UnitModelPreview.LoadAsync(database, record, records).GetAwaiter().GetResult();
            if (scene.Animations is not { } set)
            { missing++; writer.WriteLine(JsonSerializer.Serialize(new { scene.Model, Name = name, Error = "No ANM", scene.Notes })); continue; }
            skipped += set.SkippedResources.Count;
            foreach (string clipName in set.Names)
            {
                total++;
                try
                {
                    var clip = set.LoadAsync(clipName).GetAwaiter().GetResult();
                    foreach (float f in new[] { 0f, (clip.FrameCount - 1) / 2f, clip.FrameCount - 1f })
                        foreach (var p in set.Pose(clip, f).Values)
                            Require(float.IsFinite(p.Position.LengthSquared() + p.Rotation.LengthSquared()), "Non-finite pose");
                    string[] morph = clip.Tracks.Where(t => t.Value.Morphs.Length > 0).Select(t => t.Key).ToArray();
                    if (morph.Length > 0) morphs++;
                    writer.WriteLine(JsonSerializer.Serialize(new { scene.Model, Clip = clipName, clip.FrameCount, Tracks = clip.Tracks.Count, Morphs = morph, Error = (string?)null }));
                }
                catch (Exception ex) { errors++; writer.WriteLine(JsonSerializer.Serialize(new { scene.Model, Clip = clipName, Error = ex.Message })); }
            }
            Console.WriteLine($"{scene.Model}: {set.Names.Count} clips; cumulative {total}, errors {errors}");
        }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Models = names.Length, Clips = total, Errors = errors, MissingAnm = missing, MorphClips = morphs, SkippedAuxiliary = skipped }));
        return errors == 0 ? 0 : 1;
    }

    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output); CheckDecoder();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
        var records = Records(database);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        UnitModelScene? first = null;
        foreach (string name in new[] { "Human Hero", "Human Hero Hadagan1", "Human Mercenary Hadagan Thief", "BoarDarkF2", "GoblinA0", "GZOrcMaleF6", "SkeletonWarriorF10", "ZombieMaleF12", "ZombieFemaleF12", "ArmoredTurtle90", "BansheeDarkF12", "BatDarkM12" })
        {
            var record = records.First(r => r.Table == "units.prototypes" && r.Name == name);
            var scene = UnitModelPreview.LoadAsync(database, record, records).GetAwaiter().GetResult(); first ??= scene;
            var set = scene.Animations ?? throw new Exception("No animations: " + name);
            string[] clips = set.Names.Where(n => n.Contains("walk", StringComparison.OrdinalIgnoreCase)).Take(1)
                .Concat(set.Names.Where(n => n.Contains("at", StringComparison.OrdinalIgnoreCase)).Take(1)).Distinct().ToArray();
            if (clips.Length == 0) clips = set.Names.Take(1).ToArray();
            if (name == "BatDarkM12") clips = ["cflight01", "cflight01_"];
            var view = new UnitModelPreviewView { Width = 1000, Height = 800 };
            view.Measure(new Size(1000, 800)); view.Arrange(new Rect(0, 0, 1000, 800)); view.ShowScene(scene);
            foreach (string clipName in clips)
            {
                var clip = set.LoadAsync(clipName).GetAwaiter().GetResult();
                Wait(view.SelectAnimationAsync(clipName));
                foreach (int f in new[] { 0, clip.FrameCount / 2, clip.FrameCount - 1 }.Distinct())
                {
                    view.SetAnimationFrame(f); view.UpdateLayout();
                    var status = (TextBlock)typeof(UnitModelPreviewView).GetField("_animationError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
                    Require(status.Visibility == Visibility.Collapsed, status.Text);
                    var bitmap = new RenderTargetBitmap(1000, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"{name}-{clipName}-{f:D3}.png")); encoder.Save(stream);
                }
            }
            Console.WriteLine($"Rendered {name}: {scene.Model}, {string.Join(',', clips)}");
        }
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "settings.json")), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext; Wait(vm.OpenPathAsync(database));
        var attached = new UnitModelPreviewView { Width = 1000, Height = 800, DataContext = vm };
        using var presentation = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Animation headless lifecycle")
        { Width = 1000, Height = 800, WindowStyle = unchecked((int)0x80000000) }); // No WS_VISIBLE.
        presentation.RootVisual = attached; attached.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        void Loaded() { Task t; do { t = attached.LoadTask; Wait(t); } while (!ReferenceEquals(t, attached.LoadTask)); }
        var units = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes");
        vm.SelectTableCommand.Execute(units); vm.SelectedRecord = units.Records.Single(r => r.CoreRecord.IdentityValue == "Human Hero"); Loaded();
        Wait(attached.PlayAnimationAsync()); Require(attached.AnimationPlaying, "Play failed");
        Wait(Task.Delay(240)); Require(attached.AnimationFrame > 0, "Timer did not advance");
        attached.PauseAnimation(); double held = attached.AnimationFrame; Wait(Task.Delay(100)); Require(attached.AnimationFrame == held, "Pause drift");
        attached.SetAnimationFrame(3); Require(attached.AnimationFrame == 3, "Scrub failed"); attached.StopAnimation(); Require(attached.AnimationFrame == 0 && !attached.AnimationPlaying, "Stop failed");
        var perf = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) attached.SetAnimationFrame(i % 10);
        Console.WriteLine($"120 rigid preview frames: {perf.Elapsed.TotalMilliseconds:0} ms");
        Wait(attached.SelectAnimationAsync("__missing_clip__"));
        var error = (TextBlock)typeof(UnitModelPreviewView).GetField("_animationError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(attached)!;
        Require(error.Visibility == Visibility.Visible && !attached.AnimationPlaying, "Bad clip not reported");
        Wait(attached.SelectAnimationAsync(first!.Animations!.Names.First())); Require(error.Visibility == Visibility.Collapsed, "Stale error after valid clip");
        var oldLoad = attached.SelectAnimationAsync(first!.Animations!.Names.Last());
        vm.SelectedRecord = units.Records.Single(r => r.CoreRecord.IdentityValue == "BoarDarkF2"); Loaded(); Wait(oldLoad);
        Wait(attached.PlayAnimationAsync()); Require(attached.AnimationPlaying, "Stale clip poisoned next model");
        var items = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "items.weapons");
        vm.SelectTableCommand.Execute(items); vm.SelectedRecord = items.Records.First(); Loaded(); Require(!attached.AnimationPlaying, "Item kept unit timer");
        var npcs = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.npcs");
        vm.SelectTableCommand.Execute(npcs); vm.SelectedRecord = npcs.Records.Single(r => r.CoreRecord.IdentityValue == "Human Hero"); Loaded();
        Wait(attached.PlayAnimationAsync()); Require(attached.AnimationPlaying, "NPC animation failed");
        vm.SelectTableCommand.Execute(units); vm.SelectedRecord = units.Records.Single(r => r.CoreRecord.IdentityValue == "Human Hero"); Loaded();
        Wait(attached.PlayAnimationAsync()); attached.Visibility = Visibility.Collapsed; Require(!attached.AnimationPlaying, "Hidden view kept timer");
        attached.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Require(!attached.AnimationPlaying && !vm.HasChanges, "Unload or database mutation");
        Require(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database))), "Database changed");
        window.Close(); app.Shutdown(); Console.WriteLine("Decoder, frames, play/pause/stop/scrub, stale-load, unit/item switch, hide/unload, database immutability PASS."); return 0;
    }

    private static void CheckDecoder()
    {
        byte[] Track(Quaternion[] q, Vector3[] p, Vector3[][] m)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(q.Length); foreach (var r in q) { w.Write(r.W); w.Write(r.X); w.Write(r.Y); w.Write(r.Z); }
            w.Write(p.Length); foreach (var v in p) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(m.Length); w.Write(m.Length == 0 ? 0 : m[0].Length);
            foreach (var f in m) foreach (var v in f) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            return stream.ToArray();
        }
        var bytes = Track([Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)], [Vector3.Zero, new(2, 0, 0)], [[Vector3.Zero], [new(0, 2, 0)]]);
        var track = FigureAnimationTrack.Decode(bytes);
        Require(track.FrameCount == 2 && Vector3.Distance(track.Position(.5f), Vector3.UnitX) < .0001 && Vector3.Distance(track.Morph(.5f, 0), Vector3.UnitY) < .0001, "Interpolation");
        Require(Vector3.Distance(Vector3.Transform(Vector3.UnitX, track.Rotation(1)), Vector3.UnitY) < .0001, "Quaternion wxyz");
        Require(track.Morph(1, 1) == Vector3.Zero && track.Position(100) == new Vector3(2, 0, 0), "Clamp/padding");
        foreach (var bad in new[] { bytes[..^1], bytes.Concat(new byte[1]).ToArray(), new byte[] { 255,255,255,127 }, Track([new(float.NaN,0,0,1)], [], []), Track([new(0,0,0,0)], [], []) })
        { bool failed = false; try { FigureAnimationTrack.Decode(bad); } catch (Exception ex) when (ex is IOException or InvalidDataException) { failed = true; } Require(failed, "Bad animation accepted"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool didCancel = false; try { FigureAnimationTrack.Decode(bytes, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; } Require(didCancel, "Decoder cancellation");
        byte[] empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0); BitConverter.GetBytes(16).CopyTo(empty, 8);
        var set = new FigureAnimationSet("fixture", empty, new Dictionary<string,string> { ["root"] = "", ["child"] = "root", ["equipment"] = "child" },
            new Dictionary<string,Vector3> { ["root"] = Vector3.Zero, ["child"] = Vector3.UnitX, ["equipment"] = Vector3.UnitY },
            new Dictionary<string,Vector3> { ["root"] = Vector3.Zero, ["child"] = Vector3.UnitX, ["equipment"] = Vector3.One });
        var clip = new FigureAnimationClip("test", new Dictionary<string,FigureAnimationTrack> { ["root"] = track,
            ["child"] = FigureAnimationTrack.Decode(Track([Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)], [new(999,999,999)], [])) });
        var pose = set.Pose(clip, 1);
        Require(Vector3.Distance(pose["child"].Position, new(2,1,0)) < .0001, "Child used track translation or lost parent");
        // X local * Z parent: vector X rotates to Z. Reversed product rotates X to Y.
        Require(Vector3.Distance(Vector3.Transform(Vector3.UnitX, pose["child"].Rotation), Vector3.UnitZ) < .0001, "Rotation multiplication order");
        Require(pose["equipment"].Rotation == pose["child"].Rotation, "Untracked equipment parent lost");
        Console.WriteLine("Synthetic bounded decoder, interpolation, morph padding and hierarchy PASS");
    }
}
