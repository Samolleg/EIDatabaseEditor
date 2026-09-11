using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EvilIslands.DatabaseEditor.Core;

public enum MmpDxtChoice { Auto, Dxt1, Dxt3, Dxt5, Dxt1Threshold, MapAtlasX2, CompactX2, CompactAtlas512 }
public sealed record MmpConversionResult(byte[] Bytes, string Format, int Mips, bool HadSmoothAlpha, bool Layer);

public static class MmpDxtConverter
{
    public static MmpImage PrepareCompact(MmpImage image, bool alreadyAtlas, CancellationToken token = default)
    {
        int expected = alreadyAtlas ? 1024 : 512;
        if (image.Width != expected || image.Height != expected || image.Bgra.Length != expected * expected * 4)
            throw new InvalidDataException($"Этот режим ожидает {expected}×{expected}. Выберите правильный тип входа.");
        int side = expected / 2;
        byte[] pixels = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < side; x++)
            {
                int dest = (y * side + x) * 4, alpha = 0;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    alpha += image.Bgra[((2 * y + dy) * expected + 2 * x + dx) * 4 + 3];
                pixels[dest + 3] = (byte)((alpha + 2) / 4);
                for (int c = 0; c < 3; c++)
                {
                    int weighted = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    {
                        int p = ((2 * y + dy) * expected + 2 * x + dx) * 4;
                        weighted += image.Bgra[p + c] * (image.PremultipliedAlpha ? 255 : image.Bgra[p + 3]);
                    }
                    pixels[dest + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (weighted + alpha / 2) / alpha);
                }
            }
        }
        var reduced = new MmpImage(side, side, image.Format, pixels, false);
        return alreadyAtlas ? reduced : RepeatAtlasX2(reduced, token);
    }
    public static MmpImage RepeatAtlasX2(MmpImage image, CancellationToken token = default)
    {
        if (image.Width < 1 || image.Height < 1 || image.Width > 4096 || image.Height > 4096 ||
            (long)image.Width * image.Height * 4 > MmpPreviewDecoder.MaximumPixels ||
            image.Bgra.Length != checked(image.Width * image.Height * 4))
            throw new InvalidDataException("Атлас x2 превышает допустимый размер изображения.");
        int width = image.Width * 2, height = image.Height * 2;
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var row = image.Bgra.AsSpan((y % image.Height) * image.Width * 4, image.Width * 4);
            row.CopyTo(pixels.AsSpan(y * width * 4));
            row.CopyTo(pixels.AsSpan((y * width + image.Width) * 4));
        }
        return image with { Width = width, Height = height, Bgra = pixels };
    }
    public const string TexconvSha256 = "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06";
    private static void Put(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
    private static int Get(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset));

    public static async Task<MmpConversionResult> ConvertAsync(MmpImage image, MmpDxtChoice choice,
        string compressor, string scratch, CancellationToken token, byte alphaThreshold = 128)
    {
        if (choice is MmpDxtChoice.CompactX2 or MmpDxtChoice.CompactAtlas512)
            image = PrepareCompact(image, choice == MmpDxtChoice.CompactAtlas512, token);
        if (choice == MmpDxtChoice.MapAtlasX2) image = RepeatAtlasX2(image, token);
        using (var exe = File.OpenRead(compressor))
            if (Convert.ToHexString(SHA256.HashData(exe)) != TexconvSha256)
                throw new IOException("Компрессор texconv не соответствует проверенной версии may2026.");
        if (image.Width < 1 || image.Height < 1 || image.Width > 8192 || image.Height > 8192 ||
            (long)image.Width * image.Height > MmpPreviewDecoder.MaximumPixels ||
            image.Bgra.Length != checked(image.Width * image.Height * 4))
            throw new InvalidDataException("Некорректные размеры изображения.");
        byte[] straight = (byte[])image.Bgra.Clone();
        bool smooth = false;
        for (int i = 0; i < straight.Length; i += 4)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            int alpha = straight[i + 3];
            smooth |= alpha is > 0 and < 255;
            if (image.PremultipliedAlpha)
                for (int c = 0; c < 3; c++) straight[i + c] = alpha == 0 ? (byte)0 :
                    (byte)Math.Min(255, (straight[i + c] * 255 + alpha / 2) / alpha);
        }
        if (smooth && choice == MmpDxtChoice.Dxt1)
            throw new InvalidDataException("Есть плавная прозрачность: выберите DXT3/DXT5 либо явное упрощение DXT1.");
        string format = choice switch { MmpDxtChoice.Dxt3 => "DXT3", MmpDxtChoice.Dxt5 => "DXT5",
            MmpDxtChoice.Auto when smooth => "DXT5", _ => "DXT1" };
        if (choice is MmpDxtChoice.Dxt1Threshold or MmpDxtChoice.MapAtlasX2)
            for (int i = 3; i < straight.Length; i += 4) straight[i] = straight[i] >= alphaThreshold ? (byte)255 : (byte)0;
        var levels = new List<byte[]> { straight };
        int w = image.Width, h = image.Height;
        while (w > 1 || h > 1)
        {
            token.ThrowIfCancellationRequested();
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            byte[] previous = levels[^1], next = new byte[nw * nh * 4];
            // Area filter: weight straight colour by alpha before averaging; unpremultiply once.
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
            {
                if (x == 0) token.ThrowIfCancellationRequested();
                double alpha = 0, area = 0; double[] color = new double[3];
                double x0 = (double)x * w / nw, x1 = (double)(x + 1) * w / nw;
                double y0 = (double)y * h / nh, y1 = (double)(y + 1) * h / nh;
                for (int sy = (int)y0; sy < Math.Ceiling(y1); sy++) for (int sx = (int)x0; sx < Math.Ceiling(x1); sx++)
                {
                    double weight = (Math.Min(x1, sx + 1) - Math.Max(x0, sx)) * (Math.Min(y1, sy + 1) - Math.Max(y0, sy));
                    int p = (sy * w + sx) * 4; double a = previous[p + 3] * weight;
                    alpha += a; area += weight; for (int c = 0; c < 3; c++) color[c] += previous[p + c] * a;
                }
                int d = (y * nw + x) * 4; next[d + 3] = (byte)Math.Clamp((int)Math.Round(alpha / area), 0, 255);
                for (int c = 0; c < 3; c++) next[d + c] = alpha == 0 ? (byte)0 : (byte)Math.Clamp((int)Math.Round(color[c] / alpha), 0, 255);
            }
            levels.Add(next); w = nw; h = nh;
        }
        w = image.Width; h = image.Height;
        foreach (byte[] level in levels)
        {
            if (choice is MmpDxtChoice.CompactX2 or MmpDxtChoice.CompactAtlas512)
                for (int i = 3; i < level.Length; i += 4) level[i] = level[i] >= alphaThreshold ? (byte)255 : (byte)0;
            BleedTransparentEdges(level, w, h, token);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        // Keep the existing row order; no vertical flip between the MMP and DDS representations.
        byte[] header = new byte[128]; Put(header, 0, 0x20534444); Put(header, 4, 124);
        Put(header, 8, 0x2100F); Put(header, 12, image.Height); Put(header, 16, image.Width);
        Put(header, 20, image.Width * 4); Put(header, 28, levels.Count);
        Put(header, 76, 32); Put(header, 80, 0x41); Put(header, 88, 32);
        Put(header, 92, 0xff0000); Put(header, 96, 0xff00); Put(header, 100, 0xff); Put(header, 104, unchecked((int)0xff000000)); Put(header, 108, 0x401008);
        Directory.CreateDirectory(scratch);
        string input = Path.Combine(scratch, "input.dds"), output = Path.Combine(scratch, "output"); Directory.CreateDirectory(output);
        await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write))
        { await stream.WriteAsync(header, token); foreach (byte[] level in levels) await stream.WriteAsync(level, token); }
        var start = new ProcessStartInfo(compressor) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nologo", "-nogpu", "-f", format == "DXT1" ? "BC1_UNORM" : format == "DXT3" ? "BC2_UNORM" : "BC3_UNORM",
            "-m", levels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", output, input }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить texconv.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        string log = await stdout + await stderr;
        if (process.ExitCode != 0) throw new IOException("texconv: " + log);
        byte[] dds = await File.ReadAllBytesAsync(Path.Combine(output, "input.dds"), token);
        int fourcc = format == "DXT1" ? 0x31545844 : format == "DXT3" ? 0x33545844 : 0x35545844;
        if (dds.Length < 128 || Get(dds, 0) != 0x20534444 || Get(dds, 84) != fourcc || Get(dds, 16) != image.Width || Get(dds, 12) != image.Height || Get(dds, 28) != levels.Count)
            throw new InvalidDataException("Неожиданный заголовок DDS от компрессора.");
        byte[] mmp = new byte[76 + dds.Length - 128]; Put(mmp, 0, 0x00504D4D);
        Put(mmp, 4, image.Width); Put(mmp, 8, image.Height); Put(mmp, 12, levels.Count);
        Put(mmp, 16, fourcc); Put(mmp, 20, format == "DXT1" ? 4 : 8);
        dds.AsSpan(128).CopyTo(mmp.AsSpan(76)); Validate(mmp);
        return new(mmp, format, levels.Count, smooth, image.Format == "PNT3");
    }

    private static void BleedTransparentEdges(byte[] pixels, int width, int height, CancellationToken token)
    {
        // Extend visible RGB under zero alpha before block compression; alpha itself is untouched.
        int count = width * height;
        var known = new bool[count]; var queue = new int[count]; int head = 0, tail = 0;
        for (int p = 0; p < count; p++) if (pixels[p * 4 + 3] != 0) { known[p] = true; queue[tail++] = p; }
        while (head < tail)
        {
            if ((head & 4095) == 0) token.ThrowIfCancellationRequested();
            int p = queue[head++], x = p % width, y = p / width;
            void Visit(int n)
            {
                if (known[n]) return; known[n] = true;
                for (int c = 0; c < 3; c++) pixels[n * 4 + c] = pixels[p * 4 + c];
                queue[tail++] = n;
            }
            if (x > 0) Visit(p - 1); if (x + 1 < width) Visit(p + 1);
            if (y > 0) Visit(p - width); if (y + 1 < height) Visit(p + width);
        }
    }

    public static void Validate(byte[] bytes)
    {
        if (bytes.Length < 76 || Get(bytes, 0) != 0x00504D4D || Get(bytes, 72) != 0) throw new InvalidDataException("Заголовок MMP некорректен.");
        int w = Get(bytes, 4), h = Get(bytes, 8), format = Get(bytes, 16), count = Get(bytes, 12), offset = 76;
        int block = format == 0x31545844 ? 8 : format is 0x33545844 or 0x35545844 ? 16 : throw new InvalidDataException("Не DXT MMP.");
        if (w < 1 || h < 1 || w > 8192 || h > 8192 || Get(bytes,20) != block / 2) throw new InvalidDataException("Размер/битность MMP.");
        int expected = 1 + (int)Math.Floor(Math.Log2(Math.Max(w,h)));
        if (count != expected) throw new InvalidDataException("Неполная mip-цепочка.");
        for (int level = 0; level < count; level++)
        {
            int size = checked(Math.Max(1,(w+3)/4)*Math.Max(1,(h+3)/4)*block);
            if (offset + size > bytes.Length) throw new InvalidDataException("Mip-уровень обрезан.");
            byte[] single = new byte[76+size]; bytes.AsSpan(0,76).CopyTo(single); Put(single,4,w); Put(single,8,h); Put(single,12,1);
            bytes.AsSpan(offset,size).CopyTo(single.AsSpan(76)); _ = MmpPreviewDecoder.Decode(single);
            offset += size; w = Math.Max(1,w/2); h = Math.Max(1,h/2);
        }
        if (offset != bytes.Length) throw new InvalidDataException("Лишние данные после mip-цепочки.");
    }
}
