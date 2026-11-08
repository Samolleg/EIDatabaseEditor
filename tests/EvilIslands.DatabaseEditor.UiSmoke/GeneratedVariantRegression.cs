using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class GeneratedVariantRegression
{
    private static void Check(bool value, string why) { if (!value) throw new Exception("VARIANT: " + why); }
    internal static async Task Verify(FigureAnimationSet source, FigureAnimationClip generated)
    {
        string name = source.Names.Contains("cidle09") ? "cidle09" : source.Names[0];
        var original = await source.LoadAsync(name);
        byte[] baseline = source.ExportBytes();
        var edit = new FigureAnimationEditor(source);
        string[] parts = original.Tracks.Keys.Intersect(generated.Tracks.Keys).ToArray();
        await edit.BlendGeneratedVariantAsync(name, generated, 0, original.FrameCount - 1, parts, 0);
        Check(baseline.SequenceEqual(edit.ExportBytes()) && !edit.CanUndo, "zero strength mutates history");
        await edit.BlendGeneratedVariantAsync(name, generated, 0, original.FrameCount - 1, parts, 1);
        var result = await edit.Preview().LoadAsync(name);
        Check(result.FrameCount == original.FrameCount && edit.Preview().Names.SequenceEqual(source.Names), "changed duration or IDs");
        Check(Math.Abs(result.Tracks["hp"].Position(0).Z - generated.Tracks["hp"].Position(0).Z) < 1e-6, "grounded height lost");
        Check(Vector2.Distance(new(result.Tracks["hp"].Position(0).X, result.Tracks["hp"].Position(0).Y),
            new(original.Tracks["hp"].Position(0).X, original.Tracks["hp"].Position(0).Y)) < 1e-6, "XY origin jumped");
        var old = ResArchiveDocument.Parse("before", baseline); var next = ResArchiveDocument.Parse("after", edit.ExportBytes());
        foreach (var entry in old.Entries.Where(e => e.Name != name && !e.Name.StartsWith(name + "\\")))
            Check(old.GetResourceBytes(entry).Span.SequenceEqual(next.GetResourceBytes(entry.Name).Span), "other clip changed");
        var folder = Path.Combine(Path.GetTempPath(), "variant-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        foreach (bool inRes in new[] { false, true })
        {
            string path = Path.Combine(folder, inRes ? "check.res" : "check.anm");
            byte[] empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0); BitConverter.GetBytes(16).CopyTo(empty, 8);
            File.WriteAllBytes(path, inRes ? ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(), [new("model.anm", baseline), new("neighbor", [7, 9])]) : baseline);
            var target = new AnimationSaveTarget(path, inRes ? "model.anm" : null, baseline);
            await target.SaveAsync(baseline, edit.ExportBytes(), true);
            byte[] disk = inRes ? ResArchiveDocument.Open(path).GetResourceBytes("model.anm").ToArray() : File.ReadAllBytes(path);
            Check(disk.SequenceEqual(edit.ExportBytes()), "saved variant mismatch");
            var clipArchive = ResArchiveDocument.Parse("nested", ResArchiveDocument.Parse("saved", disk).GetResourceBytes(name).ToArray());
            Check(FigureAnimationTrack.Decode(clipArchive.GetResourceBytes("hp").ToArray()).Positions.Length == original.FrameCount, "independent nested root readback");
            if (inRes) Check(ResArchiveDocument.Open(path).GetResourceBytes("neighbor").Span.SequenceEqual(new byte[] { 7, 9 }), "RES neighbor changed");
        }
        edit.Undo(); Check(baseline.SequenceEqual(edit.ExportBytes()) && !edit.CanUndo, "accept is not one undo");
        int first = 2, last = original.FrameCount - 3;
        await edit.BlendGeneratedVariantAsync(name, generated, first, last, ["rh1"], .5f, 2);
        result = await edit.Preview().LoadAsync(name);
        for (int f = 0; f < original.FrameCount; f++)
            if (f <= first || f >= last) Check(Math.Abs(Quaternion.Dot(original.Tracks["rh1"].Rotation(f), result.Tracks["rh1"].Rotation(f))) > .999999f, "range seam/outside changed");
        next = ResArchiveDocument.Parse("partial", edit.ExportBytes());
        var rawOld = ResArchiveDocument.Parse("old clip", old.GetResourceBytes(name).ToArray());
        var rawNext = ResArchiveDocument.Parse("new clip", next.GetResourceBytes(name).ToArray());
        foreach (var e in rawOld.Entries.Where(e => e.Name != "rh1")) Check(rawOld.GetResourceBytes(e).Span.SequenceEqual(rawNext.GetResourceBytes(e.Name).Span), "unselected track bytes changed");
        edit.Undo(); edit.LockedParts.Add("hp"); bool blocked = false;
        try { await edit.BlendGeneratedVariantAsync(name, generated, 0, original.FrameCount - 1, parts, 1); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && baseline.SequenceEqual(edit.ExportBytes()) && !edit.CanUndo, "lock failure was not atomic");
        edit.LockedParts.Clear(); edit.MotionPins = new Dictionary<string, Vector3> { ["ll3"] = new(99, 99, 99) }; blocked = false;
        try { await edit.BlendGeneratedVariantAsync(name, generated, 0, original.FrameCount - 1, parts, 1); } catch (ArgumentException) { blocked = true; }
        Check(blocked && baseline.SequenceEqual(edit.ExportBytes()), "pin failure was not atomic");
        Check(source.ExportBytes().SequenceEqual(baseline), "preview mutated original");
        Console.WriteLine("GENERATED VARIANT PASS: full/partial/strength, root origin and height, same IDs/duration, independent nested disk ANM/RES, other tracks, one undo, lock/pin rollback and original preserved.");
    }
}
