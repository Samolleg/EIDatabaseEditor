using System.Buffers.Binary;
using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record MmpImage(int Width, int Height, string Format, byte[] Bgra, bool PremultipliedAlpha);

/// <summary>Read-only top-level preview; native 76-byte MMP header and bounded pixel decoding.</summary>
public static class MmpPreviewDecoder
{
    public const int MaximumInputBytes = 128 * 1024 * 1024;
    public const int MaximumPixels = 16 * 1024 * 1024;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static void Need(bool condition, string error)
    { if (!condition) throw new InvalidDataException(error); }

    public static MmpImage Decode(ReadOnlySpan<byte> data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Need(data.Length >= 76, "Заголовок MMP обрезан.");
        Need(data.Length <= MaximumInputBytes, "Ресурс превышает лимит предпросмотра 128 МиБ.");
        Need(U32(data, 0) == 0x00504D4D, "Неверная сигнатура MMP.");
        uint w = U32(data, 4), h = U32(data, 8), format = U32(data, 16), depth = U32(data, 20);
        Need(w > 0 && h > 0 && w <= 8192 && h <= 8192 && (ulong)w * h <= MaximumPixels,
            "Размер изображения превышает лимит предпросмотра (8192 на сторону, 16 млн пикселей) или равен нулю.");
        long start = 76L + U32(data, 72);
        Need(start <= data.Length, "Смещение данных MMP выходит за ресурс.");
        int width = (int)w, height = (int)h;
        ReadOnlySpan<byte> pixels = data[(int)start..];
        string name = format switch
        {
            0x5650 => "RGB 565", 0x5551 => "ARGB 1555", 0x4444 => "ARGB 4444", 0x8888 => "ARGB 8888",
            0x31545844 => "DXT1", 0x32545844 => "DXT2", 0x33545844 => "DXT3",
            0x34545844 => "DXT4", 0x35545844 => "DXT5", 0x33544E50 => "PNT3",
            _ => throw new NotSupportedException($"Формат MMP 0x{format:X8} пока не поддерживается."),
        };
        int expectedDepth = format switch { 0x5650 or 0x5551 or 0x4444 => 16, 0x8888 or 0x33544E50 => 32, 0x31545844 => 4, _ => 8 };
        Need(depth == expectedDepth, "Битность MMP не соответствует формату.");
        bool pnt = format == 0x33544E50;
        int required = pnt ? 0 : format <= 0x8888 ? checked(width * height * (int)depth / 8) :
            checked(((width + 3) / 4) * ((height + 3) / 4) * (format == 0x31545844 ? 8 : 16));
        Need(pixels.Length >= required, "Данные верхнего уровня MMP обрезаны.");
        byte[] output = new byte[checked(width * height * 4)];
        if (pnt)
        {
            uint linear = U32(data, 12);
            Need(linear > 0 && linear <= pixels.Length, "Длина PNT3 выходит за ресурс.");
            DecodePnt(pixels[..(int)linear], output, token);
        }
        else if (format <= 0x8888)
        {
            uint[] masks = [U32(data, 60), U32(data, 48), U32(data, 36), U32(data, 24)];
            uint used = 0;
            for (int c = 0; c < 4; c++)
            {
                uint mask = masks[c];
                Need(mask != 0 || c == 3, "В MMP отсутствует цветовая маска.");
                if (mask == 0) continue;
                int shift = BitOperations.TrailingZeroCount(mask);
                uint normalized = mask >> shift;
                Need((mask & used) == 0 && (depth == 32 || (mask >> (int)depth) == 0) &&
                    (normalized & (normalized + 1)) == 0 && normalized <= 255, "Некорректные или пересекающиеся маски MMP.");
                used |= mask;
            }
            int bytes = (int)depth / 8;
            for (int i = 0; i < width * height; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                uint value = bytes == 4 ? U32(pixels, i * bytes) : U16(pixels, i * bytes);
                for (int c = 0; c < 4; c++)
                {
                    uint mask = masks[c];
                    if (mask == 0) { output[i * 4 + c] = 255; continue; }
                    int shift = BitOperations.TrailingZeroCount(mask);
                    uint max = mask >> shift;
                    output[i * 4 + c] = (byte)((((value & mask) >> shift) * 255 + max / 2) / max);
                }
            }
        }
        else DecodeDxt(pixels, output, width, height, format, token);
        return new(width, height, name, output, pnt || format is 0x32545844 or 0x34545844);
    }

