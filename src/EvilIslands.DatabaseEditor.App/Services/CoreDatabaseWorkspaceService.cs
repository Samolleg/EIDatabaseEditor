using System.IO;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class CoreDatabaseWorkspaceService : IDatabaseWorkspaceService
{
    private DatabaseDocument? _document;
    private TextsDocument? _textsDocument;
    private SoundArchiveCatalog? _soundCatalog;
    private IReadOnlyList<DatabaseGroupViewModel> _mappedDatabases = [];
    private readonly Dictionary<TextResource, LocalizationEntryViewModel> _textViewModels = [];
    private readonly Dictionary<LocalizationEntryViewModel, List<RecordRowViewModel>> _textOwners = [];
    internal IReadOnlyList<PreparedFileWrite> PrepareModuleSave()
    {
        var result = new List<PreparedFileWrite>();
        if (_document?.IsModified == true) result.Add(new(Guid.Empty, _document.SourcePath, _document.Sha256, _document.PrepareImage()));
        if (_textsDocument?.IsModified == true) result.Add(new(new Guid("00000000-0000-0000-0000-000000000001"), _textsDocument.SourcePath, _textsDocument.Sha256, _textsDocument.PrepareImage()));
        return result;
    }
    internal WorkspaceSnapshot ReloadModuleSave() => OpenInternalAsync(RequireDocument().SourcePath, _textsDocument?.SourcePath,
        _soundCatalog?.Archives.Select(a => a.SourcePath).ToArray(), CancellationToken.None).GetAwaiter().GetResult();

    public Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken) =>
        OpenInternalAsync(path, explicitTextsPath: null, explicitSoundPaths: null, cancellationToken);

    public Task<WorkspaceSnapshot> AttachTextsAsync(string path, CancellationToken cancellationToken)
    {
        DatabaseDocument document = RequireDocument();
        return OpenInternalAsync(
            document.SourcePath,
            Path.GetFullPath(path),
            _soundCatalog?.Archives.Select(archive => archive.SourcePath).ToArray(),
            cancellationToken);
    }

    public Task<WorkspaceSnapshot> AttachSoundsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        DatabaseDocument document = RequireDocument();
        string[] combinedPaths = (_soundCatalog?.Archives.Select(archive => archive.SourcePath) ?? [])
            .Concat(paths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return OpenInternalAsync(
            document.SourcePath,
            _textsDocument?.SourcePath,
            combinedPaths,
            cancellationToken);
    }

    public async Task<DatabaseComparisonResult> CompareAsync(
        string path,
        CancellationToken cancellationToken)
    {
        DatabaseDocument current = RequireDocument();
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл для сравнения не найден.", fullPath);
        }

        DatabaseDocument compared = await DatabaseDocument.OpenAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
                () => DatabaseComparer.Compare(current, compared, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DatabaseReferenceSearchResult> FindReferencesAsync(
        string tableKey,
        int recordIndex,
        CancellationToken cancellationToken)
    {
        DatabaseDocument current = RequireDocument();
        return Task.Run(
            () => DatabaseReferenceFinder.Find(current, tableKey, recordIndex, cancellationToken),
            cancellationToken);
    }

    private async Task<WorkspaceSnapshot> OpenInternalAsync(
        string path,
        string? explicitTextsPath,
        IReadOnlyList<string>? explicitSoundPaths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл database.res не найден.", fullPath);
        }

        var document = await DatabaseDocument.OpenAsync(fullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var databases = MapDatabases(document.Databases, cancellationToken);
        TextsDocument? textsDocument = null;
        string? textsPath = explicitTextsPath ?? FindCompanionTextsPath(fullPath);
        if (textsPath is not null)
        {
            if (!File.Exists(textsPath))
            {
                throw new FileNotFoundException("Файл texts.res не найден.", textsPath);
            }

            textsDocument = await TextsDocument.OpenAsync(textsPath, cancellationToken).ConfigureAwait(false);
        }

        string[] soundPaths = (explicitSoundPaths ?? FindCompanionSoundPaths(fullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SoundArchiveCatalog? soundCatalog = soundPaths.Length == 0
            ? null
            : await SoundArchiveCatalog.OpenAsync(soundPaths, cancellationToken).ConfigureAwait(false);
        if (explicitSoundPaths is not null)
        {
            SoundArchiveInfo? emptyArchive = soundCatalog?.Archives.FirstOrDefault(archive =>
                explicitSoundPaths.Contains(archive.SourcePath, StringComparer.OrdinalIgnoreCase) &&
                archive.SoundResourceCount == 0);
            if (emptyArchive is not null)
            {
                throw new InvalidOperationException(
                    $"{emptyArchive.FileName} не содержит WAV, MP3 или OGG.");
            }
        }

        var previousTextViewModels = _textViewModels.ToArray();
        var previousTextOwners = _textOwners.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList());
        IReadOnlyList<LocalizationEntryViewModel> textResources;
        try
        {
            _textViewModels.Clear();
            _textOwners.Clear();
            textResources = textsDocument is null
                ? []
                : textsDocument.Resources
                    .Select(resource => GetOrCreateTextViewModel(resource))
                    .ToArray();
            if (textsDocument is not null)
            {
                AddTextOnlyBriefings(databases, textResources);
                AttachRelatedTexts(databases, textResources, cancellationToken);
            }

            if (soundCatalog is not null)
            {
                AttachRelatedSounds(databases, soundCatalog, cancellationToken);
            }

            AttachBriefingPhrases(databases, cancellationToken);
        }
        catch
        {
            _textViewModels.Clear();
            foreach ((TextResource resource, LocalizationEntryViewModel viewModel) in previousTextViewModels)
            {
                _textViewModels.Add(resource, viewModel);
            }

            _textOwners.Clear();
            foreach ((LocalizationEntryViewModel entry, List<RecordRowViewModel> owners) in previousTextOwners)
            {
                _textOwners.Add(entry, owners);
            }

            throw;
        }

        var resources = MapResources(document.Resources, document.Databases, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = MapDiagnostics(document.Diagnostics, databases, cancellationToken);
        var snapshot = new WorkspaceSnapshot
        {
            SourcePath = document.SourcePath,
            FileLength = document.FileLength,
            Sha256 = document.Sha256,
            Resources = resources,
            Databases = databases,
            Diagnostics = diagnostics,
            TextsPath = textsDocument?.SourcePath,
            TextsFileLength = textsDocument?.FileLength ?? 0,
            TextsSha256 = textsDocument?.Sha256,
            TextResourceCount = textResources.Count,
            TextResources = textResources,
            SoundArchives = soundCatalog?.Archives
                .Select(archive => new SoundArchiveViewModel(archive))
                .ToArray() ?? [],
            SoundResourceCount = soundCatalog?.Resources.Count ?? 0,
            ManagedBackups = BuildManagedBackups(document, textsDocument, soundCatalog),
        };
        cancellationToken.ThrowIfCancellationRequested();
        _document = document;
        _textsDocument = textsDocument;
        _soundCatalog = soundCatalog;
        _mappedDatabases = databases;
        return snapshot;
    }

    public Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(
        CancellationToken cancellationToken)
    {
        return ValidateAsync(cancellationToken, false, null);
    }

    public Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(CancellationToken cancellationToken,
        bool includeResources, IProgress<ResourceValidationProgress>? progress)
    {
        DatabaseDocument document = RequireDocument();
        var databases = _mappedDatabases;
        var linked = includeResources ? document.Databases.SelectMany(g => g.Tables).SelectMany(t =>
            t.Records.Select((r, i) => (ModelRecord.Capture(t.Key, r), new DiagnosticLocation(t.ResourceName, t.Key, i, r.IdentityValue)))).ToArray() : [];
        return Task.Run<IReadOnlyList<ValidationMessageViewModel>>(async () =>
        {
            var diagnostics = document.Validate(cancellationToken).ToList();
            if (includeResources) diagnostics.AddRange(await LinkedResourceValidation.ScanAsync(document.SourcePath, linked,
                cancellationToken, progress).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(document, _document)) throw new OperationCanceledException(cancellationToken);
            return MapDiagnostics(diagnostics, databases, cancellationToken);
        }, cancellationToken);
    }

    internal RecordRowViewModel CreateBriefingRecord(DatabaseTableViewModel table, RecordRowViewModel reference)
    {
        if (table.Key != "quests.briefings" || !reference.IsTextOnlyBriefing)
            throw new InvalidOperationException("Выберите текстовый брифинг без записи БД.");
        var core = RequireDocument().AppendBriefing(reference.DisplayName);
        var row = new RecordRowViewModel(core, table.Key, table.Title, table.IsEditable)
        { TextOnlyBriefingReference = reference };
        RefreshRelatedTexts(row);
        RefreshRelatedSounds(row);
        return row;
    }

    public RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(template);
        if (!table.CanAppendRecords || !string.Equals(table.Key, template.TableKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Выбранная строка не может быть образцом для этой таблицы.");
        }

        if (template.IsTextOnlyBriefing)
            throw new InvalidOperationException("Этот брифинг находится только в texts.res. Для новой записи БД выберите образец из quests.qdb.");
        DatabaseRecord record = RequireDocument().AppendRecordClone(template.CoreRecord);
        var viewModel = new RecordRowViewModel(record, table.Key, table.Title, table.IsEditable);
        RefreshRelatedTexts(viewModel);
        RefreshRelatedSounds(viewModel);
        return viewModel;
    }

    public void RefreshRelatedTexts(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (LocalizationEntryViewModel previous in record.RelatedTexts)
        {
            if (_textOwners.TryGetValue(previous, out List<RecordRowViewModel>? owners))
            {
                owners.Remove(record);
            }
        }

        if (_textsDocument is null)
        {
            record.SetRelatedTexts([]);
            return;
        }

        string[] expectedKeys = BuildExpectedTextKeys(record, _mappedDatabases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var related = new List<LocalizationEntryViewModel>();
        foreach (string key in expectedKeys)
        {
            if (_textsDocument.TryGetResource(key, out TextResource? resource) && resource is not null)
            {
                related.Add(GetOrCreateTextViewModel(resource));
            }
        }

        LocalizationEntryViewModel[] distinct = related
            .Distinct()
            .OrderBy(item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        record.SetRelatedTexts(distinct, expectedKeys);
        foreach (LocalizationEntryViewModel entry in distinct)
        {
            AddTextOwner(entry, record);
        }
    }

    public void RefreshRelatedSounds(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_soundCatalog is null)
        {
            record.SetRelatedSounds([]);
            record.SetBriefingPhrases(MapBriefingPhrases(record, []));
            return;
        }

        IReadOnlyList<SoundLinkViewModel> sounds = MapSoundLinks(
            record,
            DatabaseSoundLinkResolver.Resolve(record.TableKey, record.CoreRecord, _soundCatalog));
        record.SetRelatedSounds(sounds);
        record.SetBriefingPhrases(MapBriefingPhrases(record, sounds));
    }

    internal RecordRowViewModel RebindHistoryRecord(DatabaseTableViewModel table, RecordRowViewModel previous, bool present)
    {
        var core = RequireDocument().RebindHistoryRecord(previous.CoreRecord, present);
        var row = present ? table.Records.Single(r => ReferenceEquals(r.CoreRecord, core))
            : new RecordRowViewModel(core, table.Key, table.Title, table.IsEditable);
        row.TextOnlyBriefingReference = previous.TextOnlyBriefingReference;
        RefreshRelatedTexts(row);
        RefreshRelatedSounds(row);
        return row;
    }

    internal LocalizationEntryViewModel RebindHistoryText(LocalizationEntryViewModel previous, bool present) =>
        GetOrCreateTextViewModel(RequireTextsDocument().RebindHistoryResource(previous.CoreResource, present));

    public void RemoveAddedRecord(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireDocument().RemoveAddedRecord(record.CoreRecord);
    }

    public void RestoreAddedRecord(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireDocument().RestoreAddedRecord(record.CoreRecord);
    }

    public IReadOnlyList<LocalizationEntryViewModel> AddMissingTexts(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        TextsDocument texts = RequireTextsDocument();
        string[] expectedKeys = BuildExpectedTextKeys(record).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expectedKeys.Length == 0)
        {
            throw new InvalidOperationException("Для этой таблицы пока не подтверждено правило создания текстового ключа.");
        }

        var added = new List<LocalizationEntryViewModel>();
        foreach (string key in expectedKeys)
        {
            if (texts.TryGetResource(key, out TextResource? existing) && existing is not null)
            {
                continue;
            }

            TextResource resource = texts.AddResource(key);
            LocalizationEntryViewModel viewModel = GetOrCreateTextViewModel(resource);
            added.Add(viewModel);
            AddTextOwner(viewModel, record);
        }

        if (added.Count == 0)
        {
            throw new InvalidOperationException("Все ожидаемые записи texts.res для выбранной строки уже существуют.");
        }

        record.SetRelatedTexts(record.RelatedTexts
            .Concat(added)
            .Distinct()
            .OrderBy(item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        return added;
    }

    public void RemoveAddedText(LocalizationEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireTextsDocument().RemoveAddedResource(entry.CoreResource);
        if (_textOwners.TryGetValue(entry, out List<RecordRowViewModel>? owners))
        {
            foreach (RecordRowViewModel owner in owners)
            {
                owner.SetRelatedTexts(owner.RelatedTexts.Where(item => !ReferenceEquals(item, entry)).ToArray());
            }
        }
    }

    public void RestoreAddedText(LocalizationEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireTextsDocument().RestoreAddedResource(entry.CoreResource);
        if (_textOwners.TryGetValue(entry, out List<RecordRowViewModel>? owners))
        {
            foreach (RecordRowViewModel owner in owners)
            {
                owner.SetRelatedTexts(owner.RelatedTexts
                    .Append(entry)
                    .Distinct()
                    .OrderBy(item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            }
        }
    }

    public async Task<PendingSoundEditViewModel> PrepareBriefingSoundImportAsync(
        RecordRowViewModel record,
        BriefingPhraseViewModel phrase,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(phrase);
        if (!record.TableKey.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase) || !phrase.IsPhrase)
        {
            throw new InvalidOperationException("Импорт звука доступен только для реплики Брифинга.");
        }

        SoundArchiveCatalog catalog = _soundCatalog
            ?? throw new InvalidOperationException("Сначала подключите speech.res или другой звуковой RES.");
        SoundImportAsset asset = await SoundArchiveCopyEditor
            .LoadImportAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        string sceneKey = string.IsNullOrWhiteSpace(record.CoreRecord.IdentityValue)
            ? record.CoreRecord.Name
            : record.CoreRecord.IdentityValue;
        BriefingSoundImportTarget target = SoundArchiveCopyEditor.ResolveBriefingTarget(
            catalog,
            sceneKey,
            phrase.Number,
            asset.Format,
            phrase.PrimarySound?.Resource);
        return new PendingSoundEditViewModel(sceneKey, phrase.Number, phrase.Speaker, target, asset);
    }

    public Task<SoundArchiveCopyResult> SaveSoundCopyAsync(
        PendingSoundEditViewModel edit,
        string targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return SoundArchiveCopyEditor.SaveCopyAsync(edit.Target, edit.Asset, targetPath, cancellationToken);
    }

    public async Task<SaveSoundCurrentResult> SaveSoundCurrentAsync(
        PendingSoundEditViewModel edit,
        bool allowGameInstallationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SoundArchiveCatalog currentCatalog = _soundCatalog
            ?? throw new InvalidOperationException("Сначала подключите звуковой RES.");
        string[] soundPaths = currentCatalog.Archives
            .Select(archive => archive.SourcePath)
            .ToArray();
        if(PakArchive.IsPak(edit.Target.Archive.SourcePath)) {
            var soundWrite=SoundArchiveCopyEditor.PreparePakImage(edit.Target,edit.Asset);
            var writes=PrepareModuleSave().Append(soundWrite).ToList();
            var relatedDatabase=RequireDocument();
            if(!writes.Any(w=>w.Path.Equals(relatedDatabase.SourcePath,StringComparison.OrdinalIgnoreCase)))writes.Add(new(Guid.NewGuid(),relatedDatabase.SourcePath,relatedDatabase.Sha256,relatedDatabase.PrepareImage()));
            if(_textsDocument is not null&&!writes.Any(w=>w.Path.Equals(_textsDocument.SourcePath,StringComparison.OrdinalIgnoreCase)))writes.Add(new(Guid.NewGuid(),_textsDocument.SourcePath,_textsDocument.Sha256,_textsDocument.PrepareImage()));
            string backup=soundWrite.Path+".bak."+Guid.NewGuid().ToString("N");
            byte[] original=File.ReadAllBytes(soundWrite.Path);
            if(!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).Equals(soundWrite.ExpectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Sound archive changed during preparation.");
            writes.Add(new(Guid.NewGuid(),backup,"",original));
            DocumentSaveCoordinator.Commit(writes,Path.GetDirectoryName(soundWrite.Path)!,allowGameInstallationPath);
            var workspace=await OpenInternalAsync(RequireDocument().SourcePath,_textsDocument?.SourcePath,soundPaths,CancellationToken.None).ConfigureAwait(false);
            using var saved=PakArchive.Open(soundWrite.Path);
            var savedSound=new SoundArchiveCopyResult(soundWrite.Path,saved.Length,saved.Sha256,edit.ResourceName,edit.IsReplacement,saved.Entries.Count,true,backup);
            return new(savedSound,workspace.SoundArchives,workspace.SoundResourceCount,workspace.ManagedBackups,workspace);
        }
        SoundArchiveCopyResult result = await SoundArchiveCopyEditor.SaveInPlaceAsync(
            edit.Target,
            edit.Asset,
            allowGameInstallationPath,
            cancellationToken).ConfigureAwait(false);

        // The archive has already been replaced at this point. Finish refreshing the
        // in-memory view even if the caller cancels after the irreversible write.
        SoundArchiveCatalog refreshedCatalog = await SoundArchiveCatalog
            .OpenAsync(soundPaths, CancellationToken.None)
            .ConfigureAwait(false);
        AttachRelatedSounds(_mappedDatabases, refreshedCatalog, CancellationToken.None);
        AttachBriefingPhrases(_mappedDatabases, CancellationToken.None);
        _soundCatalog = refreshedCatalog;
        return new SaveSoundCurrentResult(
            result,
            refreshedCatalog.Archives.Select(archive => new SoundArchiveViewModel(archive)).ToArray(),
            refreshedCatalog.Resources.Count,
            BuildManagedBackups(RequireDocument(), _textsDocument, refreshedCatalog));
    }

    public async Task<SaveCurrentResult> SaveCurrentAsync(
        bool allowGameInstallationPath,
        CancellationToken cancellationToken, bool createBackup = true)
    {
        var document = RequireDocument();
        TextsDocument? texts = _textsDocument;
        SaveResult? textsResult = null;
        SaveResult? databaseResult = null;
        if(PakArchive.IsPak(document.SourcePath)||texts is not null&&PakArchive.IsPak(texts.SourcePath)) {
            var writes=PrepareModuleSave();
            var backups=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(createBackup)foreach(var write in writes.GroupBy(w=>w.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First())){
                byte[] original=File.ReadAllBytes(write.Path);
                if(!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).Equals(write.ExpectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Archive changed since opening.");
                string backup=write.Path+".bak."+Guid.NewGuid().ToString("N");File.WriteAllBytes(backup,original);backups.Add(write.Path,backup);
            }
            DocumentSaveCoordinator.Commit(writes,Path.GetDirectoryName(document.SourcePath)!,allowGameInstallationPath);
            var reloaded=await OpenInternalAsync(document.SourcePath,texts?.SourcePath,_soundCatalog?.Archives.Select(a=>a.SourcePath).ToArray(),CancellationToken.None).ConfigureAwait(false);
            var current=RequireDocument();var databaseSaved=new SaveResult(current.SourcePath,current.FileLength,current.Sha256,true,current.Diagnostics,backups.GetValueOrDefault(current.SourcePath));
            SaveResult? textsSaved=_textsDocument is null?null:new SaveResult(_textsDocument.SourcePath,_textsDocument.FileLength,_textsDocument.Sha256,true,[],backups.GetValueOrDefault(_textsDocument.SourcePath));
            return new SaveCurrentResult(MapSaveResult(databaseSaved),reloaded,databaseSaved.BackupPath,textsSaved is null?null:MapSaveResult(textsSaved),textsSaved?.BackupPath);
        }
        try
        {
            if (texts?.IsModified is true)
            {
                textsResult = await texts.SaveInPlaceAsync(
                    allowGameInstallationPath,
                    cancellationToken, createBackup, keepRollbackForPair: true).ConfigureAwait(false);
            }

            if (document.IsModified)
            {
                databaseResult = await document.SaveInPlaceAsync(
                    allowGameInstallationPath,
                    cancellationToken, createBackup).ConfigureAwait(false);
            }
        }
        catch
        {
            if (textsResult?.BackupPath is { Length: > 0 })
            {
                TextsDocument.RollbackSavedPair(textsResult, texts!.Sha256);
            }

            throw;
        }

        if (!createBackup && textsResult?.BackupPath is { Length: > 0 } temporaryRollback)
        {
            File.Delete(temporaryRollback);
            textsResult = textsResult with { BackupPath = null };
        }
        databaseResult ??= new SaveResult(
            document.SourcePath,
            document.FileLength,
            document.Sha256,
            true,
            document.Diagnostics,
            null);
        string[] soundPaths = _soundCatalog?.Archives
            .Select(archive => archive.SourcePath)
            .ToArray() ?? [];
        var workspace = await OpenInternalAsync(
            databaseResult.TargetPath,
            texts?.SourcePath,
            soundPaths,
            CancellationToken.None).ConfigureAwait(false);
        return new SaveCurrentResult(
            MapSaveResult(databaseResult),
            workspace,
            databaseResult.BackupPath,
            textsResult is null ? null : MapSaveResult(textsResult),
            textsResult?.BackupPath);
    }

    public async Task<RestoreManagedBackupResult> RestoreManagedBackupAsync(
        ManagedBackupViewModel backup,
        bool allowGameInstallationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ManagedResBackupInfo backupInfo = backup.Backup
            ?? throw new InvalidOperationException("Для выбранного RES нет резервной копии.");
        if (backupInfo.Kind != backup.Kind ||
            !backupInfo.TargetPath.Equals(backup.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Сведения о резервной копии не относятся к выбранному RES.");
        }

        DatabaseDocument document = RequireDocument();
        string databasePath = document.SourcePath;
        string? textsPath = _textsDocument?.SourcePath;
        string[] soundPaths = _soundCatalog?.Archives
            .Select(archive => archive.SourcePath)
            .ToArray() ?? [];

        string? expectedSha256 = null;
        long? expectedLength = null;
        DateTime? expectedLastWriteTimeUtc = null;
        switch (backup.Kind)
        {
            case ManagedResKind.Database when backup.TargetPath.Equals(
                databasePath,
                StringComparison.OrdinalIgnoreCase):
                expectedSha256 = document.Sha256;
                break;
            case ManagedResKind.Texts when _textsDocument is not null && backup.TargetPath.Equals(
                _textsDocument.SourcePath,
                StringComparison.OrdinalIgnoreCase):
                expectedSha256 = _textsDocument.Sha256;
                break;
            case ManagedResKind.Sound:
                SoundArchiveInfo archive = _soundCatalog?.Archives.FirstOrDefault(candidate =>
                    candidate.SourcePath.Equals(backup.TargetPath, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Выбранный звуковой RES больше не подключён.");
                expectedLength = archive.FileLength;
                expectedLastWriteTimeUtc = archive.LastWriteTimeUtc;
                break;
            default:
                throw new InvalidOperationException("Выбранная резервная копия не относится к текущей рабочей области.");
        }

        ManagedResRestoreResult restore = await ManagedResBackupManager.RestoreLatestAsync(
            backupInfo,
            expectedSha256,
            expectedLength,
            expectedLastWriteTimeUtc,
            allowGameInstallationPath,
            cancellationToken).ConfigureAwait(false);

        // The target file has already been replaced. Always finish rebuilding the
        // in-memory workspace so the UI cannot remain attached to the old bytes.
        WorkspaceSnapshot workspace = await OpenInternalAsync(
            databasePath,
            textsPath,
            soundPaths,
            CancellationToken.None).ConfigureAwait(false);
        return new RestoreManagedBackupResult(restore, workspace);
    }

    private static IReadOnlyList<ManagedBackupViewModel> BuildManagedBackups(
        DatabaseDocument document,
        TextsDocument? textsDocument,
        SoundArchiveCatalog? soundCatalog)
    {
        var backups = new List<ManagedBackupViewModel>
        {
            CreateManagedBackup(ManagedResKind.Database, document.SourcePath),
        };
        if (textsDocument is not null)
        {
            backups.Add(CreateManagedBackup(ManagedResKind.Texts, textsDocument.SourcePath));
        }

        if (soundCatalog is not null)
        {
            backups.AddRange(soundCatalog.Archives.Select(archive =>
                CreateManagedBackup(ManagedResKind.Sound, archive.SourcePath)));
        }

        return backups;
    }

    private static ManagedBackupViewModel CreateManagedBackup(ManagedResKind kind, string sourcePath) =>
        new(kind, sourcePath, ManagedResBackupManager.FindLatest(sourcePath, kind));

    public async Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken)
    {
        var document = RequireDocument();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string databaseTarget = Path.GetFullPath(targetPath);
        if(PakArchive.IsPak(databaseTarget)) {
            string? textTarget=_textsDocument is null?null:DeriveTextsCopyPath(databaseTarget);
            var images=new List<(string Path,string? ExpectedHash,byte[] Bytes)>{(databaseTarget,null,PakArchive.ConvertResImage(document.PrepareImage()))};
            if(textTarget is not null)images.Add((textTarget,null,PakArchive.ConvertResImage(_textsDocument!.PrepareImage())));
            foreach(var image in images)if(DatabaseSafetyPolicy.IsLikelyGameInstallationPath(image.Path)||DatabaseSafetyPolicy.ContainsReparsePoint(image.Path))throw new IOException("Save copy requires an offline regular path.");
            PakArchive.PublishImages(images);
            using var databasePak=PakArchive.Open(databaseTarget);
            var saved=new SaveResult(databaseTarget,databasePak.Length,databasePak.Sha256,true,document.Diagnostics);
            SaveResult? savedText=null;if(textTarget is not null){using var textPak=PakArchive.Open(textTarget);savedText=new SaveResult(textTarget,textPak.Length,textPak.Sha256,true,[]);}
            return new SavePairCopyResult(MapSaveResult(saved),savedText is null?null:MapSaveResult(savedText));
        }
        SaveResult databaseResult = await document.SaveAsAsync(databaseTarget, cancellationToken).ConfigureAwait(false);
        SaveResult? textsResult = null;
        try
        {
            if (_textsDocument is not null)
            {
                string textsTarget = DeriveTextsCopyPath(databaseTarget);
                textsResult = await _textsDocument.SaveAsAsync(textsTarget, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (File.Exists(databaseTarget))
            {
                File.Delete(databaseTarget);
            }

            throw;
        }

        return new SavePairCopyResult(
            MapSaveResult(databaseResult),
            textsResult is null ? null : MapSaveResult(textsResult));
    }

    private LocalizationEntryViewModel GetOrCreateTextViewModel(TextResource resource)
    {
        if (_textViewModels.TryGetValue(resource, out LocalizationEntryViewModel? existing))
        {
            return existing;
        }

        var result = new LocalizationEntryViewModel(resource);
        _textViewModels.Add(resource, result);
        return result;
    }

    private void AttachRelatedTexts(
        IReadOnlyList<DatabaseGroupViewModel> databases,
        IReadOnlyList<LocalizationEntryViewModel> texts,
        CancellationToken cancellationToken)
    {
        var byName = texts.ToDictionary(item => item.ResourceName, StringComparer.OrdinalIgnoreCase);
        foreach (DatabaseTableViewModel table in databases.SelectMany(group => group.Tables))
        {
            foreach (RecordRowViewModel record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] expectedKeys = BuildExpectedTextKeys(record, databases)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var related = new List<LocalizationEntryViewModel>();
                foreach (string key in expectedKeys)
                {
                    if (byName.TryGetValue(key, out LocalizationEntryViewModel? item))
                    {
                        related.Add(item);
                    }
                }

                LocalizationEntryViewModel[] distinct = related
                    .Distinct()
                    .OrderBy(item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                record.SetRelatedTexts(distinct, expectedKeys);
                foreach (LocalizationEntryViewModel item in distinct)
                {
                    AddTextOwner(item, record);
                }
            }
        }
    }

    private static void AttachRelatedSounds(
        IReadOnlyList<DatabaseGroupViewModel> databases,
        SoundArchiveCatalog catalog,
        CancellationToken cancellationToken)
    {
        foreach (DatabaseTableViewModel table in databases.SelectMany(group => group.Tables))
        {
            foreach (RecordRowViewModel record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record.SetRelatedSounds(MapSoundLinks(
                    record,
                    DatabaseSoundLinkResolver.Resolve(table.Key, record.CoreRecord, catalog)));
            }
        }
    }

    private static void AttachBriefingPhrases(
        IReadOnlyList<DatabaseGroupViewModel> databases,
        CancellationToken cancellationToken)
    {
        DatabaseTableViewModel? table = databases
            .SelectMany(group => group.Tables)
            .FirstOrDefault(candidate => candidate.Key.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            return;
        }

        foreach (RecordRowViewModel record in table.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.SetBriefingPhrases(MapBriefingPhrases(record, record.RelatedSounds));
        }
    }

    private static IReadOnlyList<SoundLinkViewModel> MapSoundLinks(
        RecordRowViewModel record,
        IReadOnlyList<DatabaseSoundLink> links)
    {
        if (!record.TableKey.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase))
        {
            return links.Select(link => new SoundLinkViewModel(link)).ToArray();
        }

        LocalizationEntryViewModel? briefingText = record.RelatedTexts.FirstOrDefault(text =>
            text.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase));
        IReadOnlyDictionary<int, BriefingPhrase> phrases = BriefingTranscriptParser
            .Parse(briefingText?.Description)
            .GroupBy(phrase => phrase.Number)
            .ToDictionary(group => group.Key, group => group.First());
        return links.Select(link =>
        {
            if (link.Resource is not null &&
                BriefingTranscriptParser.TryGetClipIdentity(
                    link.Resource.ResourceName,
                    out int phraseNumber,
                    out string clipId) &&
                phrases.TryGetValue(phraseNumber, out BriefingPhrase? phrase))
            {
                return new SoundLinkViewModel(link, phrase, clipId);
            }

            return new SoundLinkViewModel(link);
        }).ToArray();
    }

    private static IReadOnlyList<BriefingPhraseViewModel> MapBriefingPhrases(
        RecordRowViewModel record,
        IReadOnlyList<SoundLinkViewModel> sounds)
    {
        if (!record.TableKey.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        LocalizationEntryViewModel? briefingText = record.RelatedTexts.FirstOrDefault(text =>
            text.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<BriefingTimelineEvent> timeline = BriefingTranscriptParser.ParseTimeline(briefingText?.Description);
        IReadOnlyDictionary<int, IReadOnlyList<string>> issuesByOrdinal = BriefingTranscriptParser
            .ValidateTimeline(briefingText?.Description)
            .GroupBy(issue => issue.EventOrdinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(issue => issue.Message).ToArray());
        var phraseNumbers = timeline
            .Where(briefingEvent => briefingEvent.IsPhrase)
            .Select(briefingEvent => briefingEvent.PhraseNumber)
            .ToHashSet();
        var soundsByNumber = sounds
            .Where(sound => sound.PhraseNumber is not null)
            .GroupBy(sound => sound.PhraseNumber!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SoundLinkViewModel>)group.ToArray());

        var result = timeline
            .Select(briefingEvent =>
            {
                soundsByNumber.TryGetValue(
                    briefingEvent.PhraseNumber,
                    out IReadOnlyList<SoundLinkViewModel>? phraseSounds);
                issuesByOrdinal.TryGetValue(
                    briefingEvent.Ordinal,
                    out IReadOnlyList<string>? scriptProblems);
                return new BriefingPhraseViewModel(
                    briefingEvent,
                    briefingEvent.IsPhrase ? phraseSounds ?? [] : [],
                    scriptProblems);
            })
            .ToList();

        int nextOrdinal = timeline.Count;
        foreach (int number in soundsByNumber.Keys.Where(number => !phraseNumbers.Contains(number)).Order())
        {
            result.Add(new BriefingPhraseViewModel(
                new BriefingTimelineEvent(
                    nextOrdinal++,
                    BriefingEventKind.Phrase,
                    "phrase",
                    string.Empty,
                    number,
                    string.Empty,
                    string.Empty),
                soundsByNumber[number],
                isTimelineEvent: false));
        }

        return result;
    }

    private void AddTextOwner(LocalizationEntryViewModel entry, RecordRowViewModel owner)
    {
        if (!_textOwners.TryGetValue(entry, out List<RecordRowViewModel>? owners))
        {
            owners = [];
            _textOwners.Add(entry, owners);
        }

        if (!owners.Contains(owner))
        {
            owners.Add(owner);
        }
    }

    private IEnumerable<string> BuildExpectedTextKeys(
        RecordRowViewModel record,
        IReadOnlyList<DatabaseGroupViewModel>? databases = null)
    {
        string[] exact = BuildExactTextKeys(record).ToArray();
        if (exact.Length > 0 && record.TableKey is not (
                "items.weapons" or "items.armors" or "items.quick_items"))
        {
            return exact;
        }

        string shape = NormalizeKeyPart(GetFieldText(record, 0));
        string prefix = record.TableKey switch
        {
            "items.weapons" => "WEAPON",
            "items.armors" => "ARMOR",
            "items.quick_items" => "QITEM",
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(shape))
        {
            return [];
        }

        string materialType = GetFieldText(record, 3);
        IReadOnlyList<DatabaseGroupViewModel> databaseSource = databases ?? _mappedDatabases;
        DatabaseTableViewModel? materials = databaseSource
            .SelectMany(group => group.Tables)
            .FirstOrDefault(table => table.Key == "items.materials");
        string[] materialNames = materials?.Records
            .Where(material => string.Equals(
                GetFieldText(material, 1),
                materialType,
                StringComparison.OrdinalIgnoreCase))
            .Select(material => NormalizeKeyPart(GetFieldText(material, 0)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (record.TableKey == "items.quick_items" &&
            (materialNames.Length == 0 || materialType.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            return [$"{prefix} {shape}"];
        }

        IEnumerable<string> keys = materialNames.Select(material => $"{prefix} {shape} {material}");
        return record.TableKey is "items.weapons" or "items.armors"
            ? keys.Append($"INSTR {shape}")
            : keys;
    }

    private static void AddTextOnlyBriefings(
        IReadOnlyList<DatabaseGroupViewModel> databases,
        IReadOnlyList<LocalizationEntryViewModel> texts)
    {
        var table = databases.SelectMany(group => group.Tables).FirstOrDefault(t => t.Key == "quests.briefings");
        if (table is null) return;
        var names = table.Records.Select(r => NormalizeKeyPart(GetFieldText(r, 0))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int referenceIndex = 0;
        foreach (var text in texts.Where(t => t.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(t => t.ResourceName, StringComparer.OrdinalIgnoreCase))
        {
            string name = text.ResourceName[(text.Prefix.Length + 1)..].Trim();
            if (name.Length == 0 || !names.Add(name)) continue;
            var record = DatabaseRecord.CreateBriefingTextReference(name, referenceIndex++);
            table.Records.Add(new RecordRowViewModel(record, table.Key, table.Title, tableIsEditable: false));
        }
    }

    private static IEnumerable<string> BuildExactTextKeys(RecordRowViewModel record)
    {
        string name = NormalizeKeyPart(GetFieldText(record, 0));
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        return record.TableKey switch
        {
            "items.materials" => [$"MATERIAL {name}", $"MATSHORT {name}"],
            "items.quick_items" => [$"QITEM {name}"],
            "items.quest_items" => [$"QUESTITEM {name}"],
            "items.loot_items" => [$"LITEM {name}", $"INSTR {name}"],
            "spells.prototypes" => [$"SPELL {NormalizeKeyPart(GetFieldText(record, 1))}"],
            "spells.modifiers" => [$"MODIFIER {NormalizeKeyPart(GetFieldText(record, 1))}"],
            "units.prototypes" or "units.npcs" => [$"UNIT {name}"],
            "perks.skills" or "perks.perks" => [$"PERK {name}"],
            "quests.quests" => [$"QUEST {name}"],
            "quests.briefings" => [$"BRIEFING {name}"],
            _ => [],
        };
    }

    private static string GetFieldText(RecordRowViewModel record, int fieldId) =>
        record.CoreRecord.Fields.FirstOrDefault(field => field.Id == fieldId)?.EditorText ?? string.Empty;

    private static string NormalizeKeyPart(string value) => value.Trim().Replace(' ', '_');

    private static string? FindCompanionTextsPath(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string[] candidates =
        [
            GetTextsCompanionPath(databasePath),
            Path.Combine(directory, "texts.res"),
        ];
        return candidates.Select(ResourceArchivePaths.Resolve).FirstOrDefault(File.Exists);
    }

    private static string[] FindCompanionSoundPaths(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string[] knownNames = ["JmvSFX.res", "sfx.res", "speech.res", "atamannala.res"];
        return ResourceArchivePaths.Order(knownNames.Select(name => Path.Combine(directory,name))).Where(File.Exists).ToArray();
    }

    private static string GetTextsCompanionPath(string databaseTarget)
    {
        string directory = Path.GetDirectoryName(databaseTarget)!;
        string fileName = Path.GetFileName(databaseTarget);
        string textsName = fileName.StartsWith("database", StringComparison.OrdinalIgnoreCase)
            ? "texts" + fileName["database".Length..]
            : "texts." + fileName;
        return Path.Combine(directory, textsName);
    }

    private static string DeriveTextsCopyPath(string databaseTarget)
    {
        string result = GetTextsCompanionPath(databaseTarget);
        if (File.Exists(result))
        {
            throw new IOException($"Связанная копия texts.res уже существует: {result}");
        }

        return result;
    }

    private static SaveCopyResult MapSaveResult(SaveResult result) => new(
        result.TargetPath,
        result.FileLength,
        result.Sha256,
        result.ReopenedSuccessfully,
        result.Diagnostics.Count);

    private static IReadOnlyList<DatabaseGroupViewModel> MapDatabases(
        IReadOnlyList<DatabaseGroup> groups,
        CancellationToken cancellationToken)
    {
        var result = new DatabaseGroupViewModel[groups.Count];
        for (var groupIndex = 0; groupIndex < result.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[groupIndex];
            var tables = new DatabaseTableViewModel[group.Tables.Count];
            for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
            {
                var table = group.Tables[tableIndex];
                var records = new RecordRowViewModel[table.Records.Count];
                for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
                {
                    if ((recordIndex & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    records[recordIndex] = new RecordRowViewModel(
                        table.Records[recordIndex],
                        table.Key,
                        table.Title,
                        table.IsEditable);
                }

                tables[tableIndex] = new DatabaseTableViewModel(
                    group.Key,
                    group.Title,
                    table.Key,
                    table.Title,
                    table.ResourceName,
                    table.SectionId,
                    table.SupportState,
                    table.CanAppendRecords,
                    records);
            }

            result[groupIndex] = new DatabaseGroupViewModel(
                group.Key,
                group.Title,
                group.ResourceName,
                group.SupportState,
                tables);
        }

        return result;
    }

    private static IReadOnlyList<ResourceRowViewModel> MapResources(
        IReadOnlyList<DatabaseResourceDescriptor> resources,
        IReadOnlyList<DatabaseGroup> databases,
        CancellationToken cancellationToken)
    {
        var supportByResource = databases.ToDictionary(
            database => database.ResourceName,
            database => database.SupportState,
            StringComparer.OrdinalIgnoreCase);
        var result = new ResourceRowViewModel[resources.Count];
        for (var index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = resources[index];
            var supportState = supportByResource.TryGetValue(resource.Name, out var mappedState)
                ? mappedState
                : DatabaseSupportState.Unavailable;
            result[index] = new ResourceRowViewModel(
                resource.Index,
                resource.Name,
                resource.Kind,
                resource.Offset,
                resource.Length,
                supportState switch
                {
                    DatabaseSupportState.Editable => "Распознан · редактирование",
                    DatabaseSupportState.ReadOnly => "Распознан · просмотр",
                    _ => "Сохранён как есть",
                });
        }

        return result;
    }

    private static IReadOnlyList<ValidationMessageViewModel> MapDiagnostics(
        IEnumerable<ValidationDiagnostic> diagnostics,
        IReadOnlyList<DatabaseGroupViewModel> databases,
        CancellationToken cancellationToken = default)
    {
        var tables = databases
            .SelectMany(group => group.Tables)
            .GroupBy(table => table.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<ValidationMessageViewModel>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = diagnostic.Path ?? diagnostic.Location?.ResourceName ?? "—";
            DatabaseTableViewModel? table = null;
            if (!string.IsNullOrWhiteSpace(diagnostic.Location?.TableKey))
            {
                tables.TryGetValue(diagnostic.Location.TableKey, out table);
            }

            string tableTitle = table?.Title ?? diagnostic.Location?.TableKey ?? "Весь документ";
            string recordTitle = diagnostic.Location is { RecordIndex: >= 0 } location
                ? string.IsNullOrWhiteSpace(location.RecordName)
                    ? $"Строка {location.RecordIndex + 1:N0}"
                    : location.RecordName
                : "—";
            string fieldTitle = diagnostic.Location?.FieldId is int fieldId
                ? table?.Columns.FirstOrDefault(column =>
                    column.FieldId == fieldId && column.Projection is null)?.Title ??
                  diagnostic.Location.FieldKey ?? $"Поле {fieldId}"
                : "—";
            result.Add(new ValidationMessageViewModel(
                diagnostic.Severity.ToString(),
                FormatSeverity(diagnostic.Severity),
                diagnostic.Domain.ToString(),
                FormatDomain(diagnostic.Domain),
                diagnostic.Code,
                diagnostic.Message,
                FormatPlace(diagnostic, tableTitle, recordTitle, fieldTitle),
                FormatDetail(diagnostic, path),
                path,
                diagnostic.Offset,
                diagnostic.IsBlocking,
                diagnostic.Location?.ResourceName,
                diagnostic.Location?.TableKey,
                diagnostic.Location?.RecordIndex,
                diagnostic.Location?.FieldId,
                diagnostic.Location?.FieldKey,
                tableTitle,
                recordTitle,
                fieldTitle));
        }

        return result;
    }

    private static string FormatSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "Ошибка",
        DiagnosticSeverity.Warning => "Предупреждение",
        _ => "Информация",
    };

    private static string FormatDomain(DiagnosticDomain domain) => domain switch
    {
        DiagnosticDomain.Semantic => "Семантика",
        DiagnosticDomain.Resources => "Ресурсы",
        _ => "Структура",
    };

    private static string FormatPlace(
        ValidationDiagnostic diagnostic,
        string tableTitle,
        string recordTitle,
        string fieldTitle)
    {
        var location = diagnostic.Location;
        if (location is null)
        {
            if (string.IsNullOrWhiteSpace(diagnostic.Path))
            {
                return "Весь документ";
            }

            return diagnostic.Offset is long offset
                ? $"{diagnostic.Path} · 0x{offset:X}"
                : diagnostic.Path;
        }

        var parts = new List<string>(3) { tableTitle };
        if (location.RecordIndex >= 0)
        {
            parts.Add($"строка {location.RecordIndex + 1:N0} · {recordTitle}");
        }

        if (location.FieldId is not null)
        {
            parts.Add(fieldTitle);
        }

        return string.Join(" · ", parts);
    }

    private static string FormatDetail(ValidationDiagnostic diagnostic, string path)
    {
        var parts = new List<string>(7);
        if (diagnostic.Location is { } location)
        {
            parts.Add($"Ресурс: {location.ResourceName}");
            parts.Add($"Таблица: {location.TableKey}");
            if (location.RecordIndex >= 0)
            {
                parts.Add($"Запись: {location.RecordIndex + 1:N0} — {location.RecordName}");
            }

            if (location.FieldId is int fieldId)
            {
                var fieldName = string.IsNullOrWhiteSpace(location.FieldKey)
                    ? "без имени"
                    : location.FieldKey;
                parts.Add($"Поле: {fieldName} [{fieldId}]");
            }
        }

        if (path != "—")
        {
            parts.Add($"Путь: {path}");
        }

        if (diagnostic.Offset is long offset)
        {
            parts.Add($"Смещение: {offset:N0} (0x{offset:X})");
        }

        parts.Add(diagnostic.IsBlocking
            ? "Результат: блокирует сохранение копии."
            : "Результат: не блокирует сохранение копии.");
        return string.Join(Environment.NewLine, parts);
    }

    private DatabaseDocument RequireDocument() =>
        _document ?? throw new InvalidOperationException("Сначала откройте database.res.");

    private TextsDocument RequireTextsDocument() =>
        _textsDocument ?? throw new InvalidOperationException("Рядом с database.res не найден связанный texts.res.");
}
