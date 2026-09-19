using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;
namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class AuditFixRegression
{
    static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    internal static int Run(string source)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(source); task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS linked DB/Lua preflight lock, retry, checkpoint, document-scoped Lua"); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static async Task Check(string source)
    {
        string root=Path.Combine(Path.GetTempPath(),"db-audit-fix-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"lua"));
        string db=Path.Combine(root,"database.res"); File.Copy(source,db);
        File.WriteAllText(Path.Combine(root,"main.lua"),"UnitPerkType={Strength=13}");
        File.WriteAllText(Path.Combine(root,"lua","tables.lua"),"UnitPerk={}");
        File.WriteAllText(Path.Combine(root,"lua","npc_consumables.lua"),"NpcCombatConsumables={prototypes={}}");
        using var vm=new MainWindowViewModel { LinkedSaveJournalDirectory=Path.Combine(root,"journals") }; await vm.OpenPathAsync(db); vm.ConnectLua(root);
        var table=vm.Databases.SelectMany(d=>d.Tables).Single(t=>t.Key=="units.prototypes");
        vm.SelectTableCommand.Execute(table);
        var row=table.Records.First(); var name=row.GetCell(0)!; name.EditorText="audit-initial-prototype";
        Require(vm.HasChanges,"DB edit was not staged");
        var docs=vm.LuaWorkspace!.Documents.ToArray();
        foreach(var d in docs) { string old=d.Text; d.Apply([new(d.Text.Length,0,"\n-- audit edit")],"audit"); vm.RecordLuaGridEdit(d,old); }
        string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string initial=Hash(db);
        using(var locked=new FileStream(docs[1].Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            await vm.SaveCurrentAsync(false);
        Require(Hash(db)==initial && docs.All(d=>!File.ReadAllText(d.Path).Contains("audit edit")),"Preflight failure published an earlier file");
        Require(vm.HasChanges && vm.HasPendingLuaChanges,"Failed save discarded edits");
        await vm.SaveCurrentAsync(false, createBackup: false);
        Require(!Directory.GetFiles(root, "database.res.bak-*").Any(), "Database no-backup option ignored");
        Require(Hash(db)!=initial && docs.All(d=>File.ReadAllText(d.Path).Contains("audit edit")),"Retry did not save all files: " + vm.NotificationMessage);
        Require(!vm.HasChanges && !vm.HasPendingLuaChanges,"Saved changes still dirty");
        string second=Path.Combine(root,"second.res"); File.Copy(source,second);
        await vm.OpenPathAsync(second);
        Require(vm.LuaWorkspace is null,"Previous DB Lua leaked to next document");
    }
}
