using System.Buffers.Binary;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>The game's model-scoped animation registry. ANM contains poses; ADB selects them.</summary>
public sealed class AnimationDatabase
{
    public const int HeaderSize = 44;
    public const int EntrySize = 88;
    public const int MaximumLegacySafeIndex = 253;
    private const uint Signature = 0x00424441;
    private readonly byte[] _header;
    private readonly List<byte[]> _entries;

    public string Model { get; }
    public int Count => _entries.Count;
    public float MinimumHeight => BinaryPrimitives.ReadSingleLittleEndian(_header.AsSpan(32));
    public float MiddleHeight => BinaryPrimitives.ReadSingleLittleEndian(_header.AsSpan(36));
    public float MaximumHeight => BinaryPrimitives.ReadSingleLittleEndian(_header.AsSpan(40));
    public IReadOnlyList<AnimationDatabaseEntry> Entries => _entries.Select((entry, index) => new AnimationDatabaseEntry(index, entry)).ToArray();

    private AnimationDatabase(string model, byte[] header, List<byte[]> entries)
    { Model = model; _header = header; _entries = entries; }

    public static AnimationDatabase Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Signature)
            throw new InvalidDataException("Неверная сигнатура ADB.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (count > 100_000 || bytes.Length != HeaderSize + (long)count * EntrySize)
            throw new InvalidDataException("Размер ADB не соответствует числу записей.");
        string model = Encoding.ASCII.GetString(bytes, 8, 24).TrimEnd('\0');
        var entries = new List<byte[]>((int)count);
        for (int i = 0; i < count; i++) entries.Add(bytes.AsSpan(HeaderSize + i * EntrySize, EntrySize).ToArray());
        return new AnimationDatabase(model, bytes.AsSpan(0, HeaderSize).ToArray(), entries);
    }

    public byte[] ExportBytes()
    {
        var bytes = new byte[HeaderSize + _entries.Count * EntrySize];
        _header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)_entries.Count);
        for (int i = 0; i < _entries.Count; i++) _entries[i].CopyTo(bytes, HeaderSize + i * EntrySize);
        return bytes;
    }

    public void Update(int index, string name, uint flags, uint flags2, uint probability, uint length, float speed,
        uint[] hides, uint[] steps, uint hit, uint sfxFrame, uint[] sfxIndices)
    {
        if ((uint)index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ValidateName(name, index);
        if (probability > 100) throw new ArgumentOutOfRangeException(nameof(probability), "Вероятность: 0–100.");
        if (length == 0 || length > 100_000) throw new ArgumentOutOfRangeException(nameof(length), "Длина должна быть от 1 до 100000 кадров.");
        if (!float.IsFinite(speed) || speed < 0 || speed > 1000) throw new ArgumentOutOfRangeException(nameof(speed));
        if (hides.Length != 2 || steps.Length != 4 || sfxIndices.Length != 4) throw new ArgumentException("Неверный размер полей событий.");
        byte[] entry = _entries[index];
        entry.AsSpan(0, 16).Clear();
        Encoding.ASCII.GetBytes(name, entry.AsSpan(0, 16));
        Write(20, flags); Write(24, flags2); Write(28, probability); Write(32, length);
        BinaryPrimitives.WriteSingleLittleEndian(entry.AsSpan(36), speed);
        for (int i = 0; i < 2; i++) Write(40 + 4 * i, hides[i]);
        for (int i = 0; i < 4; i++) Write(48 + 4 * i, steps[i]);
        Write(64, hit); Write(68, sfxFrame);
        for (int i = 0; i < 4; i++) Write(72 + 4 * i, sfxIndices[i]);
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(offset), value);
    }

    public int AppendFrom(int templateIndex, string name, uint length)
    {
        if ((uint)templateIndex >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(templateIndex));
        ValidateName(name, null);
        if (length == 0 || length > 100_000) throw new ArgumentOutOfRangeException(nameof(length));
        int index = _entries.Count;
        byte[] copy = _entries[templateIndex].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16), (uint)index);
        _entries.Add(copy);
        var old = new AnimationDatabaseEntry(index, copy);
        Update(index, name, old.Flags, old.Flags2 & ~0xFu, old.Probability, length, old.Speed,
            new uint[2], new uint[4], 0, 0, new uint[4]);
        return index;
    }

    public int AppendConfigured(int? templateIndex, string name, uint flags, uint flags2, uint probability, uint length,
        float speed, uint[] hides, uint[] steps, uint hit, uint sfxFrame, uint[] sfxIndices)
    {
        int index;
        if (templateIndex is int template) index = AppendFrom(template, name, length);
        else
        {
            ValidateName(name, null);
            index = _entries.Count;
            var entry = new byte[EntrySize];
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(16), (uint)index);
            _entries.Add(entry);
        }
        try { Update(index, name, flags, flags2, probability, length, speed, hides, steps, hit, sfxFrame, sfxIndices); }
        catch { _entries.RemoveAt(index); throw; }
        return index;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index != _entries.Count - 1)
            throw new InvalidOperationException("Удалять можно только последнюю запись ADB: удаление из середины изменит номера остальных анимаций. Для сохранения номера замените ID клипа в этой записи.");
        _entries.RemoveAt(index);
    }

    private void ValidateName(string name, int? ownIndex)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 15 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Игровой ID: 1–15 латинских букв, цифр и _.", nameof(name));
        if (_entries.Select((entry, index) => (entry, index)).Any(pair => pair.index != ownIndex &&
            new AnimationDatabaseEntry(pair.index, pair.entry).Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Такой ID уже есть в ADB.", nameof(name));
    }
}

public sealed class AnimationDatabaseEntry
{
    private readonly byte[] _bytes;
    public int Index { get; }
    public string Name => Encoding.ASCII.GetString(_bytes, 0, 16).TrimEnd('\0');
    public uint StoredIndex => Read(16);
    public uint Flags => Read(20);
    public uint Flags2 => Read(24);
    public uint Probability => Read(28);
    public uint Length => Read(32);
    public float Speed => BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(36));
    public uint[] Hides => ReadArray(40, 2);
    public uint[] Steps => ReadArray(48, 4);
    public uint Hit => Read(64);
    public uint SfxFrame => Read(68);
    public uint[] SfxIndices => ReadArray(72, 4);
    internal AnimationDatabaseEntry(int index, byte[] bytes) { Index = index; _bytes = bytes; }
    private uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset));
    private uint[] ReadArray(int offset, int count) => Enumerable.Range(0, count).Select(i => Read(offset + 4 * i)).ToArray();
}
