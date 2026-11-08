using System.IO;
using System.Windows;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationNestedLayoutRegression
{
    internal static int Run(string database)
    {
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
        return 0;
    }

    private static async Task Verify(string database)
    {
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var hero = records.Single(r => r.Table == "units.prototypes" && r.Name == "Human Hero");
        var scene = await UnitModelPreview.LoadAsync(database, hero, records);
        var source = scene.Animations ?? throw new Exception("ANM LAYOUT: hero animation source missing");
        string template = source.Names.First(n => n.Contains("walk", StringComparison.OrdinalIgnoreCase));
        int tracks = (await source.LoadAsync(template)).Tracks.Count;
        byte[] original = source.ExportBytes();
        var editor = new FigureAnimationEditor(source);
        await editor.CreateAnimationAsync(template, "layout_copy", 3, false);
        await editor.CreateAnimationAsync(template, "layout_bind", 3, true);
        byte[] created = editor.ExportBytes();
        AnimationSaveRegression.CheckNested(created, "layout_copy", tracks);
        AnimationSaveRegression.CheckNested(created, "layout_bind", tracks);

        var outer = ResArchiveDocument.Parse("created", created);
        var nested = ResArchiveDocument.Parse("copy", outer.GetResourceBytes("layout_copy").ToArray());
        byte[] flat = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("original", original),
            new Dictionary<int, byte[]>(), nested.Entries.Select(e => new AddedResResource(
                "layout_flat\\" + e.Name, nested.GetResourceBytes(e).ToArray())).ToArray());
        var legacy = new FigureAnimationEditor(source.WithBytes(flat));
        if ((await legacy.Preview().LoadAsync("layout_flat")).FrameCount != 3)
            throw new Exception("ANM LAYOUT: legacy flat clip is not readable");
        await legacy.TransformAsync("layout_flat", 0, 2, 4);
        legacy.NestFlatClip("layout_flat");
        byte[] converted = legacy.ExportBytes();
        AnimationSaveRegression.CheckNested(converted, "layout_flat", tracks);
        if ((await source.WithBytes(converted).LoadAsync("layout_flat")).FrameCount != 4)
            throw new Exception("ANM LAYOUT: edited flat clip lost frames during conversion");
        var oldArchive = ResArchiveDocument.Parse("old", original);
        var newArchive = ResArchiveDocument.Parse("new", converted);
        foreach (var entry in oldArchive.Entries)
            if (!oldArchive.GetResourceBytes(entry).Span.SequenceEqual(newArchive.GetResourceBytes(entry.Name).Span))
                throw new Exception("ANM LAYOUT: unrelated clip payload changed");

        var activeArchive = ResArchiveDocument.Parse("active ANM copy", original);
        if (source.Names.Contains("twerk1", StringComparer.OrdinalIgnoreCase))
        {
            var flatTracks = activeArchive.Entries.Where(e => e.Name.Replace('/', '\\').StartsWith("twerk1\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (flatTracks.Length > 0 && !activeArchive.Entries.Any(e => e.Name.Equals("twerk1", StringComparison.OrdinalIgnoreCase)))
            {
                var actualLegacy = new FigureAnimationEditor(source);
                actualLegacy.NestFlatClip("twerk1");
                AnimationSaveRegression.CheckNested(actualLegacy.ExportBytes(), "twerk1", flatTracks.Length);
                var convertedActual = ResArchiveDocument.Parse("converted active copy", actualLegacy.ExportBytes());
                foreach (var entry in activeArchive.Entries.Except(flatTracks))
                    if (!activeArchive.GetResourceBytes(entry).Span.SequenceEqual(convertedActual.GetResourceBytes(entry.Name).Span))
                        throw new Exception("ANM LAYOUT: real unrelated clip changed during twerk1 conversion");
                Console.WriteLine("ANM LAYOUT: active flat twerk1 converts on an in-memory copy");
            }
        }

        string directory = Path.Combine(Path.GetTempPath(), "anm-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool inRes in new[] { false, true })
            {
                string path = Path.Combine(directory, inRes ? "figures.res" : "unhufe.anm");
                byte[] oldBytes = inRes ? ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", EmptyArchive()),
                    new Dictionary<int, byte[]>(), [new("unhufe.anm", flat), new("other.bin", [1, 2, 3])]) : flat;
                File.WriteAllBytes(path, oldBytes);
                await new AnimationSaveTarget(path, inRes ? "unhufe.anm" : null, flat).SaveAsync(flat, converted, true);
                byte[] saved = File.ReadAllBytes(path);
                if (inRes)
                {
                    var archive = ResArchiveDocument.Parse("saved RES", saved);
                    if (!archive.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 1, 2, 3 }))
                        throw new Exception("ANM LAYOUT: unrelated RES resource changed");
                    saved = archive.GetResourceBytes("unhufe.anm").ToArray();
                }
                AnimationSaveRegression.CheckNested(saved, "layout_flat", tracks);
            }
        }
        finally { Directory.Delete(directory, true); }

        await KimodoDraftRegression.Verify(scene, template);
        Console.WriteLine("ANM LAYOUT PASS: create, copy, Kimodo, flat resave, other clips, standalone and RES");
    }

    private static byte[] EmptyArchive()
    {
        var bytes = new byte[ResArchiveDocument.HeaderSize];
        BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(bytes, 0);
        BitConverter.GetBytes(ResArchiveDocument.HeaderSize).CopyTo(bytes, 8);
        return bytes;
    }
}
