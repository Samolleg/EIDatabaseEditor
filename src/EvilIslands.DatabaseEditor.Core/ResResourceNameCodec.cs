using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

internal static class ResResourceNameCodec
{
    private static readonly Encoding Windows1251 = CreateWindows1251();

    public static byte[] Encode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Windows1251.GetBytes(name);
    }

    public static string Decode(ReadOnlySpan<byte> bytes) => Windows1251.GetString(bytes);

    public static int ComputeHash(string name, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        int sum = 0;
        foreach (byte value in Encode(name))
        {
            sum += value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value;
        }

        return sum % count;
    }

    private static Encoding CreateWindows1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1251,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
