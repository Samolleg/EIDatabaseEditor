using System.Buffers.Binary;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record SoundArchiveInfo(
    int Priority,
    string SourcePath,
    string FileName,
    long FileLength,
    int ArchiveEntryCount,
    int SoundResourceCount);

public sealed record SoundResourceInfo(
    SoundArchiveInfo Archive,
    string ResourceName,
    string Format,
    int Length,
    int ArchiveIndex);

public sealed class SoundArchiveCatalog
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".mp3", ".wav", ".ogg"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<SoundResourceInfo>> _resourcesByName;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SoundResourceInfo>> _briefingsByKey;

    private SoundArchiveCatalog(
        IReadOnlyList<SoundArchiveInfo> archives,
        IReadOnlyList<SoundResourceInfo> resources)
    {
        Archives = archives;
        Resources = resources;
        _resourcesByName = resources
            .GroupBy(resource => NormalizeResourceName(resource.ResourceName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SoundResourceInfo>)group
                    .OrderBy(resource => resource.Archive.Priority)
                    .ThenBy(resource => resource.ArchiveIndex)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _briefingsByKey = resources
            .Select(resource => (Resource: resource, Key: TryGetBriefingKey(resource.ResourceName)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SoundResourceInfo>)group
                    .Select(item => item.Resource)
                    .OrderBy(resource => resource.Archive.Priority)
                    .ThenBy(resource => resource.ResourceName, NaturalPathComparer.Instance)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SoundArchiveInfo> Archives { get; }

    public IReadOnlyList<SoundResourceInfo> Resources { get; }

    public static SoundArchiveCatalog Open(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] fullPaths = NormalizePaths(paths);
        SoundArchiveIndex[] archives = fullPaths
            .Select(ReadArchiveIndex)
            .ToArray();
        return Create(archives);
    }

    public static async Task<SoundArchiveCatalog> OpenAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] fullPaths = NormalizePaths(paths);
        var archives = new SoundArchiveIndex[fullPaths.Length];
        for (var index = 0; index < fullPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            archives[index] = await ReadArchiveIndexAsync(fullPaths[index], cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Create(archives);
    }

    public IReadOnlyList<SoundResourceInfo> FindExact(string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return [];
        }

        string normalized = NormalizeResourceName(resourceName);
        return _resourcesByName.TryGetValue(normalized, out IReadOnlyList<SoundResourceInfo>? resources)
            ? resources
            : [];
    }

    public IReadOnlyList<SoundResourceInfo> FindBriefing(string briefingKey)
    {
        if (string.IsNullOrWhiteSpace(briefingKey))
        {
            return [];
        }

        string normalized = NormalizeBriefingKey(briefingKey);
        return _briefingsByKey.TryGetValue(normalized, out IReadOnlyList<SoundResourceInfo>? resources)
            ? resources
            : [];
    }

    private static SoundArchiveCatalog Create(IReadOnlyList<SoundArchiveIndex> sourceArchives)
    {
        var archives = new SoundArchiveInfo[sourceArchives.Count];
        var resources = new List<SoundResourceInfo>();
        for (var archiveIndex = 0; archiveIndex < sourceArchives.Count; archiveIndex++)
        {
            SoundArchiveIndex source = sourceArchives[archiveIndex];
            ResEntry[] soundEntries = source.Entries
                .Where(entry => IsSupportedSound(entry.Name))
                .ToArray();
            var archive = new SoundArchiveInfo(
                archiveIndex,
                source.SourcePath,
                Path.GetFileName(source.SourcePath),
                source.FileLength,
                source.Entries.Count,
                soundEntries.Length);
            archives[archiveIndex] = archive;
            resources.AddRange(soundEntries.Select(entry => new SoundResourceInfo(
                archive,
                NormalizeResourceName(entry.Name),
                Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant(),
                entry.Size,
                entry.Index)));
        }

        return new SoundArchiveCatalog(archives, resources);
    }

    private static SoundArchiveIndex ReadArchiveIndex(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.RandomAccess);
        byte[] header = new byte[ResArchiveDocument.HeaderSize];
        stream.ReadExactly(header);
        return ReadArchiveIndex(path, stream, header, CancellationToken.None);
    }

    private static async Task<SoundArchiveIndex> ReadArchiveIndexAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] header = new byte[ResArchiveDocument.HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadArchiveIndexAsync(path, stream, header, cancellationToken).ConfigureAwait(false);
    }

    private static SoundArchiveIndex ReadArchiveIndex(
        string path,
        FileStream stream,
        ReadOnlySpan<byte> header,
        CancellationToken cancellationToken)
    {
        ArchiveHeader parsed = ParseArchiveHeader(path, stream.Length, header);
        byte[] catalog = new byte[parsed.CatalogLength];
        stream.Position = parsed.DirectoryOffset;
        stream.ReadExactly(catalog);
        return ParseArchiveCatalog(path, stream.Length, parsed, catalog, cancellationToken);
    }

    private static async Task<SoundArchiveIndex> ReadArchiveIndexAsync(
        string path,
        FileStream stream,
        byte[] header,
        CancellationToken cancellationToken)
    {
        ArchiveHeader parsed = ParseArchiveHeader(path, stream.Length, header);
        byte[] catalog = new byte[parsed.CatalogLength];
        stream.Position = parsed.DirectoryOffset;
        await stream.ReadExactlyAsync(catalog, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParseArchiveCatalog(path, stream.Length, parsed, catalog, cancellationToken);
    }

    private static ArchiveHeader ParseArchiveHeader(
        string path,
        long fileLength,
        ReadOnlySpan<byte> header)
    {
        if (header.Length != ResArchiveDocument.HeaderSize)
        {
            throw new DatabaseFormatException("RES header is truncated.", path, 0);
        }

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (signature != ResArchiveDocument.Signature)
        {
            throw new DatabaseFormatException($"Unexpected RES signature 0x{signature:X8}.", path, 0);
        }

        uint countValue = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint directoryValue = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint namePoolValue = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (countValue > 1_000_000 || directoryValue > int.MaxValue || namePoolValue > int.MaxValue)
        {
            throw new DatabaseFormatException("RES header contains unsupported sizes.", path, 4);
        }

        int count = (int)countValue;
        int directoryOffset = (int)directoryValue;
        int namePoolSize = (int)namePoolValue;
        long directoryLength = checked((long)count * ResArchiveDocument.EntrySize);
        long catalogLength = checked(directoryLength + namePoolSize);
        long archiveEnd = checked((long)directoryOffset + catalogLength);
        if (directoryOffset < ResArchiveDocument.HeaderSize || archiveEnd > fileLength || catalogLength > int.MaxValue)
        {
            throw new DatabaseFormatException("RES directory or name pool is truncated.", path, directoryOffset);
        }

        return new ArchiveHeader(count, directoryOffset, namePoolSize, (int)directoryLength, (int)catalogLength);
    }

    private static SoundArchiveIndex ParseArchiveCatalog(
        string path,
        long fileLength,
        ArchiveHeader header,
        ReadOnlySpan<byte> catalog,
        CancellationToken cancellationToken)
    {
        var entries = new List<ResEntry>(Math.Min(header.EntryCount, 4096));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.EntryCount; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int entryOffset = index * ResArchiveDocument.EntrySize;
            ReadOnlySpan<byte> entry = catalog.Slice(entryOffset, ResArchiveDocument.EntrySize);
            uint sizeValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint dataOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[16..]);
            uint nameOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[18..]);
            if (dataOffsetValue == 0 || nameLength == 0)
            {
                continue;
            }

            if (sizeValue > int.MaxValue || dataOffsetValue > int.MaxValue)
            {
                throw new DatabaseFormatException("RES resource is too large.", path, header.DirectoryOffset + entryOffset);
            }

            int resourceOffset = (int)dataOffsetValue;
            int resourceSize = (int)sizeValue;
            if (resourceOffset < ResArchiveDocument.HeaderSize ||
                (long)resourceOffset + resourceSize > header.DirectoryOffset)
            {
                throw new DatabaseFormatException("RES resource range is invalid.", path, header.DirectoryOffset + entryOffset);
            }

            long nameEnd = checked((long)nameOffsetValue + nameLength);
            if (nameOffsetValue > header.NamePoolSize || nameEnd > header.NamePoolSize)
            {
                throw new DatabaseFormatException("RES resource name exceeds the name pool.", path, header.DirectoryOffset + entryOffset);
            }

            int nameStart = checked(header.DirectoryLength + (int)nameOffsetValue);
            string name = Encoding.ASCII.GetString(catalog.Slice(nameStart, nameLength)).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            {
                throw new DatabaseFormatException("RES resource name is invalid.", path, header.DirectoryOffset + nameStart);
            }

            if (!names.Add(name))
            {
                throw new DatabaseFormatException($"Resource name '{name}' occurs more than once.", path);
            }

            entries.Add(new(
                index,
                name,
                resourceOffset,
                resourceSize,
                header.DirectoryOffset + entryOffset,
                nameLength,
                nameOffsetValue));
        }

        return new SoundArchiveIndex(path, fileLength, entries);
    }

    private static string[] NormalizePaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Звуковой RES не найден.", fullPath);
            }

            if (identities.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result.ToArray();
    }

    private static bool IsSupportedSound(string resourceName) =>
        SupportedExtensions.Contains(Path.GetExtension(resourceName));

    private static string NormalizeResourceName(string value)
    {
        string normalized = value.Trim().Replace('/', '\\').TrimStart('\\');
        while (normalized.Contains("\\\\", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return normalized;
    }

    private static string NormalizeBriefingKey(string value)
    {
        string normalized = NormalizeResourceName(value).TrimEnd('\\');
        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int briefingIndex = Array.FindIndex(
            parts,
            part => part.Equals("Briefing", StringComparison.OrdinalIgnoreCase));
        if (briefingIndex >= 0 && briefingIndex + 1 < parts.Length)
        {
            return parts[briefingIndex + 1];
        }

        string result = parts.LastOrDefault() ?? string.Empty;
        return result.Replace(' ', '_');
    }

    private static string? TryGetBriefingKey(string resourceName)
    {
        string[] parts = NormalizeResourceName(resourceName)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int briefingIndex = Array.FindIndex(
            parts,
            part => part.Equals("Briefing", StringComparison.OrdinalIgnoreCase));
        return briefingIndex >= 0 && briefingIndex + 2 < parts.Length
            ? parts[briefingIndex + 1]
            : null;
    }

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int leftIndex = 0;
            int rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    int leftStart = leftIndex;
                    int rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                    {
                        leftIndex++;
                    }

                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                    {
                        rightIndex++;
                    }

                    ReadOnlySpan<char> leftNumber = left.AsSpan(leftStart, leftIndex - leftStart).TrimStart('0');
                    ReadOnlySpan<char> rightNumber = right.AsSpan(rightStart, rightIndex - rightStart).TrimStart('0');
                    int lengthComparison = leftNumber.Length.CompareTo(rightNumber.Length);
                    if (lengthComparison != 0)
                    {
                        return lengthComparison;
                    }

                    int numberComparison = leftNumber.CompareTo(rightNumber, StringComparison.Ordinal);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }

                    continue;
                }

                int characterComparison = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (characterComparison != 0)
                {
                    return characterComparison;
                }

                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }
    }

    private sealed record SoundArchiveIndex(
        string SourcePath,
        long FileLength,
        IReadOnlyList<ResEntry> Entries);

    private readonly record struct ArchiveHeader(
        int EntryCount,
        int DirectoryOffset,
        int NamePoolSize,
        int DirectoryLength,
        int CatalogLength);
}

