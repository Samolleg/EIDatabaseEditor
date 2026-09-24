using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record AnimationFrameBlock(int Count, Dictionary<string, byte[]> Tracks);
public sealed record AnimationAuthoringSettings
{
    public int Frame { get; init; }
    public int First { get; init; }
    public int Last { get; init; }
    public int SmoothRadius { get; init; }
    public string Part { get; init; } = "";
    public bool Limits { get; init; } = true;
    public List<string> LockedParts { get; init; } = new();
    public List<string> UnlockedLimbs { get; init; } = new();
    public Dictionary<string, Vector3> Pins { get; init; } = new();
    public List<FootContact> Contacts { get; init; } = new();
    public List<StoredAnimationPose> Poses { get; init; } = new();
    public bool Trajectory { get; init; }
    public bool Onion { get; init; }
    public bool Ghost { get; init; }
    public bool? Ground { get; init; }
    public int Fps { get; init; } = 30;
    public bool Loop { get; init; } = true;
}
public sealed record AnimationProjectDocument(int Version, string Model, string Clip, string SourceHash,
    byte[] Animation, byte[][] Undo, byte[][] Redo, AnimationAuthoringSettings Settings);

public sealed partial class FigureAnimationEditor
{
    private static readonly JsonSerializerOptions ProjectJson = new() { IncludeFields = true };
    private void Accept(byte[] bytes)
    {
        if (_bytes.AsSpan().SequenceEqual(bytes)) return;
        if (_undo.Count >= 32)
        {
            var recent = _undo.Take(31).Reverse().ToArray(); _undo.Clear();
            foreach (var state in recent) _undo.Push(state);
        }
        _undo.Push(_bytes); _redo.Clear(); _bytes = bytes;
    }
    private (ResArchiveDocument Outer, ResEntry? Nested, ResArchiveDocument Tracks) Containers(string name)
    {
        var outer = ResArchiveDocument.Parse("animation", _bytes);
        var nested = outer.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return (outer, nested, nested is null ? outer : ResArchiveDocument.Parse(name, outer.GetResourceBytes(nested).ToArray()));
    }
    private Dictionary<string, byte[]> RawTracks(string name)
    {
        var (_, nested, tracks) = Containers(name); var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tracks.Entries)
        {
            string path = e.Name.Replace('/', '\\');
            if (nested is null && !path.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            result[nested is null ? path[(name.Length + 1)..] : path] = tracks.GetResourceBytes(e).ToArray();
        }
        return result;
    }
    private async Task ReplaceTracks(string name, IReadOnlyDictionary<string, byte[]> changed)
    {
        var (outer, nested, tracks) = Containers(name); var replacements = new Dictionary<int, byte[]>();
        foreach (var pair in changed)
        {
            _ = FigureAnimationTrack.Decode(pair.Value);
            string path = (nested is null ? name + "\\" : "") + pair.Key;
            var entry = tracks.Entries.Single(e => e.Name.Replace('/', '\\').Equals(path, StringComparison.OrdinalIgnoreCase));
            replacements[entry.Index] = pair.Value;
        }
        byte[] bytes = ResArchiveWriter.Rebuild(tracks, replacements);
        if (nested is not null) bytes = ResArchiveWriter.Rebuild(outer, new Dictionary<int, byte[]> { [nested.Index] = bytes });
        var candidate = await _source.WithBytes(bytes).LoadAsync(name).ConfigureAwait(false);
        if (LockedParts.Count > 0)
        {
            var before = await Preview().LoadAsync(name).ConfigureAwait(false);
            if (before.FrameCount == candidate.FrameCount)
                for (int f = 0; f < before.FrameCount; f++) CheckLockedPose(_source, before, candidate, f, LockedParts);
        }
        Accept(bytes);
    }
    private sealed class RawTrack
    {
        public byte[] Rotation, Position, Morph; public int Rotations, Positions, Morphs, Vertices; public bool ShortFooter;
        public RawTrack(byte[] bytes)
        {
            _ = FigureAnimationTrack.Decode(bytes);
            using var r = new BinaryReader(new MemoryStream(bytes));
            Rotations = r.ReadInt32(); Rotation = r.ReadBytes(Rotations * 16);
            Positions = r.ReadInt32(); Position = r.ReadBytes(Positions * 12);
            Morphs = r.ReadInt32(); ShortFooter = r.BaseStream.Position == bytes.Length;
            Vertices = ShortFooter ? 0 : r.ReadInt32(); Morph = r.ReadBytes(checked(Morphs * Vertices * 12));
        }
    }
    private static byte[] MapTrack(RawTrack[] sources, (int Source, int Frame)[] map, Vector3 fallback)
    {
        if (map.Length is < 1 or > 10000) throw new ArgumentException("Длина клипа должна быть от 1 до 10000 кадров.");
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        void Channel(int stride, Func<RawTrack, int> count, Func<RawTrack, byte[]> data, float[] empty)
        {
            bool present = sources.Any(s => count(s) > 0); w.Write(present ? map.Length : 0);
            if (!present) return;
            foreach (var (s, f) in map)
                if (count(sources[s]) > 0) w.Write(data(sources[s]), Math.Min(f, count(sources[s]) - 1) * stride, stride);
                else foreach (float value in empty) w.Write(value);
        }
        Channel(16, s => s.Rotations, s => s.Rotation, [1, 0, 0, 0]);
        Channel(12, s => s.Positions, s => s.Position, [fallback.X, fallback.Y, fallback.Z]);
        int vertices = sources.Where(s => s.Morphs > 0).Select(s => s.Vertices).DefaultIfEmpty(0).First();
        if (sources.Any(s => s.Morphs > 0 && s.Vertices != vertices) || (long)vertices * map.Length > 8000000) throw new InvalidDataException("Несовместимые деформации кадров.");
        bool morph = sources.Any(s => s.Morphs > 0); w.Write(morph ? map.Length : 0);
        if (morph || !sources.All(s => s.ShortFooter)) w.Write(vertices);
        if (morph) foreach (var (s, f) in map)
            if (sources[s].Morphs > 0) w.Write(sources[s].Morph, Math.Min(f, sources[s].Morphs - 1) * vertices * 12, vertices * 12);
            else w.Write(new byte[vertices * 12]);
        return output.ToArray();
    }
    private static void Range(FigureAnimationClip clip, int first, int last)
    { if (first < 0 || last < first || last >= clip.FrameCount) throw new ArgumentException("Выберите существующий диапазон кадров."); }
    public async Task<AnimationFrameBlock> CopyFramesAsync(string name, int first, int last)
    {
        var clip = await Preview().LoadAsync(name); Range(clip, first, last);
        var map = Enumerable.Range(first, last - first + 1).Select(f => (0, f)).ToArray();
        return new(map.Length, RawTracks(name).ToDictionary(p => p.Key, p => MapTrack([new RawTrack(p.Value)], map, _source.Bones.GetValueOrDefault(p.Key)), StringComparer.OrdinalIgnoreCase));
    }
    public async Task DeleteFramesAsync(string name, int first, int last)
    {
        if (Contacts.Count > 0) throw new ArgumentException("Снимите диапазоны контактов перед изменением длины клипа.");
        var clip = await Preview().LoadAsync(name); Range(clip, first, last);
        if (first == 0 && last == clip.FrameCount - 1) throw new ArgumentException("Нельзя удалить последний кадр. Сбросьте его позу или сначала добавьте другой кадр.");
        var map = Enumerable.Range(0, clip.FrameCount).Where(f => f < first || f > last).Select(f => (0, f)).ToArray();
        var raw = RawTracks(name); await ReplaceTracks(name, raw.ToDictionary(p => p.Key, p => MapTrack([new RawTrack(p.Value)], map, _source.Bones.GetValueOrDefault(p.Key))));
    }
    public async Task PasteFramesAsync(string name, int before, AnimationFrameBlock block)
    {
        if (Contacts.Count > 0) throw new ArgumentException("Снимите диапазоны контактов перед изменением длины клипа.");
        var clip = await Preview().LoadAsync(name);
        if (before < 0 || before > clip.FrameCount || block.Count < 1 || clip.FrameCount + block.Count > 10000) throw new ArgumentException("Недопустимое место вставки или длина.");
        var raw = RawTracks(name);
        if (!raw.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(block.Tracks.Keys)) throw new InvalidDataException("Состав частей скопированной позы отличается.");
        var map = Enumerable.Range(0, before).Select(f => (0, f)).Concat(Enumerable.Range(0, block.Count).Select(f => (1, f))).Concat(Enumerable.Range(before, clip.FrameCount - before).Select(f => (0, f))).ToArray();
        await ReplaceTracks(name, raw.ToDictionary(p => p.Key, p => MapTrack([new RawTrack(p.Value), new RawTrack(block.Tracks[p.Key])], map, _source.Bones.GetValueOrDefault(p.Key))));
    }
    public async Task DuplicateFramesAsync(string name, int first, int last) => await PasteFramesAsync(name, last + 1, await CopyFramesAsync(name, first, last));

