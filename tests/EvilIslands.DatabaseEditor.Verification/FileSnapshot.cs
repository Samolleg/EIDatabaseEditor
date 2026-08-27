using System.Security.Cryptography;

namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record FileSnapshot(string Path, long Length, string Sha256)
{
    public static async Task<FileSnapshot> CaptureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new FileSnapshot(fullPath, stream.Length, Convert.ToHexString(hash));
    }

    public bool ContentEquals(FileSnapshot other) =>
        Length == other.Length &&
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
}
