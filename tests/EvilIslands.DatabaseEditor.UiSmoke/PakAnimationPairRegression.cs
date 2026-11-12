using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakAnimationPairRegression
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "dbe-pak-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] empty = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature);
            BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
            byte[] anm = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(), [new("idle", [1])]);
            byte[] edited = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(), [new("idle", [2])]);
            byte[] adb = new byte[44]; BinaryPrimitives.WriteUInt32LittleEndian(adb, 0x00424441); Encoding.ASCII.GetBytes("model").CopyTo(adb, 8);
            byte[] updatedAdb = adb.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(updatedAdb.AsSpan(32), .5f);
            string shared = Path.Combine(root, "shared.pak");
            PakArchive.Create(shared, new() { ["model.anm"] = anm, ["model.adb"] = adb, ["keep"] = [9] });
            var actions = (await AnimationDatabaseSource.OpenAsync(shared, null, "model"))!;
            var target = new AnimationSaveTarget(shared, "model.anm", anm);
            string? backup = await AnimationPairSave.SaveAsync(target, anm, edited, actions, AnimationDatabase.Parse(updatedAdb));
            Require(backup is not null && File.Exists(backup), "same-owner backup");
            Require(PakArchive.Read(shared, "model.anm").SequenceEqual(edited), "ANM readback");
            Require(PakArchive.Read(shared, "model.adb").SequenceEqual(updatedAdb), "ADB readback");
            Require(PakArchive.Read(shared, "keep").SequenceEqual(new byte[] { 9 }), "unrelated payload");

            string loose = Path.Combine(root, "loose.anm"); File.WriteAllBytes(loose, anm);
            target = new AnimationSaveTarget(loose, null, anm);
            actions = (await AnimationDatabaseSource.OpenAsync(shared, null, "model"))!;
            await AnimationPairSave.SaveAsync(target, anm, edited, actions, actions.CreateDraft(), false);
            Require(File.ReadAllBytes(loose).SequenceEqual(edited), "mixed loose ANM + PAK ADB");
            string beforeLoose = Hash(loose);
            PakArchive.ApplyChanges([(shared, Hash(shared), new() { ["external"] = [7] })]);
            string afterExternal = Hash(shared);
            try
            {
                await AnimationPairSave.SaveAsync(target, edited, anm, actions, actions.CreateDraft(), false);
                throw new InvalidOperationException("stale pair accepted");
            }
            catch (IOException) { }
            Require(Hash(loose) == beforeLoose && Hash(shared) == afterExternal, "stale pair changed a participant");
            Console.WriteLine("PASS animation pair: same owner, backups, ANM/ADB readback, unrelated payload, mixed loose/PAK, stale owner rejection");
        }
        finally { Directory.Delete(root, true); }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
