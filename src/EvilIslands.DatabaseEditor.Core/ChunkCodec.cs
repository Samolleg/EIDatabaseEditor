using System.Buffers.Binary;

namespace EvilIslands.DatabaseEditor.Core;

internal readonly record struct ChunkSlice(
    byte Id,
    int HeaderOffset,
    int HeaderSize,
    int DataOffset,
    int DataLength)
{
    public int EndOffset => checked(DataOffset + DataLength);

    public int TotalLength => checked(HeaderSize + DataLength);
}

internal static class ChunkCodec
{
    public static IReadOnlyList<ChunkSlice> ReadList(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        string path,
        int baseOffset = 0,
        CancellationToken cancellationToken = default)
    {
        if ((uint)start > (uint)data.Length || end < start || end > data.Length)
        {
            throw new DatabaseFormatException("Chunk range is invalid.", path, baseOffset + start);
        }

        var chunks = new List<ChunkSlice>();
        var position = start;
        while (position < end)
        {
            if ((chunks.Count & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var headerOffset = position;
            if (end - position < 2)
            {
                throw new DatabaseFormatException("Truncated chunk header.", path, baseOffset + position);
            }

            var id = data[position++];
            uint encodedLength = data[position++];
            var headerSize = 2;
            if ((encodedLength & 1) != 0)
            {
                if (end - position < 3)
                {
                    throw new DatabaseFormatException(
                        "Truncated extended chunk length.", path, baseOffset + headerOffset);
                }

                encodedLength |= (uint)data[position++] << 8;
                encodedLength |= (uint)data[position++] << 16;
                encodedLength |= (uint)data[position++] << 24;
                headerSize = 5;
            }

            var lengthValue = encodedLength >> 1;
            if (lengthValue > int.MaxValue || (long)position + lengthValue > end)
            {
                throw new DatabaseFormatException(
                    $"Chunk {id} exceeds its parent.", path, baseOffset + headerOffset);
            }

            var length = (int)lengthValue;
            chunks.Add(new(id, headerOffset, headerSize, position, length));
            position += length;
        }

        return chunks;
    }

    public static byte[] Encode(byte id, ReadOnlySpan<byte> payload, int preferredHeaderSize)
    {
        var extended = payload.Length >= 128 || preferredHeaderSize == 5;
        var headerSize = extended ? 5 : 2;
        var result = GC.AllocateUninitializedArray<byte>(checked(headerSize + payload.Length));
        result[0] = id;
        var encoded = checked(((uint)payload.Length << 1) | (extended ? 1u : 0u));
        if (extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1, 4), encoded);
        }
        else
        {
            result[1] = (byte)encoded;
        }

        payload.CopyTo(result.AsSpan(headerSize));
        return result;
    }
}
