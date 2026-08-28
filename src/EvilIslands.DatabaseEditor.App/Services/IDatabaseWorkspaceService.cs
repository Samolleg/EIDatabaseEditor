using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.Services;

internal interface IDatabaseWorkspaceService
{
    Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken);

    Task<WorkspaceSnapshot> AttachTextsAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(CancellationToken cancellationToken);

    RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template);

    void RemoveAddedRecord(RecordRowViewModel record);

    void RestoreAddedRecord(RecordRowViewModel record);

    IReadOnlyList<LocalizationEntryViewModel> AddMissingTexts(RecordRowViewModel record);

    void RemoveAddedText(LocalizationEntryViewModel entry);

    void RestoreAddedText(LocalizationEntryViewModel entry);

    Task<SaveCurrentResult> SaveCurrentAsync(
        bool allowGameInstallationPath,
        CancellationToken cancellationToken);

    Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken);
}
