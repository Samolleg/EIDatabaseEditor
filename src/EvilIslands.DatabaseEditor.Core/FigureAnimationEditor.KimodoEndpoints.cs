using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    /// <summary>Keep the selected same-rig reference pose at both boundaries of a new generated clip.</summary>
    public async Task ValidateGeneratedEndpointReferenceAsync(string name, string reference)
    {
        if (name.Equals(reference, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Опорная анимация должна отличаться от нового клипа.");
        var set = Preview();
        var clip = await set.LoadAsync(name);
        var idle = await set.LoadAsync(reference);
        if (clip.FrameCount < 2 || idle.FrameCount < 2)
            throw new ArgumentException("Для переноса начальной и конечной позы нужно не менее двух кадров в обоих клипах.");
        if (clip.Tracks.Keys.Any(part => !idle.Tracks.ContainsKey(part)))
            throw new ArgumentException("В опорной анимации отсутствуют дорожки нового клипа. Выберите другую анимацию той же модели.");
        foreach (var (part, incoming) in clip.Tracks)
        {
            var source = idle.Tracks[part];
            int vertices = incoming.Morphs.Length > 0 ? incoming.Morphs[0].Length : 0;
            if (source.Morphs.Length > 0 && source.Morphs[0].Length != vertices)
                throw new ArgumentException("Число вершин дорожки «" + part + "» отличается в опорной анимации.");
        }
    }

    public async Task AnchorGeneratedEndpointsAsync(string name, string reference)
    {
        await ValidateGeneratedEndpointReferenceAsync(name, reference);
        var set = Preview();
        var clip = await set.LoadAsync(name);
        var idle = await set.LoadAsync(reference);
        int last = clip.FrameCount - 1;
        int blend = Math.Min(12, Math.Max(2, clip.FrameCount / 10));
        var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (part, incoming) in clip.Tracks)
        {
            var source = idle.Tracks[part];
            int vertices = incoming.Morphs.Length > 0 ? incoming.Morphs[0].Length : 0;
            var rotations = new Quaternion[clip.FrameCount];
            var positions = incoming.Positions.Length > 0 || source.Positions.Length > 0 ? new Vector3[clip.FrameCount] : Array.Empty<Vector3>();
            var morphs = vertices > 0 ? new Vector3[clip.FrameCount][] : Array.Empty<Vector3[]>();
            Vector3 positionOffset = set.Parents.GetValueOrDefault(part) == "" && positions.Length > 0
                ? source.Position(0) - incoming.Position(0) : Vector3.Zero;
            for (int f = 0; f < clip.FrameCount; f++)
            {
                float start = f < blend ? 1f - f / (float)(blend - 1) : 0f;
                float end = last - f < blend ? 1f - (last - f) / (float)(blend - 1) : 0f;
                float referenceFrame = start >= end ? 0 : idle.FrameCount - 1;
                float weight = Math.Max(start, end);
                rotations[f] = Quaternion.Normalize(Quaternion.Slerp(incoming.Rotation(f), source.Rotation(referenceFrame), weight));
                if (positions.Length > 0)
                    positions[f] = Vector3.Lerp(incoming.Position(f) + positionOffset, source.Position(referenceFrame), weight);
                if (vertices > 0)
                {
                    morphs[f] = new Vector3[vertices];
                    for (int v = 0; v < vertices; v++)
                        morphs[f][v] = Vector3.Lerp(incoming.Morph(f, v), source.Morph(referenceFrame, v), weight);
                }
            }
            // Sampling through a normalized quaternion preserves the pose but can change its sign.
            rotations[0] = source.Rotation(0);
            rotations[last] = source.Rotation(idle.FrameCount - 1);
            if (positions.Length > 0) { positions[0] = source.Position(0); positions[last] = source.Position(idle.FrameCount - 1); }
            if (vertices > 0)
                for (int v = 0; v < vertices; v++)
                { morphs[0][v] = source.Morph(0, v); morphs[last][v] = source.Morph(idle.FrameCount - 1, v); }
            changed[part] = EncodeTrack(new FigureAnimationTrack(rotations, positions, morphs));
        }
        await ReplaceTracks(name, changed);
    }
}
