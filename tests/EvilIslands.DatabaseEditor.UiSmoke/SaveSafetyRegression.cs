using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class SaveSafetyRegression
{
    private static string _root = "";
    private static string _reference = "";
    private static string? _filter;
    private static readonly List<object> Results = [];
    private static int _failures;

    internal static int Run(string[] args)
    {
        Results.Clear();
        _failures = 0;
        _reference = Path.GetFullPath(args[0]);
        _root = Path.GetFullPath(args[1]);
        _filter = args.Length > 2 ? args[2] : null;
        if (Directory.Exists(_root)) throw new IOException("Use a new probe directory.");
        Directory.CreateDirectory(_root);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Run("pending-audio-after-database-save", AudioStageAfterSave);
        Run("save-copy-companion-name", SaveCopyCompanionName);
        Run("recovery-cross-session-discard", RecoveryCrossSessionDiscard);
        Run("concurrent-file-replacement", ConcurrentFileReplacement);
        Run("texts-concurrent-replacement-and-pair-rollback", TextsReplacementAndRollback);
        Run("pair-save-failure-and-retry", PairSaveFailureAndRetry);
        Run("commit-source-ownership", CommitSourceOwnership);
        Run("commit-publication-failure-and-interloper", CommitFailures);
        if (Results.Count == 0)
        {
            Console.Error.WriteLine("No matching save safety regression: " + _filter);
            _failures++;
        }
        File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Save safety regressions: FAIL={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<Task<object>> probe)
    {
        if (_filter is not null && _filter != name) return;
        try
        {
            Task<object> task = probe();
            var frame = new DispatcherFrame();
            if (!task.IsCompleted)
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
            }
            object result = task.GetAwaiter().GetResult();
            if (result.GetType().GetProperty("defect")?.GetValue(result) is not false)
                throw new InvalidOperationException("Regression requirement failed: " + JsonSerializer.Serialize(result));
            Results.Add(new { name, completed = true, result });
            Console.WriteLine(JsonSerializer.Serialize(Results[^1]));
        }
        catch (Exception exception)
        {
            _failures++;
            Results.Add(new { name, completed = false, error = exception.ToString() });
            Console.WriteLine(JsonSerializer.Serialize(Results[^1]));
        }
    }

    private static string NewDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void EmptyRes(string path)
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 16);
        File.WriteAllBytes(path, data);
        _ = ResArchiveDocument.Open(path);
    }

    private static byte[] Wav(byte sample)
    {
        byte[] bytes = new byte[44 + 8000];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 8000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 8);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 8000);
        bytes.AsSpan(44).Fill(sample);
        return bytes;
    }

    private static (string Database, string Scene, string Texts) CreatePair(string directory)
    {
        string database = Path.Combine(directory, "database.res");
        File.Copy(_reference, database);
        string scene = DatabaseDocument.Open(database).Databases.SelectMany(group => group.Tables)
            .First(table => table.Key == "quests.briefings").Records.First().IdentityValue;
        string empty = Path.Combine(directory, "empty.res");
        EmptyRes(empty);
        TextsDocument texts = TextsDocument.Open(empty);
        texts.AddResource("BRIEFING " + scene.Replace(' ', '_'), "Audit scene", "#phrase Hero 1\r\nAudit phrase\r\n");
        string textsPath = Path.Combine(directory, "texts.res");
        texts.SaveAs(textsPath);
        return (database, scene, textsPath);
    }

    private static async Task<object> AudioStageAfterSave()
    {
        string dir = NewDirectory("audio");
        var pair = CreatePair(dir);
        string originalAudio = Path.Combine(dir, "original.wav");
        string importedAudio = Path.Combine(dir, "imported.wav");
        File.WriteAllBytes(originalAudio, Wav(128));
        File.WriteAllBytes(importedAudio, Wav(129));
        ResFileManagerArchive emptyArchive = await ResFileManagerArchive.OpenAsync(pair.Texts);
        ResImportedFile audio = await ResFileManagerArchive.InspectImportAsync(originalAudio);
        string soundPath = Path.Combine(dir, "speech.res");
        await emptyArchive.SaveCopyAsync([new(ResFileEditKind.AddOrReplace, $"s\\Briefing\\{pair.Scene}\\1.wav", audio)], soundPath);
        string soundHashBefore = Hash(soundPath);
        using var vm = new MainWindowViewModel();
        await vm.OpenPathAsync(pair.Database);
        if (!vm.HasDocument) throw new Exception(vm.NotificationMessage);
        DatabaseTableViewModel table = vm.Databases.SelectMany(group => group.Tables).First(table => table.Key == "quests.briefings");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = table.Records.First();
        vm.SelectedBriefingPhrase = vm.SelectedRecord.BriefingPhrases.First(phrase => phrase.IsPhrase);
        await vm.ImportBriefingAudioPathAsync(importedAudio);
        if (!vm.HasPendingSoundEdit) throw new Exception("Audio did not stage: " + vm.NotificationMessage);
        vm.TextResources.First().Title = "Edited audit title";
        if (!vm.HasChanges) throw new Exception("Text edit was not committed.");
        bool before = vm.HasPendingSoundEdit;
        await vm.SaveCurrentAsync(false);
        Require(vm.HasPendingSoundEdit && vm.HasUnsavedWork && !vm.HasChanges, "Ctrl+S must retain only the unsaved audio.");
        bool pendingAfterSave = vm.HasPendingSoundEdit;
        bool unsavedAfterSave = vm.HasUnsavedWork;
        bool soundUnchangedAfterSave = Hash(soundPath) == soundHashBefore;
        Require(soundUnchangedAfterSave, "Ctrl+S must not write sound before its separate save.");
        Require(vm.SelectedBriefingPhrase?.Number == 1, "Selected phrase must survive DB/texts refresh.");
        Require(TextsDocument.Open(pair.Texts).Resources.First().Title == "Edited audit title", "Edited title must be saved.");
        vm.UndoCommand.Execute(null);
        Require(!vm.HasPendingSoundEdit && !vm.HasChanges, "Undo after save must undo audio, not saved text.");
        vm.RedoCommand.Execute(null);
        Require(vm.HasPendingSoundEdit, "Audio redo must survive Ctrl+S.");
        vm.CancelPendingSoundEditCommand.Execute(null);
        Require(!vm.HasUnsavedWork && Hash(soundPath) == soundHashBefore, "Cancel must leave archive intact.");
        vm.UndoCommand.Execute(null);
        Require(vm.HasPendingSoundEdit, "Cancel must remain reversible.");
        await vm.SavePendingSoundCurrentAsync(false);
        Require(!vm.HasPendingSoundEdit && !vm.HasUnsavedWork && !vm.UndoCommand.CanExecute(null), "Sound save must finish and clear sound history.");
        var savedSound = ResArchiveDocument.Open(soundPath);
        Require(savedSound.TryGetEntry($"s\\Briefing\\{pair.Scene}\\1.wav", out ResEntry? savedEntry) && savedEntry is not null &&
            savedSound.GetResourceBytes(savedEntry).Span.SequenceEqual(Wav(129)), "Saved sound payload differs from import.");
        Require(Hash(soundPath + ".bak") == soundHashBefore, "Sound backup must contain original bytes.");
        await vm.ImportBriefingAudioPathAsync(originalAudio);
        vm.TextResources.First().Title = "Second saved title";
        await vm.SaveCurrentAsync(false);
        Require(vm.HasPendingSoundEdit, "Second save must also retain audio.");
        await vm.SavePendingSoundCopyPathAsync(Path.Combine(dir, "sound-copy.res"));
        Require(!vm.HasUnsavedWork && !vm.HasPendingSoundEdit, "Separate audio copy must be savable after Ctrl+S.");
        await vm.ImportBriefingAudioPathAsync(originalAudio);
        await vm.OpenPathAsync(pair.Database);
        Require(!vm.HasPendingSoundEdit && !vm.UndoCommand.CanExecute(null), "A new session must reset audio/history.");
        return new
        {
            defect = false,
            pendingBefore = before,
            pendingAfterSave,
            unsavedAfterSave,
            soundUnchangedAfterSave,
            pendingAfterSeparateSoundSaveAndNewOpen = vm.HasPendingSoundEdit,
            notification = vm.NotificationMessage,
            titleSaved = TextsDocument.Open(pair.Texts).Resources.First().Title,
        };
    }

    private static async Task<object> SaveCopyCompanionName()
    {
        string dir = NewDirectory("companion");
        var pair = CreatePair(dir);
        var service = new CoreDatabaseWorkspaceService();
        await service.OpenAsync(pair.Database, CancellationToken.None);
        string[] names = ["database.res", "database.edited.res", "balance.res", "Balance.custom.res"];
        foreach (string name in names)
        {
            string output = Path.Combine(dir, name + "-folder");
            Directory.CreateDirectory(output);
            SavePairCopyResult saved = await service.SaveCopyAsync(Path.Combine(output, name), CancellationToken.None);
            var reopened = await new CoreDatabaseWorkspaceService().OpenAsync(saved.Database.TargetPath, CancellationToken.None);
            Require(saved.Texts is not null && reopened.TextsPath == saved.Texts.TargetPath && reopened.TextResourceCount == 1,
                "Saved companion was not found: " + name);
            if (name != "database.res")
            {
                var unrelated = TextsDocument.Open(pair.Texts);
                unrelated.Resources.First().TrySetTitle("Unrelated texts", out _);
                unrelated.SaveAs(Path.Combine(output, "texts.res"));
                reopened = await new CoreDatabaseWorkspaceService().OpenAsync(saved.Database.TargetPath, CancellationToken.None);
                Require(reopened.TextResources.First().Title == "Audit scene" && reopened.TextsPath == saved.Texts!.TargetPath,
                    "Unrelated texts.res must not override saved companion: " + name);
            }
        }
        return new { defect = false, names };
    }

    private static async Task<object> RecoveryCrossSessionDiscard()
    {
        string dir = NewDirectory("recovery");
        var pair = CreatePair(dir);
        string storeRoot = Path.Combine(dir, "store");
        var firstSession = new RecoverySnapshotStore(storeRoot);
        var secondSession = new RecoverySnapshotStore(storeRoot);
        var service = new CoreDatabaseWorkspaceService();
        WorkspaceSnapshot workspace = await service.OpenAsync(pair.Database, CancellationToken.None);
        workspace.TextResources.First().Title = "Unsaved first session";
        RecoverySnapshotDraft draft = firstSession.Prepare(pair.Database);
        SavePairCopyResult copy = await service.SaveCopyAsync(draft.DatabasePath, CancellationToken.None);
        firstSession.Complete(pair.Database, draft, copy, null);
        bool existedBefore = firstSession.FindLatest(pair.Database) is not null;
        // Exact storage call made by Window_Closing for an unmodified second window.
        secondSession.Discard(pair.Database);
        Require(existedBefore && File.Exists(draft.DatabasePath), "Second session deleted first snapshot.");
        bool firstSnapshotExistsAfterSecondSessionClose = File.Exists(draft.DatabasePath);
        RecoverySnapshotDraft inProgress = secondSession.Prepare(pair.Database);
        File.Copy(pair.Database, inProgress.DatabasePath);
        for (int i = 0; i < 3; i++)
        {
            RecoverySnapshotDraft own = firstSession.Prepare(pair.Database);
            SavePairCopyResult ownCopy = await service.SaveCopyAsync(own.DatabasePath, CancellationToken.None);
            firstSession.Complete(pair.Database, own, ownCopy, null);
        }
        Require(File.Exists(inProgress.DatabasePath), "Rotation deleted another session's unfinished snapshot.");
        firstSession.Abandon(inProgress);
        Require(File.Exists(inProgress.DatabasePath), "A foreign draft must not be abandoned.");
        secondSession.Abandon(inProgress);

        await Task.WhenAll(new[] { firstSession, secondSession }.Select(session => Task.Run(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                RecoverySnapshotDraft own = session.Prepare(pair.Database);
                File.Copy(pair.Database, own.DatabasePath);
                string textsPath = Path.Combine(own.DirectoryPath, "texts.res");
                File.Copy(pair.Texts, textsPath);
                session.Complete(pair.Database, own, new SavePairCopyResult(
                    new(own.DatabasePath, new FileInfo(own.DatabasePath).Length, Hash(own.DatabasePath), true, 0),
                    new(textsPath, new FileInfo(textsPath).Length, Hash(textsPath), true, 0)), null);
            }
        })));
        Require(Directory.GetFiles(storeRoot, "recovery.json", SearchOption.AllDirectories).Length == 4,
            "Each concurrent session must keep its own two completed snapshots.");
        Require(firstSession.FindAvailable().Count == 2, "Startup must offer recovery for both sessions.");
        secondSession.Discard(pair.Database);
        Require(firstSession.FindAvailable().Count == 1 && firstSession.FindLatest(pair.Database) is not null,
            "Discard after concurrent rotation must preserve first session.");
        firstSession.Discard(pair.Database);
        Require(firstSession.FindAvailable().Count == 0, "Owner discard must remove its own completed snapshots.");
        return new
        {
            defect = false,
            firstSnapshotExisted = existedBefore,
            firstSnapshotExistsAfterSecondSessionClose,
            firstSessionSourceTitle = TextsDocument.Open(pair.Texts).Resources.First().Title,
        };
    }

    private static Task<object> TextsReplacementAndRollback()
    {
        string dir = NewDirectory("texts-replace");
        var pair = CreatePair(dir);
        string originalHash = Hash(pair.Texts);
        var document = TextsDocument.Open(pair.Texts);
        document.Resources.First().TrySetTitle("Editor version", out _);
        string external = Path.Combine(dir, "external.res");
        var other = TextsDocument.Open(pair.Texts);
        other.Resources.First().TrySetTitle("External version", out _);
        other.SaveAs(external);
        string externalHash = Hash(external);
        int attempts = 0;
        Exception? swapError = null;
        using var done = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(dir, ".texts.res.partial-*");
        watcher.Created += (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) != 1) return;
            try { File.Replace(external, pair.Texts, null); }
            catch (Exception error) { swapError = error; }
            finally { done.Set(); }
        };
        watcher.EnableRaisingEvents = true;
        SaveResult saved = document.SaveInPlace();
        Require(done.Wait(TimeSpan.FromSeconds(3)) && swapError is IOException && Hash(external) == externalHash,
            "Texts replacement must be blocked while source is guarded.");
        watcher.EnableRaisingEvents = false;
        Require(Hash(saved.BackupPath!) == originalHash, "Texts backup differs.");
        TextsDocument.RollbackSavedPair(saved, originalHash);
        Require(Hash(pair.Texts) == originalHash, "Pair rollback must restore original bytes.");
        Require(Directory.GetFiles(dir, "texts.res.bak*").Length <= 2, "Rollback must retain at most two managed backups.");

        document = TextsDocument.Open(pair.Texts);
        document.Resources.First().TrySetTitle("Second editor version", out _);
        saved = document.SaveInPlace();
        File.Replace(external, pair.Texts, null);
        bool refused = false;
        try { TextsDocument.RollbackSavedPair(saved, originalHash); }
        catch (IOException) { refused = true; }
        Require(refused && Hash(pair.Texts) == externalHash, "Rollback overwrote a newer external texts version.");
        refused = false;
        try { document.SaveInPlace(); }
        catch (IOException) { refused = true; }
        Require(refused && Hash(pair.Texts) == externalHash, "Save of stale texts must be refused before write.");
        return Task.FromResult<object>(new { defect = false, attempts, staleRollbackRefused = refused });
    }

    private static async Task<object> PairSaveFailureAndRetry()
    {
        string dir = NewDirectory("pair-retry");
        var pair = CreatePair(dir);
        string databaseHash = Hash(pair.Database);
        string textsHash = Hash(pair.Texts);
        using var vm = new MainWindowViewModel();
        await vm.OpenPathAsync(pair.Database);
        vm.TextResources.First().Title = "Saved after retry";
        var table = vm.Databases.SelectMany(group => group.Tables).First(table => table.Key == "items.materials");
        vm.SelectTableCommand.Execute(table);
        vm.SelectedRecord = table.Records.First();
        // Change a real database field through the ordinary ViewModel editing path.
        var field = vm.SelectedRecord.GetCell(4)!;
        string originalPrice = field.EditorText;
        field.EditorText = originalPrice == "4" ? "5" : "4";
        Require(vm.HasChanges, "Pair changes must be staged.");
        using (var reader = new FileStream(pair.Database, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.SaveCurrentAsync(false);
        }
        Require(Hash(pair.Database) == databaseHash && Hash(pair.Texts) == textsHash,
            "Failure to acquire database must roll back the saved texts.");
        Require(vm.HasChanges && vm.NotificationMessage is not null, "Failed save must retain edits and report the error.");
        await vm.SaveCurrentAsync(false);
        Require(!vm.HasChanges && Hash(pair.Database) != databaseHash &&
            TextsDocument.Open(pair.Texts).Resources.First().Title == "Saved after retry",
            "The same edited session must save successfully after releasing the conflicting reader.");
        Require(Directory.GetFiles(dir, "texts.res.bak*").Length <= 2, "Pair retry exceeded backup retention.");
        return new { defect = false, rollbackAndRetry = true };
    }

    private static Task<object> CommitSourceOwnership()
    {
        string dir = NewDirectory("ownership");
        string source = Path.Combine(dir, "source.res");
        string prepared = Path.Combine(dir, "prepared.res");
        string backup = Path.Combine(dir, "source.res.bak");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(prepared, [4, 5, 6]);
        string originalHash = Hash(source);
        using (var guard = new GuardedFileCommit(source, originalHash))
        {
            bool writeRefused = false;
            bool replaceRefused = false;
            try { using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { writeRefused = true; }
            try { File.Replace(prepared, source, null); }
            catch (IOException) { replaceRefused = true; }
            Require(writeRefused && replaceRefused && Hash(source) == originalHash,
                "The actual source object must deny both writes and replacement until commit.");
            File.WriteAllBytes(backup, [7, 8, 9]);
            bool backupCollisionRefused = false;
            try { guard.Commit(prepared, backup, Hash(prepared)); }
            catch (IOException) { backupCollisionRefused = true; }
            Require(backupCollisionRefused && Hash(source) == originalHash &&
                File.ReadAllBytes(backup).SequenceEqual(new byte[] { 7, 8, 9 }),
                "A backup collision must leave source and foreign backup intact.");
        }
        File.Replace(prepared, source, null);
        string externalHash = Hash(source);
        bool staleRefused = false;
        try { using var stale = new GuardedFileCommit(source, originalHash); }
        catch (IOException) { staleRefused = true; }
        Require(staleRefused && Hash(source) == externalHash, "A pre-existing external replacement must be preserved.");
        return Task.FromResult<object>(new { defect = false, writeAndReplaceRefused = true, staleRefused });
    }

    private static Task<object> CommitFailures()
    {
        string dir = NewDirectory("commit-failure");
        foreach (bool insertForeignFile in new[] { false, true })
        {
            string source = Path.Combine(dir, insertForeignFile + ".res");
            string prepared = source + ".prepared";
            string backup = source + ".bak";
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(prepared, [4, 5, 6]);
            string originalHash = Hash(source);
            bool failed = false;
            using (var guard = new GuardedFileCommit(source, originalHash))
            {
                try
                {
                    guard.Commit(prepared, backup, Hash(prepared), () =>
                    {
                        if (insertForeignFile) File.WriteAllBytes(source, [7, 8, 9]);
                        else throw new IOException("Injected publication failure");
                    });
                }
                catch (IOException) { failed = true; }
            }

            Require(failed, "Injected publication failure must be reported.");
            Require(File.ReadAllBytes(source).SequenceEqual(insertForeignFile ? new byte[] { 7, 8, 9 } : [1, 2, 3]),
                "Publication/rollback overwrote the wrong file.");
            if (insertForeignFile) Require(Hash(backup) == originalHash, "Original must remain in backup when rollback path is occupied.");
            Require(File.ReadAllBytes(prepared).SequenceEqual(new byte[] { 4, 5, 6 }), "Failed commit must preserve prepared data.");
        }
        return Task.FromResult<object>(new { defect = false, rollbackWithoutOverwrite = true });
    }

    private static Task<object> ConcurrentFileReplacement()
    {
        string dir = NewDirectory("replace");
        string source = Path.Combine(dir, "database.res");
        File.Copy(_reference, source);
        DatabaseDocument document = DatabaseDocument.Open(source);
        DatabaseField price = document.Spells.Spells.First(record => record.IdentityValue == "powerhit").Fields.First(field => field.Key == "Price");
        if (!price.TrySetValue("12345", out string? error)) throw new Exception(error);
        string outsideVersion = Path.Combine(dir, "other-version.res");
        DatabaseDocument other = DatabaseDocument.Open(source);
        DatabaseField otherPrice = other.Spells.Spells.First(record => record.IdentityValue == "powerhit").Fields.First(field => field.Key == "Price");
        if (!otherPrice.TrySetValue("54321", out error)) throw new Exception(error);
        other.SaveAs(outsideVersion);
        string outsideHash = Hash(outsideVersion);
        int swapped = 0;
        string? swapError = null;
        using var swappedEvent = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(dir, ".database.res.partial-*");
        watcher.Created += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref swapped, 1, 0) != 0) return;
            try
            {
                File.Replace(outsideVersion, source, Path.Combine(dir, "displaced-original.res"));
            }
            catch (Exception ex) { swapError = ex.ToString(); }
            finally { swappedEvent.Set(); }
        };
        watcher.EnableRaisingEvents = true;
        string? saveError = null;
        try { document.SaveInPlace(); }
        catch (Exception ex) { saveError = ex.Message; }
        swappedEvent.Wait(TimeSpan.FromSeconds(2));
        string currentPrice = DatabaseDocument.Open(source).Spells.Spells.First(record => record.IdentityValue == "powerhit")
            .Fields.First(field => field.Key == "Price").EditorText;
        return Task.FromResult<object>(new
        {
            defect = swapped != 1 || swapError is null || saveError is not null || currentPrice != "12345" || Hash(outsideVersion) != outsideHash,
            swapped,
            swapError,
            saveError,
            currentPrice,
            externalPrice = "54321",
            externalVersionStillCurrent = Hash(source) == outsideHash,
        });
    }
}
