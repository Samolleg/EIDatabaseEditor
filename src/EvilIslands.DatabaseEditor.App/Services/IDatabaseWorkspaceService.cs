using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal interface IDatabaseWorkspaceService
{
    Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken);

    Task<WorkspaceSnapshot> AttachTextsAsync(string path, CancellationToken cancellationToken);

    Task<WorkspaceSnapshot> AttachSoundsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(CancellationToken cancellationToken);

    Task<DatabaseComparisonResult> CompareAsync(string path, CancellationToken cancellationToken);

    Task<DatabaseReferenceSearchResult> FindReferencesAsync(
        string tableKey,
        int recordIndex,
        CancellationToken cancellationToken);

    RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template);

    void RefreshRelatedTexts(RecordRowViewModel record);

    void RefreshRelatedSounds(RecordRowViewModel record);

    void RemoveAddedRecord(RecordRowViewModel record);

    void RestoreAddedRecord(RecordRowViewModel record);

    IReadOnlyList<LocalizationEntryViewModel> AddMissingTexts(RecordRowViewModel record);

    void RemoveAddedText(LocalizationEntryViewModel entry);

    void RestoreAddedText(LocalizationEntryViewModel entry);

    Task<PendingSoundEditViewModel> PrepareBriefingSoundImportAsync(
        RecordRowViewModel record,
        BriefingPhraseViewModel phrase,
        string sourcePath,
        CancellationToken cancellationToken);

    Task<SoundArchiveCopyResult> SaveSoundCopyAsync(
        PendingSoundEditViewModel edit,
        string targetPath,
        CancellationToken cancellationToken);

    Task<SaveSoundCurrentResult> SaveSoundCurrentAsync(
        PendingSoundEditViewModel edit,
        bool allowGameInstallationPath,
        CancellationToken cancellationToken);

    Task<RestoreManagedBackupResult> RestoreManagedBackupAsync(
        ManagedBackupViewModel backup,
        bool allowGameInstallationPath,
        CancellationToken cancellationToken);

    Task<SaveCurrentResult> SaveCurrentAsync(
        bool allowGameInstallationPath,
        CancellationToken cancellationToken, bool createBackup = true);

    Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken);
}
