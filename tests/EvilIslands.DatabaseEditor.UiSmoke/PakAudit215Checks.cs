using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class PakAudit215Checks
{
    sealed class Codec : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot snapshot) => Result.Ok;
        public PreparedDocument Prepare(DocumentSnapshot snapshot,string kind) => new(Result.Ok,[]);
    }
    static void Require(bool condition,string message) { if(!condition)throw new Exception(message); Console.WriteLine("PASS "+message); }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static int Run(string database,string output)
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task=Check(database,Path.GetFullPath(output));
        task.ContinueWith(_=>app.Dispatcher.InvokeShutdown(),TaskScheduler.Default); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static async Task Check(string database,string output)
    {
        Directory.CreateDirectory(output); string initial=Hash(database);
        byte[] empty=new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(empty,ResArchiveDocument.Signature); BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8),16);
        string res=Path.Combine(output,"explicit.res"),pak=Path.ChangeExtension(res,".pak");
        File.WriteAllBytes(res,ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty",empty),new Dictionary<int,byte[]>(),[new("legacy.bin",[1]) ]));
        PakArchive.Create(pak,new(){{"modern.bin",[2]}});
        Require(ResFileManagerArchive.ReadCatalog(res).Single().Name=="legacy.bin" && ResFileManagerArchive.ReadResource(res,"legacy.bin").SequenceEqual(new byte[]{1}),"explicit RES catalog/payload preserve selected source with PAK sibling");
        Require(await TextureSources.ResolveDatabasePathAsync(res)==res,"explicit database selection is not redirected to sibling PAK");
        string root=Path.Combine(output,"Game"),baseDir=Path.Combine(root,"Base","Res"),mod=Path.Combine(root,"MODS","Fixture"),modRes=Path.Combine(mod,"res");
        Directory.CreateDirectory(baseDir);Directory.CreateDirectory(modRes);
        string fixtureDb=Path.Combine(modRes,"database.pak"); PakArchive.Create(fixtureDb,new(){{"marker",[1]}});
        string config=Path.Combine(mod,"config.ini"); File.WriteAllText(config,"[RES]\ndatabase.res=res/database.res\nfigures.res=res/models.res\n");
        var anm=ResFileManagerArchive.ReadResource(Path.Combine(Path.GetDirectoryName(database)!,"unhuma.pak"),"unhuma.anm");
        string basePak=Path.Combine(baseDir,"figures.pak"),modPak=Path.Combine(modRes,"models.pak");
        PakArchive.Create(basePak,new(){{"unhuma.anm",anm}});PakArchive.Create(modPak,new(){{"marker",[1]}});
        var index=await AnimationGlobalClipIndex.OpenAsync(fixtureDb);
        Require(index.Hits.Any(h=>h.Model=="unhuma"),"global animation search includes Base PAK from profile");
        Require(ReferenceEquals(index,await AnimationGlobalClipIndex.OpenAsync(fixtureDb)),"unchanged global index reused");
        PakArchive.ApplyChanges([(modPak,Hash(modPak),new(){{"unhuma.anm",null}})]);
        var changed=await AnimationGlobalClipIndex.OpenAsync(fixtureDb);
        Require(!changed.Hits.Any(h=>h.Model=="unhuma"),"changed PAK invalidates global index and tombstone hides Base");
        File.WriteAllBytes(Path.Combine(mod,"unhuma.anm"),anm);
        Require((await AnimationGlobalClipIndex.OpenAsync(fixtureDb)).Hits.Any(h=>h.Model=="unhuma"),"new loose animation invalidates index and wins over tombstone");
        string copy=Path.Combine(output,"database.pak");File.Copy(database,copy);
        using var vm=new MainWindowViewModel();await vm.OpenPathAsync(database);
        var first=vm.ModuleWorkspace.Snapshot();Require(ReferenceEquals(first,vm.ModuleWorkspace.Snapshot()),"workspace snapshot reused on identical revision");
        var record=first.Records.First(r=>r.Fields.Any(f=>f.Value.Kind=="String"&&f.Value.Editable));
        var field=record.Fields.First(f=>f.Value.Kind=="String"&&f.Value.Editable);
        var result=vm.ApplyModuleTransaction(new(Guid.NewGuid(),first.Stamp,"audit snapshot",[new SetField(new(record.Reference,field.FieldId,field.Occurrence,[]),field.Value,field.Value with { ExactValue=field.Value.ExactValue+"_audit" })]));
        Require(result.Succeeded,"snapshot field transaction accepted");
        var edited=vm.ModuleWorkspace.Snapshot();Require(edited.Stamp!=first.Stamp && !ReferenceEquals(edited,first) && edited.Records.Single(r=>r.Reference==record.Reference).Fields.Single(f=>f.FieldId==field.FieldId&&f.Occurrence==field.Occurrence).Value.ExactValue.EndsWith("_audit"),"snapshot invalidates for unsaved DB edits");
        vm.UndoCommand.Execute(null);Require(vm.ModuleWorkspace.Snapshot().Records.Single(r=>r.Reference==record.Reference).Fields.Single(f=>f.FieldId==field.FieldId&&f.Occurrence==field.Occurrence).Value==field.Value,"snapshot invalidates on Undo");
        vm.ModuleDocuments.RegisterCodec("audit", "text", 1, new Codec());
        var document=vm.ModuleDocuments.Open(new("audit","text",1,"test","original",[]));
        var beforeScript=vm.ModuleWorkspace.Snapshot();
        Require(vm.ApplyModuleTransaction(new(Guid.NewGuid(),beforeScript.Stamp,"script edit",[new UpdateDocument(document.DocumentId,document.Revision,"edited",[])])).Succeeded,"document-only transaction accepted");
        var afterScript=vm.ModuleWorkspace.Snapshot();
        Require(afterScript.Stamp!=beforeScript.Stamp && afterScript.Modified && afterScript.Records==beforeScript.Records,"script revision keeps immutable DB records and updates dirty flag");
        await vm.OpenPathAsync(copy);Require(vm.ModuleWorkspace.Snapshot().Stamp.Generation==first.Stamp.Generation,"dirty module still blocks database switch");
        Require(vm.ModuleDocuments.Close(document.DocumentId,vm.ModuleDocuments.Get(document.DocumentId)!.Revision,true).Succeeded,"fixture document closed after explicit discard");
        await vm.OpenPathAsync(copy);Require(vm.ModuleWorkspace.Snapshot().Stamp.Generation!=first.Stamp.Generation,"snapshot invalidates on database switch");
        var archive=await ResFileManagerArchive.OpenAsync(copy);string original=Hash(copy),bad=Path.Combine(output,"broken.udb");File.WriteAllBytes(bad,[1,2,3]);
        var imported=await ResFileManagerArchive.InspectImportAsync(bad);bool rejected=false;
        try { await archive.SaveInPlaceAsync([new(ResFileEditKind.AddOrReplace,"units.udb",imported)]); } catch(InvalidDataException) { rejected=true; }
        Require(rejected&&Hash(copy)==original,"file manager rejects malformed known PAK before original replacement");
        string valid=Path.Combine(output,"units.udb");File.WriteAllBytes(valid,ResFileManagerArchive.ReadResource(copy,"units.udb"));
        var good=await ResFileManagerArchive.InspectImportAsync(valid);
        var saved=await archive.SaveInPlaceAsync([new(ResFileEditKind.AddOrReplace,"units.udb",good)],createBackup:true);
        Require(saved.BackupPath is not null&&Hash(saved.BackupPath)==original&&DatabaseDocument.Open(copy).Databases.Count>0,"known PAK save reopens and backup retains original");
        PakDiscoveryRegression.Run();
        Require(Hash(database)==initial,"installed database unchanged");
    }
}
