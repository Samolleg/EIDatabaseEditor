using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Conservative limits relative to the source pose, only for verified two-link limbs.</summary>
internal static class AnimationPoseConstraints
{
    internal static void Apply(FigureAnimationSet set, FigureAnimationClip clip, int frame,
        Dictionary<string, Quaternion> rotations, Dictionary<string, Vector3> positions,
        bool limits, IReadOnlyDictionary<string, Vector3> pins)
    {
        FigureAnimationClip Current() => FigureAnimationEditor.VirtualPose(set, clip, frame, rotations, positions);
        foreach (var pin in pins)
        {
            if (pin.Key is not ("ll3" or "rl3") || !FigureLimbIk.Supports(set, clip, pin.Key) || !float.IsFinite(pin.Value.LengthSquared()))
                throw new ArgumentException("Для фиксации стоп нужна цепочка бедро–голень–стопа.");
            var temporary = Current();
            var pose = set.Pose(temporary, frame);
            var solution = FigureLimbIk.Solve(set, temporary, frame, pin.Key, pin.Value, pose[pin.Key[..2] + "2"].Position);
            foreach (var q in solution) rotations[q.Key] = q.Value;
            float length = set.Bones[pin.Key].Length() + set.Bones[pin.Key[..2] + "2"].Length();
            if (Vector3.Distance(set.Pose(Current(), frame)[pin.Key].Position, pin.Value) > Math.Max(.00001f, length * .0001f))
                throw new ArgumentException("Стопа вне досягаемости: уменьшите перемещение таза или снимите фиксацию. Изменение отменено целиком.");
        }
        if (!limits) return;
        var reference = set.Pose(clip, frame); var edited = set.Pose(Current(), frame);
        foreach (string end in new[] { "lh3", "rh3", "ll3", "rl3" })
        {
            if (!FigureLimbIk.Supports(set, clip, end)) continue;
            string middle = end[..2] + "2", upper = end[..2] + "1";
            float Bend(IReadOnlyDictionary<string, FigurePose> pose)
            {
                var a = Vector3.Normalize(pose[middle].Position - pose[upper].Position);
                var b = Vector3.Normalize(pose[end].Position - pose[middle].Position);
                return MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1));
            }
            float maximum = Math.Max(Bend(reference), (end[1] == 'l' ? 150 : 165) * MathF.PI / 180);
            if (Bend(edited) > maximum + .001f) throw new ArgumentException("Предел сгиба локтя/колена превышен. Уменьшите правку или отключите ограничения.");
            // Prevent reverse bending in the original upper-limb local plane. Straight
            // source poses have no reliable pole; do not invent an anatomical axis.
            Vector3 Normal(IReadOnlyDictionary<string, FigurePose> pose) => Vector3.Transform(
                Vector3.Cross(pose[middle].Position - pose[upper].Position, pose[end].Position - pose[middle].Position),
                Quaternion.Inverse(pose[upper].Rotation));
            var n0 = Normal(reference); var n1 = Normal(edited);
            if (n0.LengthSquared() > 1e-10f && n1.LengthSquared() > 1e-10f && Vector3.Dot(Vector3.Normalize(n0), Vector3.Normalize(n1)) < -.05f)
                throw new ArgumentException("Обратный сгиб локтя/колена запрещён ограничениями. Изменение не применено.");
        }
    }
}
