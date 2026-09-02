using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record ResEntry(
    int Index,
    string Name,
    int Offset,
    int Size,
    long DirectoryOffset,
    ushort NameLength,
    uint NameOffset);

public sealed class ResArchiveDocument
{
    public const uint Signature = 0x019CE23C;
    public const int HeaderSize = 16;
    public const int EntrySize = 22;

    private readonly byte[] _data;
    private readonly Dictionary<string, ResEntry> _entriesByName;

    private ResArchiveDocument(
        string sourcePath,
        byte[] data,
        int declaredEntryCount,
        int directoryOffset,
        int namePoolSize,
        IReadOnlyList<ResEntry> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourcePath = sourcePath;
        _data = data;
        DeclaredEntryCount = declaredEntryCount;
        DirectoryOffset = directoryOffset;
        NamePoolSize = namePoolSize;
        Entries = entries;
        _entriesByName = new Dictionary<string, ResEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            _entriesByName.TryAdd(entries[index].Name, entries[index]);
        }

        Sha256 = ComputeSha256(data, cancellationToken);
    }

    public string SourcePath { get; }

    public int Length => _data.Length;

    public string Sha256 { get; }

    public int DeclaredEntryCount { get; }

    public int DirectoryOffset { get; }

    public int NamePoolSize { get; }

    public IReadOnlyList<ResEntry> Entries { get; }

    public static ResArchiveDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);
        return Parse(fullPath, data);
    }

    public static async Task<ResArchiveDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var data = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(fullPath, data, cancellationToken);
    }

    public bool TryGetEntry(string name, out ResEntry? entry) =>
        _entriesByName.TryGetValue(name, out entry);

    public ReadOnlyMemory<byte> GetResourceBytes(ResEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureOwnedEntry(entry);
        return _data.AsMemory(entry.Offset, entry.Size);
    }

    public ReadOnlyMemory<byte> GetResourceBytes(string name)
    {
        if (!TryGetEntry(name, out var entry) || entry is null)
        {
            throw new KeyNotFoundException($"Resource '{name}' was not found.");
        }

        return GetResourceBytes(entry);
    }

    public IReadOnlyList<ValidationDiagnostic> Validate() => Validate(CancellationToken.None);

    internal IReadOnlyList<ValidationDiagnostic> Validate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<ValidationDiagnostic>();
        var ordered = Entries.OrderBy(entry => entry.Offset).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < ordered.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = ordered[index];
            if ((long)entry.Offset + entry.Size > DirectoryOffset)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "RES_RESOURCE_OVERLAP_DIRECTORY",
                    $"Resource '{entry.Name}' overlaps the RES directory.",
                    entry.Name,
                    entry.Offset));
            }

            if (index > 0)
            {
                var previous = ordered[index - 1];
                if (previous.Size > 0 && entry.Size > 0 &&
                    (long)previous.Offset + previous.Size > entry.Offset)
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "RES_RESOURCE_OVERLAP",
                        $"Resources '{previous.Name}' and '{entry.Name}' overlap.",
                        entry.Name,
                        entry.Offset));
                }
            }
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Entries.Count; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var name = Entries[index].Name;
            nameCounts[name] = nameCounts.TryGetValue(name, out var count) ? count + 1 : 1;
        }

        foreach (var (name, count) in nameCounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 2)
            {
                continue;
            }

            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "RES_DUPLICATE_NAME",
                $"Resource name '{name}' occurs more than once.",
                name));
        }

        return diagnostics;
    }

    internal ReadOnlyMemory<byte> OriginalBytes => _data;

    internal static ResArchiveDocument Parse(
        string sourcePath,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (data.Length < HeaderSize)
        {
            throw new DatabaseFormatException("RES header is truncated.", sourcePath, 0);
        }

        var header = data.AsSpan(0, HeaderSize);
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (signature != Signature)
        {
            throw new DatabaseFormatException(
                $"Unexpected RES signature 0x{signature:X8}.", sourcePath, 0);
        }

        var countValue = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var directoryValue = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var namePoolValue = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (countValue > 1_000_000 || directoryValue > int.MaxValue || namePoolValue > int.MaxValue)
        {
            throw new DatabaseFormatException("RES header contains unsupported sizes.", sourcePath, 4);
        }

        var count = (int)countValue;
        var directoryOffset = (int)directoryValue;
        var namePoolSize = (int)namePoolValue;
        var directoryLength = checked((long)count * EntrySize);
        var namePoolOffset = checked((long)directoryOffset + directoryLength);
        var archiveEnd = checked(namePoolOffset + namePoolSize);
        if (directoryOffset < HeaderSize || archiveEnd > data.Length)
        {
            throw new DatabaseFormatException("RES directory or name pool is truncated.", sourcePath, directoryOffset);
        }

        var entries = new List<ResEntry>(Math.Min(count, 4096));
        for (var index = 0; index < count; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entryOffset = checked(directoryOffset + index * EntrySize);
            var span = data.AsSpan(entryOffset, EntrySize);
            var sizeValue = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            var dataOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
            var nameOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(span[18..]);
            if (dataOffsetValue == 0 || nameLength == 0)
            {
                continue;
            }

            if (sizeValue > int.MaxValue || dataOffsetValue > int.MaxValue)
            {
                throw new DatabaseFormatException("RES resource is too large.", sourcePath, entryOffset);
            }

            var resourceOffset = (int)dataOffsetValue;
            var resourceSize = (int)sizeValue;
            if (resourceOffset < HeaderSize || (long)resourceOffset + resourceSize > directoryOffset)
            {
                throw new DatabaseFormatException("RES resource range is invalid.", sourcePath, entryOffset);
            }

            var nameStart = checked(namePoolOffset + nameOffsetValue);
            var nameEnd = checked(nameStart + nameLength);
            if (nameOffsetValue > namePoolValue || nameEnd > archiveEnd)
            {
                throw new DatabaseFormatException("RES resource name exceeds the name pool.", sourcePath, entryOffset);
            }

            var name = ResResourceNameCodec.Decode(data.AsSpan((int)nameStart, nameLength)).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            {
                throw new DatabaseFormatException("RES resource name is invalid.", sourcePath, (long)nameStart);
            }

            entries.Add(new(
                index,
                name,
                resourceOffset,
                resourceSize,
                entryOffset,
                nameLength,
                nameOffsetValue));
        }

        var document = new ResArchiveDocument(
            sourcePath,
            data,
            count,
            directoryOffset,
            namePoolSize,
            entries,
            cancellationToken);
        var errors = document.Validate(cancellationToken)
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new DatabaseFormatException(errors[0].Message, errors[0].Path, errors[0].Offset);
        }

        return document;
    }

    private static string ComputeSha256(byte[] data, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 1024 * 1024;
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(data, offset, Math.Min(chunkSize, data.Length - offset));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void EnsureOwnedEntry(ResEntry entry)
    {
        if (!_entriesByName.TryGetValue(entry.Name, out var owned) || owned != entry)
        {
            throw new ArgumentException("The resource entry does not belong to this archive.", nameof(entry));
        }
    }
}