public enum DatabaseSoundLinkKind
{
    DirectResource,
    BriefingFolder,
}

public sealed record DatabaseSoundLink(
    DatabaseSoundLinkKind Kind,
    string Source,
    string Reference,
    SoundResourceInfo? Resource,
    bool IsPreferred)
{
    public bool IsResolved => Resource is not null;
}

public static class DatabaseSoundLinkResolver
{
    private const int AcksFileFieldId = 1;

    public static IReadOnlyList<DatabaseSoundLink> Resolve(
        string tableKey,
        DatabaseRecord record,
        SoundArchiveCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableKey);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(catalog);

        if (tableKey.StartsWith("acks.", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDirectReferences(record, catalog);
        }

        if (tableKey.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveBriefing(record, catalog);
        }

        return [];
    }

    private static IReadOnlyList<DatabaseSoundLink> ResolveDirectReferences(
        DatabaseRecord record,
        SoundArchiveCatalog catalog)
    {
        var result = new List<DatabaseSoundLink>();
        foreach (DatabaseField field in record.Fields)
        {
            foreach (StructuredListStringValue reference in field.GetStructuredListStringValues(AcksFileFieldId))
            {
                if (string.IsNullOrWhiteSpace(reference.Value))
                {
                    continue;
                }

                string source = $"{field.DisplayName} · реплика {reference.ItemIndex + 1:N0}";
                AddMatches(
                    result,
                    DatabaseSoundLinkKind.DirectResource,
                    source,
                    reference.Value,
                    catalog.FindExact(reference.Value));
            }
        }

        return result
            .OrderBy(link => link.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(link => link.Resource?.Archive.Priority ?? int.MaxValue)
            .ThenBy(link => link.Resource?.ResourceName ?? link.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DatabaseSoundLink> ResolveBriefing(
        DatabaseRecord record,
        SoundArchiveCatalog catalog)
    {
        string key = string.IsNullOrWhiteSpace(record.IdentityValue)
            ? record.Name
            : record.IdentityValue;
        key = key.Trim().Replace(' ', '_');
        var result = new List<DatabaseSoundLink>();
        AddMatches(
            result,
            DatabaseSoundLinkKind.BriefingFolder,
            $"Брифинг {key}",
            $"Briefing\\{key}\\*",
            catalog.FindBriefing(key));
        return result;
    }

    private static void AddMatches(
        ICollection<DatabaseSoundLink> destination,
        DatabaseSoundLinkKind kind,
        string source,
        string reference,
        IReadOnlyList<SoundResourceInfo> matches)
    {
        if (matches.Count == 0)
        {
            destination.Add(new(kind, source, reference, null, false));
            return;
        }

        var preferredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < matches.Count; index++)
        {
            SoundResourceInfo match = matches[index];
            destination.Add(new(
                kind,
                source,
                reference,
                match,
                preferredNames.Add(match.ResourceName)));
        }
    }
}
