using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationClipDeletionRegression
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task Verify(UnitModelScene scene, AnimationDatabaseSource adb, string output)
    {
        var source = scene.Animations!;
        byte[] original = source.ExportBytes();
        string clip = source.Names.First(n => !adb.Database.Entries.Any(e => e.Name == n));
        foreach (bool inRes in new[] { false, true })
        {
            string path = Path.Combine(output, inRes ? "delete-test.res" : "delete-test.anm");
            var empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0); BitConverter.GetBytes(16).CopyTo(empty, 8);
            byte[] container = inRes ? ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
                [new("unhufe.anm", original), new("other.bin", [3, 2, 1])]) : original;
            File.WriteAllBytes(path, container);
            var target = new AnimationSaveTarget(path, inRes ? "unhufe.anm" : null, original);
            var editable = new FigureAnimationSet("test", original, source.Parents, source.Bones, source.BindPositions) { SaveTarget = target };
            bool blocked = false;
            try { await AnimationClipDeletion.SaveAsync(editable, adb, adb.Database.Entries[0].Name); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked && File.ReadAllBytes(path).SequenceEqual(container), "referenced clip deletion not blocked");
            var saved = await AnimationClipDeletion.SaveAsync(editable, adb, clip);
            byte[] disk = inRes ? ResArchiveDocument.Open(path).GetResourceBytes("unhufe.anm").ToArray() : File.ReadAllBytes(path);
            var after = ResArchiveDocument.Parse("saved", disk);
            var before = ResArchiveDocument.Parse("before", original);
            Check(!after.Entries.Any(e => e.Name.Equals(clip, StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith(clip + "\\", StringComparison.OrdinalIgnoreCase)), "deleted clip remains on disk");
            foreach (var e in before.Entries.Where(e => !e.Name.Equals(clip, StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith(clip + "\\", StringComparison.OrdinalIgnoreCase)))
                Check(before.GetResourceBytes(e).Span.SequenceEqual(after.GetResourceBytes(e.Name).Span), "other clip changed");
            Check(saved.SaveTarget!.OriginalBytes.SequenceEqual(disk), "saved baseline stale");
            if (inRes) Check(ResArchiveDocument.Open(path).GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 3, 2, 1 }), "unrelated RES changed");
            blocked = false;
            try { await AnimationClipDeletion.SaveAsync(editable, adb, source.Names.First(n => n != clip && !adb.Database.Entries.Any(e => e.Name == n))); }
            catch (IOException) { blocked = true; }
            Check(blocked, "stale ANM deletion accepted");
            Check(Directory.GetFiles(output).Any(f => f.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase) && File.ReadAllBytes(f).SequenceEqual(container)), "original backup missing");
        }
        var flatHeader = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(flatHeader, 0); BitConverter.GetBytes(16).CopyTo(flatHeader, 8);
        byte[] flat = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", flatHeader), new Dictionary<int, byte[]>(),
            [new("test\\hp", [1, 2]), new("test/lh1", [3]), new("test2\\hp", [4]), new("keep", [5])]);
        var deleted = ResArchiveDocument.Parse("deleted", AnimationClipDeletion.Remove(flat, "TEST"));
        Check(deleted.Entries.Count == 2 && deleted.GetResourceBytes("test2\\hp").Span.SequenceEqual(new byte[] { 4 }), "flat exact-prefix removal");
        Console.WriteLine("ANM DELETE PASS: nested/flat, standalone/RES, reference rejection, backups, saved baseline, external-change protection, unrelated bytes intact.");
    }
}
