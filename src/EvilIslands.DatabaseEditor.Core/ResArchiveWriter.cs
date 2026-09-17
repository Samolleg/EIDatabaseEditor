using System.Buffers.Binary;

namespace EvilIslands.DatabaseEditor.Core;

internal sealed record AddedResResource(string Name, byte[] Data);

internal static class ResArchiveWriter
{
    public static byte[] Rebuild(
        ResArchiveDocument archive,
        IReadOnlyDictionary<int, byte[]> replacements)
    {
        if (replacements.Count == 0)
        {
            return archive.OriginalBytes.ToArray();
        }

        var source = archive.OriginalBytes.Span;
        var estimatedLength = archive.Length + archive.Entries.Sum(entry =>
            replacements.TryGetValue(entry.Index, out var value) ? value.Length - entry.Size : 0);
        using var output = new MemoryStream(Math.Max(estimatedLength, ResArchiveDocument.HeaderSize));
        var newOffsets = new Dictionary<int, int>(archive.Entries.Count);
        var newSizes = new Dictionary<int, int>(archive.Entries.Count);
        var cursor = 0;

        foreach (var entry in archive.Entries.OrderBy(entry => entry.Offset))
        {
            if (entry.Offset < cursor)
            {
                throw new DatabaseFormatException("Overlapping resources cannot be rebuilt.", entry.Name, entry.Offset);
            }

            output.Write(source[cursor..entry.Offset]);
            newOffsets.Add(entry.Index, checked((int)output.Position));
            if (replacements.TryGetValue(entry.Index, out var replacement))
            {
                output.Write(replacement);
                newSizes.Add(entry.Index, replacement.Length);
            }
            else
            {
                output.Write(source.Slice(entry.Offset, entry.Size));
                newSizes.Add(entry.Index, entry.Size);
            }

            cursor = checked(entry.Offset + entry.Size);
        }

        output.Write(source[cursor..archive.DirectoryOffset]);
        var newDirectoryOffset = checked((int)output.Position);
        output.Write(source[archive.DirectoryOffset..]);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), checked((uint)newDirectoryOffset));

        foreach (var entry in archive.Entries)
        {
            var directoryEntryOffset = checked(newDirectoryOffset + entry.Index * ResArchiveDocument.EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(directoryEntryOffset + 4, 4),
                checked((uint)newSizes[entry.Index]));
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(directoryEntryOffset + 8, 4),
                checked((uint)newOffsets[entry.Index]));
        }

        return result;
    }

    public static byte[] Rebuild(
        ResArchiveDocument archive,
        IReadOnlyDictionary<int, byte[]> replacements,
        IReadOnlyList<AddedResResource> additions,
        IReadOnlySet<int>? removals = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(additions);
        if (additions.Count == 0 && replacements.Count == 0 && (removals is null || removals.Count == 0))
        {
            return archive.OriginalBytes.ToArray();
        }

        var names = new HashSet<string>(
            archive.Entries.Select(entry => entry.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (AddedResResource addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition.Name) || !names.Add(addition.Name))
            {
                throw new DatabaseFormatException(
                    $"RES resource name '{addition.Name}' is empty or duplicated.",
                    addition.Name);
            }

            if (!addition.Name.All(character => character <= 0x7F && !char.IsControl(character)))
            {
                throw new DatabaseFormatException(
                    $"RES resource name '{addition.Name}' is not a valid ASCII name.",
                    addition.Name);
            }
        }

        var resources = new List<BuildResource>(archive.Entries.Count + additions.Count);
        foreach (ResEntry entry in archive.Entries.OrderBy(entry => entry.Offset))
        {
            if (removals?.Contains(entry.Index) == true) continue;
            byte[] data = replacements.TryGetValue(entry.Index, out byte[]? replacement)
                ? replacement
                : archive.GetResourceBytes(entry).ToArray();
            resources.Add(new BuildResource(entry.Name, data));
        }

        resources.AddRange(additions.Select(addition => new BuildResource(addition.Name, addition.Data)));
        return BuildCompleteArchive(resources);
    }

    private static byte[] BuildCompleteArchive(IReadOnlyList<BuildResource> resources)
    {
        if (resources.Count == 0)
        {
            throw new DatabaseFormatException("RES archive cannot be empty.");
        }

        int count = resources.Count;
        var table = new BuildResource?[count];
        var groups = resources
            .GroupBy(resource => ComputeHash(resource.Name, count))
            .OrderBy(group => group.Key)
            .ToArray();
        foreach (IGrouping<int, BuildResource> group in groups)
        {
            table[group.Key] = group.First();
        }

        using IEnumerator<int> freeSlots = Enumerable.Range(0, count)
            .Where(index => table[index] is null)
            .GetEnumerator();
        var chains = new Dictionary<int, List<int>>();
        foreach (IGrouping<int, BuildResource> group in groups)
        {
            var slots = new List<int> { group.Key };
            foreach (BuildResource resource in group.Skip(1))
            {
                if (!freeSlots.MoveNext())
                {
                    throw new DatabaseFormatException("RES hash table has no free collision slot.");
                }

                table[freeSlots.Current] = resource;
                slots.Add(freeSlots.Current);
            }

            chains.Add(group.Key, slots);
        }

        var nextIndices = Enumerable.Repeat(uint.MaxValue, count).ToArray();
        foreach (IReadOnlyList<int> slots in chains.Values)
        {
            for (var index = 0; index + 1 < slots.Count; index++)
            {
                nextIndices[slots[index]] = checked((uint)slots[index + 1]);
            }
        }

        var offsets = new Dictionary<BuildResource, int>(ReferenceEqualityComparer.Instance);
        using var data = new MemoryStream();
        data.SetLength(ResArchiveDocument.HeaderSize);
        data.Position = ResArchiveDocument.HeaderSize;
        foreach (BuildResource resource in resources)
        {
            offsets.Add(resource, checked((int)data.Position));
            data.Write(resource.Data);
        }

        int directoryOffset = checked((int)data.Position);
        var namePool = new MemoryStream();
        for (var index = 0; index < table.Length; index++)
        {
            BuildResource resource = table[index]
                ?? throw new DatabaseFormatException("RES hash table contains an empty slot.");
            byte[] nameBytes = ResResourceNameCodec.Encode(resource.Name);
            int nameOffset = checked((int)namePool.Position);
            namePool.Write(nameBytes);

            Span<byte> entry = new byte[ResArchiveDocument.EntrySize];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, nextIndices[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint)resource.Data.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)offsets[resource]));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], checked((ushort)nameBytes.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[18..], checked((uint)nameOffset));
            data.Write(entry);
        }

        namePool.Position = 0;
        namePool.CopyTo(data);
        byte[] result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)directoryOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)namePool.Length));
        return result;
    }

    private static int ComputeHash(string name, int count)
    {
        return ResResourceNameCodec.ComputeHash(name, count);
    }

    private sealed record BuildResource(string Name, byte[] Data);
}
