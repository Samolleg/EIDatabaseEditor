using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record MobAppearance(string Path, uint Id, string Name, string Prototype, string Model,
    string Primary, string Secondary, Vector3 Morph, string[] Parts)
{
    public string Label => $"{Id} · {Prototype} · {Name}";

    public static IReadOnlyList<MobAppearance> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 128 * 1024 * 1024) throw new InvalidDataException("MOB превышает 128 МиБ.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        return Parse(path, bytes);
    }

    public static IReadOnlyList<MobAppearance> Parse(string path, byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string Text(ReadOnlyMemory<byte> data) => Encoding.GetEncoding(1251).GetString(data.Span).TrimEnd('\0');
        var root = Chunks(bytes).SingleOrDefault(c => c.Id == 0xA000);
        if (root.Data.IsEmpty) throw new InvalidDataException("Нет заголовка MOB.");
        var section = Chunks(root.Data).SingleOrDefault(c => c.Id == 0xB000);
        if (section.Data.IsEmpty) return [];
        var result = new List<MobAppearance>();
        foreach (var chunk in Chunks(section.Data).Where(c => c.Id == 0xBBBB0000))
        {
            var fields = Chunks(chunk.Data).Where(c => c.Id is 0xB002 or 0xB004 or 0xB006 or 0xB007 or 0xB008 or 0xB00C or 0xB00D or 0xBBBB0002)
                .ToDictionary(c => c.Id, c => c.Data); // Logic blocks may legitimately repeat.
            ReadOnlyMemory<byte> Field(uint id) => fields.GetValueOrDefault(id);
            var id = Field(0xB002); var morph = Field(0xB00C); var parts = Field(0xB00D);
            if (id.Length != 4 || morph.Length != 12 || parts.Length < 4)
                throw new InvalidDataException("Повреждены параметры внешности существа MOB.");
            var list = Chunks(parts[4..]);
            if (list.Count != BinaryPrimitives.ReadUInt32LittleEndian(parts.Span) || list.Any(c => c.Id != 0xB00D))
                throw new InvalidDataException("Повреждён список частей MOB.");
            float F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(morph.Span[offset..]);
            var m = new Vector3(F(4), F(0), F(8)); // MOB: grace, strength, tall.
            if (!float.IsFinite(m.X + m.Y + m.Z)) throw new InvalidDataException("Некорректная комплекция MOB.");
            result.Add(new(System.IO.Path.GetFullPath(path), BinaryPrimitives.ReadUInt32LittleEndian(id.Span),
                Text(Field(0xB004)), Text(Field(0xBBBB0002)), Text(Field(0xB006)),
                Text(Field(0xB007)), Text(Field(0xB008)), m, list.Select(c => Text(c.Data)).ToArray()));
        }
        return result;
    }

    private static List<(uint Id, ReadOnlyMemory<byte> Data)> Chunks(ReadOnlyMemory<byte> data)
    {
        var result = new List<(uint, ReadOnlyMemory<byte>)>();
        while (!data.IsEmpty)
        {
            if (data.Length < 8) throw new InvalidDataException("Оборван заголовок MOB.");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.Span);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[4..]);
            if (size < 8 || size > data.Length) throw new InvalidDataException("Некорректная длина блока MOB.");
            result.Add((id, data.Slice(8, (int)size - 8))); data = data[(int)size..];
        }
        return result;
    }
}
