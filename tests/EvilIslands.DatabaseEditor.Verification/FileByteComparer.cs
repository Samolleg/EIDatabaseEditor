namespace EvilIslands.DatabaseEditor.Verification;

internal static class FileByteComparer
{
    public static async Task<bool> AreEqualAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken = default)
    {
        const int BufferSize = 128 * 1024;
        await using var left = OpenRead(leftPath, BufferSize);
        await using var right = OpenRead(rightPath, BufferSize);
        if (left.Length != right.Length)
        {
            return false;
        }

        byte[] leftBuffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        byte[] rightBuffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        while (true)
        {
            int leftRead = await left.ReadAsync(leftBuffer, cancellationToken).ConfigureAwait(false);
            int rightRead = await right.ReadAsync(rightBuffer, cancellationToken).ConfigureAwait(false);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static FileStream OpenRead(string path, int bufferSize) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
