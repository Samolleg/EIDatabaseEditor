using System.IO;
using System.Numerics;
using System.Text;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationAuthoringRegression
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Authoring: " + message); }
    private static bool Same(Quaternion a, Quaternion b) => Math.Abs(Quaternion.Dot(a, b)) > .99999f;
    internal static async Task Verify(UnitModelScene scene, string name)
    {
        var source = scene.Animations!; var clip = await source.LoadAsync(name); int count = clip.FrameCount;
        var editor = new FigureAnimationEditor(source); byte[] original = editor.ExportBytes();
        var block = await editor.CopyFramesAsync(name, 1, 2);
        Check(block.Count == 2 && original.SequenceEqual(editor.ExportBytes()) && !editor.CanUndo, "copy mutated source");
        await editor.PasteFramesAsync(name, 3, block);
        var pasted = await editor.Preview().LoadAsync(name);
        Check(pasted.FrameCount == count + 2, "paste length");
        foreach (var pair in clip.Tracks)
        {
            var target = pasted.Tracks[pair.Key];
            for (int f = 0; f < count + 2; f++)
            {
                int old = f < 3 ? f : f < 5 ? f - 2 : f - 2;
                Check(Same(target.Rotation(f), pair.Value.Rotation(old)), "paste rotation mapping");
                Check(Vector3.Distance(target.Position(f), pair.Value.Position(old)) < .00001f, "paste position mapping");
                if (pair.Value.Morphs.Length > 0) Check(Vector3.Distance(target.Morph(f, 0), pair.Value.Morph(old, 0)) < .00001f, "paste morph mapping");
            }
        }
        await editor.DeleteFramesAsync(name, 3, 4);
        var restored = await editor.Preview().LoadAsync(name);
        foreach (var pair in clip.Tracks) for (int f = 0; f < count; f++) Check(Same(restored.Tracks[pair.Key].Rotation(f), pair.Value.Rotation(f)), "delete mapping");
        editor.Undo(); editor.Undo(); Check(original.SequenceEqual(editor.ExportBytes()), "frame operations byte undo");
        await editor.DuplicateFramesAsync(name, 1, 2); Check((await editor.Preview().LoadAsync(name)).FrameCount == count + 2, "duplicate");
        editor.Undo();
        try { await editor.DeleteFramesAsync(name, 0, count - 1); throw new Exception("delete-all accepted"); } catch (ArgumentException) { }
        Check(original.SequenceEqual(editor.ExportBytes()), "delete-all mutation");
        string part = clip.Tracks.ContainsKey("hd") ? "hd" : clip.Tracks.Keys.First(); int frame = Math.Min(3, count - 2);
        var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .25f);
        var desired = Quaternion.Normalize(delta * clip.Tracks[part].Rotation(frame));
        await editor.ApplyAuthoringAsync(name, frame, new Dictionary<string, Quaternion> { [part] = desired }, new Dictionary<string, Vector3>(), 1, false, new Dictionary<string, Vector3>());
        var smooth = await editor.Preview().LoadAsync(name);
        Check(Same(smooth.Tracks[part].Rotation(frame), desired), "smooth center");
        for (int f = 0; f < count; f++)
        {
            if (Math.Abs(f - frame) > 1) Check(Same(smooth.Tracks[part].Rotation(f), clip.Tracks[part].Rotation(f)), "smooth touched outside radius");
            else if (f != frame) Check(Same(smooth.Tracks[part].Rotation(f), Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, delta, .5f) * clip.Tracks[part].Rotation(f))), "smooth falloff");
        }
        var settings = new AnimationAuthoringSettings { Frame = frame, First = 1, Last = 2, Part = part, SmoothRadius = 1, Ghost = true, Ground = false, Limits = false, Fps = 15, Loop = false };
        byte[] project = editor.SaveProject(scene.Model, name, settings); var reopened = new FigureAnimationEditor(source);
        var loadedSettings = await reopened.LoadProjectAsync(project, scene.Model, name);
        Check(loadedSettings.Frame == frame && loadedSettings.Ghost && loadedSettings.Ground == false && loadedSettings.Fps == 15 && !loadedSettings.Loop && loadedSettings.SmoothRadius == 1 && reopened.ExportBytes().SequenceEqual(editor.ExportBytes()), "project roundtrip");
        reopened.Undo(); Check(reopened.ExportBytes().SequenceEqual(original), "project undo history"); reopened.Redo();
        var ownSaved = new FigureAnimationEditor(source.WithSavedBytes(editor.ExportBytes()));
        var ownSettings = await ownSaved.LoadProjectAsync(project, scene.Model, name);
        Check(ownSaved.ProjectMayWriteSource && ownSettings.Fps == 15 && ownSaved.CanUndo, "own saved ANM project recovery");
        ownSaved.Undo(); ownSaved.Redo();
        Check(ownSaved.ExportBytes().SequenceEqual(editor.ExportBytes()), "own saved ANM history recovery");
        var projectAfterAnmSave = ownSaved.SaveProject(scene.Model, name, settings);
        var reopenedAfterAnmSave = new FigureAnimationEditor(source.WithSavedBytes(editor.ExportBytes()));
        await reopenedAfterAnmSave.LoadProjectAsync(projectAfterAnmSave, scene.Model, name);
        Check(reopenedAfterAnmSave.ProjectMayWriteSource && reopenedAfterAnmSave.ExportBytes().SequenceEqual(editor.ExportBytes()),
            "project saved after ANM save cannot reopen");
        var external = new FigureAnimationEditor(source);
        string projectPart = clip.Tracks.ContainsKey("hp") ? "hp" : clip.Tracks.Keys.First();
        await external.SetPoseAsync(name, projectPart, 0,
            Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .07f) * clip.Tracks[projectPart].Rotation(0)));
        var externalOpen = new FigureAnimationEditor(source.WithSavedBytes(external.ExportBytes()));
        await externalOpen.LoadProjectAsync(project, scene.Model, name);
        Check(!externalOpen.ProjectMayWriteSource && externalOpen.ExportBytes().SequenceEqual(editor.ExportBytes()), "external ANM may be recovered but not overwritten");
        var resavedRecovery = externalOpen.SaveProject(scene.Model, name, settings);
        var stillBlocked = new FigureAnimationEditor(source.WithSavedBytes(external.ExportBytes()));
        await stillBlocked.LoadProjectAsync(resavedRecovery, scene.Model, name);
        Check(!stillBlocked.ProjectMayWriteSource, "resaving project must not authorize conflicting ANM");
        var newEditor = new FigureAnimationEditor(source);
        await newEditor.CreateAnimationAsync(name, "project_draft", 12, true);
        var newChild = new FigureAnimationEditor(newEditor.Preview());
        var draftTrack = (await newChild.Preview().LoadAsync("project_draft")).Tracks[projectPart];
        await newChild.SetPoseAsync("project_draft", projectPart, 0,
            Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .02f) * draftTrack.Rotation(0)));
        var draftSettings = settings with { Frame = 0, First = 0, Last = 1, Part = projectPart };
        var draftProject = newChild.SaveProject(scene.Model, "project_draft", draftSettings);
        var draftOpen = new FigureAnimationEditor(source);
        await draftOpen.LoadProjectAsync(draftProject, scene.Model, "project_draft");
        Check(draftOpen.ProjectMayWriteSource && draftOpen.CanUndo && draftOpen.Preview().Names.Contains("project_draft") &&
              draftOpen.ExportBytes().SequenceEqual(newChild.ExportBytes()), "project-only generated/new clip recovery");
        draftOpen.Undo(); draftOpen.Redo();
        Check(draftOpen.ExportBytes().SequenceEqual(newChild.ExportBytes()), "new clip history recovery");
        if (scene.Model.Equals("unhufe", StringComparison.OrdinalIgnoreCase))
        {
            var projectDialog = new AnimationEditorDialog(scene, name);
            await projectDialog.Refresh();
            await projectDialog.RestoreProjectAsync(draftProject);
            Check(projectDialog.Title.Contains("project_draft", StringComparison.Ordinal), "UI project-only clip selection");
            await projectDialog.Refresh();
            var savedDialog = new AnimationEditorDialog(scene with { Animations = source.WithSavedBytes(editor.ExportBytes()) }, name);
            await savedDialog.Refresh();
            await savedDialog.RestoreProjectAsync(project);
            Check(savedDialog.Title.Contains(name, StringComparison.Ordinal), "UI reopen after own ANM save");
            Console.WriteLine("PROJECT UI PASS: new clip and own saved ANM reopen without picker");
        }
        var foreignBones = source.Bones.ToDictionary(p => p.Key, p => p.Value);
        if (foreignBones.Count > 0) foreignBones[foreignBones.Keys.First()] += Vector3.One;
        else foreignBones["audit_foreign"] = Vector3.One;
        var foreignRig = new FigureAnimationSet("foreign", source.ExportBytes(), source.Parents,
            foreignBones, source.BindPositions);
        try { await new FigureAnimationEditor(foreignRig).LoadProjectAsync(project, scene.Model, name); throw new Exception("foreign rig accepted"); }
        catch (InvalidDataException) { }
        var legacy = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            System.Text.Json.JsonSerializer.Deserialize<AnimationProjectDocument>(project, new System.Text.Json.JsonSerializerOptions { IncludeFields = true })! with
            { Version = 1, SourceAnimation = null, SkeletonHash = null, WriteBaselineHash = null },
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        await new FigureAnimationEditor(source).LoadProjectAsync(legacy, scene.Model, name);
        try { await new FigureAnimationEditor(source.WithSavedBytes(editor.ExportBytes())).LoadProjectAsync(legacy, scene.Model, name); throw new Exception("legacy mismatch accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("не хватает снимка"), "legacy recovery explanation"); }
        byte[] stable = reopened.ExportBytes();
        try { await reopened.LoadProjectAsync(project, "wrong-model", name); throw new Exception("wrong project accepted"); } catch (System.IO.InvalidDataException) { }
        Check(stable.SequenceEqual(reopened.ExportBytes()), "bad project partially applied");
        var invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(project).Replace("\"SmoothRadius\":1", "\"SmoothRadius\":999"));
        try { await reopened.LoadProjectAsync(invalid, scene.Model, name); throw new Exception("invalid settings accepted"); } catch (System.IO.InvalidDataException) { }
        Check(stable.SequenceEqual(reopened.ExportBytes()), "invalid settings mutated editor");
        editor.Undo();
        await editor.MirrorPoseAsync(name, frame, false, new Dictionary<string, Vector3>());
        var mirrored = await editor.Preview().LoadAsync(name);
        if (clip.Tracks.ContainsKey("lh2") && clip.Tracks.ContainsKey("rh2"))
        {
            var right = clip.Tracks["rh2"].Rotation(frame);
            Check(Same(mirrored.Tracks["lh2"].Rotation(frame), new Quaternion(right.X, -right.Y, -right.Z, right.W)), "mirror counterpart");
        }
        await editor.MirrorPoseAsync(name, frame, false, new Dictionary<string, Vector3>());
        var twice = await editor.Preview().LoadAsync(name);
        foreach (var pair in clip.Tracks) Check(Same(pair.Value.Rotation(frame), twice.Tracks[pair.Key].Rotation(frame)), "mirror twice");
        editor.Undo(); editor.Undo();
        var feet = new Dictionary<string, Vector3>(); var pose = source.Pose(clip, frame);
        foreach (var end in new[] { "ll3", "rl3" }.Where(e => FigureLimbIk.Supports(source, clip, e))) feet[end] = pose[end].Position;
        string? root = source.Parents.FirstOrDefault(p => p.Value == "" && clip.Tracks.ContainsKey(p.Key)).Key;
        if (feet.Count > 0 && root is not null)
        {
            Vector3 old = pose[root].Position;
            var target = old + (feet.Values.Aggregate(Vector3.Zero, (a, b) => a + b) / feet.Count - old) * .001f;
            await editor.ApplyAuthoringAsync(name, frame, new Dictionary<string, Quaternion>(), new Dictionary<string, Vector3> { [root] = target }, 0, false, feet);
            var pinned = await editor.Preview().LoadAsync(name); var pinnedPose = editor.Preview().Pose(pinned, frame);
            foreach (var foot in feet) Check(Vector3.Distance(pinnedPose[foot.Key].Position, foot.Value) < .0001f, "foot slid");
            byte[] pinnedBytes = editor.ExportBytes();
            try { await editor.ApplyAuthoringAsync(name, frame, new Dictionary<string, Quaternion>(), new Dictionary<string, Vector3> { [root] = old + new Vector3(100, 100, 100) }, 0, false, feet); throw new Exception("unreachable pin accepted"); } catch (ArgumentException) { }
            Check(pinnedBytes.SequenceEqual(editor.ExportBytes()), "unreachable pin partially applied");
            var pinSettings = settings with { Pins = feet };
            var pinProject = editor.SaveProject(scene.Model, name, pinSettings);
            var pinReload = await reopened.LoadProjectAsync(pinProject, scene.Model, name);
            Check(pinReload.Pins.SequenceEqual(feet), "project pin positions");
            editor.Undo(); Check(original.SequenceEqual(editor.ExportBytes()), "pin atomic undo");
            Console.WriteLine("AUTHORING FOOT PIN PASS " + scene.Model);
        }
        bool testedLimit = false;
        foreach (string end in new[] { "lh3", "rh3", "ll3", "rl3" }.Where(e => FigureLimbIk.Supports(source, clip, e)))
        {
            var a = pose[end[..2] + "1"].Position; var b = pose[end[..2] + "2"].Position; var c = pose[end].Position;
            var axis = Vector3.Normalize(c - a); var perpendicular = b - a - axis * Vector3.Dot(b - a, axis);
            if (perpendicular.LengthSquared() < 1e-7f) continue;
            var reversed = FigureLimbIk.Solve(source, clip, frame, end, c, a - perpendicular);
            try { await editor.ApplyAuthoringAsync(name, frame, reversed, new Dictionary<string, Vector3>(), 0, true, new Dictionary<string, Vector3>()); }
            catch (ArgumentException) { testedLimit = true; Check(original.SequenceEqual(editor.ExportBytes()), "constraint failure atomicity"); break; }
            editor.Undo();
        }
        if (feet.Count > 0) Check(testedLimit, "no reverse limb constraint exercised");
        var view = new UnitModelPreviewView { AllowAnimationEditing = false }; view.ShowScene(scene); await view.SelectAnimationAsync(name);
        if (feet.Count > 0 && root is not null)
        {
            view.SetAnimationFrame(frame);
            view.SetSkeleton(EvilIslands.DatabaseEditor.App.Dialogs.AnimationEditorDialog.EditableParts(scene, clip), root);
            view.Measure(new System.Windows.Size(900, 700)); view.Arrange(new System.Windows.Rect(0, 0, 900, 700)); view.UpdateLayout();
            view.PoseFilter = (f, qs, ps) => FigureAnimationEditor.ConstrainPreview(source, clip, f, qs, ps, false, feet);
            Check(view.BeginJointGesture(root, 2, true, new System.Windows.Point(0, 0)), "pinned root gesture unavailable");
            view.MoveJointGesture(new System.Windows.Point(0, 1));
            var live = view.CurrentJointPose;
            foreach (var foot in feet) Check(Vector3.Distance(live[foot.Key].Position, foot.Value) < .0001f, "live foot pin slid");
            Check(Vector3.Distance(live[root].Position, pose[root].Position) > .000001f, "live pinned root did not move");
            view.EndJointDrag(); Check(Vector3.Distance(view.CurrentJointPose[root].Position, pose[root].Position) < .000001f, "live pin cancel failed");
        }
        view.SetComparison(source, clip, false, true); Check(view.GhostMeshCount == scene.Meshes.Count, "ghost mesh count");
        view.SetComparison(source, clip, true, true); Check(view.ShowingOriginal && view.GhostMeshCount == 0, "original comparison mode");
        view.SetComparison(source, clip, false, false); Check(!view.ShowingOriginal && view.GhostMeshCount == 0, "comparison reset");
        Console.WriteLine("AUTHORING SEVEN PASS " + scene.Model + ": range/paste/delete/duplicate, smoothing, mirror, constraints=" + testedLimit + ", pins=" + feet.Count + ", comparison, project+history+invalid rollback");
    }
}