    internal static FigureAnimationTrack Patched(FigureAnimationTrack track, int frames, int frame, Quaternion? rotation, Vector3? position, Vector3 fallback)
    {
        var rotations = rotation.HasValue ? Enumerable.Range(0, frames).Select(f => f == frame ? rotation.Value : track.Rotation(f)).ToArray() : track.Rotations;
        var positions = position.HasValue ? Enumerable.Range(0, frames).Select(f => f == frame ? position.Value : track.Positions.Length > 0 ? track.Position(f) : fallback).ToArray() : track.Positions;
        return new FigureAnimationTrack(rotations, positions, track.Morphs);
    }
    internal static FigureAnimationClip VirtualPose(FigureAnimationSet source, FigureAnimationClip clip, int frame, IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Vector3> positions)
    {
        var tracks = new Dictionary<string, FigureAnimationTrack>(clip.Tracks, StringComparer.OrdinalIgnoreCase);
        foreach (var key in rotations.Keys.Concat(positions.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            tracks[key] = Patched(tracks[key], clip.FrameCount, frame, rotations.TryGetValue(key, out var q) ? q : null, positions.TryGetValue(key, out var p) ? p : null, source.Bones.GetValueOrDefault(key));
        return new(clip.Name, tracks);
    }
    public static IReadOnlyDictionary<string, Quaternion> ConstrainPreview(FigureAnimationSet set, FigureAnimationClip clip, int frame,
        IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Vector3> positions, bool limits, IReadOnlyDictionary<string, Vector3> pins, IReadOnlySet<string>? unlocked = null, IReadOnlySet<string>? locked = null)
    {
        var qs = new Dictionary<string, Quaternion>(rotations); var ps = new Dictionary<string, Vector3>(positions);
        AnimationPoseConstraints.Apply(set, clip, frame, qs, ps, limits, pins, unlocked);
        CheckLockedPose(set, clip, VirtualPose(set, clip, frame, qs, ps), frame, locked); return qs;
    }
    public async Task ApplyAuthoringAsync(string name, int frame, IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Vector3> positions, int radius, bool limits, IReadOnlyDictionary<string, Vector3> pins)
    {
        var clip = await Preview().LoadAsync(name); Range(clip, frame, frame);
        if (radius < 0 || radius > 120) throw new ArgumentException("Радиус сглаживания: 0–120 кадров.");
        var current = Preview(); var raw = RawTracks(name); var edits = new Dictionary<string, Dictionary<int, (Quaternion? Q, Vector3? P)>>();
        for (int f = Math.Max(0, frame - radius); f <= Math.Min(clip.FrameCount - 1, frame + radius); f++)
        {
            float t = radius == 0 ? 1 : 1 - Math.Abs(f - frame) / (float)(radius + 1); float weight = t * t * (3 - 2 * t);
            var qs = new Dictionary<string, Quaternion>(); var ps = new Dictionary<string, Vector3>();
            foreach (var pair in rotations)
            {
                if (!clip.Tracks.ContainsKey(pair.Key) || !float.IsFinite(pair.Value.LengthSquared()) || pair.Value.LengthSquared() < .0001f) throw new ArgumentException("Некорректный поворот.");
                var delta = Quaternion.Normalize(pair.Value * Quaternion.Inverse(clip.Tracks[pair.Key].Rotation(frame)));
                qs[pair.Key] = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, delta, weight) * clip.Tracks[pair.Key].Rotation(f));
            }
            foreach (var pair in positions)
            {
                if (current.Parents.GetValueOrDefault(pair.Key) != "" || !float.IsFinite(pair.Value.LengthSquared())) throw new ArgumentException("Положение доступно только для корня.");
                var track = clip.Tracks[pair.Key]; Vector3 At(int n) => track.Positions.Length > 0 ? track.Position(n) : current.Bones[pair.Key];
                ps[pair.Key] = At(f) + (pair.Value - At(frame)) * weight;
            }
            var allPins = ContactPins(f); foreach (var pin in pins) allPins[pin.Key] = pin.Value;
            AnimationPoseConstraints.Apply(current, clip, f, qs, ps, limits, allPins, UnlockedLimbs);
            foreach (string key in qs.Keys.Concat(ps.Keys).Distinct())
            {
                if (!edits.TryGetValue(key, out var frames)) edits[key] = frames = new();
                frames[f] = (qs.TryGetValue(key, out var q) ? q : null, ps.TryGetValue(key, out var p) ? p : null);
            }
        }
        var replacements = new Dictionary<string, byte[]>();
        foreach (var part in edits)
        {
            var source = new RawTrack(raw[part.Key]);
            // Only changed channels are materialized. Other channels retain raw bytes/counts.
            int rc = part.Value.Values.Any(v => v.Q.HasValue) ? clip.FrameCount : source.Rotations;
            int pc = part.Value.Values.Any(v => v.P.HasValue) ? clip.FrameCount : source.Positions;
            using var output = new MemoryStream(); using var w = new BinaryWriter(output);
            w.Write(rc);
            for (int f = 0; f < rc; f++)
                if (part.Value.TryGetValue(f, out var edit) && edit.Q.HasValue) { var q = edit.Q.Value; w.Write(q.W); w.Write(q.X); w.Write(q.Y); w.Write(q.Z); }
                else if (source.Rotations > 0) w.Write(source.Rotation, Math.Min(f, source.Rotations - 1) * 16, 16);
                else { w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); }
            w.Write(pc);
            for (int f = 0; f < pc; f++)
                if (part.Value.TryGetValue(f, out var edit) && edit.P.HasValue) { var p = edit.P.Value; w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                else if (source.Positions > 0) w.Write(source.Position, Math.Min(f, source.Positions - 1) * 12, 12);
                else { var p = current.Bones.GetValueOrDefault(part.Key); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            w.Write(source.Morphs); if (!source.ShortFooter) w.Write(source.Vertices); w.Write(source.Morph);
            replacements[part.Key] = output.ToArray();
        }
        await ReplaceTracks(name, replacements);
    }
    public async Task MirrorPoseAsync(string name, int frame, bool limits, IReadOnlyDictionary<string, Vector3> pins)
    {
        var clip = await Preview().LoadAsync(name); Range(clip, frame, frame);
        var rotations = new Dictionary<string, Quaternion>();
        foreach (string part in clip.Tracks.Keys)
        {
            string other = part.StartsWith("lh", StringComparison.Ordinal) ? "rh" + part[2..] : part.StartsWith("rh", StringComparison.Ordinal) ? "lh" + part[2..] : part.StartsWith("ll", StringComparison.Ordinal) ? "rl" + part[2..] : part.StartsWith("rl", StringComparison.Ordinal) ? "ll" + part[2..] : part;
            if (other == part && part.StartsWith("l_", StringComparison.Ordinal)) other = "r_" + part[2..];
            else if (other == part && part.StartsWith("r_", StringComparison.Ordinal)) other = "l_" + part[2..];
            if (!clip.Tracks.ContainsKey(other)) continue;
            var q = clip.Tracks[other].Rotation(frame); rotations[part] = new Quaternion(q.X, -q.Y, -q.Z, q.W);
        }
        await ApplyAuthoringAsync(name, frame, rotations, new Dictionary<string, Vector3>(), 0, limits, pins);
    }
    private string SourceIdentity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_source.ExportBytes());
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(_source.Parents.OrderBy(p => p.Key, StringComparer.Ordinal), ProjectJson));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(_source.Bones.OrderBy(p => p.Key, StringComparer.Ordinal), ProjectJson));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public byte[] SaveProject(string model, string clip, AnimationAuthoringSettings settings)
    {
        var document = new AnimationProjectDocument(1, model, clip, SourceIdentity(), ExportBytes(), _undo.ToArray(), _redo.ToArray(), settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, ProjectJson);
        if (bytes.Length > 256 * 1024 * 1024) throw new InvalidDataException("Проект превышает 256 МиБ. Экспортируйте ANM отдельно.");
        return bytes;
    }
    public async Task<AnimationAuthoringSettings> LoadProjectAsync(byte[] bytes, string model, string clip)
    {
        if (bytes.Length > 256 * 1024 * 1024) throw new InvalidDataException("Проект превышает 256 МиБ.");
        var d = JsonSerializer.Deserialize<AnimationProjectDocument>(bytes, ProjectJson) ?? throw new InvalidDataException("Пустой проект.");
        if (d.Version != 1 || d.Model != model || d.Clip != clip || d.SourceHash != SourceIdentity()) throw new InvalidDataException("Проект создан для другой модели, клипа или исходного ANM.");
        if (d.Animation is null || d.Undo is null || d.Redo is null || d.Settings is null || d.Settings.Pins is null || d.Settings.Part is null ||
            d.Undo.Length + d.Redo.Length > 32 || d.Settings.SmoothRadius is < 0 or > 120 ||
            d.Settings.Fps is not (10 or 15 or 20 or 25 or 30 or 60) ||
            d.Settings.Pins.Any(p => p.Key is not ("ll3" or "rl3") || !float.IsFinite(p.Value.LengthSquared()))) throw new InvalidDataException("Некорректные настройки проекта.");
        foreach (byte[] state in d.Undo.Concat(d.Redo).Append(d.Animation))
        {
            if (state is null) throw new InvalidDataException("Пустое состояние проекта.");
            _ = await _source.WithBytes(state).LoadAsync(clip);
        }
        var loaded = await _source.WithBytes(d.Animation).LoadAsync(clip);
        if (d.Settings.Frame < 0 || d.Settings.Frame >= loaded.FrameCount || d.Settings.First < 0 || d.Settings.Last < d.Settings.First || d.Settings.Last >= loaded.FrameCount ||
            (d.Settings.Part.Length > 0 && !loaded.Tracks.ContainsKey(d.Settings.Part)) || d.Settings.Pins.Keys.Any(p => !FigureLimbIk.Supports(_source, loaded, p)))
            throw new InvalidDataException("Кадры или части проекта не соответствуют клипу.");
        if (d.Settings.Contacts is null || d.Settings.Poses is null || d.Settings.Contacts.Count > 1000 || d.Settings.Poses.Count > 500) throw new InvalidDataException("Некорректная библиотека/контакты.");
        foreach(var c in d.Settings.Contacts) if(c.First<0||c.Last<c.First||c.Last>=loaded.FrameCount||!float.IsFinite(c.Anchor.LengthSquared())||!FigureLimbIk.Supports(_source,loaded,c.Part)) throw new InvalidDataException("Некорректный контакт.");
        foreach(var p in d.Settings.Poses) if(p.Rotations is null || p.Positions is null || p.Rotations.Any(q=>!float.IsFinite(q.Value.LengthSquared())||q.Value.LengthSquared()<.0001f)||p.Positions.Any(v=>!float.IsFinite(v.Value.LengthSquared()))) throw new InvalidDataException("Некорректная поза.");
        if (d.Settings.LockedParts is null || d.Settings.UnlockedLimbs is null ||
            d.Settings.LockedParts.Any(p => p is null || !loaded.Tracks.ContainsKey(p)) ||
            d.Settings.UnlockedLimbs.Any(p => p is not ("lh3" or "rh3" or "ll3" or "rl3")))
            throw new InvalidDataException("Некорректные блокировки частей.");
        LockedParts.Clear(); LockedParts.UnionWith(d.Settings.LockedParts);
        UnlockedLimbs.Clear(); UnlockedLimbs.UnionWith(d.Settings.UnlockedLimbs);
        Contacts = d.Settings.Contacts.ToArray();
        _bytes = d.Animation; _undo.Clear(); _redo.Clear(); foreach (byte[] state in d.Undo.Reverse()) _undo.Push(state); foreach (byte[] state in d.Redo.Reverse()) _redo.Push(state);
        return d.Settings;
    }
}
