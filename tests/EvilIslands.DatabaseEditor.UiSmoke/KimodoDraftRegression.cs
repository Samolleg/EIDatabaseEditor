using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class KimodoDraftRegression
{
    internal static async Task Verify(UnitModelScene scene, string template)
    {
        const int frames = 4;
        string suggested = AnimationEditorDialog.SuggestKimodoClipId("2hacidle01", ["a2hacidle01_ai"]);
        if (suggested != "a2hacidle01_ai2" ||
            AnimationEditorDialog.SuggestKimodoClipId("cwalk01", []).Equals("cwalk01_ai", StringComparison.Ordinal) == false ||
            AnimationEditorDialog.SuggestKimodoClipId(new string('x', 80), []).Length != 15)
            throw new Exception("KIMODO: suggested clip ID is invalid or not collision-free.");
        if (!AnimationEditorDialog.TryReadKimodoStep("profile motion.diffusion_step index=19 ms=123.4", 20, out int first) || first != 1 ||
            !AnimationEditorDialog.TryReadKimodoStep("profile motion.diffusion_step index=0 ms=123.4", 20, out int last) || last != 20 ||
            AnimationEditorDialog.TryReadKimodoStep("profile text.resident_load layers=32 ms=200", 20, out _) ||
            AnimationEditorDialog.TryReadKimodoStep("profile motion.diffusion_step index=20 ms=1", 20, out _))
            throw new Exception("KIMODO: diffusion progress is not based on completed generator steps.");
        var suggestionEditor = new FigureAnimationEditor(scene.Animations!);
        await suggestionEditor.CreateAnimationAsync(template, suggested, frames, true);
        string temp = Path.Combine(Path.GetTempPath(), "kimodo-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using (var root = new BinaryWriter(File.Create(Path.Combine(temp, "root_positions.f32"))))
            using (var pose = new BinaryWriter(File.Create(Path.Combine(temp, "local_rotations_xyzw.f32"))))
                for (int f = 0; f < frames; f++)
                {
                    root.Write(0f); root.Write(0f); root.Write(.1f * f);
                    for (int joint = 0; joint < KimodoDraft.SomaJoints; joint++)
                    {
                        Quaternion q = joint == 11 ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, f * .1f) : Quaternion.Identity;
                        pose.Write(q.X); pose.Write(q.Y); pose.Write(q.Z); pose.Write(q.W);
                    }
                }
            var draft = KimodoDraft.ReadSoma30(temp, frames);
            var editor = new FigureAnimationEditor(scene.Animations!);
            var original = editor.ExportBytes();
            await editor.CreateAnimationAsync(template, "kmd_regress", frames, true);
            var created = editor.ExportBytes();
            await editor.ApplyKimodoDraftAsync("kmd_regress", draft);
            var clip = await editor.Preview().LoadAsync("kmd_regress");
            if (clip.FrameCount != frames ||
                Math.Abs(Quaternion.Dot(clip.Tracks["lh1"].Rotation(3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f))) < .9999f ||
                clip.Tracks["hp"].Positions.Length != frames ||
                clip.Tracks["hp"].Position(3).Y >= clip.Tracks["hp"].Position(0).Y - .2f ||
                Math.Abs(clip.Tracks["hp"].Position(3).Z - clip.Tracks["hp"].Position(0).Z) > .001f)
                throw new Exception("KIMODO: draft did not reach the humanoid ANM clip.");
            var before = ResArchiveDocument.Parse("before", original);
            var after = ResArchiveDocument.Parse("after", editor.ExportBytes());
            foreach (var entry in before.Entries)
                if (!before.GetResourceBytes(entry).Span.SequenceEqual(after.GetResourceBytes(entry.Name).Span))
                    throw new Exception("KIMODO: existing animation changed.");
            editor.Undo();
            if (!created.SequenceEqual(editor.ExportBytes())) throw new Exception("KIMODO: import was not one Undo operation.");
            var bad = Path.Combine(temp, "local_rotations_xyzw.f32");
            using (var stream = new FileStream(bad, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - 4);
            try { KimodoDraft.ReadSoma30(temp, frames); throw new Exception("KIMODO: truncated stream accepted."); }
            catch (InvalidDataException) { }
            Console.WriteLine("KIMODO PASS: SOMA30 stream, humanoid mapping, old ANM payloads, atomic undo, truncated input");
            string? realOutput = Environment.GetEnvironmentVariable("KIMODO_REAL_OUTPUT");
            string? installedRoot = Environment.GetEnvironmentVariable("KIMODO_INSTALLED_ROOT");
            if (!string.IsNullOrWhiteSpace(installedRoot))
            {
                string motionPath = AnimationEditorDialog.KimodoCompatiblePath(Path.Combine(installedRoot, "motion.gguf"));
                string textPath = AnimationEditorDialog.KimodoCompatiblePath(Path.Combine(installedRoot, "text", "Llama-3-Kimodo-Q4_K.gguf"));
                if (!File.Exists(motionPath) || !File.Exists(textPath) ||
                    !textPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                    motionPath.Any(character => character > 127) || textPath.Any(character => character > 127))
                    throw new Exception("KIMODO: Unicode editor installation was not converted to working ASCII paths.");
                Console.WriteLine("KIMODO PATH PASS: installed Unicode path resolves to usable Windows 8.3 paths");
            }
            if (!string.IsNullOrWhiteSpace(realOutput))
            {
                int actualFrames = checked((int)(new FileInfo(Path.Combine(realOutput, "root_positions.f32")).Length / (3 * sizeof(float))));
                var actual = KimodoDraft.ReadSoma30(realOutput, actualFrames);
                var realEditor = new FigureAnimationEditor(scene.Animations!);
                await realEditor.CreateAnimationAsync(template, "kmd_real", actualFrames, true);
                await realEditor.ApplyKimodoDraftAsync("kmd_real", actual);
                var actualClip = await realEditor.Preview().LoadAsync("kmd_real");
                if (actualClip.FrameCount != actualFrames || actualClip.Tracks["hp"].Positions.Length != actualFrames)
                    throw new Exception("KIMODO: real generated stream did not round-trip through humanoid ANM.");
                var sourceTravel = actual.RootPositions[^1] - actual.RootPositions[0];
                var gameTravel = actualClip.Tracks["hp"].Position(actualFrames - 1) - actualClip.Tracks["hp"].Position(0);
                if (sourceTravel.Z > .5f && (gameTravel.Y > -.3f || Math.Abs(gameTravel.Z) > .2f))
                    throw new Exception("KIMODO: forward SOMA travel was not mapped to horizontal game travel.");
                if (scene.Model == "unhuma")
                {
                    var actualPose = realEditor.Preview().Pose(actualClip, 0);
                    if (actualPose["hd"].Position.Z < actualPose["hp"].Position.Z + .4f ||
                        actualPose["lh3"].Position.Z > actualPose["lh1"].Position.Z ||
                        actualPose["ll3"].Position.Z > actualPose["ll1"].Position.Z - .5f)
                        throw new Exception("KIMODO: generated walking frame does not form an upright humanoid.");
                    var view = new AnimationEditorDialog(scene with { Animations = realEditor.Preview() }, "kmd_real");
                    await view.Refresh();
                    foreach (int frameIndex in new[] { 0, actualFrames / 2, actualFrames - 1 })
                    {
                        view.SelectJoint("bd", frameIndex);
                        var rootView = (FrameworkElement)view.Content;
                        rootView.Measure(new Size(1100, 800)); rootView.Arrange(new Rect(0, 0, 1100, 800)); rootView.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(1100, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(rootView);
                        var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(realOutput, $"game-frame-{frameIndex}.png")); image.Save(file);
                    }
                    view.Close();
                }
                Console.WriteLine($"KIMODO REAL PASS: {actualFrames} frames from upstream generator round-trip through Human Hero ANM");
            }
        }
        finally { Directory.Delete(temp, true); }
    }
}
