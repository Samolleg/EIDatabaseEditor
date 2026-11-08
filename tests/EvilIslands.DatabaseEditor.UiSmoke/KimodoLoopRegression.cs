using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class KimodoLoopRegression
{
    internal static int Run(string database, string directory)
    {
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var hero = records.Single(r => r.Table == "units.prototypes" && r.Name == "Human Hero");
        var scene = UnitModelPreview.LoadAsync(database, hero, records).GetAwaiter().GetResult();
        int frames = checked((int)(new FileInfo(Path.Combine(directory, "root_positions.f32")).Length / 12));
        Verify(scene.Animations!, KimodoDraft.ReadSoma30(directory, frames)).GetAwaiter().GetResult();
        return 0;
    }

    internal static async Task Verify(FigureAnimationSet source, KimodoDraft real)
    {
        const int n = 60;
        var roots = Enumerable.Range(0, n).Select(f => new Vector3(.003f * f, 1 + .15f * MathF.Sin(f * MathF.PI / 30), .005f * f)).ToArray();
        var rotations = Enumerable.Range(0, n).Select(f => Enumerable.Range(0, 30)
            .Select(j => j == 11 ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f * f / (n - 1)) : Quaternion.Identity).ToArray()).ToArray();
        await CheckDraft(new(n, roots, rotations), true);
        await CheckDraft(real, false);

        async Task CheckDraft(KimodoDraft draft, bool synthetic)
        {
            var once = new FigureAnimationEditor(source);
            await once.CreateAnimationAsync("cidle09", "loop_check", draft.Frames, true);
            byte[] baseline = once.ExportBytes();
            var loop = new FigureAnimationEditor(once.Preview());
            await once.ApplyKimodoDraftAsync("loop_check", draft, KimodoPlayback.Once);
            await loop.ApplyKimodoDraftAsync("loop_check", draft, KimodoPlayback.StationaryLoop);
            var a = await once.Preview().LoadAsync("loop_check");
            var b = await loop.Preview().LoadAsync("loop_check");
            int last = draft.Frames - 1;
            Check(Vector3.Distance(a.Tracks["hp"].Position(0), b.Tracks["hp"].Position(0)) < 1e-6f, "lost model anchor");
            Check(b.Tracks["hp"].Position(last) == b.Tracks["hp"].Position(0), "root seam remains");
            foreach (var track in b.Tracks.Values)
                Check(Math.Abs(Quaternion.Dot(track.Rotation(0), track.Rotation(last))) > .99999f, "rotation seam remains");
            int tail = draft.Frames - Math.Clamp(draft.Frames / 10, 2, 12);
            for (int f = 0; f < tail; f++)
                Check(Math.Abs(a.Tracks["hp"].Position(f).Z - b.Tracks["hp"].Position(f).Z) < 1e-6f, "vertical movement flattened");
            if (synthetic)
            {
                Check(Vector3.Distance(a.Tracks["hp"].Position(last), a.Tracks["hp"].Position(0)) > .2f, "one-shot lost travel");
                Check((await once.DiagnoseAsync("loop_check", false)).Count == 0, "one-shot reported cycle seam");
                Check((await once.DiagnoseAsync("loop_check", true)).Count > 0, "repeat failed to report seam");
                Check((await loop.DiagnoseAsync("loop_check")).Count == 0, "stationary loop has jumps");
            }
            byte[] bytes = loop.ExportBytes();
            string temporary = Path.Combine(Path.GetTempPath(), "kimodo-loop-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                string anm = Path.Combine(temporary, "model.anm");
                await File.WriteAllBytesAsync(anm, baseline);
                await new AnimationSaveTarget(anm, null, baseline).SaveAsync(baseline, bytes, false);
                Check(File.ReadAllBytes(anm).SequenceEqual(bytes), "standalone save differs");
                byte[] empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0);
                BitConverter.GetBytes(16).CopyTo(empty, 8);
                byte[] res = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
                    [new("model.anm", baseline), new("other.bin", [9, 8, 7])]);
                string path = Path.Combine(temporary, "model.res");
                await File.WriteAllBytesAsync(path, res);
                await new AnimationSaveTarget(path, "model.anm", baseline).SaveAsync(baseline, bytes, false);
                var readback = ResArchiveDocument.Open(path);
                Check(readback.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 9, 8, 7 }), "RES neighbor changed");
                Check(readback.GetResourceBytes("model.anm").Span.SequenceEqual(bytes), "RES save differs");
                bytes = File.ReadAllBytes(anm);
            }
            finally { Directory.Delete(temporary, true); }
            var outer = ResArchiveDocument.Parse("loop", bytes);
            var nested = ResArchiveDocument.Parse("tracks", outer.GetResourceBytes("loop_check").ToArray());
            var exported = ResArchiveDocument.Parse("history", loop.ExportClipTracks("loop_check"));
            foreach (var entry in nested.Entries)
                Check(nested.GetResourceBytes(entry).Span.SequenceEqual(exported.GetResourceBytes(entry.Name).Span), "history lost track bytes");
            // Independently read the on-disk root layout, without FigureAnimationSet/track Decode.
            using var reader = new BinaryReader(new MemoryStream(nested.GetResourceBytes("hp").ToArray()));
            int qs = reader.ReadInt32(); reader.BaseStream.Position += qs * 16;
            int ps = reader.ReadInt32(); Check(ps == draft.Frames, "saved position count");
            for (int f = 0; f < ps; f++)
                Check(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) == b.Tracks["hp"].Position(f), "saved root differs from preview");
            var original = ResArchiveDocument.Parse("before", baseline);
            foreach (var entry in original.Entries.Where(e => e.Name != "loop_check"))
                Check(original.GetResourceBytes(entry).Span.SequenceEqual(outer.GetResourceBytes(entry.Name).Span), "other clip changed");
            loop.Undo(); Check(loop.ExportBytes().SequenceEqual(baseline), "import is not atomic Undo");
            loop.Redo(); Check(loop.ExportBytes().SequenceEqual(bytes), "Redo differs");
            Console.WriteLine($"KIMODO LOOP PASS: {(synthetic ? "synthetic" : "real SOMA")}, {draft.Frames} frames, vertical profile, exact root/rotation seam, raw nested ANM, other clips and Undo/Redo");
            Console.WriteLine($"  root start={b.Tracks["hp"].Position(0)}, original seam={(a.Tracks["hp"].Position(last) - a.Tracks["hp"].Position(0)).Length():F6}, closed seam=0, Z={b.Tracks["hp"].Positions.Min(p => p.Z):F6}..{b.Tracks["hp"].Positions.Max(p => p.Z):F6}, issues={(await loop.DiagnoseAsync("loop_check")).Count}");
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("KIMODO LOOP: " + message); }
}
