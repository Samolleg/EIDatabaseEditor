using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using DatabaseEditor.Modules.V1;

namespace DatabaseEditor.Modules.Host;

public sealed class DocumentStore : IModuleDocuments
{
    private readonly Dictionary<(string, string, int), IDocumentCodec> codecs = new();
    private readonly Dictionary<Guid, DocumentSnapshot> documents = new();
    private readonly Dictionary<Guid, string> checkpoints = new();
    private readonly HashSet<Guid> closed = new();
    private readonly Dictionary<Guid, DocumentRegistration> registrations = new();
    public DocumentRegistration? Registration(Guid id) => registrations.GetValueOrDefault(id);
    public ImmutableArray<DocumentSnapshot> Active => documents.Values.Where(d => !closed.Contains(d.DocumentId)).ToImmutableArray();
    public event EventHandler<DocumentEvent>? Changed;
    public bool HasDirty => documents.Values.Any(d => d.IsDirty && !closed.Contains(d.DocumentId));
    public Result RegisterCodec(string ownerId, string formatId, int stateVersion, IDocumentCodec codec)
    {
        if (!codecs.TryAdd((ownerId, formatId, stateVersion), codec)) return new(ResultStatus.Conflict, "Формат уже зарегистрирован.");
        return Result.Ok;
    }
    public DocumentSnapshot Open(DocumentRegistration registration)
    {
        var snapshot = new DocumentSnapshot(Guid.NewGuid(), registration.OwnerId, registration.FormatId, registration.StateVersion,
            registration.DisplayName, 1, registration.Text, registration.State, registration.InitiallyDirty, "");
        var result = Validate(snapshot);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        documents.Add(snapshot.DocumentId, snapshot);
        registrations.Add(snapshot.DocumentId, registration with { Text = "", State = [] });
        checkpoints.Add(snapshot.DocumentId, registration.InitiallyDirty ? "" : ContentHash(snapshot));
        Notify(snapshot, "Opened"); return snapshot;
    }
    public DocumentSnapshot? Get(Guid documentId) => documents.GetValueOrDefault(documentId);
    public ImmutableArray<DocumentSnapshot> List(string ownerId) => documents.Values.Where(d => d.OwnerId == ownerId && !closed.Contains(d.DocumentId)).ToImmutableArray();
    public ImmutableArray<DocumentSnapshot> All => documents.Values.ToImmutableArray();
    public Result Close(Guid documentId, long expectedRevision, bool discardChanges)
    {
        if (!documents.TryGetValue(documentId, out var d)) return new(ResultStatus.Unavailable);
        if (d.Revision != expectedRevision) return new(ResultStatus.Conflict, "Документ изменился.");
        if (d.IsDirty && !discardChanges) return new(ResultStatus.Conflict, "Документ содержит несохранённые изменения.");
        closed.Add(documentId); Notify(d, "Closed"); return Result.Ok;
    }
    public Result Validate(DocumentSnapshot snapshot)
    {
        if (snapshot.Text.Length > 16 * 1024 * 1024 || snapshot.State.Length > 192 * 1024 * 1024) return new(ResultStatus.Failed, "Превышен размер документа.");
        if (!codecs.TryGetValue((snapshot.OwnerId, snapshot.FormatId, snapshot.StateVersion), out var codec)) return new(ResultStatus.Unsupported, "Формат модуля недоступен.");
        try { return codec.Validate(snapshot); } catch (Exception ex) { return new(ResultStatus.Failed, ex.Message); }
    }
    public PreparedDocument Prepare(Guid documentId, string kind)
    {
        if (!documents.TryGetValue(documentId, out var d)) return new(new(ResultStatus.Unavailable), []);
        if (!codecs.TryGetValue((d.OwnerId, d.FormatId, d.StateVersion), out var codec)) return new(new(ResultStatus.Unsupported), []);
        try { return codec.Prepare(d, kind); } catch (Exception ex) { return new(new(ResultStatus.Failed, ex.Message), []); }
    }
    public bool Matches(DocumentSnapshot expected) => documents.TryGetValue(expected.DocumentId, out var d) &&
        d.Text == expected.Text && d.State.AsSpan().SequenceEqual(expected.State.AsSpan());
    public void Commit(DocumentSnapshot snapshot, bool notify = true)
    {
        if (!documents.TryGetValue(snapshot.DocumentId, out var current)) throw new InvalidOperationException("Документ закрыт.");
        var updated = snapshot with { Revision = current.Revision + 1, IsDirty = ContentHash(snapshot) != checkpoints[snapshot.DocumentId], PersistedHash = current.PersistedHash };
        documents[snapshot.DocumentId] = updated;
        closed.Remove(snapshot.DocumentId);
        if (notify) Notify(updated, "ContentChanged");
    }
    public void Publish(Guid id) { if (documents.TryGetValue(id, out var d)) Notify(d, "ContentChanged"); }
    public void Checkpoint(Guid id, long revision, string hash)
    {
        if (!documents.TryGetValue(id, out var d) || d.Revision != revision) return;
        checkpoints[id] = ContentHash(d); d = d with { IsDirty = false, PersistedHash = hash }; documents[id] = d; Notify(d, "Saved");
    }
    private sealed record Recovery(int Version, DocumentSnapshot[] Documents, Dictionary<Guid, string> Checkpoints, Guid[] Closed,
        Dictionary<Guid, DocumentRegistration>? Registrations = null);
    public byte[] CaptureRecovery() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Recovery(1, documents.Values.ToArray(), checkpoints, closed.ToArray(), registrations));
    public Result RestoreRecovery(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var r = System.Text.Json.JsonSerializer.Deserialize<Recovery>(bytes.Span);
            if (r is null || r.Version != 1 || r.Documents is null || r.Checkpoints is null || r.Closed is null)
                return new(ResultStatus.Unsupported, "Неизвестный формат документов recovery.");
            var staged = r.Documents.ToDictionary(d => d.DocumentId);
            if (staged.Keys.Any(id => !r.Checkpoints.ContainsKey(id)) || r.Closed.Any(id => !staged.ContainsKey(id)))
                return new(ResultStatus.Failed, "Неполный recovery документов.");
            foreach (var d in staged.Values)
            {
                // Missing codecs must never discard or rewrite recovered bytes.
                if (codecs.ContainsKey((d.OwnerId, d.FormatId, d.StateVersion)))
                { var v = Validate(d); if (!v.Succeeded) return v; }
            }
            documents.Clear(); checkpoints.Clear(); closed.Clear(); registrations.Clear();
            foreach (var pair in r.Registrations ?? []) if (staged.ContainsKey(pair.Key)) registrations.Add(pair.Key, pair.Value);
            foreach (var d in staged.Values) { documents.Add(d.DocumentId, d); checkpoints.Add(d.DocumentId, r.Checkpoints[d.DocumentId]); }
            closed.UnionWith(r.Closed);
            foreach (var d in staged.Values) Notify(d, "Restored");
            return Result.Ok;
        }
        catch (Exception ex) { return new(ResultStatus.Failed, ex.Message); }
    }
    private void Notify(DocumentSnapshot d, string kind)
    {
        foreach (EventHandler<DocumentEvent> handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, new(d.DocumentId, d.Revision, kind)); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Module document subscriber: " + ex.Message); }
    }
    private static string ContentHash(DocumentSnapshot d)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(d.Text)); hash.AppendData([0]); hash.AppendData(d.State.AsSpan());
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
