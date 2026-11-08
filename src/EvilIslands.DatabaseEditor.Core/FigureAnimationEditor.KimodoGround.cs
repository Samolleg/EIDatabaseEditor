using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    // SOMA30 lower-body offsets from the bundled Kimodo skeleton.hpp.
    // Toe-base height supplies the sole plane; heel is at the same local height.
    private static readonly Vector3[][] SomaLegOffsets =
    [
        [new(.10043214f,-.0843452671f,.0259565473f), new(-1e-8f,-.432217537f,-.00802912805f), new(1e-8f,-.421550959f,-.0348152298f), new(0,-.0505947206f,.132315294f)],
        [new(-.10047278f,-.0829525995f,.0262031695f), new(1e-8f,-.433622059f,-.00805555828f), new(2e-8f,-.421173943f,-.0347839785f), new(-3.42907669e-9f,-.0507960932f,.132841956f)]
    ];

    public static float KimodoSoleClearance(KimodoDraft draft, int frame)
    {
        float floor = float.PositiveInfinity;
        for (int side = 0; side < 2; side++)
        {
            int first = side == 0 ? 22 : 26;
            Vector3 position = draft.RootPositions[frame];
            Quaternion rotation = draft.Rotations[frame][0];
            for (int j = 0; j < 3; j++)
            {
                position += Vector3.Transform(SomaLegOffsets[side][j], rotation);
                rotation = Quaternion.Normalize(rotation * draft.Rotations[frame][first + j]);
            }
            var toe = SomaLegOffsets[side][3];
            floor = Math.Min(floor, (position + Vector3.Transform(toe, rotation)).Y);
            floor = Math.Min(floor, (position + Vector3.Transform(new Vector3(0, toe.Y, 0), rotation)).Y);
        }
        return Math.Max(0, floor);
    }

    public IReadOnlyList<FigureMesh> KimodoFootGeometry(IReadOnlyList<FigureMesh> meshes)
    {
        string Foot(string name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(name))
            {
                if (name.Equals("ll3", StringComparison.OrdinalIgnoreCase)) return "ll3";
                if (name.Equals("rl3", StringComparison.OrdinalIgnoreCase)) return "rl3";
                if (!_source.Parents.TryGetValue(name, out var parent) || parent.Length == 0) break;
                name = parent;
            }
            return "";
        }
        var feet = meshes.Where(m => m.Positions.Length > 0 && Foot(m.Name).Length > 0).ToArray();
        if (!feet.Any(m => Foot(m.Name) == "ll3") || !feet.Any(m => Foot(m.Name) == "rl3"))
            throw new InvalidDataException("Для автоматической высоты нужна геометрия обеих стоп модели, включая надетую броню. Проверьте комплект модели и экипировки.");
        if (feet.Any(m => !_source.BindPositions.ContainsKey(m.Name) || !_source.Bones.ContainsKey(m.Name)))
            throw new InvalidDataException("Для геометрии стоп отсутствует привязка к скелету модели.");
        return feet;
    }

    public async Task ApplyKimodoGroundedDraftAsync(string name, KimodoDraft draft, IReadOnlyList<FigureMesh> meshes,
        KimodoPlayback playback = KimodoPlayback.Once)
    {
        var feet = KimodoFootGeometry(meshes);
        var candidate = new FigureAnimationEditor(Preview());
        await candidate.ApplyKimodoDraftAsync(name, draft, playback);
        var set = candidate.Preview();
        var clip = await set.LoadAsync(name);
        var root = clip.Tracks["hp"];
        float scale = (_source.Bones["ll1"].Length() + _source.Bones["ll2"].Length() + _source.Bones["ll3"].Length()) / (.0844f + .4322f + .4216f);
        float[] clearance = Enumerable.Range(0, draft.Frames).Select(f => KimodoSoleClearance(draft, f) * scale).ToArray();
        if (playback == KimodoPlayback.StationaryLoop)
        {
            int blend = Math.Min(draft.Frames, Math.Clamp(draft.Frames / 10, 2, 12));
            int start = draft.Frames - blend;
            for (int f = start; f < draft.Frames; f++)
                clearance[f] += (clearance[0] - clearance[f]) * Ease((f - start) / (float)(blend - 1), MotionEase.Smooth);
            clearance[^1] = clearance[0];
        }
        var positions = new Vector3[draft.Frames];
        for (int f = 0; f < draft.Frames; f++)
        {
            var pose = set.Pose(clip, f);
            float min = float.PositiveInfinity;
            foreach (var mesh in feet)
            {
                var node = pose[mesh.Name]; var bind = set.BindPositions[mesh.Name];
                var track = clip.Tracks.GetValueOrDefault(mesh.Name);
                for (int v = 0; v < mesh.Positions.Length; v++)
                {
                    var morph = track is { Morphs.Length: > 0 } ? track.Morph(f, mesh.SourceVertices[v]) : Vector3.Zero;
                    min = Math.Min(min, (Vector3.Transform(mesh.Positions[v] - bind + morph, node.Rotation) + node.Position).Z);
                }
            }
            if (!float.IsFinite(min) || !float.IsFinite(clearance[f])) throw new InvalidDataException("Не удалось определить поверхность стоп.");
            var p = set.RootSpace.ToGame(root.Position(f));
            var start = set.RootSpace.ToGame(root.Position(0));
            positions[f] = set.RootSpace.ToRaw(new(p.X - start.X, p.Y - start.Y, p.Z + clearance[f] - min));
        }
        if (playback == KimodoPlayback.StationaryLoop) positions[^1] = positions[0];
        await candidate.ReplaceTracks(name, new Dictionary<string, byte[]> { ["hp"] = EncodeTrack(new FigureAnimationTrack(root.Rotations, positions, root.Morphs)) });
        if ((MotionPins.Count > 0 || LockedParts.Count > 0) && Preview().Names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var before = await Preview().LoadAsync(name);
            var after = await candidate.Preview().LoadAsync(name);
            if (LockedParts.Count > 0)
                for (int f = 0; f < before.FrameCount; f++) CheckLockedPose(Preview(), before, after, f, LockedParts);
            CheckMotionPins(Preview(), before, after, 0, before.FrameCount - 1);
        }
        Accept(candidate.ExportBytes());
    }
}
