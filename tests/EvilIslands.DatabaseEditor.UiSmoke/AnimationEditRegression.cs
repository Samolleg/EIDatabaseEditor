using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationEditRegression
{
    static void Check(bool yes, string message) { if (!yes) throw new Exception(message); }
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
    }
    static async Task Verify(string database, string output)
    {
        // Unequal channels, short zero-morph footer, and interpolated morph vertices.
        using (var bytes = new MemoryStream())
        {
            using var w = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true);
            w.Write(2); foreach (int n in new[] { 0, 1 }) { w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); }
            w.Write(0); w.Write(2); w.Write(1);
            foreach (float f in new[] { 0f, 0f, 0f, 2f, 4f, 6f }) w.Write(f);
            var decoded = FigureAnimationTrack.Decode(FigureAnimationEditor.TransformTrack(bytes.ToArray(), 0, 1, 3));
            Check(decoded.Morph(1, 0) == new Vector3(1, 2, 3) && decoded.Positions.Length == 0, "Morph interpolation/empty channel");
            var shortFooter = bytes.ToArray()[..40].Concat(BitConverter.GetBytes(0)).ToArray();
            Check(FigureAnimationTrack.Decode(FigureAnimationEditor.TransformTrack(shortFooter, 0, 1, 3)).FrameCount == 3, "Short zero morph footer");
        }
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        foreach (var name in new[] { "Human Hero", "OrcMaleF5", "GZOrcMaleF6", "BatDarkM12" })
        {
            var row = records.FirstOrDefault(r => r.Table == "units.prototypes" && r.Name == name); if (row is null) continue;
            var scene = await UnitModelPreview.LoadAsync(database, row, records);
            var source = scene.Animations ?? throw new Exception("ANM missing");
            string clipName = source.Names.FirstOrDefault(n => n.Contains("walk", StringComparison.OrdinalIgnoreCase)) ?? source.Names.First();
            var clip = await source.LoadAsync(clipName); Check(clip.FrameCount >= 4, "Clip too short");
            await AnimationAuthoringRegression.Verify(scene, clipName);
            await AnimationMotionRegression.Verify(scene, clipName);
            var parts = AnimationEditorDialog.EditableParts(scene, clip);
            foreach (string end in new[] { "lh3", "rh3", "ll3", "rl3" }.Where(e => FigureLimbIk.Supports(source, clip, e)))
            {
                var bindPose = source.Pose(clip, 0);
                string upper = end[..2] + "1", middle = end[..2] + "2";
                var a0 = bindPose[upper].Position; var b0 = bindPose[middle].Position; var c0 = bindPose[end].Position;
                float length1 = Vector3.Distance(a0, b0), length2 = Vector3.Distance(b0, c0);
                var target = a0 + Vector3.Normalize(c0 - a0) * ((length1 + length2) * .8f);
                var solution = FigureLimbIk.Solve(source, clip, 0, end, target);
                var solved = source.Pose(clip, 0, rotations: solution);
                Check(Vector3.Distance(solved[end].Position, target) < .001f, "IK target not reached: " + end);
                Check(Math.Abs(Vector3.Distance(solved[upper].Position, solved[middle].Position) - length1) < .00001f && Math.Abs(Vector3.Distance(solved[middle].Position, solved[end].Position) - length2) < .00001f, "IK changed bone lengths");
                Check(Math.Abs(Quaternion.Dot(solved[end].Rotation, bindPose[end].Rotation)) > .9999f, "IK end orientation changed");
                var distant = FigureLimbIk.Solve(source, clip, 0, end, a0 + Vector3.UnitX * 1000);
                float reached = Vector3.Distance(source.Pose(clip, 0, rotations: distant)[end].Position, a0);
                Check(reached <= length1 + length2 + .0001f && reached > (length1 + length2) * .99f, "IK reach clamp failed");
                var batch = new FigureAnimationEditor(source); var beforeBatch = batch.ExportBytes();
                await batch.SetRotationsAsync(clipName, 0, solution);
                var persisted = await batch.Preview().LoadAsync(clipName);
                var bendSource = batch.Preview();
                var bendPose = bendSource.Pose(persisted, 0);
                var direction = Vector3.Normalize(bendPose[end].Position - bendPose[upper].Position);
                var offset = bendPose[middle].Position - bendPose[upper].Position;
                var poleDirection = Vector3.Normalize(Vector3.Cross(direction, offset));
                var bend = FigureLimbIk.Solve(bendSource, persisted, 0, end, bendPose[end].Position, bendPose[upper].Position + poleDirection);
                var bent = bendSource.Pose(persisted, 0, rotations: bend);
                Check(Vector3.Distance(bent[end].Position, bendPose[end].Position) < .001f, "Pole moved fixed endpoint");
                var bendOffset = bent[middle].Position - bent[upper].Position;
                var perpendicular = Vector3.Normalize(bendOffset - direction * Vector3.Dot(bendOffset, direction));
                Check(Vector3.Dot(perpendicular, poleDirection) > .999f, "Pole direction not followed");
                Check(Vector3.Distance(bent[middle].Position, bendPose[middle].Position) > .0001f, "Pole did not move elbow");
                Check(Math.Abs(Vector3.Distance(bent[upper].Position, bent[middle].Position) - length1) < .00001f && Math.Abs(Vector3.Distance(bent[middle].Position, bent[end].Position) - length2) < .00001f, "Pole changed lengths");
                Console.WriteLine("POLE PASS " + name + "/" + end);
                Check(Vector3.Distance(batch.Preview().Pose(persisted, 0)[end].Position, target) < .001f, "IK save differs from preview");
                foreach (var pair in clip.Tracks)
                    for (int f = 1; f < clip.FrameCount; f++) Check(Math.Abs(Quaternion.Dot(pair.Value.Rotation(f), persisted.Tracks[pair.Key].Rotation(f))) > .9999f, "IK changed neighboring frame");
                byte[] afterBatch = batch.ExportBytes(); batch.Undo(); Check(beforeBatch.SequenceEqual(batch.ExportBytes()) && !batch.CanUndo, "IK batch undo not atomic");
                batch.Redo(); Check(afterBatch.SequenceEqual(batch.ExportBytes()), "IK batch redo mismatch");
                try { await batch.SetRotationsAsync(clipName, 0, new Dictionary<string, Quaternion> { [upper] = Quaternion.Identity, ["invalid-part"] = Quaternion.Identity }); throw new Exception("Invalid IK batch accepted"); } catch (ArgumentOutOfRangeException) { }
                Check(afterBatch.SequenceEqual(batch.ExportBytes()), "Invalid IK batch partially applied");
                Console.WriteLine("IK PASS " + name + "/" + end + ": reach, lengths, orientation, neighboring frames, atomic undo/redo");
            }
            Check(parts.Length > 0, "No editable displayed parts");
            foreach (var mesh in scene.Meshes)
                if (clip.Tracks.ContainsKey(mesh.Name)) Check(parts.Contains(mesh.Name, StringComparer.OrdinalIgnoreCase), "Visible part missing");
            var injected = new Dictionary<string, FigureAnimationTrack>(clip.Tracks) { ["unrelated-test-part"] = clip.Tracks.First().Value };
            Check(!AnimationEditorDialog.EditableParts(scene, new FigureAnimationClip(clip.Name, injected)).Contains("unrelated-test-part"), "Unrelated track offered");
            if (name == "Human Hero")
            {
                Check(!parts.Contains("arrows") && !parts.Any(p => p.StartsWith("crbow1", StringComparison.OrdinalIgnoreCase)), "Unused human equipment offered");
                if (clip.Tracks.ContainsKey("arrows")) Check(parts.Length < clip.Tracks.Count, "Human list not filtered");
            }
            File.WriteAllLines(Path.Combine(output, scene.Model + "-parts.txt"), parts);
            var gestureView = new EvilIslands.DatabaseEditor.App.UnitModelPreviewView { AllowAnimationEditing = false };
            gestureView.ShowScene(scene); await gestureView.SelectAnimationAsync(clipName);
            gestureView.SetSkeleton(parts, parts[0]);
            gestureView.Measure(new Size(900, 700)); gestureView.Arrange(new Rect(0, 0, 900, 700)); gestureView.UpdateLayout();
            string gesturePart = parts.First(p => source.Parents[p].Length > 0);
            var originalPose = gestureView.CurrentJointPose;
            int commits = 0; Quaternion committedRotation = default;
            gestureView.JointEdited += (_, _, q, _) => { commits++; committedRotation = q; };
            var ikEnd = parts.FirstOrDefault(p => FigureLimbIk.Supports(source, clip, p));
            if (ikEnd is not null)
            {
                int ikCommits = 0;
                gestureView.LimbEdited += (_, changes) => { Check(changes.Count == 3, "IK gesture must edit three rotations"); ikCommits++; };
                Check(gestureView.BeginLimbGesture(ikEnd, new Point(100, 100)), "IK gesture unavailable");
                gestureView.MoveJointGesture(new Point(120, 110));
                Check(Vector3.Distance(gestureView.CurrentJointPose[ikEnd].Position, originalPose[ikEnd].Position) > .00001f, "IK live gesture did not move endpoint");
                gestureView.EndJointDrag(); Check(ikCommits == 0 && gestureView.CurrentJointPose[ikEnd] == originalPose[ikEnd], "IK cancel failed");
                gestureView.BeginLimbGesture(ikEnd, new Point(100, 100)); gestureView.MoveJointGesture(new Point(120, 110)); gestureView.CommitJointGesture();
                Check(ikCommits == 1, "IK gesture commit count");
                Check(gestureView.BeginBendGesture(ikEnd, new Point(100, 100)), "Bend gesture unavailable");
                gestureView.MoveJointGesture(new Point(130, 80));
                Check(Vector3.Distance(gestureView.CurrentJointPose[ikEnd].Position, originalPose[ikEnd].Position) < .001f, "Bend gesture moved endpoint");
                gestureView.EndJointDrag(); Check(ikCommits == 1 && gestureView.CurrentJointPose[ikEnd] == originalPose[ikEnd], "Bend cancel failed");
                gestureView.BeginBendGesture(ikEnd, new Point(100, 100)); gestureView.MoveJointGesture(new Point(130, 80)); gestureView.CommitJointGesture();
                Check(ikCommits == 2, "Bend commit count");
            }
            Check(!gestureView.BeginJointGesture(gesturePart, 0, true, new Point(0, 0)), "Child translation gesture accepted");
            Check(gestureView.BeginJointGesture(gesturePart, 0, false, new Point(0, 0)), "Rotation gesture unavailable");
            gestureView.MoveJointGesture(new Point(30, 0));
            var expectedRotation = Quaternion.Normalize(clip.Tracks[gesturePart].Rotation(0) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6));
            var expectedPose = source.Pose(clip, 0, gesturePart, expectedRotation);
            foreach (var key in expectedPose.Keys)
            {
                Check(Vector3.Distance(expectedPose[key].Position, gestureView.CurrentJointPose[key].Position) < .00001f, "Live pose position mismatch");
                Check(Math.Abs(Quaternion.Dot(expectedPose[key].Rotation, gestureView.CurrentJointPose[key].Rotation)) > .9999f, "Live rotation mismatch");
            }
            gestureView.EndJointDrag(); Check(commits == 0 && gestureView.CurrentJointPose[gesturePart] == originalPose[gesturePart], "Cancel did not restore pose");
            gestureView.BeginJointGesture(gesturePart, 0, false, new Point(0, 0)); gestureView.MoveJointGesture(new Point(30, 0)); gestureView.CommitJointGesture();
            Check(commits == 1 && Math.Abs(Quaternion.Dot(expectedRotation, committedRotation)) > .9999f, "Gesture commit mismatch");
            string? gestureRoot = parts.FirstOrDefault(p => source.Parents[p].Length == 0);
            if (gestureRoot is not null)
            {
                Check(gestureView.BeginJointGesture(gestureRoot, 0, true, new Point(0, 0)), "Root gesture unavailable");
                gestureView.MoveJointGesture(new Point(30, 0));
                var shift = gestureView.CurrentJointPose[gestureRoot].Position - originalPose[gestureRoot].Position;
                Check(Math.Abs(shift.X) > .00001f && Math.Abs(shift.Y) < .00001f && Math.Abs(shift.Z) < .00001f, "Root axis movement mismatch");
                gestureView.EndJointDrag(); Check(commits == 1 && gestureView.CurrentJointPose[gestureRoot] == originalPose[gestureRoot], "Root cancel mismatch");
            }
            Console.WriteLine("GESTURE PASS: live hierarchy, child guard, cancel, one commit");
            Console.WriteLine($"PARTS {name}: {parts.Length}/{clip.Tracks.Count}: {string.Join(", ", parts)}");
            var editor = new FigureAnimationEditor(source); byte[] raw = editor.ExportBytes();
            await editor.TransformAsync(clipName, 0, clip.FrameCount - 1, clip.FrameCount);
            Check(raw.SequenceEqual(editor.ExportBytes()) && !editor.CanUndo, "No-op changed raw bytes");
            int last = clip.FrameCount - 2;
            await editor.TransformAsync(clipName, 1, last, 7);
            var changed = editor.ExportBytes(); var read = editor.Preview(); var result = await read.LoadAsync(clipName);
            Check(result.FrameCount == 7, "Resample count");
            foreach (var pair in clip.Tracks)
            {
                var next = result.Tracks[pair.Key];
                Check(Vector3.Distance(pair.Value.Position(1), next.Position(0)) < 0.00001f && Vector3.Distance(pair.Value.Position(last), next.Position(6)) < 0.00001f, "Position endpoints");
                Check(Math.Abs(Quaternion.Dot(pair.Value.Rotation(1), next.Rotation(0))) > 0.9999f, "Rotation endpoint");
                if (pair.Value.Morphs.Length > 0 && pair.Value.Morphs[0].Length > 0)
                    Check(Vector3.Distance(pair.Value.Morph(last, 0), next.Morph(6, 0)) < 0.00001f, "Morph endpoint");
            }
            var a = ResArchiveDocument.Parse("a", raw); var b = ResArchiveDocument.Parse("b", changed);
            foreach (var entry in a.Entries.Where(e => !e.Name.Equals(clipName, StringComparison.OrdinalIgnoreCase) && !e.Name.Replace('/', '\\').StartsWith(clipName + "\\", StringComparison.OrdinalIgnoreCase)))
                Check(a.GetResourceBytes(entry).Span.SequenceEqual(b.GetResourceBytes(entry.Name).Span), "Unrelated clip changed");
            editor.Undo(); Check(raw.SequenceEqual(editor.ExportBytes()), "Undo not byte exact");
            editor.Redo(); Check(changed.SequenceEqual(editor.ExportBytes()), "Redo not byte exact");
            try { await editor.TransformAsync(clipName, -1, 4, 7); throw new Exception("Invalid range accepted"); } catch (ArgumentOutOfRangeException) { }
            Check(changed.SequenceEqual(editor.ExportBytes()), "Invalid operation changed state");
            async Task PoseChecks(FigureAnimationEditor target, string selectedClip)
            {
                var before = target.ExportBytes(); var original = await target.Preview().LoadAsync(selectedClip);
                string part = original.Tracks.Keys.First(k => source.Parents.TryGetValue(k, out var parent) && parent.Length > 0);
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
                await target.SetPoseAsync(selectedClip, part, 2, q);
                var after = await target.Preview().LoadAsync(selectedClip);
                Check(Math.Abs(Quaternion.Dot(after.Tracks[part].Rotation(2), q)) > .99999f, "Pose rotation lost");
                foreach (var pair in original.Tracks)
                    for (int f = 0; f < original.FrameCount; f++)
                    {
                        var t = after.Tracks[pair.Key];
                        if (pair.Key != part || f != 2) Check(Math.Abs(Quaternion.Dot(pair.Value.Rotation(f), t.Rotation(f))) > .99999f, "Other rotation changed");
                        Check(pair.Value.Position(f) == t.Position(f), "Position changed unexpectedly");
                        if (pair.Value.Morphs.Length > 0)
                            for (int v = 0; v < pair.Value.Morphs[0].Length; v++) Check(pair.Value.Morph(f, v) == t.Morph(f, v), "Morph changed");
                    }
                byte[] pose = target.ExportBytes();
                try { await target.SetPoseAsync(selectedClip, part, 2, q, Vector3.One); throw new Exception("Child position accepted"); } catch (ArgumentException) { }
                try { await target.SetPoseAsync(selectedClip, part, 2, new Quaternion(float.NaN, 0, 0, 1)); throw new Exception("NaN accepted"); } catch (ArgumentException) { }
                Check(pose.SequenceEqual(target.ExportBytes()), "Invalid pose changed state");
                target.Undo(); Check(before.SequenceEqual(target.ExportBytes()), "Pose undo bytes");
                target.Redo(); Check(pose.SequenceEqual(target.ExportBytes()), "Pose redo bytes"); target.Undo();
                string? rootPart = original.Tracks.Keys.FirstOrDefault(k => source.Parents.TryGetValue(k, out var parent) && parent.Length == 0);
                if (rootPart is not null)
                {
                    await target.SetPoseAsync(selectedClip, rootPart, 2, original.Tracks[rootPart].Rotation(2), new Vector3(1, 2, 3));
                    Check((await target.Preview().LoadAsync(selectedClip)).Tracks[rootPart].Position(2) == new Vector3(1, 2, 3), "Root position lost");
                    target.Undo(); Check(before.SequenceEqual(target.ExportBytes()), "Root undo bytes");
                }
            }
            await PoseChecks(editor, clipName);
            File.WriteAllBytes(Path.Combine(output, scene.Model + ".anm"), changed);
            // The same transformations on the other container layout, with identical payloads.
            var flatEntries = new List<AddedResResource>();
            foreach (var track in clip.Tracks.Keys)
            {
                var nested = a.Entries.FirstOrDefault(e => e.Name == clipName);
                var container = nested is null ? a : ResArchiveDocument.Parse("clip", a.GetResourceBytes(nested).ToArray());
                var entry = container.Entries.Single(e => e.Name.Replace('/', '\\') == (nested is null ? clipName + "\\" : "") + track);
                flatEntries.Add(new("test\\" + track, container.GetResourceBytes(entry).ToArray()));
            }
            var extra = ResArchiveWriter.Rebuild(a, new Dictionary<int, byte[]>(), flatEntries);
            var flat = new FigureAnimationEditor(source.WithBytes(extra));
            await flat.TransformAsync("test", 1, last, 7);
            Check((await flat.Preview().LoadAsync("test")).FrameCount == 7, "Flat format edit failed");
            await PoseChecks(flat, "test");
            var dialog = new AnimationEditorDialog(scene with { Animations = read }, clipName);
            await dialog.Refresh();
            static IEnumerable<DependencyObject> Children(DependencyObject item)
            {
                yield return item;
                foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) foreach (var nested in Children(child)) yield return nested;
            }
            async Task Click(string caption)
            {
                var button = Children(dialog).OfType<System.Windows.Controls.Button>().Single(b => b.Content as string == caption);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                for (int wait = 0; dialog.EditingBusy && wait < 200; wait++) await Task.Delay(10);
                Check(!dialog.EditingBusy, "Authoring dialog busy timeout");
            }
            var preview=Children(dialog).OfType<EvilIslands.DatabaseEditor.App.UnitModelPreviewView>().Single();
            dialog.SelectJoint(parts[0],3);
            var fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var visual=(System.Windows.Media.Media3D.ModelVisual3D)preview.GetType().GetField("_visual",fields)!.GetValue(preview)!;
            var camera=(System.Windows.Media.Media3D.PerspectiveCamera)preview.GetType().GetField("_camera",fields)!.GetValue(preview)!;
            var displayedModel=visual.Content;camera.Position+=new System.Windows.Media.Media3D.Vector3D(.13,.21,.34);var cameraPosition=camera.Position;
            var displayedFrames=new List<double>();preview.FrameDisplayed+=displayedFrames.Add;
            await dialog.Refresh();
            Check(displayedFrames.Count>0&&displayedFrames.All(f=>f==3),"Refresh displayed an intermediate frame instead of selected frame3");
            Check(ReferenceEquals(displayedModel,visual.Content),"Refresh rebuilt model geometry");
            Check(camera.Position==cameraPosition,"Refresh reset camera");
            await Click("Копировать кадры"); await Click("Вставить кадры");
            Check(dialog.EditingFrameCount == 14, "Actual UI paste command failed");
            await Click("Отменить"); Check(dialog.EditingFrameCount == 7, "Actual UI undo command failed");
            Check(displayedFrames.All(f=>f==3)&&preview.AnimationFrame==3,"Paste/undo reset selected frame");
            Check(ReferenceEquals(displayedModel,visual.Content)&&camera.Position==cameraPosition,"Paste/undo reset scene or camera");
            preview.FrameDisplayed-=displayedFrames.Add;
            Console.WriteLine("REFRESH PASS: no intermediate frame0, mesh identity/camera/frame retained through refresh/paste/undo");
            await Click("Запомнить позу"); await Click("Удалить позу"); await Click("Проверить анимацию");
            var trajectoryCheck = Children(dialog).OfType<System.Windows.Controls.CheckBox>().Single(c => c.Content as string == "Траектория выбранной части");trajectoryCheck.IsChecked = true;
            var onionCheck = Children(dialog).OfType<System.Windows.Controls.CheckBox>().Single(c => c.Content as string == "Соседние позы −3, −1, +1, +3");onionCheck.IsChecked = true;
            var ghostCheck = Children(dialog).OfType<System.Windows.Controls.CheckBox>().Single(c => c.Content as string == "Призрак оригинала");
            ghostCheck.IsChecked = true;
            if (parts.Contains("lh3")) dialog.SelectJoint("lh3", 0);
            var root = (FrameworkElement)dialog.Content; root.Measure(new Size(1100, 800)); root.Arrange(new Rect(0, 0, 1100, 800)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, scene.Model + ".png"))) png.Save(file);
            dialog.Close();
            Console.WriteLine($"PASS {name}/{clipName}: crop/resample, pose/root edit, both layouts, other frames/morphs, Undo/Redo, invalid guard, hidden editor render");
        }
        Check(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database))), "Database changed");
    }
}
