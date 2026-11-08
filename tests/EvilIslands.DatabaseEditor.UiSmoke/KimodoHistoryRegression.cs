using System.IO;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class KimodoHistoryRegression
{
    private static void Check(bool pass, string message)
    { if (!pass) throw new Exception("KIMODO HISTORY: " + message); }

    internal static int Run(string database, string raw, string output)
    { Verify(database, raw, output).GetAwaiter().GetResult(); return 0; }

    private static async Task Verify(string database, string raw, string output)
    {
        Directory.CreateDirectory(output);
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var source = scene.Animations ?? throw new InvalidDataException("Нет ANM для Human Hero.");
        var parent = new FigureAnimationEditor(source);
        byte[] original = parent.ExportBytes();
        var incompatibleScene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "BatDarkM12"), rows);
        var incompatible = new FigureAnimationEditor(incompatibleScene.Animations!);
        var batClip = incompatible.Preview().Names.First(n => n.Equals("cwalk", StringComparison.OrdinalIgnoreCase));
        try { await incompatible.ValidateKimodoTargetAsync(batClip, (await incompatible.Preview().LoadAsync(batClip)).FrameCount);
            throw new Exception("incompatible skeleton passed Kimodo preflight"); }
        catch (ArgumentException ex) { Check(ex.Message.Contains("гуманоид", StringComparison.OrdinalIgnoreCase), "wrong preflight reason"); }
        int frames = checked((int)(new FileInfo(Path.Combine(raw, KimodoGenerationHistory.RootStream)).Length / 12));
        string weights = Path.Combine(output, "weights"); Directory.CreateDirectory(weights);
        string exe = Path.Combine(weights, "generator.exe"), motion = Path.Combine(weights, "motion.gguf"), text = Path.Combine(weights, "text.gguf");
        File.WriteAllText(exe, "generator fixture"); File.WriteAllText(motion, "motion fixture"); File.WriteAllText(text, "text fixture");
        KimodoGenerationRecord Record(string id) => new()
        {
            Model = scene.Model, Template = "cidle09", Id = id, DisplayName = "Тест", Prompt = "A person walks",
            Frames = frames, Steps = 20, Seed = 1, Playback = KimodoPlayback.StationaryLoop,
            SourceAnmSha256 = KimodoGenerationHistory.Sha(original),
            SkeletonSha256 = FigureAnimationEditor.SkeletonFingerprint(source),
            FootGeometrySha256 = KimodoGenerationHistory.GeometrySha(parent.KimodoFootGeometry(scene.Meshes)),
            GeneratorPath = exe, MotionWeightsPath = motion, TextWeightsPath = text
        };
        async Task<FigureAnimationEditor> Candidate(string id)
        {
            var editor = new FigureAnimationEditor(parent.Preview());
            await editor.CreateAnimationAsync("cidle09", id, frames, true);
            await editor.ValidateKimodoTargetAsync(id, frames);
            return editor;
        }

        var success = KimodoGenerationHistory.Begin(Path.Combine(output, "successful"), Record("history_ok") with
        { UserPrompt = "A person walks", Avoid = "sliding feet", Prompt = "A person walks\nAvoid: sliding feet", EndpointReference = "cidle09" });
        var constrained = KimodoGenerationHistory.Begin(Path.Combine(output, "constrained"), Record("history_constraints") with
        {
            KinematicConstraintsJson = """{"version":1,"space":"kimodo_y_up_meters","root2d":[{"frame":0,"x":0,"z":0}]}""",
            ResolvedSpatialTargets = "root 0 0 0\n", Playback = KimodoPlayback.Once
        });
        Check(KimodoGenerationHistory.Open(constrained.DirectoryPath).Record.ResolvedSpatialTargets == "root 0 0 0\n",
            "constrained targets not retained for replay");
        File.AppendAllText(Path.Combine(constrained.DirectoryPath, "spatial-targets.txt"), "root 1 0 0\n");
        try { KimodoGenerationHistory.Open(constrained.DirectoryPath); throw new Exception("changed targets accepted"); }
        catch (InvalidDataException) { }
        string poseJson = """{"version":1,"space":"kimodo_y_up_meters","key_poses":[{"frame":0,"source_clip":"cidle09","source_frame":0}]}""";
        var poseGoals = await KinematicConstraintsConfig.Parse(poseJson, frames)
            .ResolveAsync(source, frames, source, scene.Meshes);
        var constrainedReplay = KimodoGenerationHistory.Begin(Path.Combine(output, "constrained-replay"), Record("history_pose") with
        { KinematicConstraintsJson = poseJson, ResolvedSpatialTargets = poseGoals.NativeTargets, Playback = KimodoPlayback.Once });
        constrainedReplay.CaptureRaw(raw);
        var poseCandidate = await Candidate("history_pose");
        await constrainedReplay.ImportAsync(poseCandidate, scene.Meshes);
        var poseCandidateSecond = await Candidate("history_pose");
        await KimodoGenerationHistory.Open(constrainedReplay.DirectoryPath).ImportAsync(poseCandidateSecond, scene.Meshes);
        Check(poseCandidate.ExportBytes().SequenceEqual(poseCandidateSecond.ExportBytes()) &&
              constrainedReplay.Record.ClipSha256 is not null,
            "conditioned replay or game-rig alignment changed across imports");
        byte[] importedPoseBytes = poseCandidate.ExportBytes();
        poseCandidate.Undo();
        Check(poseCandidate.ExportBytes().SequenceEqual((await Candidate("history_pose")).ExportBytes()),
            "constrained import did not undo atomically");
        poseCandidate.Redo();
        Check(poseCandidate.ExportBytes().SequenceEqual(importedPoseBytes), "constrained import did not redo exactly");
        var lockedHistory = KimodoGenerationHistory.Begin(Path.Combine(output, "locked-constrained"), Record("history_locked") with
        { KinematicConstraintsJson = poseJson, ResolvedSpatialTargets = poseGoals.NativeTargets, Playback = KimodoPlayback.Once });
        lockedHistory.CaptureRaw(raw);
        var lockedCandidate = await Candidate("history_locked");
        lockedCandidate.LockedParts.Add("hp");
        byte[] beforeLocked = lockedCandidate.ExportBytes();
        try { await lockedHistory.ImportAsync(lockedCandidate, scene.Meshes); throw new Exception("locked root changed"); }
        catch (InvalidOperationException) { }
        Check(lockedCandidate.ExportBytes().SequenceEqual(beforeLocked), "locked constrained import changed ANM");
        var contactHistory = KimodoGenerationHistory.Begin(Path.Combine(output, "contact-constrained"), Record("history_contact") with
        { KinematicConstraintsJson = poseJson, ResolvedSpatialTargets = poseGoals.NativeTargets, Playback = KimodoPlayback.Once });
        contactHistory.CaptureRaw(raw);
        var contactCandidate = await Candidate("history_contact");
        var contactClip = await contactCandidate.Preview().LoadAsync("history_contact");
        contactCandidate.Contacts = [new FootContact("ll3", 0, 0,
            contactCandidate.Preview().Pose(contactClip, 0)["ll3"].Position)];
        byte[] beforeContact = contactCandidate.ExportBytes();
        try { await contactHistory.ImportAsync(contactCandidate, scene.Meshes); throw new Exception("contact changed"); }
        catch (ArgumentException) { }
        Check(contactCandidate.ExportBytes().SequenceEqual(beforeContact), "contact constrained import changed ANM");
        Check(success.Record.Status == "prepared" && success.Record.GeneratorSha256.Length == 64 &&
              success.Record.MotionWeightsSha256.Length == 64 && success.Record.TextWeightsSha256.Length == 64,
            "generator/weights identity");
        success.CaptureRaw(raw);
        Check(success.Record.Status == "generated" && File.Exists(Path.Combine(success.DirectoryPath, KimodoGenerationHistory.RootStream)),
            "raw captured before import");
        var replay = KimodoGenerationHistory.Open(success.DirectoryPath);
        replay.VerifySource(scene.Model, parent);
        var candidate = await Candidate("history_ok");
        var issues = await replay.ImportAsync(candidate, scene.Meshes);
        Check(replay.Record.Status == "imported" && replay.Record.Issues.Length == issues.Count &&
              replay.Record.ClipSha256 is not null && candidate.Preview().Names.Contains("history_ok") &&
              parent.ExportBytes().SequenceEqual(original), "replay import without inference");
        var second = await Candidate("history_ok");
        await KimodoGenerationHistory.Open(success.DirectoryPath).ImportAsync(second, scene.Meshes);
        Check(second.ExportBytes().SequenceEqual(candidate.ExportBytes()), "repeated import not deterministic");
        var generatedClip = await candidate.Preview().LoadAsync("history_ok");
        var idle = await source.LoadAsync("cidle09");
        Check(success.Record.EndpointReference == "cidle09" && success.Record.Avoid == "sliding feet" &&
              Math.Abs(System.Numerics.Quaternion.Dot(generatedClip.Tracks["hp"].Rotation(0), idle.Tracks["hp"].Rotation(0))) > .99999f &&
              System.Numerics.Vector3.Distance(generatedClip.Tracks["hp"].Position(generatedClip.FrameCount - 1),
                  idle.Tracks["hp"].Position(idle.FrameCount - 1)) < .00001f,
            "reference endpoints or avoidance prompt were not retained in replay");
        var hp = generatedClip.Tracks["hp"];
        var childEditor = new FigureAnimationEditor(candidate.Preview());
        await childEditor.SetPoseAsync("history_ok", "hp", 0, hp.Rotation(0), hp.Position(0) + new System.Numerics.Vector3(0, 0, .01f));
        var project = childEditor.SaveProject(scene.Model, "history_ok", new AnimationAuthoringSettings
        { Frame = 0, First = 0, Last = 1, Part = "hp", Fps = 15, Loop = false, Ghost = true });
        var reopenedProject = new FigureAnimationEditor(source);
        var restoredSettings = await reopenedProject.LoadProjectAsync(project, scene.Model, "history_ok");
        Check(restoredSettings.Fps == 15 && !restoredSettings.Loop && restoredSettings.Ghost &&
              reopenedProject.CanUndo && reopenedProject.Preview().Names.Contains("history_ok") &&
              reopenedProject.ExportBytes().SequenceEqual(childEditor.ExportBytes()),
            "Kimodo clip project-only restore");
        reopenedProject.Undo(); reopenedProject.Redo();
        Check(reopenedProject.ExportBytes().SequenceEqual(childEditor.ExportBytes()), "Kimodo project undo/redo");

        var badRaw = Path.Combine(output, "bad-raw"); Directory.CreateDirectory(badRaw);
        File.WriteAllBytes(Path.Combine(badRaw, KimodoGenerationHistory.RootStream), [1, 2, 3]);
        File.Copy(Path.Combine(raw, KimodoGenerationHistory.RotationStream),
            Path.Combine(badRaw, KimodoGenerationHistory.RotationStream), true);
        var failed = KimodoGenerationHistory.Begin(Path.Combine(output, "conversion-failed"), Record("history_bad"));
        failed.CaptureRaw(badRaw);
        var failedEditor = await Candidate("history_bad");
        byte[] failedBefore = failedEditor.ExportBytes();
        try { await failed.ImportAsync(failedEditor, scene.Meshes); throw new Exception("invalid raw accepted"); }
        catch (InvalidDataException) { }
        Check(failed.Record.Status == "import-failed" && !string.IsNullOrWhiteSpace(failed.Record.Error) &&
              File.Exists(Path.Combine(failed.DirectoryPath, KimodoGenerationHistory.RootStream)) &&
              failedEditor.ExportBytes().SequenceEqual(failedBefore) && parent.ExportBytes().SequenceEqual(original),
            "conversion error loses raw or mutates animation");

        var transfer = KimodoGenerationHistory.Begin(Path.Combine(output, "transfer-failed"), Record("history_pin"));
        transfer.CaptureRaw(raw);
        var transferEditor = await Candidate("history_pin");
        byte[] transferBefore = transferEditor.ExportBytes();
        var transferClip = await transferEditor.Preview().LoadAsync("history_pin");
        transferEditor.MotionPins = new Dictionary<string, System.Numerics.Vector3>
        { ["ll3"] = transferEditor.Preview().Pose(transferClip, 0)["ll3"].Position };
        try { await transfer.ImportAsync(transferEditor, scene.Meshes); throw new Exception("pinned transfer accepted"); }
        catch (InvalidOperationException) { }
        Check(transfer.Record.Status == "import-failed" && transferEditor.ExportBytes().SequenceEqual(transferBefore) &&
              File.Exists(Path.Combine(transfer.DirectoryPath, KimodoGenerationHistory.RotationStream)) &&
              parent.ExportBytes().SequenceEqual(original), "transfer error lost raw or partially applied");

        var cancelled = KimodoGenerationHistory.Begin(Path.Combine(output, "cancelled"), Record("history_cancel"));
        cancelled.Mark("cancelled", "Отменено пользователем.");
        Check(KimodoGenerationHistory.Open(cancelled.DirectoryPath).Record.Status == "cancelled" &&
              parent.ExportBytes().SequenceEqual(original), "cancel status/source");

        var historyError = KimodoGenerationHistory.Begin(Path.Combine(output, "history-write-failed"), Record("history_write"));
        historyError.CaptureRaw(raw);
        Directory.CreateDirectory(Path.Combine(historyError.DirectoryPath, "generation.json.tmp"));
        var writeCandidate = await Candidate("history_write");
        try { await historyError.ImportAsync(writeCandidate, scene.Meshes); throw new Exception("history write failure accepted"); }
        catch (IOException ex) { Check(ex.Message.Contains(historyError.DirectoryPath), "history failure path absent"); }
        Check(File.Exists(Path.Combine(historyError.DirectoryPath, KimodoGenerationHistory.RootStream)) &&
              File.Exists(Path.Combine(historyError.DirectoryPath, "import-failure.txt")) &&
              parent.ExportBytes().SequenceEqual(original), "history write failure lost raw or source");
        Directory.Delete(Path.Combine(historyError.DirectoryPath, "generation.json.tmp"));
        var afterWriteFailure = await Candidate("history_write");
        await KimodoGenerationHistory.Open(historyError.DirectoryPath).ImportAsync(afterWriteFailure, scene.Meshes);
        Check(afterWriteFailure.Preview().Names.Contains("history_write") && parent.ExportBytes().SequenceEqual(original),
            "history write failure could not be replayed");
        Console.WriteLine("KIMODO HISTORY PASS: raw-before-import, replay, hashes, import failure, cancel, history write failure");
    }
}
