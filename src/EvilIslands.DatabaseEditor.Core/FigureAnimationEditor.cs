using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

// Edits an ANM copy, retaining untouched resources and their original bytes.
public sealed partial class FigureAnimationEditor
{
    private byte[] _bytes;
    private readonly Stack<byte[]> _undo = new(), _redo = new();
    private readonly FigureAnimationSet _source;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasChanges => !_bytes.AsSpan().SequenceEqual(_source.ExportBytes());
    public FigureAnimationEditor(FigureAnimationSet source) { _source = source; _bytes = source.ExportBytes(); }
    public byte[] ExportBytes() => (byte[])_bytes.Clone();
    public FigureAnimationSet Preview() => _source.WithBytes(_bytes);
    public void Undo() { if (_undo.TryPop(out var b)) { _redo.Push(_bytes); _bytes = b; } }
    public void Redo() { if (_redo.TryPop(out var b)) { _undo.Push(_bytes); _bytes = b; } }

    public async Task TransformAsync(string name, int first, int last, int frames, CancellationToken token = default)
    {
        if (Contacts.Count > 0) throw new ArgumentException("Снимите диапазоны контактов перед изменением длины клипа.");
        var clip = await Preview().LoadAsync(name, token).ConfigureAwait(false);
        if (first < 0 || last < first || last >= clip.FrameCount || frames < 1 || frames > 10000)
            throw new ArgumentOutOfRangeException(nameof(first), "Укажите диапазон существующих кадров и длину от 1 до 10000 кадров.");
        if (first == 0 && last == clip.FrameCount - 1 && frames == clip.FrameCount) return;
        var archive = ResArchiveDocument.Parse("animation.anm", _bytes, token);
        var nested = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var tracks = nested is null ? archive : ResArchiveDocument.Parse(name, archive.GetResourceBytes(nested).ToArray(), token);
        var replacements = new Dictionary<int, byte[]>();
        foreach (var entry in tracks.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = entry.Name.Replace('/', '\\');
            if (nested is null && !path.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            replacements[entry.Index] = TransformTrack(tracks.GetResourceBytes(entry).ToArray(), first, last, frames);
        }
        var changed = ResArchiveWriter.Rebuild(tracks, replacements);
        if (nested is not null) changed = ResArchiveWriter.Rebuild(archive, new Dictionary<int, byte[]> { [nested.Index] = changed });
        // Decode the rebuilt clip before accepting a state; all other payloads stay byte-identical.
        var checkedClip = await _source.WithBytes(changed).LoadAsync(name, token).ConfigureAwait(false);
        if (checkedClip.FrameCount != frames) throw new InvalidDataException("Длина сохранённого клипа не совпадает с выбранной.");
        if (_undo.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 операций. Экспортируйте ANM перед дальнейшими изменениями.");
        _undo.Push(_bytes); _redo.Clear(); _bytes = changed;
    }

    public async Task SetRotationsAsync(string name, int frame, IReadOnlyDictionary<string, Quaternion> rotations)
    {
        if (rotations.Count == 0) return;
        if (_undo.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 операций.");
        var candidate = new FigureAnimationEditor(Preview());
        foreach (var pair in rotations) await candidate.SetPoseAsync(name, pair.Key, frame, pair.Value).ConfigureAwait(false);
        byte[] changed = candidate.ExportBytes();
        if (_bytes.AsSpan().SequenceEqual(changed)) return;
        _undo.Push(_bytes); _redo.Clear(); _bytes = changed;
    }

    public async Task SetPoseAsync(string name, string part, int frame, Quaternion rotation, Vector3? position = null)
    {
        var clip = await Preview().LoadAsync(name).ConfigureAwait(false);
        if (!clip.Tracks.ContainsKey(part) || frame < 0 || frame >= clip.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), "Выберите часть и существующий кадр.");
        if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 0.0001f)
            throw new ArgumentException("Некорректный поворот.");
        if (position is Vector3 v && (!float.IsFinite(v.LengthSquared()) || Math.Abs(v.X) >= 100000 || Math.Abs(v.Y) >= 100000 || Math.Abs(v.Z) >= 100000))
            throw new ArgumentException("Некорректное положение.");
        if (position.HasValue && (!_source.Parents.TryGetValue(part, out var parent) || parent.Length != 0))
            throw new ArgumentException("Перемещать можно только корень: положение дочерней части задаётся BON.");
        var archive = ResArchiveDocument.Parse("animation.anm", _bytes);
        var nested = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var tracks = nested is null ? archive : ResArchiveDocument.Parse(name, archive.GetResourceBytes(nested).ToArray());
        string path = (nested is null ? name + "\\" : "") + part;
        var entry = tracks.Entries.Single(e => e.Name.Replace('/', '\\').Equals(path, StringComparison.OrdinalIgnoreCase));
        byte[] raw = tracks.GetResourceBytes(entry).ToArray();
        using var input = new BinaryReader(new MemoryStream(raw));
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        var q = Quaternion.Normalize(rotation);
        void Channel(int stride, float[]? value, float[] fallback)
        {
            int count = input.ReadInt32(); byte[] data = input.ReadBytes(checked(count * stride));
            if (value is null) { writer.Write(count); writer.Write(data); return; }
            // Materialize held frames so editing one frame does not change later held frames.
            writer.Write(clip.FrameCount);
            for (int i = 0; i < clip.FrameCount; i++)
                if (i == frame) foreach (float f in value) writer.Write(f);
                else if (count > 0) writer.Write(data, Math.Min(i, count - 1) * stride, stride);
                else foreach (float f in fallback) writer.Write(f);
        }
        Channel(16, new[] { q.W, q.X, q.Y, q.Z }, new[] { 1f, 0f, 0f, 0f });
        var bone = _source.Bones.GetValueOrDefault(part);
        Channel(12, position is Vector3 pos ? new[] { pos.X, pos.Y, pos.Z } : null, new[] { bone.X, bone.Y, bone.Z });
        writer.Write(raw, (int)input.BaseStream.Position, raw.Length - (int)input.BaseStream.Position);
        var changed = ResArchiveWriter.Rebuild(tracks, new Dictionary<int, byte[]> { [entry.Index] = output.ToArray() });
        if (nested is not null) changed = ResArchiveWriter.Rebuild(archive, new Dictionary<int, byte[]> { [nested.Index] = changed });
        _ = await _source.WithBytes(changed).LoadAsync(name).ConfigureAwait(false);
        if (_bytes.AsSpan().SequenceEqual(changed)) return;
        if (_undo.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 операций.");
        _undo.Push(_bytes); _redo.Clear(); _bytes = changed;
    }

    internal static byte[] TransformTrack(byte[] bytes, int first, int last, int frames)
    {
        _ = FigureAnimationTrack.Decode(bytes);
        using var input = new BinaryReader(new MemoryStream(bytes));
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        void Channel(int count, int stride, bool rotation)
        {
            var raw = input.ReadBytes(checked(count * stride));
            writer.Write(count == 0 ? 0 : frames);
            for (int f = 0; count > 0 && f < frames; f++)
            {
                double position = frames == 1 ? first : first + (last - first) * (double)f / (frames - 1);
                position = Math.Clamp(position, 0, count - 1);
                int a = (int)position, b = Math.Min(a + 1, count - 1); float t = (float)(position - a);
                if (t == 0) { writer.Write(raw, a * stride, stride); continue; }
                if (rotation)
                {
                    Quaternion Q(int n) => new(BitConverter.ToSingle(raw, n * 16 + 4), BitConverter.ToSingle(raw, n * 16 + 8), BitConverter.ToSingle(raw, n * 16 + 12), BitConverter.ToSingle(raw, n * 16));
                    var q = Quaternion.Slerp(Quaternion.Normalize(Q(a)), Quaternion.Normalize(Q(b)), t);
                    writer.Write(q.W); writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z);
                }
                else for (int v = 0; v < stride; v += 4)
                { float x = BitConverter.ToSingle(raw, a * stride + v), y = BitConverter.ToSingle(raw, b * stride + v); writer.Write(x + (y - x) * t); }
            }
        }
        Channel(input.ReadInt32(), 16, true); Channel(input.ReadInt32(), 12, false);
        int morphs = input.ReadInt32();
        if (morphs == 0) { writer.Write(0); if (input.BaseStream.Position < bytes.Length) writer.Write(input.ReadInt32()); }
        else
        {
            int vertices = input.ReadInt32();
            if ((long)vertices * frames > 8000000) throw new InvalidDataException("Слишком большой объём деформаций после изменения длины.");
            // Channel writes its count before data; retain the ANM vertex count between them.
            writer.Write(frames); writer.Write(vertices);
            byte[] raw = input.ReadBytes(checked(morphs * vertices * 12));
            for (int f = 0; f < frames; f++)
            {
                double p = Math.Clamp(frames == 1 ? first : first + (last - first) * (double)f / (frames - 1), 0, morphs - 1);
                int a = (int)p, b = Math.Min(a + 1, morphs - 1); float t = (float)(p - a);
                for (int v = 0; v < vertices * 3; v++)
                { float x = BitConverter.ToSingle(raw, (a * vertices * 3 + v) * 4), y = BitConverter.ToSingle(raw, (b * vertices * 3 + v) * 4); writer.Write(t == 0 ? x : x + (y - x) * t); }
            }
        }
        var result = output.ToArray(); _ = FigureAnimationTrack.Decode(result); return result;
    }
}
