using System.IO;
using System.Security.Cryptography;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class ModuleSaveRegression
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "database-module-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PreparedFileWrite Make(string name)
            {
                string path = Path.Combine(root, name); File.WriteAllBytes(path, [1, 2, 3]);
                return new(Guid.NewGuid(), path, Convert.ToHexString(SHA256.HashData([1, 2, 3])), [4, 5, 6]);
            }
            var first = Make("first.res"); var second = Make("second.mob");
            var partial = DocumentSaveCoordinator.Commit([first, second], Path.Combine(root, "journals"), beforePublish: i =>
            { if (i == 1) throw new IOException("injected second-file failure"); });
            Require(!partial.Complete && partial.Files[0].Status == "Saved" && partial.Files[1].Status == "Failed", "Incorrect partial report");
            Require(File.ReadAllBytes(first.Path).SequenceEqual(first.Bytes), "First was not committed");
            Require(File.ReadAllBytes(second.Path).SequenceEqual(new byte[] { 1, 2, 3 }), "Unsaved participant changed");
            Require(File.ReadAllBytes(partial.Files[0].BackupPath).SequenceEqual(new byte[] { 1, 2, 3 }), "Original missing");
            Require(File.ReadAllBytes(partial.Files[1].PreparedPath).SequenceEqual(second.Bytes), "Prepared participant missing");
            Require(File.ReadAllText(partial.JournalPath).Contains("Saved"), "Durable partial journal missing");
            var resumed = DocumentSaveCoordinator.Commit([first, second], Path.Combine(root, "journals"));
            Require(resumed.Complete && File.ReadAllBytes(second.Path).SequenceEqual(second.Bytes), "Retry of partial save failed");
            Require(!File.Exists(resumed.Files[0].BackupPath), "Already published participant was rewritten");
            var a = Make("conflict-a.res"); var b = Make("conflict-b.mob"); File.WriteAllBytes(b.Path, [9]);
            var conflict = DocumentSaveCoordinator.Commit([a, b], Path.Combine(root, "journals"));
            Require(!conflict.Complete && conflict.Files.All(f => f.Status != "Saved"), "External conflict partially published");
            Require(File.ReadAllBytes(a.Path).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllBytes(b.Path).SequenceEqual(new byte[] { 9 }), "Conflict changed originals");
            var c = Make("success-a.res"); var d = Make("success-b.mob");
            var success = DocumentSaveCoordinator.Commit([c, d], Path.Combine(root, "journals"));
            Require(success.Complete && success.Files.All(f => f.Status == "Saved"), "Full save failed");
            Console.WriteLine("PASS coordinated save: all-file preflight, external conflict, injected partial failure, backup/prepared recovery journal, complete save"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
