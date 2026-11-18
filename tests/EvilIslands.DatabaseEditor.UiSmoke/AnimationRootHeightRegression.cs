using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationRootHeightRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("ANM ROOT HEIGHT: " + message);
    }

    internal static int Run(string database)
    {
        Verify(database).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task Verify(string database)
    {
        var records = DatabaseDocument.Open(database).Databases.SelectMany(group => group.Tables)
            .SelectMany(table => table.Records.Select(row => ModelRecord.Capture(table.Key, row))).ToArray();
        var hero = records.Single(row => row.Table == "units.prototypes" && row.Name == "Human Hero");
        var scene = await UnitModelPreview.LoadAsync(database, hero, records);
        var source = scene.Animations ?? throw new Exception("ANM ROOT HEIGHT: no model ANM");
        Check(scene.Model == "unhufe", "expected installed JamaisVu135 unhufe model");
        var reference = await source.LoadAsync("cidle09");
        var bad = await source.LoadAsync("twerk1");
        Vector3 anchor = reference.Tracks["hp"].Position(0);
        Check(anchor.Z > .5f && bad.Tracks["hp"].Position(0).Z == 0,
            "input clips no longer reproduce the reported initial heights");
        Check(await AnimationEditorDialog.SuggestHeightReference(source, "twerk1") == "cidle09" &&
              await AnimationEditorDialog.SuggestHeightReference(source, "cidle09") == "cidle09",
            "bad current clip did not suggest a same-model grounded reference");

        int frames = reference.FrameCount;
        var editor = new FigureAnimationEditor(source);
        byte[] original = source.ExportBytes();
        await editor.CreateAnimationAsync("cidle09", "height_copy", frames, false);
        var copy = await editor.Preview().LoadAsync("height_copy");
        Check(Vector3.Distance(copy.Tracks["hp"].Position(0), anchor) < .00001f,
            "ordinary copy lost the template's absolute root position");
        await editor.CreateAnimationAsync("cidle09", "height_draft", frames, true);
        var created = await editor.Preview().LoadAsync("height_draft");
        Check(Vector3.Distance(created.Tracks["hp"].Position(0), anchor) < .00001f,
            "bind-pose creation lost the template's absolute root position");
        Check(created.Tracks["hp"].Rotations.All(rotation => Math.Abs(Quaternion.Dot(rotation, Quaternion.Identity)) > .99999f),
            "bind-pose creation did not reset root rotation");

        var root = created.Tracks["hp"];
        var repair = new FigureAnimationEditor(source);
        float shift = await repair.AlignRootHeightAsync("twerk1", "cidle09");
        var repaired = await repair.Preview().LoadAsync("twerk1");
        var oldRoot = bad.Tracks["hp"];
        var repairedRoot = repaired.Tracks["hp"];
        Check(Math.Abs(shift - anchor.Z) < .00001f &&
              Math.Abs(repairedRoot.Position(0).Z - anchor.Z) < .00001f &&
              Math.Abs(repairedRoot.Positions.Min(position => position.Z) - .667781f) < .00001f &&
              Math.Abs(repairedRoot.Positions.Max(position => position.Z) - 1.007539f) < .00001f,
            "existing twerk1 was not reanchored while retaining its vertical profile");
        var firstPose = repair.Preview().Pose(repaired, 0);
        Check(firstPose["ll3"].Position.Z > .1f && firstPose["rl3"].Position.Z > .1f,
            "repaired first frame still places the feet below the model's ground level");
        for (int frame = 0; frame < oldRoot.Positions.Length; frame++)
        {
            Vector3 beforePosition = oldRoot.Position(frame), afterPosition = repairedRoot.Position(frame);
            Check(Math.Abs(beforePosition.X - afterPosition.X) < .000001f &&
                  Math.Abs(beforePosition.Y - afterPosition.Y) < .000001f &&
                  Math.Abs((afterPosition.Z - beforePosition.Z) - shift) < .000001f,
                $"twerk1 frame {frame} changed horizontal movement or vertical delta");
        }
        byte[] repairedBytes = repair.ExportBytes();
        var originalOuter = ResArchiveDocument.Parse("original twerk", original);
        var repairedOuter = ResArchiveDocument.Parse("repaired twerk", repairedBytes);
        foreach (var entry in originalOuter.Entries.Where(entry => !entry.Name.Equals("twerk1", StringComparison.OrdinalIgnoreCase)))
            Check(originalOuter.GetResourceBytes(entry).Span.SequenceEqual(repairedOuter.GetResourceBytes(entry.Name).Span),
                "reanchoring changed another clip: " + entry.Name);
        var oldTracks = ResArchiveDocument.Parse("old twerk tracks", originalOuter.GetResourceBytes("twerk1").ToArray());
        var newTracks = ResArchiveDocument.Parse("new twerk tracks", repairedOuter.GetResourceBytes("twerk1").ToArray());
        foreach (var entry in oldTracks.Entries.Where(entry => !entry.Name.Equals("hp", StringComparison.OrdinalIgnoreCase)))
            Check(oldTracks.GetResourceBytes(entry).Span.SequenceEqual(newTracks.GetResourceBytes(entry.Name).Span),
                "reanchoring changed another twerk1 track: " + entry.Name);
        byte[] expectedRootBytes = oldTracks.GetResourceBytes("hp").ToArray();
        byte[] recordedRootBytes = newTracks.GetResourceBytes("hp").ToArray();
        int positionStart = checked(8 + BitConverter.ToInt32(expectedRootBytes, 0) * 16);
        for (int frame = 0; frame < oldRoot.Positions.Length; frame++)
            Array.Copy(recordedRootBytes, positionStart + frame * 12 + 8, expectedRootBytes, positionStart + frame * 12 + 8, 4);
        Check(expectedRootBytes.AsSpan().SequenceEqual(recordedRootBytes),
            "reanchoring changed rotation, morph, or non-Z root bytes");
        repair.Undo();
        Check(repair.ExportBytes().AsSpan().SequenceEqual(original), "reanchoring Undo did not restore original ANM");
        repair.Redo();
        Check(repair.ExportBytes().AsSpan().SequenceEqual(repairedBytes), "reanchoring Redo changed repaired ANM");

        byte[] saved = editor.ExportBytes();
        AnimationSaveRegression.CheckNested(saved, "height_draft", created.Tracks.Count);
        var before = ResArchiveDocument.Parse("before", original);
        var after = ResArchiveDocument.Parse("after", saved);
        foreach (var entry in before.Entries)
            Check(before.GetResourceBytes(entry).Span.SequenceEqual(after.GetResourceBytes(entry.Name).Span),
                "unrelated source clip changed: " + entry.Name);

        string temp = Path.Combine(Path.GetTempPath(), "ei-root-height-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (bool inRes in new[] { false, true })
            {
                string path = Path.Combine(temp, inRes ? "figure.res" : "unhufe.anm");
                byte[] initial = original;
                if (inRes)
                {
                    byte[] empty = new byte[ResArchiveDocument.HeaderSize];
                    BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0);
                    BitConverter.GetBytes(ResArchiveDocument.HeaderSize).CopyTo(empty, 8);
                    initial = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty),
                        new Dictionary<int, byte[]>(), [new("unhufe.anm", original), new("other.bin", [1, 2, 3])]);
                }
                File.WriteAllBytes(path, initial);
                await new AnimationSaveTarget(path, inRes ? "unhufe.anm" : null, original)
                    .SaveAsync(original, saved, false);
                byte[] readback = File.ReadAllBytes(path);
                if (inRes)
                {
                    var archive = ResArchiveDocument.Parse("figure", readback);
                    Check(archive.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 1, 2, 3 }),
                        "unrelated RES member changed");
                    readback = archive.GetResourceBytes("unhufe.anm").ToArray();
                }
                Check(readback.AsSpan().SequenceEqual(saved), "ANM save/readback bytes differ");
                var raw = ResArchiveDocument.Parse("written ANM", readback);
                var nested = ResArchiveDocument.Parse("written clip", raw.GetResourceBytes("height_draft").ToArray());
                var recordedRoot = FigureAnimationTrack.Decode(nested.GetResourceBytes("hp").ToArray());
                Check(recordedRoot.Positions.Length == frames, "raw saved track lost frames");
                for (int frame = 0; frame < frames; frame++)
                    Check(Vector3.Distance(recordedRoot.Position(frame), root.Position(frame)) < .00001f,
                        $"raw saved ANM changed root position at frame {frame}");

                string repairPath = Path.Combine(temp, inRes ? "repaired-figure.res" : "repaired-unhufe.anm");
                File.WriteAllBytes(repairPath, initial);
                await new AnimationSaveTarget(repairPath, inRes ? "unhufe.anm" : null, original)
                    .SaveAsync(original, repairedBytes, false);
                byte[] repairedReadback = File.ReadAllBytes(repairPath);
                if (inRes)
                {
                    var archive = ResArchiveDocument.Parse("repaired figure", repairedReadback);
                    Check(archive.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 1, 2, 3 }),
                        "reanchoring changed unrelated RES member");
                    repairedReadback = archive.GetResourceBytes("unhufe.anm").ToArray();
                }
                Check(repairedReadback.AsSpan().SequenceEqual(repairedBytes), "repaired twerk1 save/readback differs");
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
        Console.WriteLine($"ANM ROOT HEIGHT PASS: unhufe anchor Z={anchor.Z:F6}, root minimum Z={root.Positions.Min(position => position.Z):F6}, twerk1 range {repairedRoot.Positions.Min(position => position.Z):F6}..{repairedRoot.Positions.Max(position => position.Z):F6}, Undo/Redo, standalone ANM/RES readback");
    }
}
