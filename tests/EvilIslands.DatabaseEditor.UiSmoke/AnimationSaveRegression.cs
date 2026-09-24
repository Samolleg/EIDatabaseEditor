using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationSaveRegression
{
    public static async Task Verify(FigureAnimationSet source, string clip)
    {
        var editor = new FigureAnimationEditor(source);
        await editor.CreateAnimationAsync(clip, "save_new", 3, false);
        byte[] before = source.ExportBytes(), after = editor.ExportBytes();
        string directory = Path.Combine(Path.GetTempPath(), "ei-anm-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
            byte[] Current()
            {
                byte[] data = File.ReadAllBytes(path);
                if (!nested) return data;
                var archive = ResArchiveDocument.Parse(path, data);
                Check(archive.GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "unrelated resource changed");
                return archive.GetResourceBytes("model.anm").ToArray();
            }
            Check(Current().SequenceEqual(after), "saved bytes differ");
            var reopened = source.WithBytes(before).WithSavedBytes(Current());
            Check(reopened.SaveTarget is null || reopened.SaveTarget.OriginalBytes.SequenceEqual(after), "saved baseline not refreshed");
            Check((await source.WithBytes(Current()).LoadAsync("save_new")).FrameCount == 3, "new clip cannot reload");
            var second = new FigureAnimationEditor(reopened);
            await second.CreateAnimationAsync("save_new", "save_two", 3, false);
            await target.SaveAsync(reopened.SaveTarget?.OriginalBytes ?? after, second.ExportBytes(), false);
            Check((await source.WithBytes(Current()).LoadAsync("save_two")).FrameCount == 3, "second save cannot reload");
            await target.SaveAsync(second.ExportBytes(), after, false);
            var files = Directory.GetFiles(directory).Order().ToArray();
            Check(await target.SaveAsync(after, before, false) is null, "no-backup result");
            var filesAfter = Directory.GetFiles(directory).Order().ToArray();
            Check(files.SequenceEqual(filesAfter), "no-backup left files: before=" + string.Join(",", files.Select(System.IO.Path.GetFileName)) + " after=" + string.Join(",", filesAfter.Select(System.IO.Path.GetFileName)));
            Check(Current().SequenceEqual(before), "repeat save differs");
            bool blocked = false;
            try { await target.SaveAsync(after, after, false); } catch (IOException) { blocked = true; }
            Check(blocked && Current().SequenceEqual(before), "stale source overwritten");
        }
        Check(ReferenceEquals(source.SaveTarget, source.WithBytes(after).SaveTarget), "source provenance lost");
        foreach (string name in new[] { "2hacidle01", "a-very-long-source-name", "bad-name" })
        {
            string first = FigureAnimationEditor.SuggestCopyId(name, []);
            string second = FigureAnimationEditor.SuggestCopyId(name, [first]);
            FigureAnimationEditor.ValidateNewId(first); FigureAnimationEditor.ValidateNewId(second);
            Check(first != second && first.Length <= 15 && second.Length <= 15 && !first.Contains('-'), "copy ID is not game-compatible");
        }
        Console.WriteLine("ANM SAVE PASS: standalone/RES, backup/no-backup, repeat, conflict, other resource, new clip roundtrip");
    }
}
