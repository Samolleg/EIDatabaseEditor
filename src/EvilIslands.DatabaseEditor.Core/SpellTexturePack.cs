using System.Buffers.Binary;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record SpellTextureOverride(string Spell, string Original, string Replacement);

public static class SpellTexturePack
{
    public const string Header = "EISPELLTEXTURES1";
    public static string Normalize(string text) => text.Replace('\\', '/').ToLowerInvariant();
    private static bool Name(string text, bool texture) => text.Length is > 0 and <= 120 &&
        text[0] != '/' && text[^1] != '/' && !text.Contains("..", StringComparison.Ordinal) &&
        text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' || texture && c is '.' or '/') &&
        (!texture || text.Length > 4 && text.EndsWith(".mmp", StringComparison.Ordinal));

    public static IReadOnlyList<SpellTextureOverride> Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 262144) throw new InvalidDataException("Файл привязок слишком большой.");
        using var reader = new StringReader(text);
        if (reader.ReadLine() != Header) throw new InvalidDataException("Неизвестный формат привязок.");
        var rows = new List<SpellTextureOverride>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t').Select(Normalize).ToArray();
            if (f.Length != 3 || !Name(f[0], false) || !Name(f[1], true) || !Name(f[2], true) || f[1] == f[2] ||
                !keys.Add(f[0] + "\t" + f[1]) || rows.Count >= 512)
                throw new InvalidDataException("Недопустимая или повторяющаяся привязка. Используйте латиницу, цифры, _ и -; исходное и новое имя должны различаться.");
            rows.Add(new(f[0], f[1], f[2]));
        }
        return rows;
    }
    public static string Serialize(IEnumerable<SpellTextureOverride> rows)
    {
        var text = Header + "\n" + string.Join("", rows.Select(r => $"{Normalize(r.Spell)}\t{Normalize(r.Original)}\t{Normalize(r.Replacement)}\n"));
        _ = Parse(text); return text;
    }
    public static byte[] ReadTexture(TextureResourceCatalog catalog, string name)
    {
        if (!catalog.IsCurrent()) throw new IOException("Источники изменились. Обновите список текстур.");
        var match = catalog.Check(name).Matches.FirstOrDefault() ?? throw new FileNotFoundException(name);
        if (match.Length > MmpPreviewDecoder.MaximumInputBytes) throw new InvalidDataException("MMP превышает лимит размера.");
        using var stream = File.OpenRead(match.Source);
        stream.Position = match.Offset; var data = new byte[match.Length]; stream.ReadExactly(data);
        if (!catalog.IsCurrent()) throw new IOException("Источник изменился во время чтения.");
        ValidateTexture(data); return data;
    }
    public static MmpImage ValidateTexture(byte[] data)
    {
        var image = MmpPreviewDecoder.Decode(data);
        if (image.Format.Contains("PNT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PNT — слой составной текстуры; для эффекта нужен готовый игровой MMP.");
        return image;
    }
    public static void Export(string folder, IReadOnlyList<SpellTextureOverride> rows, IReadOnlyDictionary<string, byte[]> textures)
    {
        string target = Path.GetFullPath(folder);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Выберите новое имя папки пакета. Существующие файлы не перезаписываются.");
        string text = Serialize(rows);
        if (rows.Count == 0) throw new InvalidDataException("Нет привязок для экспорта.");
        var names = rows.Select(r => Normalize(r.Replacement)).Distinct().ToArray();
        var added = names.Select(name =>
        {
            if (!textures.TryGetValue(name, out var data)) throw new InvalidDataException("Нет данных текстуры: " + name);
            ValidateTexture(data); return new AddedResResource(name.Replace('/', '\\'), data);
        }).ToArray();
        byte[] empty = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
        byte[] res = ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(), added);
        var check = ResArchiveDocument.Parse("spell-textures.res", res);
        foreach (var entry in added)
            if (!check.GetResourceBytes(entry.Name).Span.SequenceEqual(entry.Data)) throw new InvalidDataException("Ошибка проверки RES.");
        string staging = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "config")); Directory.CreateDirectory(Path.Combine(staging, "res"));
            File.WriteAllText(Path.Combine(staging, "config", "spell-textures.tsv"), text, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(staging, "res", "spell-textures.res"), res);
            File.WriteAllText(Path.Combine(staging, "README.txt"),
                "Привязки текстур заклинаний — экспериментальный addon с поддержкой EISPELLTEXTURES1.\n" +
                "Этот пакет сам ничего не устанавливает. После отдельного решения об установке: config/spell-textures.tsv положить в config мода; " +
                "res/spell-textures.res подключить в группе textures.res действующего config.ini/REG. Не заменять весь config чужой копией. Перезапустить игру.\n" +
                "TSV содержит полный набор пользовательских привязок, а не дополнение: перед изменением существующего набора откройте его в редакторе.\n" +
                "Имена исходных ресурсов не изменены. Новая текстура применяется к указанному заклинанию вместо указанного атласа во всех поддержанных фазах. " +
                "Сетка кадров, движение, число частиц и режим смешивания остаются игровыми. Иконки не меняются. " +
                "Диагностический этап: игровая проверка обязательна. На удалённом сетевом клиенте и при неизвестном контексте остаётся штатный атлас. Полное восстановление временных фаз после загрузки ещё не подтверждено. Если новый ресурс не подключён, addon оставляет исходный атлас. Изменения требуют игровой проверки; совместимость со старым addon не заявлена.\n");
            _ = ResArchiveDocument.Open(Path.Combine(staging, "res", "spell-textures.res"));
            if (File.ReadAllText(Path.Combine(staging, "config", "spell-textures.tsv")) != text) throw new IOException("Ошибка повторного чтения привязок.");
            Directory.Move(staging, target);
        }
        catch
        {
            // Leave an explicitly incomplete folder for diagnosis; never publish it as a completed pack.
            throw;
        }
    }
    public static (IReadOnlyList<SpellTextureOverride> Rows, Dictionary<string, byte[]> Textures) Open(string folder)
    {
        var rows = Parse(File.ReadAllText(Path.Combine(folder, "config", "spell-textures.tsv")));
        var res = ResArchiveDocument.Open(Path.Combine(folder, "res", "spell-textures.res"));
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in rows.Select(r => r.Replacement).Distinct())
        {
            byte[] data = res.GetResourceBytes(name.Replace('/', '\\')).ToArray(); ValidateTexture(data); textures.Add(name, data);
        }
        return (rows, textures);
    }
}
