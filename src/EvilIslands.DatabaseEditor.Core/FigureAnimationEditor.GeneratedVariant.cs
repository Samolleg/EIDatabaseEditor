using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    /// <summary>Blend a generated same-rig clip into existing frames; do not change identity or duration.</summary>
    public async Task BlendGeneratedVariantAsync(string name, FigureAnimationClip generated,
        int first, int last, IReadOnlyCollection<string> parts, float strength, int transitionFrames = 6)
    {
        var clip = await Preview().LoadAsync(name);
        if (first < 0 || last < first || last >= clip.FrameCount || !float.IsFinite(strength) || strength is < 0 or > 1 || transitionFrames < 0)
            throw new ArgumentException("Проверьте диапазон кадров и силу изменения (0–100%).");
        if (parts.Count == 0 || parts.Any(p => !clip.Tracks.ContainsKey(p) || !generated.Tracks.ContainsKey(p)))
            throw new ArgumentException("Выберите части, присутствующие в исходном и созданном движении.");
        if (strength == 0) return;
        int count = last - first + 1;
        var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var original = clip.Tracks[part]; var incoming = generated.Tracks[part];
            var rotations = new Quaternion[count];
            bool root = _source.Parents.GetValueOrDefault(part) == "";
            var positions = original.Positions.Length > 0 || root ? new Vector3[count] : Array.Empty<Vector3>();
            var morphs = original.Morphs.Length > 0 ? new Vector3[count][] : Array.Empty<Vector3[]>();
            Vector3 SamplePosition(FigureAnimationTrack t, float f) => t.Positions.Length == 0 ? _source.Bones.GetValueOrDefault(part) : t.Position(f);
            Vector3 anchor = SamplePosition(original, first) - SamplePosition(incoming, 0);
            anchor.Z = 0; // Generated Z is already grounded for this rig; never re-anchor it to an arbitrary hip height.
            for (int i = 0; i < count; i++)
            {
                int frame = first + i;
                float sourceFrame = count == 1 ? 0 : i * (generated.FrameCount - 1f) / (count - 1);
                float weight = strength;
                if (transitionFrames > 0)
                {
                    if (first > 0) weight *= Math.Min(1, i / (float)transitionFrames);
                    if (last < clip.FrameCount - 1) weight *= Math.Min(1, (count - 1 - i) / (float)transitionFrames);
                }
                rotations[i] = Quaternion.Slerp(original.Rotation(frame), incoming.Rotation(sourceFrame), weight);
                if (positions.Length > 0) positions[i] = Vector3.Lerp(SamplePosition(original, frame), SamplePosition(incoming, sourceFrame) + (root ? anchor : Vector3.Zero), weight);
                if (morphs.Length > 0) morphs[i] = original.Morphs[Math.Min(frame, original.Morphs.Length - 1)].ToArray();
            }
            changed[part] = EncodeTrack(new FigureAnimationTrack(rotations, positions, morphs));
        }
        // Existing replacement validates world-space locks and foot anchors before accepting one undo operation.
        await ReplaceFramesAsync(name, first, new AnimationFrameBlock(count, changed));
    }
}
