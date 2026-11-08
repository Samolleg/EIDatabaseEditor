using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed record KinematicTarget(int Frame, int Joint, Vector3 Position);

internal sealed class KinematicConstraintsConfig
{
    private static readonly Dictionary<string, int> Effectors = new(StringComparer.Ordinal)
    { ["left_hand"] = 13, ["right_hand"] = 19, ["left_foot"] = 24, ["right_foot"] = 28 };
    private static readonly (string Part, int Joint)[] PoseJoints =
    [
        ("hp", 0), ("bd", 3), ("hd", 6), ("lh1", 11), ("lh2", 12), ("lh3", 13),
        ("rh1", 17), ("rh2", 18), ("rh3", 19), ("ll1", 22), ("ll2", 23),
        ("ll3", 24), ("rl1", 26), ("rl2", 27), ("rl3", 28)
    ];
    private readonly record struct PoseRequest(int Frame, string Clip, int SourceFrame);
    private readonly List<(int Frame, float X, float Z)> _roots = [];
    private readonly List<KinematicTarget> _joints = [];
    private readonly List<PoseRequest> _poses = [];
    private float _textGuidance = 2f, _constraintGuidance = 2f;
    internal string SourceJson { get; }
    private KinematicConstraintsConfig(string json) => SourceJson = json;

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ожидался объект JSON.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Неизвестное или повторное поле JSON: " + field.Name);
    }
    private static JsonElement Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var result) ? result : throw new InvalidDataException("Отсутствует поле: " + name);
    private static int Integer(JsonElement value, string name, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || n < minimum || n > maximum)
            throw new InvalidDataException($"{name}: требуется целое {minimum}–{maximum}.");
        return n;
    }
    private static float Number(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float n) || !float.IsFinite(n) || Math.Abs(n) > 100)
            throw new InvalidDataException(name + ": требуется конечное число в метрах (|x| ≤ 100).");
        return n;
    }
    private static string Text(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(name + ": требуется строка.");
        return value.GetString()!;
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException(name + ": требуется массив.");
        return value.EnumerateArray();
    }

    internal static KinematicConstraintsConfig Parse(string json, int frames)
    {
        if (frames is < 2 or > 1000 || json.Length > 100_000) throw new InvalidDataException("Некорректная длина клипа или JSON.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12, AllowTrailingCommas = false });
        var root = document.RootElement;
        Fields(root, "version", "space", "text_guidance", "constraint_guidance", "root2d", "effectors", "key_poses");
        if (Integer(Required(root, "version"), "version", 1, 1) != 1 ||
            Text(Required(root, "space"), "space") != "kimodo_y_up_meters")
            throw new InvalidDataException("Поддерживается только version 1, space kimodo_y_up_meters.");
        var config = new KinematicConstraintsConfig(json);
        if (root.TryGetProperty("text_guidance", out var textGuidance))
            config._textGuidance = Number(textGuidance, "text_guidance");
        if (root.TryGetProperty("constraint_guidance", out var constraintGuidance))
            config._constraintGuidance = Number(constraintGuidance, "constraint_guidance");
        if (config._textGuidance is < 0 or > 10 || config._constraintGuidance is <= 0 or > 10)
            throw new InvalidDataException("text_guidance: 0–10; constraint_guidance: >0–10.");
        if (root.TryGetProperty("root2d", out var paths)) foreach (var item in Array(paths, "root2d"))
        {
            Fields(item, "frame", "x", "z");
            config._roots.Add((Integer(Required(item, "frame"), "frame", 0, frames - 1),
                Number(Required(item, "x"), "x"), Number(Required(item, "z"), "z")));
        }
        if (root.TryGetProperty("effectors", out var effectors)) foreach (var item in Array(effectors, "effectors"))
        {
            Fields(item, "frame", "joint", "position");
            int frame = Integer(Required(item, "frame"), "frame", 0, frames - 1);
            string joint = Text(Required(item, "joint"), "joint");
            if (!Effectors.TryGetValue(joint, out int index)) throw new InvalidDataException("Неподдерживаемая конечность: " + joint);
            var coords = Required(item, "position");
            if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() != 3)
                throw new InvalidDataException("position: требуется [x, y, z] в метрах.");
            config._joints.Add(new(frame, index, new(Number(coords[0], "x"), Number(coords[1], "y"), Number(coords[2], "z"))));
        }
        if (root.TryGetProperty("key_poses", out var poses)) foreach (var item in Array(poses, "key_poses"))
        {
            Fields(item, "frame", "source_clip", "source_frame");
            config._poses.Add(new(Integer(Required(item, "frame"), "frame", 0, frames - 1),
                Text(Required(item, "source_clip"), "source_clip"),
                Integer(Required(item, "source_frame"), "source_frame", 0, 10000)));
        }
        if (config._roots.Count + config._joints.Count + config._poses.Count == 0)
            throw new InvalidDataException("Добавьте хотя бы одну цель root2d, effectors или key_poses.");
        if (config._roots.GroupBy(x => x.Frame).Any(g => g.Count() > 1) ||
            config._joints.GroupBy(x => (x.Frame, x.Joint)).Any(g => g.Count() > 1) ||
            config._poses.GroupBy(x => x.Frame).Any(g => g.Count() > 1))
            throw new InvalidDataException("Повторная цель для того же кадра и сустава.");
        if (config._roots.Any(x => x.Frame == 0 && (Math.Abs(x.X) > .0001f || Math.Abs(x.Z) > .0001f)))
            throw new InvalidDataException("Корень в кадре 0 должен иметь координаты [0, 0].");
        return config;
    }

    internal async Task<KinematicResolved> ResolveAsync(FigureAnimationSet set, int frames,
        FigureAnimationSet? groundReference = null, IReadOnlyList<FigureMesh>? meshes = null)
    {
        var roots = _roots.ToDictionary(x => x.Frame, x => new Vector2(x.X, x.Z));
        var joints = new List<KinematicTarget>(_joints);
        float scale = 1f, floor = 0f, referencePelvisHeight = 0f;
        if (_poses.Count > 0 || _joints.Count > 0)
        {
            if (groundReference is null || meshes is null)
                throw new InvalidDataException("Для целей суставов нужны исходный ANM и геометрия стоп модели.");
            if (PoseJoints.Any(pair => !set.Bones.ContainsKey(pair.Part) || !set.Parents.ContainsKey(pair.Part)))
                throw new InvalidDataException("Скелет исходной модели несовместим с целями SOMA30.");
            float gameLeg = set.Bones["ll1"].Length() + set.Bones["ll2"].Length() + set.Bones["ll3"].Length();
            scale = gameLeg / (.0844f + .4322f + .4216f);
            if (!float.IsFinite(scale) || scale is < .01f or > 100f) throw new InvalidDataException("Некорректный масштаб скелета.");
            string idleName = groundReference.Names.FirstOrDefault(n => n.Equals("cidle09", StringComparison.OrdinalIgnoreCase))
                ?? groundReference.Names.FirstOrDefault(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Для высоты позы нужен исходный клип покоя модели.");
            var idleClip = await groundReference.LoadAsync(idleName);
            var idlePose = groundReference.Pose(idleClip, 0);
            var feet = new FigureAnimationEditor(groundReference).KimodoFootGeometry(meshes);
            floor = float.PositiveInfinity;
            foreach (var mesh in feet)
            {
                var node = idlePose[mesh.Name];
                var bind = groundReference.BindPositions[mesh.Name];
                var track = idleClip.Tracks.GetValueOrDefault(mesh.Name);
                for (int vertex = 0; vertex < mesh.Positions.Length; vertex++)
                {
                    var morph = track is { Morphs.Length: > 0 } ? track.Morph(0, mesh.SourceVertices[vertex]) : Vector3.Zero;
                    floor = Math.Min(floor, (Vector3.Transform(mesh.Positions[vertex] - bind + morph, node.Rotation) + node.Position).Z);
                }
            }
            if (!float.IsFinite(floor)) throw new InvalidDataException("Не удалось определить исходную плоскость подошв.");
            referencePelvisHeight = (idlePose["hp"].Position.Z - floor) / scale;
        }
        if (_poses.Count > 0)
        {
            Quaternion gameToSoma = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
            foreach (var request in _poses)
            {
                var clip = await set.LoadAsync(request.Clip);
                await new FigureAnimationEditor(set).ValidateKimodoTargetAsync(request.Clip, clip.FrameCount);
                if (request.SourceFrame >= clip.FrameCount) throw new InvalidDataException("Кадр исходной позы вне клипа: " + request.Clip);
                var pose = set.Pose(clip, request.SourceFrame);
                var start = set.Pose(clip, 0);
                Vector3 delta = Vector3.Transform(pose["hp"].Position - start["hp"].Position, gameToSoma) / scale;
                if (!roots.TryAdd(request.Frame, new(delta.X, delta.Z)))
                {
                    var defined = roots[request.Frame];
                    if (Vector2.Distance(defined, new(delta.X, delta.Z)) > .02f)
                        throw new InvalidDataException("Корень позы конфликтует с root2d в кадре " + request.Frame);
                }
                var root = roots[request.Frame];
                // Use the immutable source idle's rendered sole plane, not an
                // animated ankle in the editable draft (which would erase jumps).
                float height = (pose["hp"].Position.Z - floor) / scale;
                if (!float.IsFinite(height) || height is < .1f or > 2.5f)
                    throw new InvalidDataException("Высота таза относительно стоп вне проверяемого диапазона.");
                foreach (var (part, index) in PoseJoints)
                {
                    Vector3 relative = Vector3.Transform(pose[part].Position - pose["hp"].Position, gameToSoma) / scale;
                    var target = new Vector3(root.X + relative.X, height + relative.Y, root.Y + relative.Z);
                    if (joints.Any(x => x.Frame == request.Frame && x.Joint == index))
                        throw new InvalidDataException("Поза конфликтует с отдельной целью конечности в кадре " + request.Frame);
                    joints.Add(new(request.Frame, index, target));
                }
            }
        }
        foreach (var target in joints)
            if (!roots.ContainsKey(target.Frame)) throw new InvalidDataException("Для конечности требуется root2d в том же кадре: " + target.Frame);
        var orderedRoots = roots.OrderBy(x => x.Key).ToArray();
        for (int index = 1; index < orderedRoots.Length; index++)
        {
            float seconds = (orderedRoots[index].Key - orderedRoots[index - 1].Key) / 30f;
            if (Vector2.Distance(orderedRoots[index].Value, orderedRoots[index - 1].Value) / seconds > 5f)
                throw new InvalidDataException("Между целями корня требуется скорость более 5 м/с.");
        }
        foreach (var target in joints)
        {
            var root = roots[target.Frame];
            float pelvisHeight = joints.FirstOrDefault(x => x.Frame == target.Frame && x.Joint == 0)?.Position.Y ?? referencePelvisHeight;
            if (Vector3.Distance(target.Position, new Vector3(root.X, pelvisHeight, root.Y)) > 2.2f)
                throw new InvalidDataException("Цель сустава слишком далека от таза в кадре " + target.Frame);
        }
        if (roots.TryGetValue(0, out var origin) && origin.Length() > .0001f)
            throw new InvalidDataException("Корень в кадре 0 должен быть [0, 0].");
        var lines = new StringBuilder();
        foreach (var (frame, value) in roots.OrderBy(x => x.Key))
            lines.Append("root ").Append(frame).Append(' ').Append(value.X.ToString("R", CultureInfo.InvariantCulture))
                .Append(' ').Append(value.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var target in joints.OrderBy(x => x.Frame).ThenBy(x => x.Joint))
            lines.Append("joint ").Append(target.Frame).Append(' ').Append(target.Joint).Append(' ')
                .Append(target.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(target.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(target.Position.Z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return new KinematicResolved(lines.ToString(), roots.Count, joints.Count, _textGuidance, _constraintGuidance);
    }
}

internal sealed record KinematicResolved(string NativeTargets, int RootCount, int JointCount,
    float TextGuidance, float ConstraintGuidance);
