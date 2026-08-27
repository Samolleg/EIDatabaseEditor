using System.Buffers.Binary;

namespace EvilIslands.DatabaseEditor.Core;

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
}
