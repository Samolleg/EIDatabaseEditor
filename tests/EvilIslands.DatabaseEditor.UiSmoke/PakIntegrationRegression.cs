using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakIntegrationRegression
{
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static int Run(string databaseSource,string output)
    {
        Directory.CreateDirectory(output);
        try {
            string sourceSnapshot=Path.Combine(output,"baseline.res");File.Copy(databaseSource,sourceSnapshot);databaseSource=sourceSnapshot;
            PakDiscoveryRegression.Run();
            PakAnimationPairRegression.RunAsync().GetAwaiter().GetResult();
            string database=Path.Combine(output,"database.pak"),texts=Path.Combine(output,"texts.pak"),loose=Path.Combine(output,"map.mob");
            DatabaseDocument legacy=DatabaseDocument.Open(databaseSource);
            legacy.SaveAs(database);
            DatabaseDocument document=DatabaseDocument.Open(database);
            Require(document.Resources.Count==legacy.Resources.Count,"RES to PAK database resources lost");
            document.AppendBriefing("pak_regression_entry");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding=Encoding.GetEncoding(1251);
            PakArchive.Create(texts,new Dictionary<string,byte[]>{{"текст?",encoding.GetBytes("Название\r\nОписание")},{"keep",encoding.GetBytes("Не менять")}});
            var textDocument=TextsDocument.Open(texts);Require(textDocument.Resources.Single(r=>r.Name=="текст?").TrySetTitle("Изменено",out _),"text edit rejected");
            File.WriteAllBytes(loose,[1,2,3]);
            var writes=new[]{new PreparedFileWrite(Guid.NewGuid(),database,document.Sha256,document.PrepareImage()),new PreparedFileWrite(Guid.NewGuid(),texts,textDocument.Sha256,textDocument.PrepareImage()),new PreparedFileWrite(Guid.NewGuid(),loose,Hash(loose),[4,5,6])};
            var result=DocumentSaveCoordinator.Commit(writes,output);Require(result.Complete,"mixed save incomplete");
            Require(DatabaseDocument.Open(database).Databases.SelectMany(g=>g.Tables).SelectMany(t=>t.Records).Any(r=>r.Name=="pak_regression_entry"),"database edit missing");
            Require(TextsDocument.Open(texts).Resources.Single(r=>r.Name=="текст?").Title=="Изменено","text edit missing");
            Require(File.ReadAllBytes(loose).SequenceEqual(new byte[]{4,5,6}),"loose participant missing");
            string dbHash=Hash(database),textHash=Hash(texts);bool rejected=false;
            try{DocumentSaveCoordinator.Commit(writes,output);}catch(IOException){rejected=true;}
            Require(rejected&&Hash(database)==dbHash&&Hash(texts)==textHash,"stale group overwrote current state");
            var manager=ResFileManagerArchive.OpenAsync(texts).GetAwaiter().GetResult();
            manager.SaveInPlaceAsync([new(ResFileEditKind.Delete,"текст?")],createBackup:false).GetAwaiter().GetResult();
            using(var archive=PakArchive.Open(texts)){Require(archive.Deleted.Contains("текст?"),"delete not a tombstone");Require(archive.Read("KEEP").SequenceEqual(encoding.GetBytes("Не менять")),"unchanged resource damaged");}
            var currentTexts=TextsDocument.Open(texts);string copy=Path.Combine(output,"copy.pak");currentTexts.SaveAs(copy);
            Require(Hash(copy)==Hash(texts),"copy lost tombstone or exact image");
            PakArchive.PublishImages([(texts,Hash(texts),writes[1].Bytes)]);
            Require(Hash(texts)==Convert.ToHexString(SHA256.HashData(writes[1].Bytes)),"exact restoration failed");
            // Opening any member completes a previously committed mixed manifest.
            string pending=loose+".eipak.tmp";File.WriteAllBytes(pending,[7,8]);
            File.WriteAllText(Path.Combine(output,"pak-txn-regression.json"),JsonSerializer.Serialize(new{version=2,targets=new[]{new{kind="file",target="map.mob",tmp="map.mob.eipak.tmp",sha256=Hash(pending)}}}));
            using(var archive=PakArchive.Open(texts))Require(archive.Entries.Count>0,"recovered archive unreadable");
            Require(File.ReadAllBytes(loose).SequenceEqual(new byte[]{7,8}),"mixed recovery not completed");
            var service=new CoreDatabaseWorkspaceService();
            var workspace=service.OpenAsync(database,CancellationToken.None).GetAwaiter().GetResult();
            var briefing=workspace.Databases.SelectMany(g=>g.Tables).Single(t=>t.Key=="quests.briefings").Records.Single(r=>r.CoreRecord.Name=="pak_regression_entry");
            Require(briefing.CoreRecord.Fields.Single(f=>f.Id==1).TrySetValue("1",out _),"UI database edit rejected");
            workspace.TextResources.Single(r=>r.ResourceName=="текст?").Title="Через редактор";
            var savedWorkspace=service.SaveCurrentAsync(false,CancellationToken.None).GetAwaiter().GetResult();
            Require(savedWorkspace.Workspace.TextResources.Single(r=>r.ResourceName=="текст?").Title=="Через редактор","workspace pair save failed");
            string speech=Path.Combine(output,"speech.pak");byte[] wave="RIFF1234WAVEold"u8.ToArray(),newWave="RIFF1234WAVEnew"u8.ToArray();
            PakArchive.Create(speech,new(){{"s\\Briefing\\pak_regression_entry\\1.wav",wave}});
            workspace=service.AttachSoundsAsync([speech],CancellationToken.None).GetAwaiter().GetResult();
            workspace.TextResources.Single(r=>r.ResourceName=="текст?").Title="Со звуком";
            var catalog=SoundArchiveCatalog.Open([speech]);var resource=catalog.Resources.Single();
            var target=SoundArchiveCopyEditor.ResolveBriefingTarget(catalog,"pak_regression_entry",1,"WAV",resource);
            var asset=new SoundImportAsset("synthetic.wav","synthetic.wav","WAV",newWave,Convert.ToHexString(SHA256.HashData(newWave)));
            var pendingEdit=new PendingSoundEditViewModel("pak_regression_entry",1,"",target,asset);
            var soundSaved=service.SaveSoundCurrentAsync(pendingEdit,false,CancellationToken.None).GetAwaiter().GetResult();
            Require(soundSaved.Workspace?.TextResources.Single(r=>r.ResourceName=="текст?").Title=="Со звуком","sound and text were not published together");
            Require(PakArchive.Read(speech,resource.ResourceName).SequenceEqual(newWave),"sound payload mismatch");
            string shared=Path.Combine(output,"shared.pak");PakArchive.Create(shared,new(){{"a",[1]},{"b",[2]}});string sharedHash=Hash(shared);
            var sharedDoc=ResArchiveDocument.Open(shared);
            byte[] firstImage=ResArchiveWriter.Rebuild(sharedDoc,new Dictionary<int,byte[]>{{sharedDoc.Entries.Single(e=>e.Name=="a").Index,[3]}});
            byte[] secondImage=ResArchiveWriter.Rebuild(sharedDoc,new Dictionary<int,byte[]>{{sharedDoc.Entries.Single(e=>e.Name=="b").Index,[4]}});
            DocumentSaveCoordinator.Commit([new(Guid.NewGuid(),shared,sharedHash,firstImage),new(Guid.NewGuid(),shared,sharedHash,secondImage)],output);
            Require(PakArchive.Read(shared,"a").SequenceEqual(new byte[]{3})&&PakArchive.Read(shared,"b").SequenceEqual(new byte[]{4}),"shared owner lost an edit");
            var standalone=TextsDocument.Open(texts);string standaloneBaseline=standalone.Sha256;
            Require(standalone.Resources.Single(r=>r.Name=="текст?").TrySetTitle("Отдельное сохранение",out _),"standalone edit failed");
            var standaloneSaved=standalone.SaveInPlace();
            Require(TextsDocument.Open(texts).Resources.Single(r=>r.Name=="текст?").Title=="Отдельное сохранение","share-delete guard blocked PAK publication");
            var managed=ManagedResBackupManager.FindLatest(texts,ManagedResKind.Texts)??throw new IOException("PAK backup not discoverable");
            ManagedResBackupManager.RestoreLatestAsync(managed,standaloneSaved.Sha256).GetAwaiter().GetResult();
            Require(Hash(texts)==standaloneBaseline,"managed restore changed PAK bytes");
            // RES remains readable after all PAK operations.
            Require(DatabaseDocument.Open(databaseSource).Sha256==legacy.Sha256,"legacy source changed");
            Console.WriteLine("PASS PAK: database/text edits, CP1251 names, exact copy/restore, tombstones, mixed MOB commit, stale group rejection, recovery and RES regression");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