    private static void DecodePnt(ReadOnlySpan<byte> source, Span<byte> output, CancellationToken token)
    {
        // Native PutOneLayer consumes groups of four premultiplied BGRA pixels.
        // A nonzero DWORD with zero alpha at a group boundary skips that many output bytes.
        int read = 0, write = 0;
        Need(output.Length % 16 == 0, "PNT3 требует группы по четыре пикселя.");
        while (write < output.Length)
        {
            token.ThrowIfCancellationRequested();
            Need(read <= source.Length - 4, "Поток PNT3 обрезан.");
            uint word = U32(source, read);
            if ((word >> 24) == 0 && word != 0)
            {
                Need(word % 16 == 0 && word <= output.Length - write, "Некорректный пропуск PNT3.");
                write += (int)word;
                read += 4;
            }
            else
            {
                Need(read <= source.Length - 16 && write <= output.Length - 16, "Группа пикселей PNT3 обрезана.");
                source.Slice(read, 16).CopyTo(output[write..]);
                read += 16; write += 16;
            }
        }
        Need(read == source.Length, "После изображения PNT3 остались лишние данные.");
    }

    private static void Color565(ushort value, Span<byte> color)
    {
        int r = value >> 11, g = (value >> 5) & 63, b = value & 31;
        color[0] = (byte)((b << 3) | (b >> 2));
        color[1] = (byte)((g << 2) | (g >> 4));
        color[2] = (byte)((r << 3) | (r >> 2));
        color[3] = 255;
    }

    private static void DecodeDxt(ReadOnlySpan<byte> source, Span<byte> output, int width, int height,
        uint format, CancellationToken token)
    {
        bool dxt1 = format == 0x31545844;
        bool explicitAlpha = format is 0x32545844 or 0x33545844;
        int offset = 0;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        for (int y = 0; y < height; y += 4)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x += 4)
            {
                var block = source.Slice(offset, dxt1 ? 8 : 16);
                int co = dxt1 ? 0 : 8;
                ushort c0 = U16(block, co), c1 = U16(block, co + 2);
                Color565(c0, colors); Color565(c1, colors[4..]);
                bool three = dxt1 && c0 <= c1;
                for (int c = 0; c < 3; c++)
                {
                    colors[8 + c] = (byte)(three ? (colors[c] + colors[4 + c]) / 2 : (2 * colors[c] + colors[4 + c]) / 3);
                    colors[12 + c] = (byte)(three ? 0 : (colors[c] + 2 * colors[4 + c]) / 3);
                }
                colors[11] = 255; colors[15] = three ? (byte)0 : (byte)255;
                uint indices = U32(block, co + 4);
                ulong alphaBits = 0;
                if (!dxt1 && !explicitAlpha)
                {
                    alphas[0] = block[0]; alphas[1] = block[1];
                    if (alphas[0] > alphas[1])
                        for (int i = 1; i <= 6; i++) alphas[i + 1] = (byte)(((7 - i) * alphas[0] + i * alphas[1]) / 7);
                    else
                    {
                        for (int i = 1; i <= 4; i++) alphas[i + 1] = (byte)(((5 - i) * alphas[0] + i * alphas[1]) / 5);
                        alphas[6] = 0; alphas[7] = 255;
                    }
                    for (int i = 0; i < 6; i++) alphaBits |= (ulong)block[i + 2] << (i * 8);
                }
                for (int p = 0; p < 16; p++)
                {
                    int px = x + p % 4, py = y + p / 4;
                    if (px >= width || py >= height) continue;
                    int target = (py * width + px) * 4;
                    colors.Slice((int)((indices >> (2 * p)) & 3) * 4, 4).CopyTo(output[target..]);
                    if (!dxt1) output[target + 3] = explicitAlpha
                        ? (byte)(((block[p / 2] >> ((p % 2) * 4)) & 15) * 17)
                        : alphas[(int)((alphaBits >> (3 * p)) & 7)];
                }
                offset += dxt1 ? 8 : 16;
            }
        }
    }
}

