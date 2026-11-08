using System.Globalization;
using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    // Apply the already conditioned sparse targets to the game rig. This is a
    // separate, checked transfer step; it cannot make unmapped SOMA joints exact.
    public async Task<IReadOnlyList<string>> AlignKimodoSpatialTargetsAsync(string name, string targets)
    {
        if (string.IsNullOrWhiteSpace(targets)) return [];
        if (Contacts.Count > 0) throw new ArgumentException("Снимите контакты перед переносом пространственных целей.");
        var set = Preview();
        var clip = await set.LoadAsync(name);
        float scale = (set.Bones["ll1"].Length() + set.Bones["ll2"].Length() + set.Bones["ll3"].Length()) /
            (.0844f + .4322f + .4216f);
        var roots = new SortedDictionary<int, Vector2>();
        var joints = new List<(int Frame, int Joint, Vector3 Target)>();
        float Number(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        foreach (var line in targets.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 4 && fields[0] == "root")
                roots.Add(int.Parse(fields[1], CultureInfo.InvariantCulture), new(Number(fields[2]), Number(fields[3])));
            else if (fields.Length == 6 && fields[0] == "joint")
                joints.Add((int.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture),
                    new(Number(fields[3]), Number(fields[4]), Number(fields[5]))));
            else throw new InvalidDataException("Некорректная сохранённая пространственная цель.");
        }
        if (roots.Keys.Any(frame => frame < 0 || frame >= clip.FrameCount) ||
            joints.Any(j => j.Frame < 0 || j.Frame >= clip.FrameCount || !float.IsFinite(j.Target.LengthSquared())))
            throw new InvalidDataException("Сохранённая цель выходит за пределы клипа.");
        var working = new FigureAnimationEditor(set) { MotionPins = MotionPins, Contacts = Contacts };
        working.LockedParts.UnionWith(LockedParts);
        var baseRoot = clip.Tracks["hp"];
        var corrections = roots.Select(kv => (kv.Key, Delta: new Vector2(
            kv.Value.X * scale - set.RootSpace.ToGame(baseRoot.Position(kv.Key)).X,
            -kv.Value.Y * scale - set.RootSpace.ToGame(baseRoot.Position(kv.Key)).Y))).ToArray();
        var heightCorrections = joints.Where(j => j.Joint == 0).Select(j => (j.Frame,
            Delta: j.Target.Y * scale - set.RootSpace.ToGame(baseRoot.Position(j.Frame)).Z)).OrderBy(p => p.Frame).ToArray();
        T Between<T>(int frame, (int Key, T Delta)[] points, Func<T, T, float, T> blend, T zero)
        {
            if (points.Length == 0) return zero;
            if (frame <= points[0].Key) return points[0].Delta;
            for (int i = 1; i < points.Length; i++)
                if (frame <= points[i].Key)
                    return blend(points[i - 1].Delta, points[i].Delta,
                        (frame - points[i - 1].Key) / (float)(points[i].Key - points[i - 1].Key));
            return points[^1].Delta;
        }
        if (corrections.Length > 0 || heightCorrections.Length > 0)
        {
            var positions = Enumerable.Range(0, clip.FrameCount).Select(frame =>
            {
                Vector3 p = set.RootSpace.ToGame(baseRoot.Position(frame));
                Vector2 horizontal = Between(frame, corrections, Vector2.Lerp, Vector2.Zero);
                float vertical = Between(frame, heightCorrections, (a, b, t) => a + (b - a) * t, 0f);
                return set.RootSpace.ToRaw(new Vector3(p.X + horizontal.X, p.Y + horizontal.Y, p.Z + vertical));
            }).ToArray();
            await working.ReplaceTracks(name, new Dictionary<string, byte[]>
            { ["hp"] = EncodeTrack(new FigureAnimationTrack(baseRoot.Rotations, positions, baseRoot.Morphs)) });
        }
        var messages = new List<string>();
        var ends = new Dictionary<int, string> { [13] = "lh3", [19] = "rh3", [24] = "ll3", [28] = "rl3" };
        var fixedFrames = new HashSet<(string End, int Frame)>();
        foreach (var goal in joints.Where(j => ends.ContainsKey(j.Joint)).OrderBy(j => j.Frame).ThenBy(j => j.Joint))
        {
            string end = ends[goal.Joint];
            Vector3 target = new(goal.Target.X * scale, -goal.Target.Z * scale, goal.Target.Y * scale);
            var currentSet = working.Preview();
            var currentClip = await currentSet.LoadAsync(name);
            var pose = currentSet.Pose(currentClip, goal.Frame);
            string upper = end[..2] + "1", middle = end[..2] + "2";
            float reach = Vector3.Distance(pose[upper].Position, pose[middle].Position) +
                Vector3.Distance(pose[middle].Position, pose[end].Position);
            if (Vector3.Distance(pose[upper].Position, target) > reach + .005f * scale)
                throw new InvalidDataException($"Кадр {goal.Frame}: цель {end} вне длины игровой цепи.");
            var rotations = FigureLimbIk.Solve(currentSet, currentClip, goal.Frame, end, target);
            const int blendRadius = 8;
            for (int frame = Math.Max(0, goal.Frame - blendRadius);
                 frame <= Math.Min(clip.FrameCount - 1, goal.Frame + blendRadius); frame++)
            {
                // A later overlapping goal must not move an endpoint that has
                // already been solved and validated at its own key frame.
                if (frame != goal.Frame && fixedFrames.Contains((end, frame))) continue;
                float t = 1f - Math.Abs(frame - goal.Frame) / (float)(blendRadius + 1);
                float weight = t * t * (3f - 2f * t);
                var neighborSet = working.Preview();
                var neighborClip = await neighborSet.LoadAsync(name);
                var softened = rotations.ToDictionary(pair => pair.Key, pair =>
                {
                    Quaternion beforeGoal = currentClip.Tracks[pair.Key].Rotation(goal.Frame);
                    Quaternion correction = Quaternion.Normalize(pair.Value * Quaternion.Inverse(beforeGoal));
                    Quaternion baseRotation = neighborClip.Tracks[pair.Key].Rotation(frame);
                    return Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, correction, weight) * baseRotation);
                }, StringComparer.OrdinalIgnoreCase);
                await working.SetRotationsAsync(name, frame, softened);
            }
            var resultSet = working.Preview();
            var result = await resultSet.LoadAsync(name);
            float error = Vector3.Distance(resultSet.Pose(result, goal.Frame)[end].Position, target) / scale;
            if (error > .03f)
                throw new InvalidDataException($"Кадр {goal.Frame}: {end} отличается от цели на {error:F3} м после IK.");
            fixedFrames.Add((end, goal.Frame));
            messages.Add($"frame={goal.Frame} part={end} game_error_m={error.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        foreach (var chain in fixedFrames.GroupBy(p => p.End))
        {
            var frames = chain.Select(p => p.Frame).Order().ToArray();
            for (int index = 1; index < frames.Length; index++)
            {
                int first = frames[index - 1], last = frames[index];
                if (last - first < 2 || last - first > 16) continue;
                var solvedSet = working.Preview();
                var solvedClip = await solvedSet.LoadAsync(name);
                foreach (int frame in Enumerable.Range(first + 1, last - first - 1))
                {
                    float fraction = (frame - first) / (float)(last - first);
                    var blended = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
                    foreach (string part in new[] { chain.Key[..2] + "1", chain.Key[..2] + "2", chain.Key })
                        blended[part] = Quaternion.Slerp(solvedClip.Tracks[part].Rotation(first),
                            solvedClip.Tracks[part].Rotation(last), fraction);
                    await working.SetRotationsAsync(name, frame, blended);
                }
            }
        }
        var finalSet = working.Preview();
        var finalClip = await finalSet.LoadAsync(name);
        foreach (var (frame, target) in roots)
        {
            Vector3 actual = finalSet.Pose(finalClip, frame)["hp"].Position;
            if (Vector2.Distance(new(actual.X / scale, -actual.Y / scale), target) > .001f)
                throw new InvalidDataException($"Кадр {frame}: путь корня изменился после доводки конечностей.");
        }
        messages.Clear();
        foreach (var goal in joints)
        {
            string? part = goal.Joint == 0 ? "hp" : ends.GetValueOrDefault(goal.Joint);
            if (part is null) continue; // Other SOMA joints are documented soft targets.
            Vector3 actual = finalSet.Pose(finalClip, goal.Frame)[part].Position;
            Vector3 target = new(goal.Target.X * scale, -goal.Target.Z * scale, goal.Target.Y * scale);
            float error = part == "hp" ? Math.Abs(actual.Z - target.Z) / scale :
                Vector3.Distance(actual, target) / scale;
            if (error > .03f)
                throw new InvalidDataException($"Кадр {goal.Frame}: {part} изменился на {error:F3} м после других целей.");
            messages.Add($"frame={goal.Frame} part={part} game_error_m={error.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        if (LockedParts.Count > 0)
            for (int frame = 0; frame < clip.FrameCount; frame++)
                CheckLockedPose(set, clip, finalClip, frame, LockedParts);
        CheckMotionPins(set, clip, finalClip, 0, clip.FrameCount - 1);
        AdoptSavedBytes(working.ExportBytes());
        return messages;
    }
}
