using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationSaveRegression
{
    internal static void CheckNested(byte[] bytes, string name, int expectedTracks)
    {
        // Inspect both RES directories directly; the preview loader also accepts
        // flat clip\part paths and therefore cannot prove game-compatible layout.
        var outer = ResArchiveDocument.Parse("raw ANM", bytes);
        if (outer.Entries.Any(e => e.Name.Replace('/', '\\').StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("ANM LAYOUT: flat top-level tracks remain for " + name);
        var clip = outer.Entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var tracks = ResArchiveDocument.Parse("raw nested clip", outer.GetResourceBytes(clip).ToArray());
        if (tracks.Entries.Count != expectedTracks || tracks.Entries.Count == 0)
            throw new Exception("ANM LAYOUT: nested track count differs for " + name);
        foreach (var track in tracks.Entries)
        {
            if (track.Name.Contains('\\') || track.Name.Contains('/'))
                throw new Exception("ANM LAYOUT: nested track has a path prefix");
            _ = FigureAnimationTrack.Decode(tracks.GetResourceBytes(track).ToArray());
        }
    }

    public static async Task Verify(FigureAnimationSet source, string clip)
    {
        var editor = new FigureAnimationEditor(source);
        await editor.CreateAnimationAsync(clip, "save_new", 3, false);
        byte[] before = source.ExportBytes(), after = editor.ExportBytes();
        int trackCount = (await source.LoadAsync(clip)).Tracks.Count;
        CheckNested(after, "save_new", trackCount);
        string directory = Path.Combine(Path.GetTempPath(), "ei-anm-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(bool ok, string why) { if (!ok) throw new Exception("ANM SAVE: " + why); }
        foreach (bool nested in new[] { false, true })
        {
            string path = Path.Combine(directory, nested ? "figures.res" : "standalone.anm");
            byte[] initial;
            if (nested)
            {
                byte[] empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0);
                BitConverter.GetBytes(16).CopyTo(empty, 8);
                initial = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
                    [new("model.anm", before), new("other.bin", [1, 2, 3, 4])]);
            }
            else initial = before;
            File.WriteAllBytes(path, initial);
            var target = new AnimationSaveTarget(path, nested ? "model.anm" : null, before);
            string? backup = await target.SaveAsync(before, after, true);
            Check(backup is not null && File.ReadAllBytes(backup).SequenceEqual(initial), "backup differs");
            expectedFiles.Add(path); expectedFiles.Add(backup!);
            byte[] Current()
            {
                byte[] data = File.ReadAllBytes(path);
                if (!nested) return data;
                var archive = ResArchiveDocument.Parse(path, data);
                Check(archive.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "unrelated resource changed");
                return archive.GetResourceBytes("model.anm").ToArray();
            }
            Check(Current().SequenceEqual(after), "saved bytes differ");
            CheckNested(Current(), "save_new", trackCount);
            var reopened = source.WithBytes(before).WithSavedBytes(Current());
            Check(reopened.SaveTarget is null || reopened.SaveTarget.OriginalBytes.SequenceEqual(after), "saved baseline not refreshed");
            Check((await source.WithBytes(Current()).LoadAsync("save_new")).FrameCount == 3, "new clip cannot reload");
            var second = new FigureAnimationEditor(reopened);
            await second.CreateAnimationAsync("save_new", "save_two", 3, false);
            await target.SaveAsync(reopened.SaveTarget?.OriginalBytes ?? after, second.ExportBytes(), false);
            Check((await source.WithBytes(Current()).LoadAsync("save_two")).FrameCount == 3, "second save cannot reload");
            CheckNested(Current(), "save_two", trackCount);
            await target.SaveAsync(second.ExportBytes(), after, false);
            Check(await target.SaveAsync(after, before, false) is null, "no-backup result");
            // Windows file filters can briefly expose a renamed, delete-pending *.tmp.
            // Require the exact expected set, including backups; never ignore leftover files.
            string[] filesAfter = Directory.GetFiles(directory);
            for (int retry = 0; retry < 20 && !expectedFiles.SetEquals(filesAfter); retry++)
            { await Task.Delay(100); filesAfter = Directory.GetFiles(directory); }
            Check(expectedFiles.SetEquals(filesAfter), "no-backup left files: " + string.Join(",", filesAfter.Select(System.IO.Path.GetFileName)));
            Check(Current().SequenceEqual(before), "repeat save differs");
            bool blocked = false;
            try { await target.SaveAsync(after, after, false); } catch (IOException) { blocked = true; }
            Check(blocked && Current().SequenceEqual(before), "stale source overwritten");
        }
        // Emulate a clip written by the old editor and verify that saving just
        // this clip nests its tracks without changing any other clip payload.
        var outerSource = ResArchiveDocument.Parse("source", before);
        var created = ResArchiveDocument.Parse("created", after);
        var createdClip = ResArchiveDocument.Parse("created clip", created.GetResourceBytes("save_new").ToArray());
        var flatEntries = createdClip.Entries.Select(e => new AddedResResource("flat_legacy\\" + e.Name, createdClip.GetResourceBytes(e).ToArray())).ToArray();
        byte[] legacy = ResArchiveWriter.Rebuild(outerSource, new Dictionary<int, byte[]>(), flatEntries);
        var legacyEditor = new FigureAnimationEditor(source.WithBytes(legacy));
        Check((await legacyEditor.Preview().LoadAsync("flat_legacy")).FrameCount == 3, "legacy flat clip no longer readable");
        legacyEditor.NestFlatClip("flat_legacy");
        byte[] converted = legacyEditor.ExportBytes();
        CheckNested(converted, "flat_legacy", trackCount);
        var convertedOuter = ResArchiveDocument.Parse("converted", converted);
        foreach (var entry in outerSource.Entries)
            Check(outerSource.GetResourceBytes(entry).Span.SequenceEqual(convertedOuter.GetResourceBytes(entry.Name).Span), "other clip payload changed during flat conversion");
        string legacyPath = Path.Combine(directory, "legacy.anm");
        File.WriteAllBytes(legacyPath, legacy);
        await new AnimationSaveTarget(legacyPath, null, legacy).SaveAsync(legacy, converted, false);
        CheckNested(File.ReadAllBytes(legacyPath), "flat_legacy", trackCount);
        Check(ReferenceEquals(source.SaveTarget, source.WithBytes(after).SaveTarget), "source provenance lost");
        foreach (string name in new[] { "2hacidle01", "a-very-long-source-name", "bad-name" })
        {
            string first = FigureAnimationEditor.SuggestCopyId(name, []);
            string second = FigureAnimationEditor.SuggestCopyId(name, [first]);
            FigureAnimationEditor.ValidateNewId(first); FigureAnimationEditor.ValidateNewId(second);
            Check(first != second && first.Length <= 15 && second.Length <= 15 && !first.Contains('-'), "copy ID is not game-compatible");
        }
        Console.WriteLine("ANM SAVE PASS: standalone/RES, nested clips, legacy flat conversion, backup/no-backup, repeat, conflict, other resource");
    }
}
