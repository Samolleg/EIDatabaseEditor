using System.Collections.Immutable;
using System.Security.Cryptography;
using DatabaseEditor.Modules.V1;

namespace DatabaseEditor.Modules.Host;

/// <summary>Session lifetime: closed documents and Undo may still reference any blob.
/// No collection or file access. DocumentStore persists this pool with recovery.</summary>
public sealed class DocumentBlobStore : IModuleBlobStore
{
    public const int MaximumBlobBytes = 192 * 1024 * 1024;
    public const long MaximumTotalBytes = 512L * 1024 * 1024;
    public const int MaximumCount = 4096;
    private readonly object gate = new();
    private Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
    private readonly int maximumBlobBytes, maximumCount;
    private readonly long maximumTotalBytes;

    public DocumentBlobStore(int maximumBlobBytes = MaximumBlobBytes,
        long maximumTotalBytes = MaximumTotalBytes, int maximumCount = MaximumCount)
    {
        if (maximumBlobBytes < 0 || maximumBlobBytes > MaximumBlobBytes || maximumTotalBytes < 0 ||
            maximumTotalBytes > MaximumTotalBytes || maximumCount < 0 || maximumCount > MaximumCount)
            throw new ArgumentOutOfRangeException(nameof(maximumBlobBytes));
        this.maximumBlobBytes = maximumBlobBytes;
        this.maximumTotalBytes = maximumTotalBytes;
        this.maximumCount = maximumCount;
    }

    private static string Id(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Неверный SHA-256 blob.");
        return value.ToUpperInvariant();
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public BlobPutResult Put(ImmutableArray<byte> bytes)
    {
        if (bytes.IsDefault || bytes.Length > maximumBlobBytes)
            return new(new(ResultStatus.Failed, "Не заданы данные или превышен размер blob."), null);
        // Copy once to own the bytes even if the caller retains an unsafe mutable alias.
        byte[] owned = bytes.ToArray();
        string id = Hash(owned);
        lock (gate)
        {
            if (entries.TryGetValue(id, out var old))
                return old.AsSpan().SequenceEqual(owned) ? new(Result.Ok, id) : new(new(ResultStatus.Failed, "Конфликт SHA-256 blob."), null);
            if (entries.Count >= maximumCount || entries.Values.Sum(v => (long)v.Length) + owned.Length > maximumTotalBytes)
                return new(new(ResultStatus.Failed, "Превышен объём хранилища blobs."), null);
            entries.Add(id, owned);
            return new(Result.Ok, id);
        }
    }
    public BlobGetResult Get(string sha256)
    {
        string id;
        try { id = Id(sha256); }
        catch (ArgumentException ex) { return new(new(ResultStatus.Failed, ex.Message), []); }
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var data)) return new(new(ResultStatus.Unavailable, "Blob отсутствует: " + id), []);
            if (Hash(data) != id) return new(new(ResultStatus.Failed, "Контрольная сумма blob не совпала."), []);
            return new(Result.Ok, ImmutableArray.CreateRange(data));
        }
    }

    // Serialization takes a stable dictionary of owned arrays; callers never receive these arrays.
    internal Dictionary<string, byte[]> Capture()
    {
        lock (gate) return new(entries, StringComparer.Ordinal);
    }

    // All existing blobs survive, including those referenced only by host history.
    // Validation and codec reads occur under the same reentrant gate. Failure rolls back the pool.
    internal Result Restore(Dictionary<string, byte[]>? incoming, Func<Result> validateAndCommit)
    {
        lock (gate)
        {
            var staged = new Dictionary<string, byte[]>(entries, StringComparer.Ordinal);
            long total = staged.Values.Sum(v => (long)v.Length);
            try
            {
                foreach (var pair in incoming ?? [])
                {
                    string id = Id(pair.Key);
                    if (pair.Value is null || pair.Value.Length > maximumBlobBytes || Hash(pair.Value) != id)
                        return new(ResultStatus.Failed, "Повреждён blob recovery: " + id);
                    if (staged.TryGetValue(id, out var old))
                    {
                        if (!old.AsSpan().SequenceEqual(pair.Value)) return new(ResultStatus.Failed, "Конфликт blob recovery.");
                        continue;
                    }
                    total += pair.Value.Length;
                    if (staged.Count >= maximumCount || total > maximumTotalBytes)
                        return new(ResultStatus.Failed, "Превышен объём blobs recovery.");
                    staged.Add(id, pair.Value.ToArray());
                }
                var previous = entries;
                entries = staged;
                bool committed = false;
                try
                {
                    var result = validateAndCommit();
                    committed = result.Succeeded;
                    return result;
                }
                finally { if (!committed) entries = previous; }
            }
            catch (ArgumentException ex) { return new(ResultStatus.Failed, ex.Message); }
        }
    }
}
