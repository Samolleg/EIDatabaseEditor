using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record FigurePose(Vector3 Position, Quaternion Rotation);

public sealed class FigureAnimationTrack
{
    public Quaternion[] Rotations { get; }
    public Vector3[] Positions { get; }
    public Vector3[][] Morphs { get; }
    public int FrameCount => Math.Max(Rotations.Length, Math.Max(Positions.Length, Morphs.Length));
    private FigureAnimationTrack(Quaternion[] rotations, Vector3[] positions, Vector3[][] morphs)
    { Rotations = rotations; Positions = positions; Morphs = morphs; }

    public static FigureAnimationTrack Decode(byte[] bytes, CancellationToken token = default)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        int Count(int limit, int stride)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > limit || (long)count * stride > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Некорректная длина дорожки анимации.");
            return count;
        }
        float Number()
        { float n = r.ReadSingle(); return float.IsFinite(n) && Math.Abs(n) < 100000 ? n : throw new InvalidDataException("Некорректная координата анимации."); }
        Vector3 Vector() => new(Number(), Number(), Number());
        var rotations = new Quaternion[Count(10000, 16)];
        for (int i = 0; i < rotations.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            float w = Number(); var q = new Quaternion(Number(), Number(), Number(), w);
            if (q.LengthSquared() < .0001f || q.LengthSquared() > 4) throw new InvalidDataException("Некорректный кватернион анимации.");
            rotations[i] = Quaternion.Normalize(q);
        }
        var positions = new Vector3[Count(10000, 12)];
        for (int i = 0; i < positions.Length; i++) { token.ThrowIfCancellationRequested(); positions[i] = Vector(); }
        int frames = Count(10000, 0);
        // Some installed JMV tracks end with only a zero morph-frame count.
        int vertices = frames == 0 && r.BaseStream.Position == r.BaseStream.Length ? 0 : Count(65536, 0);
        if ((long)frames * vertices > 8000000 || (long)frames * vertices * 12 != r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Некорректные размеры деформаций анимации.");
        var morphs = new Vector3[frames][];
        for (int i = 0; i < frames; i++)
        { token.ThrowIfCancellationRequested(); morphs[i] = new Vector3[vertices]; for (int v = 0; v < vertices; v++) morphs[i][v] = Vector(); }
        return new(rotations, positions, morphs);
    }

    private static (int A, int B, float T) Sample(float frame, int count)
    {
        if (!float.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        float clamped = Math.Clamp(frame, 0, Math.Max(0, count - 1)); int a = (int)clamped;
        return (a, Math.Min(a + 1, count - 1), clamped - a);
    }
    public Quaternion Rotation(float frame)
    { if (Rotations.Length == 0) return Quaternion.Identity; var s = Sample(frame, Rotations.Length); return Quaternion.Slerp(Rotations[s.A], Rotations[s.B], s.T); }
    public Vector3 Position(float frame)
    { if (Positions.Length == 0) return Vector3.Zero; var s = Sample(frame, Positions.Length); return Vector3.Lerp(Positions[s.A], Positions[s.B], s.T); }
    public Vector3 Morph(float frame, int vertex)
    {
        if (Morphs.Length == 0 || vertex < 0 || vertex >= Morphs[0].Length) return Vector3.Zero;
        var s = Sample(frame, Morphs.Length); return Vector3.Lerp(Morphs[s.A][vertex], Morphs[s.B][vertex], s.T);
    }
}

public sealed record FigureAnimationClip(string Name, IReadOnlyDictionary<string, FigureAnimationTrack> Tracks)
{
    public int FrameCount => Math.Max(1, Tracks.Values.Select(t => t.FrameCount).DefaultIfEmpty().Max());
}

public sealed class FigureAnimationSet
{
    private readonly ResArchiveDocument _archive;
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<string> SkippedResources { get; }
    public IReadOnlyDictionary<string, string> Parents { get; }
    public IReadOnlyDictionary<string, Vector3> Bones { get; }
    public IReadOnlyDictionary<string, Vector3> BindPositions { get; }
    public FigureAnimationSet(string name, byte[] bytes, IReadOnlyDictionary<string, string> parents,
        IReadOnlyDictionary<string, Vector3> bones, IReadOnlyDictionary<string, Vector3> bindPositions, CancellationToken token = default)
    {
        _archive = ResArchiveDocument.Parse(name + ".anm", bytes, token);
        bool Auxiliary(ResEntry e)
        {
            if (Path.GetExtension(e.Name).Equals(".rar", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(e.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Size != 16) return false;
            var data = _archive.GetResourceBytes(e).Span;
            // Installed turtle tpose: empty RES marker, no tracks or directory.
            return BitConverter.ToUInt32(data) == ResArchiveDocument.Signature && data[4..].IndexOfAnyExcept((byte)0) < 0;
        }
        SkippedResources = _archive.Entries.Where(Auxiliary).Select(e => e.Name).ToArray();
        Names = _archive.Entries.Where(e => !SkippedResources.Contains(e.Name)).Select(e => e.Name.Replace('/', '\\').Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Parents = new Dictionary<string, string>(parents, StringComparer.OrdinalIgnoreCase);
        Bones = new Dictionary<string, Vector3>(bones, StringComparer.OrdinalIgnoreCase);
        BindPositions = new Dictionary<string, Vector3>(bindPositions, StringComparer.OrdinalIgnoreCase);
    }
    public Task<FigureAnimationClip> LoadAsync(string name, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var tracks = new Dictionary<string, FigureAnimationTrack>(StringComparer.OrdinalIgnoreCase);
        var entry = _archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var archive = entry is null ? _archive : ResArchiveDocument.Parse(name, _archive.GetResourceBytes(entry).ToArray(), token);
        foreach (var part in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = part.Name.Replace('/', '\\');
            if (entry is null && !path.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            string partName = entry is null ? path[(name.Length + 1)..] : path;
            try { tracks.Add(partName, FigureAnimationTrack.Decode(archive.GetResourceBytes(part).ToArray(), token)); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            { throw new InvalidDataException(partName + ": " + ex.Message, ex); }
        }
        if (tracks.Count == 0) throw new InvalidDataException("В клипе нет дорожек.");
        return new FigureAnimationClip(name, tracks);
    }, token);

    public IReadOnlyDictionary<string, FigurePose> Pose(FigureAnimationClip clip, float frame)
    {
        if (!float.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        var result = new Dictionary<string, FigurePose>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FigurePose Calculate(string name)
        {
            if (result.TryGetValue(name, out var pose)) return pose;
            if (visiting.Count >= 128 || !visiting.Add(name)) throw new InvalidDataException("Цикл или слишком глубокая иерархия анимации.");
            if (!Parents.TryGetValue(name, out var parent) || !Bones.TryGetValue(name, out var bone)) throw new InvalidDataException("Нет узла анимации: " + name);
            var track = clip.Tracks.GetValueOrDefault(name);
            var rotation = track?.Rotation(frame) ?? Quaternion.Identity;
            if (parent.Length == 0) pose = new(track is null || track.Positions.Length == 0 ? bone : track.Position(frame), rotation);
            else
            {
                var ancestor = Calculate(parent);
                // EI quaternion *= pre-multiplies: local * parent. Child position
                // comes from BON rotated by parent, not the track's position array.
                pose = new(ancestor.Position + Vector3.Transform(bone, ancestor.Rotation), Quaternion.Normalize(rotation * ancestor.Rotation));
            }
            visiting.Remove(name); result.Add(name, pose); return pose;
        }
        foreach (string name in Parents.Keys) Calculate(name);
        return result;
    }
}
