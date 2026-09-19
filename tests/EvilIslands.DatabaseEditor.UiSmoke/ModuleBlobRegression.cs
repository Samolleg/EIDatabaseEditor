using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using DatabaseEditor.Modules.Host;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class ModuleBlobRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Codec(DocumentStore store) : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot d) => store.Blobs.Get(Encoding.UTF8.GetString(d.State.AsSpan())).Result;
        public PreparedDocument Prepare(DocumentSnapshot d, string kind)
        { var b = store.Blobs.Get(Encoding.UTF8.GetString(d.State.AsSpan())); return new(b.Result, b.Bytes); }
    }
    internal static int Restart(string path)
    {
        var store = new DocumentStore(); store.RegisterCodec("fixture", "blob", 1, new Codec(store));
        var result = store.RestoreRecovery(File.ReadAllBytes(path));
        return result.Succeeded && store.All.Length == 1 && store.Validate(store.All[0]).Succeeded ? 0 : 1;
    }
    internal static int Run()
    {
        try
        {
            var limited = new DocumentBlobStore(3, 3, 1);
            Check(!limited.Put(default).Result.Succeeded, "default accepted");
            byte[] alias = [1, 2, 3];
            var put = limited.Put(ImmutableCollectionsMarshal.AsImmutableArray(alias));
            Check(put.Result.Succeeded, "put"); alias[0] = 9;
            Check(limited.Put([1, 2, 3]).Sha256 == put.Sha256, "dedup at capacity");
            Check(!limited.Put([4]).Result.Succeeded && !limited.Put([1,2,3,4]).Result.Succeeded, "limits");
            var get = limited.Get(put.Sha256!.ToLowerInvariant());
            Check(get.Bytes[0] == 1, "caller alias changed blob");
            ImmutableCollectionsMarshal.AsArray(get.Bytes)![0] = 8;
            Check(limited.Get(put.Sha256).Bytes[0] == 1, "Get exposed mutable storage");
            Check(limited.Get("bad").Result.Status == ResultStatus.Failed, "invalid ID");
            Check(limited.Get(new string('0',64)).Result.Status == ResultStatus.Unavailable, "missing ID");
            Check(new DocumentBlobStore().Put([]).Result.Succeeded, "empty initialized blob");
            Check(!new DocumentBlobStore(3,2,2).Put([1,2,3]).Result.Succeeded, "aggregate limit");

            using var vm = new MainWindowViewModel();
            var store = vm.ModuleDocuments;
            var baseline = Enumerable.Range(0, 8192).Select(i => (byte)i).ToImmutableArray();
            string id = store.Blobs.Put(baseline).Sha256!;
            var state = Encoding.UTF8.GetBytes(id).ToImmutableArray();
            store.RegisterCodec("fixture", "blob", 1, new Codec(store));
            var doc = store.Open(new("fixture", "blob", 1, "blob", "initial", state));
            for (int i=0; i<20; i++)
                Check(vm.ApplyModuleTransaction(new(Guid.NewGuid(), vm.ModuleWorkspace.Snapshot().Stamp, "edit",
                    [new UpdateDocument(doc.DocumentId, store.Get(doc.DocumentId)!.Revision, "edit"+i, state)])).Succeeded, "transaction");
            Check(store.Close(doc.DocumentId, store.Get(doc.DocumentId)!.Revision, true).Succeeded, "close");
            Check(vm.ApplyModuleHistory(false).Succeeded && store.Get(doc.DocumentId)!.Text == "edit18", "closed Undo");
            Check(vm.ApplyModuleHistory(true).Succeeded && store.Get(doc.DocumentId)!.Text == "edit19", "Redo");
            Check(store.Prepare(doc.DocumentId,"raw").Bytes.AsSpan().SequenceEqual(baseline.AsSpan()), "history blob lost");
            store.Close(doc.DocumentId, store.Get(doc.DocumentId)!.Revision, true);
            var bytes = store.CaptureRecovery();
            var json = JsonNode.Parse(bytes)!;
            Check((int)json["Version"]! == 2 && json["Blobs"]!.AsObject().Count == 1 && bytes.Length < 16000, "baseline duplicated");
            string path = Path.Combine(Path.GetTempPath(), "module-blobs-"+Guid.NewGuid().ToString("N")+".json");
            File.WriteAllBytes(path,bytes);
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--module-blob-restore"); start.ArgumentList.Add(path);
            using (var child = System.Diagnostics.Process.Start(start)!)
            {
                if (!child.WaitForExit(15000)) { child.Kill(); throw new InvalidOperationException("restart timed out"); }
                Check(child.ExitCode == 0, "fresh process recovery failed");
            }
            var fresh = new DocumentStore(); fresh.RegisterCodec("fixture","blob",1,new Codec(fresh));
            Check(fresh.RestoreRecovery(File.ReadAllBytes(path)).Succeeded, "durable restore before codec");
            Check(fresh.Active.Length == 0 && fresh.All.Length == 1 && fresh.Validate(fresh.All[0]).Succeeded, "closed restore");
            var before = fresh.CaptureRecovery();
            json["Blobs"]![id] = Convert.ToBase64String([0]);
            Check(!fresh.RestoreRecovery(Encoding.UTF8.GetBytes(json.ToJsonString())).Succeeded, "corruption accepted");
            Check(before.AsSpan().SequenceEqual(fresh.CaptureRecovery()), "failed restore mutated store");
            json = JsonNode.Parse(bytes)!; json["Blobs"] = new JsonObject();
            var missing = new DocumentStore(); missing.RegisterCodec("fixture","blob",1,new Codec(missing));
            Check(!missing.RestoreRecovery(Encoding.UTF8.GetBytes(json.ToJsonString())).Succeeded && missing.All.IsEmpty, "missing blob accepted");
            json = JsonNode.Parse(bytes)!;
            json["Documents"]![0]!["State"] = JsonSerializerState(new string('0',64));
            Check(!missing.RestoreRecovery(Encoding.UTF8.GetBytes(json.ToJsonString())).Succeeded, "codec rejection");
            Check(missing.Blobs.Get(id).Result.Status == ResultStatus.Unavailable, "staged pool leaked after rejection");
            var unknown = new DocumentStore(); Check(unknown.RestoreRecovery(bytes).Succeeded, "unknown codec restore");
            unknown.RegisterCodec("fixture","blob",1,new Codec(unknown));
            Check(unknown.Validate(unknown.All[0]).Succeeded, "unknown codec bytes lost");
            var legacy = new DocumentStore().CaptureRecovery();
            Check((int)JsonNode.Parse(legacy)!["Version"]! == 1 && new DocumentStore().RestoreRecovery(legacy).Succeeded, "legacy recovery");
            Console.WriteLine("PASS module blobs: ownership, dedup, limits, 20 compact transactions, closed Undo/Redo, durable codec ordering, corruption/missing rollback, unknown codec, legacy");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static JsonNode JsonSerializerState(string id) => System.Text.Json.JsonSerializer.SerializeToNode(Encoding.UTF8.GetBytes(id).ToImmutableArray())!;
}
