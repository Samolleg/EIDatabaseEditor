using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.Services;

internal interface IDatabaseWorkspaceService
{
    Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(CancellationToken cancellationToken);

    RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template);

    void RemoveAddedRecord(RecordRowViewModel record);

    void RestoreAddedRecord(RecordRowViewModel record);

    Task<SaveCurrentResult> SaveCurrentAsync(
        bool allowGameInstallationPath,
        CancellationToken cancellationToken);

    Task<SaveCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken);
}
