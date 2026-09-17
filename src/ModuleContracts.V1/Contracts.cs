using System.Collections.Immutable;

namespace DatabaseEditor.Modules.V1;

public enum ResultStatus { Success, Conflict, Unavailable, Unsupported, Cancelled, Failed }
public sealed record Result(ResultStatus Status, string Message = "")
{
    public bool Succeeded => Status == ResultStatus.Success;
    public static Result Ok => new(ResultStatus.Success);
}
public sealed record WorkspaceStamp(Guid SessionId, long Generation, long Revision);
public sealed record RecordRef(Guid SessionId, long Generation, string TableKey, Guid RecordId);
public sealed record ValueDto(string Kind, string ExactValue, string Label, bool Editable);
public sealed record FieldDto(int FieldId, int Occurrence, ImmutableArray<int> NestedPath, ValueDto Value);
public sealed record TextDto(string ResourceKey, string Title, string Description, bool Modified);
public sealed record RecordDto(RecordRef Reference, string Name, string Label, ImmutableArray<FieldDto> Fields, ImmutableArray<TextDto> Texts);
public sealed record WorkspaceSnapshot(WorkspaceStamp Stamp, string DisplayPath, bool Modified, ImmutableArray<RecordDto> Records);
public sealed record WorkspaceEvent(WorkspaceStamp Stamp, string Kind);
public sealed record OpenedFile(string DisplayPath, string Sha256, ImmutableArray<byte> Content)
{
    public Guid SourceToken { get; init; }
}
public enum ModuleCommand { Save, Undo, Redo, Find, Validate }
public interface IWorkspaceReader
{
    WorkspaceSnapshot Snapshot();
    Result Navigate(RecordRef reference);
    event EventHandler<WorkspaceEvent>? Changed;
}
// The host owns dialogs and writes. Read results are immutable snapshots, not write permissions.
public interface IModuleFiles
{
    OpenedFile? Open(string filter);
    Result Export(string suggestedName, string filter, ReadOnlyMemory<byte> bytes);
}
public interface IModuleFileBrowser : IModuleFiles
{
    // Only paths returned by Open/SelectFolder may subsequently be read.
    ImmutableArray<string> SelectFolder(string extension);
    OpenedFile Read(string path);
}
public interface IModuleHost
{
    IWorkspaceReader Workspace { get; }
    IModuleFiles Files { get; }
    ImmutableArray<string> Capabilities { get; }
    void Report(string message);
}
public interface IEditorModule : IDisposable
{
    void Initialize(IModuleHost host);
    void Activate(bool active);
    bool Execute(ModuleCommand command);
    bool CanClose();
    bool IsDirty { get; }
    // Recovery bytes are module-owned versioned data. The host preserves unknown formats.
    ReadOnlyMemory<byte> CaptureRecovery();
    Result RestoreRecovery(ReadOnlyMemory<byte> bytes);
}
