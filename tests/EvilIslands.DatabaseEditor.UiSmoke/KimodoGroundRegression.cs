using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class KimodoGroundRegression
{
    private static void Check(bool yes, string message) { if (!yes) throw new Exception("GROUND: " + message); }
    internal static int Run(string database, string raw)
    {
        Verify(database, raw).GetAwaiter().GetResult(); return 0;
    }
    private static async Task Verify(string database, string raw)
    {
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database, rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var source = scene.Animations!;
        var armored = await UnitModelPreview.LoadAsync(database, rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Kania Army Officer MC F11"), rows);
        Check(!armored.Meshes.Any(m => m.Name is "ll3" or "rl3"), "fixture must replace bare feet with armor");
        var armorFeet = new FigureAnimationEditor(armored.Animations!).KimodoFootGeometry(armored.Meshes);
        Check(armorFeet.Count >= 2, "armored soles missing");
        int n = 40;
        var positions = Enumerable.Range(0, n).Select(f => new Vector3(f * .01f, 2, 0)).ToArray();
        var rotations = Enumerable.Range(0, n).Select(f => Enumerable.Repeat(Quaternion.Identity, 30).ToArray()).ToArray();
        for (int f = 0; f < n; f++)
        {
            float bend = f < 20 ? f / 20f : (40 - f) / 20f;
            rotations[f][22] = rotations[f][26] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend * .8f);
            rotations[f][23] = rotations[f][27] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -bend * 1.6f);
            rotations[f][24] = rotations[f][28] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend * .8f);
        }
        var draft = new KimodoDraft(n, positions, rotations);
        for (int f = 0; f < n; f++) positions[f].Y -= FigureAnimationEditor.KimodoSoleClearance(draft, f);
        positions[30].Y += .4f; // airborne frame must not be flattened
        var armoredEdit = await Import(armored.Animations!, armored.Meshes, draft, -3);
        var armoredClip = await armoredEdit.Preview().LoadAsync("ground_test");
        Check(armoredClip.Tracks["hp"].Position(20).Z < armoredClip.Tracks["hp"].Position(0).Z - .1f, "armored crouch erased");
        var a = await Import(source, scene.Meshes, draft, -3);
        var b = await Import(source, scene.Meshes, draft, 5);
        var ca = await a.Preview().LoadAsync("ground_test"); var cb = await b.Preview().LoadAsync("ground_test");
        Check(ca.Tracks["hp"].Positions.Zip(cb.Tracks["hp"].Positions).All(p => Vector3.Distance(p.First, p.Second) < 2e-6), "reference hip height leaked");
        Check(ca.Tracks["hp"].Position(20).Z < ca.Tracks["hp"].Position(0).Z - .1f, "crouch erased");
        float scale = (source.Bones["ll1"].Length() + source.Bones["ll2"].Length() + source.Bones["ll3"].Length()) / (.0844f + .4322f + .4216f);
        Check(Math.Abs(FootMin(a.Preview(), ca, scene.Meshes, 30) - .4f * scale) < 2e-5, "jump erased");
        var doubled = new FigureAnimationSet("double", source.ExportBytes(), source.Parents,
            source.Bones.ToDictionary(p => p.Key, p => p.Value * 2), source.BindPositions.ToDictionary(p => p.Key, p => p.Value * 2)) { RootSpace = source.RootSpace };
        var mesh2 = scene.Meshes.Select(m => m with { Positions = m.Positions.Select(p => p * 2).ToArray() }).ToArray();
        var c = await Import(doubled, mesh2, draft, 8); var cc = await c.Preview().LoadAsync("ground_test");
        Check(ca.Tracks["hp"].Positions.Zip(cc.Tracks["hp"].Positions).All(p => Vector3.Distance(p.First * 2, p.Second) < 1e-5), "rig scale not preserved");
        int frames = (int)(new FileInfo(Path.Combine(raw, "root_positions.f32")).Length / 12);
        var real = KimodoDraft.ReadSoma30(raw, frames);
        await Import(source, scene.Meshes, real, -5);
        await Import(armored.Animations!, armored.Meshes, real, -5);
        var loop = new FigureAnimationEditor(source); await loop.CreateAnimationAsync(source.Names[0], "ground_test", frames, true);
        await loop.ApplyKimodoGroundedDraftAsync("ground_test", real, scene.Meshes, KimodoPlayback.StationaryLoop);
        var lc = await loop.Preview().LoadAsync("ground_test");
        await GeneratedVariantRegression.Verify(source, lc);
        Check(lc.Tracks["hp"].Position(0) == lc.Tracks["hp"].Position(frames - 1), "loop root seam");
        Console.WriteLine("KIMODO GROUND PASS: crouch, airborne clearance, template independence, scale x2, real180 frames, loop seam, saved nested root/other clips, atomic Undo.");
    }
    private static async Task<FigureAnimationEditor> Import(FigureAnimationSet source, IReadOnlyList<FigureMesh> meshes, KimodoDraft draft, float height)
    {
        var edit = new FigureAnimationEditor(source); await edit.CreateAnimationAsync(source.Names[0], "ground_test", draft.Frames, true);
        await edit.SetPoseAsync("ground_test", "hp", 0, Quaternion.Identity, new Vector3(4, 7, height));
        byte[] before = edit.ExportBytes();
        bool rejected = false;
        try { await edit.ApplyKimodoGroundedDraftAsync("ground_test", draft, []); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && before.SequenceEqual(edit.ExportBytes()), "missing feet must reject without changes");
        await edit.ApplyKimodoGroundedDraftAsync("ground_test", draft, meshes);
        var clip = await edit.Preview().LoadAsync("ground_test");
        float scale = (source.Bones["ll1"].Length() + source.Bones["ll2"].Length() + source.Bones["ll3"].Length()) / (.0844f + .4322f + .4216f);
        for (int f = 0; f < draft.Frames; f++)
            Check(Math.Abs(FootMin(edit.Preview(), clip, meshes, f) - FigureAnimationEditor.KimodoSoleClearance(draft, f) * scale) < 2e-5, "clearance differs: " + f);
        var outer = ResArchiveDocument.Parse("result", edit.ExportBytes());
        string directory = Path.Combine(Path.GetTempPath(), "kimodo-ground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (bool inRes in new[] { false, true })
        {
            string path = Path.Combine(directory, inRes ? "test.res" : "test.anm");
            var empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0); BitConverter.GetBytes(16).CopyTo(empty, 8);
            byte[] baseline = inRes ? ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
                [new("test.anm", before), new("other", [1, 2])]) : before;
            File.WriteAllBytes(path, baseline);
            await new AnimationSaveTarget(path, inRes ? "test.anm" : null, before).SaveAsync(before, edit.ExportBytes(), true);
            byte[] saved = inRes ? ResArchiveDocument.Open(path).GetResourceBytes("test.anm").ToArray() : File.ReadAllBytes(path);
            Check(saved.SequenceEqual(edit.ExportBytes()), "disk ANM differs from preview");
            if (inRes) Check(ResArchiveDocument.Open(path).GetResourceBytes("other").Span.SequenceEqual(new byte[] { 1, 2 }), "neighbor resource changed");
            outer = ResArchiveDocument.Parse("disk", saved);
        }
        var nested = ResArchiveDocument.Parse("tracks", outer.GetResourceBytes("ground_test").ToArray());
        using var reader = new BinaryReader(new MemoryStream(nested.GetResourceBytes("hp").ToArray()));
        int q = reader.ReadInt32(); reader.BaseStream.Position += q * 16;
        Check(reader.ReadInt32() == draft.Frames, "saved root length");
        for (int f = 0; f < draft.Frames; f++) Check(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) == clip.Tracks["hp"].Position(f), "saved root mismatch");
        var original = ResArchiveDocument.Parse("old", source.ExportBytes());
        foreach (var e in original.Entries) Check(original.GetResourceBytes(e).Span.SequenceEqual(outer.GetResourceBytes(e.Name).Span), "unrelated clip changed");
        edit.Undo(); Check(before.SequenceEqual(edit.ExportBytes()), "atomic Undo failed"); edit.Redo();
        Console.WriteLine($"GROUND {draft.Frames} frames: hipZ={clip.Tracks["hp"].Positions.Min(p => p.Z):F6}..{clip.Tracks["hp"].Positions.Max(p => p.Z):F6}, foot0={FootMin(edit.Preview(), clip, meshes, 0):F6}");
        return edit;
    }
    private static float FootMin(FigureAnimationSet set, FigureAnimationClip clip, IReadOnlyList<FigureMesh> meshes, int frame)
    {
        var pose = set.Pose(clip, frame);
        // Independent fixture oracle: both bare feet and their armor surfaces.
        return meshes.Where(m => m.Name is "ll3" or "rl3" || m.Name.StartsWith("ll3.armor") || m.Name.StartsWith("rl3.armor")).SelectMany(m => m.Positions.Select(p =>
            (Vector3.Transform(p - set.BindPositions[m.Name], pose[m.Name].Rotation) + pose[m.Name].Position).Z)).Min();
    }
}
