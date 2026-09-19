using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    public HashSet<string> LockedParts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnlockedLimbs { get; } = new(StringComparer.OrdinalIgnoreCase);

    // A locked part cannot move indirectly through its ancestors either.
    public static void CheckLockedPose(FigureAnimationSet set, FigureAnimationClip before,
        FigureAnimationClip after, int frame, IReadOnlySet<string>? locked)
    {
        if (locked is null || locked.Count == 0) return;
        var a = set.Pose(before, frame); var b = set.Pose(after, frame);
        foreach (string part in locked)
            if (!a.TryGetValue(part, out var old) || !b.TryGetValue(part, out var next) ||
                Vector3.DistanceSquared(old.Position, next.Position) > 1e-10f ||
                Math.Abs(Quaternion.Dot(old.Rotation, next.Rotation)) < .999999f)
                throw new InvalidOperationException("Часть «" + part + "» заблокирована. Разблокируйте её (Ctrl+L); изменение отменено целиком.");
    }

    /// <summary>Replace existing frames, never insert or silently truncate the clipboard.</summary>
    public async Task ReplaceFramesAsync(string name, int first, AnimationFrameBlock block)
    {
        var set = Preview(); var clip = await set.LoadAsync(name);
        if (block.Count < 1 || first < 0 || first > clip.FrameCount - block.Count)
            throw new ArgumentException("Скопированные кадры не помещаются: выберите более ранний кадр или вставку новых кадров.");
        var raw = RawTracks(name);
        var incoming = new Dictionary<string, byte[]>(block.Tracks, StringComparer.OrdinalIgnoreCase);
        if (!raw.Keys.Any(incoming.ContainsKey)) throw new InvalidDataException("У анимаций нет общих частей.");
        var map = Enumerable.Range(0, clip.FrameCount).Select(f => f >= first && f < first + block.Count ? (1, f - first) : (0, f)).ToArray();
        var changed = raw.ToDictionary(p => p.Key, p => incoming.TryGetValue(p.Key, out var bytes)
            ? MapTrack([new RawTrack(p.Value), new RawTrack(bytes)], map, _source.Bones.GetValueOrDefault(p.Key)) : p.Value);
        var candidate = new FigureAnimationClip(name, changed.ToDictionary(p => p.Key, p => FigureAnimationTrack.Decode(p.Value)));
        for (int f = first; f < first + block.Count; f++)
        {
            CheckLockedPose(set, clip, candidate, f, LockedParts);
            // Pasted poses are exact, not IK-corrected. Reject conflicting foot anchors.
            var pins = ContactPins(f); foreach (var pin in MotionPins) pins[pin.Key] = pin.Value;
            var pose = set.Pose(candidate, f);
            foreach (var pin in pins)
                if (!pose.TryGetValue(pin.Key, out var p) || Vector3.Distance(p.Position, pin.Value) > .0001f)
                    throw new ArgumentException("Вставляемая поза нарушает фиксацию стопы. Снимите фиксацию перед заменой.");
        }
        await ReplaceTracks(name, changed);
    }

    public async Task ResetFrameAsync(string name, int frame, bool bindPose)
    {
        if (!bindPose)
        {
            var original = new FigureAnimationEditor(_source); var clip = await _source.LoadAsync(name);
            if (frame >= clip.FrameCount) throw new ArgumentException("У добавленного кадра нет исходного кадра. Выберите сброс в Т-позу.");
            await ReplaceFramesAsync(name, frame, await original.CopyFramesAsync(name, frame, frame)); return;
        }
        var raw = RawTracks(name); var tracks = new Dictionary<string, byte[]>();
        foreach (var p in raw)
        {
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write(1); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(1); var position = _source.Bones.GetValueOrDefault(p.Key);
            w.Write(position.X); w.Write(position.Y); w.Write(position.Z); w.Write(0); w.Write(0);
            tracks[p.Key] = ms.ToArray();
        }
        await ReplaceFramesAsync(name, frame, new(1, tracks));
    }
}
