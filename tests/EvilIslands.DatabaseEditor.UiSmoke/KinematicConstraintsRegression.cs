using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class KinematicConstraintsRegression
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("KINEMATIC CONSTRAINTS: " + message); }
    private static void Reject(string json, int frames)
    {
        try { KinematicConstraintsConfig.Parse(json, frames); throw new Exception("invalid JSON accepted: " + json); }
        catch (InvalidDataException) { }
    }
    internal static int Run(string database, string? targetFile = null, string? rawDirectory = null)
    { Verify(database, targetFile, rawDirectory).GetAwaiter().GetResult(); return 0; }
    internal static int RunHand(string database, string rawDirectory)
    { VerifyHand(database, rawDirectory).GetAwaiter().GetResult(); return 0; }
    internal static int RunCapabilities(string oldGenerator, string newGenerator)
    { VerifyCapabilities(oldGenerator, newGenerator).GetAwaiter().GetResult(); return 0; }
    internal static int RunOverlap(string database, string rawDirectory)
    { VerifyOverlap(database, rawDirectory).GetAwaiter().GetResult(); return 0; }
    private static async Task VerifyOverlap(string database, string rawDirectory)
    {
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var editor = new FigureAnimationEditor(scene.Animations!);
        await editor.CreateAnimationAsync("cidle09", "kin_overlap", 40, true);
        await editor.ApplyKimodoGroundedDraftAsync("kin_overlap", KimodoDraft.ReadSoma30(rawDirectory, 40),
            scene.Meshes, KimodoPlayback.Once);
        var initial = editor.Preview();
        var clip = await initial.LoadAsync("kin_overlap");
        float scale = (initial.Bones["ll1"].Length() + initial.Bones["ll2"].Length() + initial.Bones["ll3"].Length()) /
            (.0844f + .4322f + .4216f);
        Vector3 Goal(int frame, float side)
        {
            var pose = initial.Pose(clip, frame);
            Vector3 shoulder = pose["lh1"].Position, hand = pose["lh3"].Position;
            Vector3 direction = Vector3.Normalize(hand - shoulder);
            Vector3 lateral = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitZ));
            float reach = Vector3.Distance(shoulder, pose["lh2"].Position) + Vector3.Distance(pose["lh2"].Position, hand);
            return shoulder + direction * (.65f * reach) + lateral * (side * .12f * scale);
        }
        Vector3 first = Goal(0, 1), second = Goal(4, -1);
        string F(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string Root(int frame)
        {
            var p = initial.Pose(clip, frame)["hp"].Position;
            return $"root {frame} {F(p.X / scale)} {F(-p.Y / scale)}\n";
        }
        string Joint(int frame, Vector3 game) =>
            $"joint {frame} 13 {F(game.X / scale)} {F(game.Z / scale)} {F(-game.Y / scale)}\n";
        string targets = Root(0) + Root(4) + Joint(0, first) + Joint(4, second);
        await editor.AlignKimodoSpatialTargetsAsync("kin_overlap", targets);
        var result = editor.Preview();
        var changed = await result.LoadAsync("kin_overlap");
        float e0 = Vector3.Distance(result.Pose(changed, 0)["lh3"].Position, first) / scale;
        float e4 = Vector3.Distance(result.Pose(changed, 4)["lh3"].Position, second) / scale;
        float maxStep = Enumerable.Range(1, 8).Max(frame => Vector3.Distance(
            result.Pose(changed, frame - 1)["lh3"].Position,
            result.Pose(changed, frame)["lh3"].Position) / scale);
        Console.WriteLine("KINEMATIC OVERLAP STEPS: " + string.Join(",", Enumerable.Range(1, 8).Select(frame =>
            (Vector3.Distance(result.Pose(changed, frame - 1)["lh3"].Position,
                result.Pose(changed, frame)["lh3"].Position) / scale).ToString("F4"))));
        Console.WriteLine($"KINEMATIC OVERLAP: frame0_m={e0:F6}, frame4_m={e4:F6}, max_neighbor_step_m={maxStep:F6}");
        Check(e0 <= .03f && e4 <= .03f, "overlapping hand targets were not both retained");
        Check(maxStep < .15f, "overlapping hand targets produced a one-frame spike");
        byte[] beforeConflict = editor.ExportBytes();
        try
        {
            await editor.AlignKimodoSpatialTargetsAsync("kin_overlap", Root(0) + Joint(0, first) + Joint(0, second));
            throw new Exception("two conflicting same-frame hand goals were accepted");
        }
        catch (InvalidDataException) { }
        Check(editor.ExportBytes().SequenceEqual(beforeConflict), "final all-goal refusal changed ANM");
    }
    private static async Task VerifyCapabilities(string oldGenerator, string newGenerator)
    {
        try
        {
            await AnimationEditorDialog.VerifyKinematicGeneratorAsync(oldGenerator);
            throw new Exception("old generator accepted spatial config");
        }
        catch (InvalidDataException) { }
        await AnimationEditorDialog.VerifyKinematicGeneratorAsync(newGenerator);
        Console.WriteLine("KINEMATIC CAPABILITIES PASS: old generator refused, new generator accepted before draft/history changes.");
    }
    private static async Task VerifyHand(string database, string rawDirectory)
    {
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var config = KinematicConstraintsConfig.Parse(
            """{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0},{"frame":39,"x":0.3,"z":0.1}],"effectors":[{"frame":39,"joint":"left_hand","position":[0.2,0.9,0.4]}]}""", 40);
        var targets = await config.ResolveAsync(scene.Animations!, 40, scene.Animations!, scene.Meshes);
        var editor = new FigureAnimationEditor(scene.Animations!);
        await editor.CreateAnimationAsync("cidle09", "kinematic_hand", 40, true);
        await editor.ApplyKimodoGroundedDraftAsync("kinematic_hand", KimodoDraft.ReadSoma30(rawDirectory, 40),
            scene.Meshes, KimodoPlayback.Once);
        var before = editor.Preview();
        var clip = await before.LoadAsync("kinematic_hand");
        var pose = before.Pose(clip, 39);
        float scale = (before.Bones["ll1"].Length() + before.Bones["ll2"].Length() + before.Bones["ll3"].Length()) /
            (.0844f + .4322f + .4216f);
        Vector3 target = new(.2f * scale, -.4f * scale, .9f * scale);
        float prior = Vector3.Distance(pose["lh3"].Position, target) / scale;
        var details = await editor.AlignKimodoSpatialTargetsAsync("kinematic_hand", targets.NativeTargets);
        var result = editor.Preview();
        var finalClip = await result.LoadAsync("kinematic_hand");
        var finalPose = result.Pose(finalClip, 39);
        float error = Vector3.Distance(finalPose["lh3"].Position, target) / scale;
        Check(Math.Abs(finalPose["hp"].Position.X / scale - .3f) < .001f &&
              Math.Abs(-finalPose["hp"].Position.Y / scale - .1f) < .001f,
            "root path target did not survive game transfer");
        Check(error < .03f && error < prior, "single hand goal did not reach game rig");
        byte[] accepted = editor.ExportBytes();
        try
        {
            await editor.AlignKimodoSpatialTargetsAsync("kinematic_hand",
                "root 0 0 0\nroot 39 0.3 0.1\njoint 39 13 1.9 0.9 0.4\n");
            throw new Exception("unreachable game arm accepted");
        }
        catch (InvalidDataException) { }
        Check(editor.ExportBytes().SequenceEqual(accepted), "unreachable target changed ANM");
        float seam = Vector3.Distance(result.Pose(finalClip, 38)["lh3"].Position, finalPose["lh3"].Position) / scale;
        Console.WriteLine($"KINEMATIC HAND: before_m={prior:F6}, after_m={error:F6}, frame38_to_39_m={seam:F6}, {string.Join("; ", details)}");
    }
    private static async Task Verify(string database, string? targetFile, string? rawDirectory)
    {
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var source = scene.Animations!;
        const string path = """
            {"version":1,"space":"kimodo_y_up_meters", "root2d":[
              {"frame":0,"x":0,"z":0},{"frame":39,"x":0.3,"z":0.1}],
             "effectors":[{"frame":39,"joint":"left_hand","position":[0.2,0.9,0.4]}]}
            """;
        var resolved = await KinematicConstraintsConfig.Parse(path, 40).ResolveAsync(source, 40, source, scene.Meshes);
        Check(resolved.RootCount == 2 && resolved.JointCount == 1 &&
            resolved.NativeTargets.Contains("root 39 0.3 0.1") &&
            resolved.NativeTargets.Contains("joint 39 13 0.2 0.9 0.4"), "root/hand encoding");
        var pose = await KinematicConstraintsConfig.Parse(
            """{"version":1,"space":"kimodo_y_up_meters","key_poses":[{"frame":0,"source_clip":"cidle09","source_frame":0}]}""", 40)
            .ResolveAsync(source, 40, source, scene.Meshes);
        Check(pose.RootCount == 1 && pose.JointCount == 15 &&
            pose.NativeTargets.Contains("joint 0 13 ") && pose.NativeTargets.Contains("joint 0 28 "),
            "pose did not cover the mapped game rig");
        float Height(KinematicResolved value) => float.Parse(value.NativeTargets.Split('\n')
            .Single(s => s.StartsWith("joint 0 0 ", StringComparison.Ordinal)).Split(' ')[4],
            System.Globalization.CultureInfo.InvariantCulture);
        var sourceClip = await source.LoadAsync("cidle09");
        var rootTrack = sourceClip.Tracks["hp"];
        float poseScale = (source.Bones["ll1"].Length() + source.Bones["ll2"].Length() + source.Bones["ll3"].Length()) /
            (.0844f + .4322f + .4216f);
        var idlePose = source.Pose(sourceClip, 0);
        float sourceFloor = float.PositiveInfinity;
        foreach (var mesh in new FigureAnimationEditor(source).KimodoFootGeometry(scene.Meshes))
        {
            var node = idlePose[mesh.Name];
            var bind = source.BindPositions[mesh.Name];
            var track = sourceClip.Tracks.GetValueOrDefault(mesh.Name);
            for (int vertex = 0; vertex < mesh.Positions.Length; vertex++)
            {
                var morph = track is { Morphs.Length: > 0 } ? track.Morph(0, mesh.SourceVertices[vertex]) : Vector3.Zero;
                sourceFloor = Math.Min(sourceFloor,
                    (Vector3.Transform(mesh.Positions[vertex] - bind + morph, node.Rotation) + node.Position).Z);
            }
        }
        Check(Math.Abs(sourceFloor) > .00001f &&
              Math.Abs(Height(pose) - (idlePose["hp"].Position.Z - sourceFloor) / poseScale) < .002f,
            "nonzero original sole plane was not normalized to game floor zero");
        foreach (float heightDelta in new[] { .2f, -.2f })
        {
            var draftEditor = new FigureAnimationEditor(source);
            await draftEditor.SetPoseAsync("cidle09", "hp", 0, rootTrack.Rotation(0),
                rootTrack.Position(0) + new Vector3(0, 0, heightDelta / source.RootSpace.Coefficient));
            var draftPose = await KinematicConstraintsConfig.Parse(
                """{"version":1,"space":"kimodo_y_up_meters","key_poses":[{"frame":0,"source_clip":"cidle09","source_frame":0}]}""", 40)
                .ResolveAsync(draftEditor.Preview(), 40, source, scene.Meshes);
            Check(Math.Abs(Height(draftPose) - Height(pose) - heightDelta / poseScale) < .002f,
                "current unsaved draft and absolute pelvis height were not captured exactly");
        }
        var liftedFoot = new FigureAnimationEditor(source);
        await liftedFoot.SetPoseAsync("cidle09", "ll2", 0,
            Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f) * sourceClip.Tracks["ll2"].Rotation(0)));
        var lifted = await KinematicConstraintsConfig.Parse(
            """{"version":1,"space":"kimodo_y_up_meters","key_poses":[{"frame":0,"source_clip":"cidle09","source_frame":0}]}""", 40)
            .ResolveAsync(liftedFoot.Preview(), 40, source, scene.Meshes);
        Check(Math.Abs(Height(lifted) - Height(pose)) < .002f,
            "lifting one animated foot moved the immutable source sole plane");
        var armored = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Kania Army Officer MC F11"), rows);
        var armorSet = armored.Animations!;
        var armorClip = await armorSet.LoadAsync("cidle09");
        var armorPose = await KinematicConstraintsConfig.Parse(
            """{"version":1,"space":"kimodo_y_up_meters","key_poses":[{"frame":0,"source_clip":"cidle09","source_frame":0}]}""", 40)
            .ResolveAsync(armorSet, 40, armorSet, armored.Meshes);
        var armorWorld = armorSet.Pose(armorClip, 0);
        float armorFloor = float.PositiveInfinity;
        foreach (var mesh in new FigureAnimationEditor(armorSet).KimodoFootGeometry(armored.Meshes))
        {
            var node = armorWorld[mesh.Name];
            var bind = armorSet.BindPositions[mesh.Name];
            var track = armorClip.Tracks.GetValueOrDefault(mesh.Name);
            for (int vertex = 0; vertex < mesh.Positions.Length; vertex++)
            {
                var morph = track is { Morphs.Length: > 0 } ? track.Morph(0, mesh.SourceVertices[vertex]) : Vector3.Zero;
                armorFloor = Math.Min(armorFloor,
                    (Vector3.Transform(mesh.Positions[vertex] - bind + morph, node.Rotation) + node.Position).Z);
            }
        }
        float armorScale = (armorSet.Bones["ll1"].Length() + armorSet.Bones["ll2"].Length() + armorSet.Bones["ll3"].Length()) /
            (.0844f + .4322f + .4216f);
        Check(Math.Abs(Height(armorPose) - (armorWorld["hp"].Position.Z - armorFloor) / armorScale) < .002f,
            "armored sole height was not used for pose conversion");
        Console.WriteLine($"KINEMATIC SOLES: hero_world_z={sourceFloor:F6}, armored_world_z={armorFloor:F6}, normalized_to_zero=PASS");
        if (targetFile is not null) File.WriteAllText(targetFile, pose.NativeTargets);
        if (rawDirectory is not null)
        {
            int generatedFrames = (int)(new FileInfo(Path.Combine(rawDirectory, KimodoGenerationHistory.RootStream)).Length / 12);
            Check(generatedFrames == 40, "raw clip frame count");
            byte[] rawJoints = File.ReadAllBytes(Path.Combine(rawDirectory, KimodoGenerationHistory.JointStream));
            var targets = pose.NativeTargets.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith("joint "))
                .Select(s => s.Split(' ')).ToDictionary(s => int.Parse(s[2]), s => new Vector3(
                    float.Parse(s[3], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(s[4], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(s[5], System.Globalization.CultureInfo.InvariantCulture)));
            Vector3 Native(int joint)
            {
                int at = joint * 12;
                return new(BitConverter.ToSingle(rawJoints, at), BitConverter.ToSingle(rawJoints, at + 4), BitConverter.ToSingle(rawJoints, at + 8));
            }
            var rawErrors = targets.ToDictionary(p => p.Key, p => Vector3.Distance(p.Value, Native(p.Key)));
            double rawMean = rawErrors.Values.Average();
            var editor = new FigureAnimationEditor(source);
            await editor.CreateAnimationAsync("cidle09", "kinematic_test", 40, true);
            await editor.ApplyKimodoGroundedDraftAsync("kinematic_test", KimodoDraft.ReadSoma30(rawDirectory, 40), scene.Meshes, KimodoPlayback.Once);
            var importedSet = editor.Preview();
            var imported = await importedSet.LoadAsync("kinematic_test");
            var gamePose = importedSet.Pose(imported, 0);
            var root = gamePose["hp"].Position;
            float scale = (source.Bones["ll1"].Length() + source.Bones["ll2"].Length() + source.Bones["ll3"].Length()) /
                (.0844f + .4322f + .4216f);
            var toSoma = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
            var mapped = new (string Part, int Joint)[] { ("hp",0),("bd",3),("hd",6),("lh1",11),("lh2",12),
                ("lh3",13),("rh1",17),("rh2",18),("rh3",19),("ll1",22),("ll2",23),("ll3",24),
                ("rl1",26),("rl2",27),("rl3",28) };
            var importedErrors = mapped.ToDictionary(p => p.Joint, p => Vector3.Distance(targets[p.Joint],
                Vector3.Transform(gamePose[p.Part].Position - new Vector3(root.X, root.Y, 0), toSoma) / scale));
            Console.WriteLine($"KINEMATIC METRICS: raw_mean_m={rawMean:F6}, raw_max_m={rawErrors.Values.Max():F6}, imported_mean_m={importedErrors.Values.Average():F6}, imported_max_m={importedErrors.Values.Max():F6}, targets={targets.Count}");
            foreach (var item in mapped)
                Console.WriteLine($"KINEMATIC TARGET: {item.Part} soma={item.Joint} raw_m={rawErrors[item.Joint]:F6} game_m={importedErrors[item.Joint]:F6}");
            var aligned = await editor.AlignKimodoSpatialTargetsAsync("kinematic_test", pose.NativeTargets);
            Console.WriteLine("KINEMATIC ALIGN: " + string.Join("; ", aligned));
            var checkedSet = editor.Preview();
            var checkedPose = checkedSet.Pose(await checkedSet.LoadAsync("kinematic_test"), 0);
            var alignedErrors = mapped.ToDictionary(p => p.Joint, p => Vector3.Distance(targets[p.Joint],
                Vector3.Transform(checkedPose[p.Part].Position - new Vector3(checkedPose["hp"].Position.X,
                    checkedPose["hp"].Position.Y, 0), toSoma) / scale));
            Console.WriteLine($"KINEMATIC ALIGNED: mean_m={alignedErrors.Values.Average():F6}, max_m={alignedErrors.Values.Max():F6}");
            foreach (var item in mapped)
                Console.WriteLine($"KINEMATIC ALIGNED TARGET: {item.Part} soma={item.Joint} game_m={alignedErrors[item.Joint]:F6}");
            var alignedClip = await checkedSet.LoadAsync("kinematic_test");
            var nextPose = checkedSet.Pose(alignedClip, 1);
            foreach (string part in new[] { "hp", "lh3", "rh3", "ll3", "rl3" })
                Console.WriteLine($"KINEMATIC POSE SEAM: {part} frame0_to_1_m={Vector3.Distance(checkedPose[part].Position, nextPose[part].Position) / scale:F6}");
        }
        Reject("""{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":40,"x":0,"z":0}]}""", 40);
        Reject("""{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":1,"z":0}]}""", 40);
        Reject("""{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0}],"unknown":1}""", 40);
        Reject("""{"version":1,"space":"kimodo_y_up_meters","effectors":[{"frame":0,"joint":"sword","position":[0,0,0]}]}""", 40);
        Reject("""{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0},{"frame":0,"x":0,"z":0}]}""", 40);
        Reject("""{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0}],"root2d":[]}""", 40);
        try
        {
            await KinematicConstraintsConfig.Parse(
                """{"version":1,"space":"kimodo_y_up_meters","effectors":[{"frame":0,"joint":"left_hand","position":[0,1,0]}]}""", 40)
                .ResolveAsync(source, 40, source, scene.Meshes);
            throw new Exception("end effector without root accepted");
        }
        catch (InvalidDataException) { }
        foreach (string impossible in new[]
        {
            """{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0},{"frame":1,"x":10,"z":0}]}""",
            """{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0}],"effectors":[{"frame":0,"joint":"right_hand","position":[10,1,0]}]}"""
        })
        {
            try { await KinematicConstraintsConfig.Parse(impossible, 40).ResolveAsync(source, 40, source, scene.Meshes); throw new Exception("unreachable target accepted"); }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("KINEMATIC CONSTRAINTS PASS: strict schema, bounds, axes, root/hand and real-rig pose mapping.");
    }
}
