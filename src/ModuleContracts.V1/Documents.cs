using System.Collections.Immutable;

namespace DatabaseEditor.Modules.V1;

public sealed record DocumentRegistration(string OwnerId, string FormatId, int StateVersion, string DisplayName,
    string Text, ImmutableArray<byte> State, Guid? SourceToken = null, bool InitiallyDirty = false)
{
    public string? SaveArtifactKind { get; init; }
}
public sealed record DocumentSnapshot(Guid DocumentId, string OwnerId, string FormatId, int StateVersion,
    string DisplayName, long Revision, string Text, ImmutableArray<byte> State, bool IsDirty, string PersistedHash);
public sealed record DocumentEvent(Guid DocumentId, long Revision, string Kind);
public sealed record PreparedDocument(Result Result, ImmutableArray<byte> Bytes);
public interface IDocumentCodec
{
    Result Validate(DocumentSnapshot snapshot);
    PreparedDocument Prepare(DocumentSnapshot snapshot, string artifactKind);
}
public interface IModuleDocuments
{
    Result RegisterCodec(string ownerId, string formatId, int stateVersion, IDocumentCodec codec);
    DocumentSnapshot Open(DocumentRegistration registration);
    DocumentSnapshot? Get(Guid documentId);
    ImmutableArray<DocumentSnapshot> List(string ownerId);
    Result Close(Guid documentId, long expectedRevision, bool discardChanges);
    event EventHandler<DocumentEvent>? Changed;
}
public sealed record FieldAddress(RecordRef Record, int FieldId, int Occurrence, ImmutableArray<int> NestedPath);
public sealed record TextAddress(string ResourceKey, string Component);
public abstract record ChangeOperation;
public sealed record SetField(FieldAddress Address, ValueDto Expected, ValueDto Value) : ChangeOperation;
public sealed record SetText(TextAddress Address, string Expected, string Value) : ChangeOperation;
public sealed record ReplaceScriptRange(Guid DocumentId, long ExpectedRevision, int Start, int Length,
    string Expected, string Value) : ChangeOperation;
// State includes unpublished card input; text and state enter history together.
public sealed record UpdateDocument(Guid DocumentId, long ExpectedRevision, string Text, ImmutableArray<byte> State) : ChangeOperation;
public sealed record ChangeSet(Guid TransactionId, WorkspaceStamp ExpectedWorkspace, string Description,
    ImmutableArray<ChangeOperation> Operations);
public interface IModuleTransactions
{
    Result Apply(ChangeSet changeSet);
    Result Undo();
    Result Redo();
}
public sealed record FileSaveResult(Guid DocumentId, string Path, string Status, string Message);
public sealed record SaveReport(ImmutableArray<FileSaveResult> Files, bool Complete);
public interface IModuleSave
{
    Result Export(Guid documentId, string artifactKind, string suggestedName, string filter);
    // Initial host may return Unsupported until all participating documents have prepared artifacts.
    SaveReport SaveAll();
}
// Capability extension; the original read-only interface remains binary compatible.
public interface IModuleDocumentHost : IModuleHost
{
    IModuleDocuments Documents { get; }
    IModuleTransactions Transactions { get; }
    IModuleSave Save { get; }
}
