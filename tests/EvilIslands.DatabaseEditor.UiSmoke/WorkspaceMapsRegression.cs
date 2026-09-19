using System.Collections.Immutable;
using System.IO;
using DatabaseEditor.Modules.Host;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class WorkspaceMapsRegression
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Codec : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot snapshot) => Result.Ok;
        public PreparedDocument Prepare(DocumentSnapshot snapshot, string kind) => new(Result.Ok, snapshot.State);
    }
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "maps-cache-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Verify(root).GetAwaiter().GetResult();
            using var vm = new MainWindowViewModel();
            vm.ModuleDocuments.RegisterCodec("fixture", "maps", 1, new Codec());
            var doc = vm.ModuleDocuments.Open(new("fixture", "maps", 1, "fixture.mob", "changed", [4,5,6], InitiallyDirty:true) { SaveArtifactKind="mob" });
            var adapter = new ModuleHostAdapter(null!, vm, Path.Combine(root,"journals"));
            var before = vm.ModuleDocuments.Get(doc.DocumentId)!;
            Check(adapter.ExportCopy(doc.DocumentId,"malachite-project","copy","*",(_,_,bytes)=>
            { Check(bytes.Span.SequenceEqual(new byte[]{4,5,6}),"copy bytes"); return Result.Ok; }).Succeeded,"export copy");
            Check(vm.ModuleDocuments.Get(doc.DocumentId)==before && before.IsDirty,"copy checkpointed dirty source");
            string target = Path.Combine(root,"fixture.mob"); adapter.BindSaveTarget(doc.DocumentId,target,"");
            Check(adapter.SaveAll().Complete,"ordinary SaveAll failed");
            Check(!vm.ModuleDocuments.Get(doc.DocumentId)!.IsDirty && File.ReadAllBytes(target).SequenceEqual(new byte[]{4,5,6}),"SaveAll did not checkpoint actual source");
            // RestoreFiles is the existing path-authorization boundary, without an OS picker.
            var token=Guid.NewGuid();
            adapter.RestoreFiles(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Sources=new[]{new {Id=token,Path=target,Hash="old"}},Targets=Array.Empty<object>() }));
            var opened=adapter.Read(target);
            var first=vm.ModuleDocuments.Open(new("fixture","maps",1,"fixture.mob","first",[7],opened.SourceToken,true){SaveArtifactKind="mob"});
            Check(adapter.SaveAll().Complete,"first source save");
            Check(vm.ApplyModuleTransaction(new(Guid.NewGuid(),vm.ModuleWorkspace.Snapshot().Stamp,"again",[new UpdateDocument(first.DocumentId,vm.ModuleDocuments.Get(first.DocumentId)!.Revision,"again",[8])])).Succeeded,"second edit");
            Check(adapter.SaveAll().Complete && File.ReadAllBytes(target).SequenceEqual(new byte[]{8}),"second source save");
            vm.ModuleDocuments.Close(first.DocumentId,vm.ModuleDocuments.Get(first.DocumentId)!.Revision,false);
            var reopened=adapter.Read(target);
            Check(reopened.SourceToken!=opened.SourceToken && reopened.Sha256!=opened.Sha256,"Read reused source token/hash");
            var second=vm.ModuleDocuments.Open(new("fixture","maps",1,"fixture.mob","reopened",[9],reopened.SourceToken,true){SaveArtifactKind="mob"});
            Check(adapter.SaveAll().Complete && File.ReadAllBytes(target).SequenceEqual(new byte[]{9}),"reopened source save");
            vm.ModuleDocuments.Close(second.DocumentId,vm.ModuleDocuments.Get(second.DocumentId)!.Revision,false);
            var stale=vm.ModuleDocuments.Open(new("fixture","maps",1,"fixture.mob","stale",[10],opened.SourceToken,true){SaveArtifactKind="mob"});
            _=adapter.Read(target);
            Check(!adapter.SaveAll().Complete && File.ReadAllBytes(target).SequenceEqual(new byte[]{9}),"new read authorized stale document overwrite");
            Console.WriteLine("PASS repeated source saves: SaveAll twice, reopen/edit/save, fresh token, stale source cannot overwrite after index reread");
            Console.WriteLine("PASS maps-cache: precedence, empty directory, stale captures/completions, canonical namespaces, reopen, corruption, cancellation, atomic replacement; export dirty vs SaveAll checkpoint");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // Root was created above from a fixed temp parent and a GUID; never delete a supplied path.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true);
        }
    }
    static async Task Verify(string root)
    {
        string dbdir = Path.Combine(root,"mod","Res"); Directory.CreateDirectory(dbdir);
        string maps = Path.Combine(root,"mod","Maps"); Directory.CreateDirectory(maps);
        File.WriteAllBytes(Path.Combine(maps,"b.MOB"),[0]); File.WriteAllBytes(Path.Combine(maps,"a.mob"),[1]); File.WriteAllBytes(Path.Combine(maps,"ignore.txt"),[1]);
        Directory.CreateDirectory(Path.Combine(maps,"nested")); File.WriteAllBytes(Path.Combine(maps,"nested","hidden.mob"),[1]);
        var current = new MapsWorkspaceContext(new(Guid.NewGuid(),1,0),Path.Combine(dbdir,"database.res"));
        File.WriteAllBytes(current.DatabasePath,[0]);
        var initial = current; int serialCalls = 0; int mutateOnCall = -1; bool cancelOnCommit=false;
        using var cancel = new CancellationTokenSource();
        ImmutableArray<string> authorized=[];
        Result Serialize(Func<Result> callback)
        {
            serialCalls++;
            if(serialCalls==mutateOnCall) current=current with{Stamp=current.Stamp with{Generation=current.Stamp.Generation+1}};
            if(cancelOnCommit && serialCalls==2)cancel.Cancel();
            return callback();
        }
        string cacheDir=Path.Combine(root,"cache");
        WorkspaceMapsCache Make() => new(()=>current,Serialize,(files,_)=>authorized=files,cacheDir);
        var service=Make(); var found=await service.DiscoverAsync(current.Stamp);
        Check(found.Result.Succeeded && found.Paths.Length==2 && Path.GetFileName(found.Paths[0])=="a.mob" && authorized.SequenceEqual(found.Paths),"fallback discovery");
        Directory.CreateDirectory(Path.Combine(dbdir,"Maps")); found=await service.DiscoverAsync(current.Stamp);
        Check(found.Result.Succeeded && found.Paths.IsEmpty,"empty preferred folder fell back");
        File.WriteAllBytes(Path.Combine(dbdir,"Maps","preferred.mob"),[2]); found=await service.DiscoverAsync(current.Stamp);
        Check(found.Paths.Length==1 && found.Paths[0].Contains("preferred.mob"),"preferred directory");
        Check((await service.LoadAsync(current.Stamp)).Result.Status==ResultStatus.Unavailable,"missing cache");
        Check((await service.SaveAsync(current.Stamp,[1,2,3])).Succeeded,"save cache");
        current=current with{Stamp=current.Stamp with{Revision=1}};
        Check((await service.LoadAsync(initial.Stamp)).Bytes.SequenceEqual(new byte[]{1,2,3}),"revision unnecessarily rejected");
        current=current with{Stamp=new(Guid.NewGuid(),9,0),DatabasePath=current.DatabasePath.ToUpperInvariant()};
        service=Make(); Check((await service.LoadAsync(current.Stamp)).Bytes.SequenceEqual(new byte[]{1,2,3}),"reopened canonical namespace");
        Check((await service.LoadAsync(initial.Stamp)).Result.Status==ResultStatus.Conflict,"stale session accepted");
        var dbA=current; current=current with{DatabasePath=Path.Combine(root,"different","database.res")};
        Check((await service.LoadAsync(current.Stamp)).Result.Status==ResultStatus.Unavailable,"different DB leaked cache");
        current=dbA;
        serialCalls=0; mutateOnCall=2;
        Check((await service.SaveAsync(current.Stamp,[9])).Status==ResultStatus.Conflict,"late save accepted");
        mutateOnCall=-1;
        Check((await service.LoadAsync(current.Stamp)).Bytes.SequenceEqual(new byte[]{1,2,3}),"late save changed cache");
        serialCalls=0; mutateOnCall=2;
        Check((await service.LoadAsync(current.Stamp)).Result.Status==ResultStatus.Conflict,"late read accepted");
        serialCalls=0; authorized=[];
        Check((await service.DiscoverAsync(current.Stamp)).Result.Status==ResultStatus.Conflict && authorized.IsEmpty,"late discovery authorized files");
        mutateOnCall=-1; serialCalls=0; cancelOnCommit=true;
        Check((await service.SaveAsync(current.Stamp,[8],cancel.Token)).Status==ResultStatus.Cancelled,"cancel before commit");
        cancelOnCommit=false;
        Check((await service.LoadAsync(current.Stamp)).Bytes.SequenceEqual(new byte[]{1,2,3}),"cancel changed cache");
        Check(!Directory.EnumerateFiles(cacheDir,"*.tmp").Any(),"aborted write leaked temp");
        Check((await service.SaveAsync(current.Stamp,default)).Status==ResultStatus.Failed,"default payload accepted");
        Check((await service.SaveAsync(current.Stamp,ImmutableArray.Create(new byte[WorkspaceMapsCache.MaxBytes+1]))).Status==ResultStatus.Failed,"oversize accepted");
        string cachePath=Directory.GetFiles(cacheDir,"*.cache").Single();
        var corrupt=File.ReadAllBytes(cachePath); corrupt[^1]^=0xff; File.WriteAllBytes(cachePath,corrupt);
        Check((await service.LoadAsync(current.Stamp)).Result.Status==ResultStatus.Failed,"corruption undetected");
        Check((await service.SaveAsync(current.Stamp,[])).Succeeded && (await service.LoadAsync(current.Stamp)).Bytes.IsEmpty,"atomic corrupt replacement/empty payload");
    }
}
