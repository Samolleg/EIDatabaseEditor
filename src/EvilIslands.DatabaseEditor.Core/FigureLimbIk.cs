using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public static class FigureLimbIk
{
    public static bool Supports(FigureAnimationSet set, FigureAnimationClip clip, string end)
    {
        if (end is not ("lh3" or "rh3" or "ll3" or "rl3")) return false;
        string prefix = end[..2], middle = prefix + "2", upper = prefix + "1";
        return set.Parents.GetValueOrDefault(end) == middle && set.Parents.GetValueOrDefault(middle) == upper
            && clip.Tracks.ContainsKey(end) && clip.Tracks.ContainsKey(middle) && clip.Tracks.ContainsKey(upper);
    }
    public static IReadOnlyDictionary<string, Quaternion> Solve(FigureAnimationSet set, FigureAnimationClip clip, int frame, string end, Vector3 target, Vector3? poleTarget = null)
    {
        if (!Supports(set, clip, end) || frame < 0 || frame >= clip.FrameCount || !float.IsFinite(target.LengthSquared()) || (poleTarget.HasValue && !float.IsFinite(poleTarget.Value.LengthSquared())))
            throw new ArgumentException("Для IK нужна цепочка плечо–локоть–кисть или бедро–колено–стопа и конечная цель.");
        string middle = end[..2] + "2", upper = end[..2] + "1";
        var pose = set.Pose(clip, frame);
        Vector3 a = pose[upper].Position, b = pose[middle].Position, c = pose[end].Position;
        float l1 = Vector3.Distance(a, b), l2 = Vector3.Distance(b, c);
        if (Math.Min(l1, l2) < .00001f) throw new InvalidDataException("Нулевая длина сегмента IK.");
        Vector3 to = target - a;
        if (to.LengthSquared() < 1e-12f) to = c - a;
        if (to.LengthSquared() < 1e-12f) to = b - a;
        Vector3 direction = Vector3.Normalize(to);
        float epsilon = poleTarget.HasValue ? 0 : Math.Min(l1, l2) * .001f;
        float distance = Math.Clamp(to.Length(), Math.Max(1e-8f, Math.Abs(l1 - l2) + epsilon), l1 + l2 - epsilon);
        target = a + direction * distance;
        Vector3 guide = (poleTarget ?? b) - a;
        Vector3 pole = guide - direction * Vector3.Dot(guide, direction);
        if (pole.LengthSquared() < 1e-10f) pole = b - a - direction * Vector3.Dot(b - a, direction);
        if (pole.LengthSquared() < 1e-10f)
        {
            var axis = Vector3.Transform(Vector3.UnitZ, pose[upper].Rotation);
            pole = axis - direction * Vector3.Dot(axis, direction);
            if (pole.LengthSquared() < 1e-10f) pole = Vector3.Cross(direction, Math.Abs(direction.X) < .8f ? Vector3.UnitX : Vector3.UnitY);
        }
        pole = Vector3.Normalize(pole);
        float along = (l1 * l1 - l2 * l2 + distance * distance) / (2 * distance);
        Vector3 desiredMiddle = a + direction * along + pole * MathF.Sqrt(Math.Max(0, l1 * l1 - along * along));
        var rotations = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
        void SetWorld(string node, Quaternion world, IReadOnlyDictionary<string, FigurePose> current)
        {
            string parent = set.Parents[node];
            rotations[node] = Quaternion.Normalize(parent.Length == 0 ? world : world * Quaternion.Inverse(current[parent].Rotation));
        }
        SetWorld(upper, Between(b - a, desiredMiddle - a) * pose[upper].Rotation, pose);
        var next = set.Pose(clip, frame, rotations: rotations);
        SetWorld(middle, Between(next[end].Position - next[middle].Position, target - next[middle].Position) * next[middle].Rotation, next);
        next = set.Pose(clip, frame, rotations: rotations);
        // Keep the hand/foot orientation while moving its endpoint.
        SetWorld(end, pose[end].Rotation, next);
        return rotations;
    }
    // Moving a joint origin rotates its parent bone, not the joint itself.
    // Preserve bind offsets/lengths and move the rest of that parent's subtree coherently.
    public static bool SupportsJoint(FigureAnimationSet set, FigureAnimationClip clip, string joint) =>
        set.Parents.TryGetValue(joint, out var parent) && parent.Length > 0 && clip.Tracks.ContainsKey(parent);

    public static IReadOnlyDictionary<string, Quaternion> SolveJoint(FigureAnimationSet set,
        FigureAnimationClip clip, int frame, string joint, Vector3 target)
    {
        if (!SupportsJoint(set, clip, joint) || frame < 0 || frame >= clip.FrameCount || !float.IsFinite(target.LengthSquared()))
            throw new ArgumentException("Для перемещения сустава нужна редактируемая родительская кость.");
        var pose = set.Pose(clip, frame);
        string parent = set.Parents[joint];
        Vector3 from = pose[joint].Position - pose[parent].Position, to = target - pose[parent].Position;
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
            throw new InvalidDataException("Сустав совпадает с опорной точкой: используйте кольца вращения.");
        var world = Quaternion.Normalize(Between(from, to) * pose[parent].Rotation);
        string ancestor = set.Parents[parent];
        return new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase)
        { [parent] = Quaternion.Normalize(ancestor.Length == 0 ? world : world * Quaternion.Inverse(pose[ancestor].Rotation)) };
    }
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        var cross = Vector3.Cross(from, to);
        if (dot >= 0 && cross.LengthSquared() < 1e-16f) return Quaternion.Identity;
        if (dot < 0 && cross.LengthSquared() < 1e-16f)
        {
            var axis = Vector3.Normalize(Vector3.Cross(from, Math.Abs(from.X) < .8f ? Vector3.UnitX : Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(cross), MathF.Atan2(cross.Length(), dot));
    }
}
