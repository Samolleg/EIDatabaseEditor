using System.IO;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationDialogSaveRegression
{
    private static void Check(bool value, string reason)
    { if (!value) throw new Exception("ANM DIALOG: " + reason); }

    internal static async Task Verify(UnitModelScene scene, string template, string output)
    {
        FigureAnimationSet source = scene.Animations!;
        byte[] original = source.ExportBytes();
        foreach (bool nested in new[] { false, true })
        {
            string path = Path.Combine(output, nested ? "dialog-nested.res" : "dialog-standalone.anm");
            if (nested)
            {
                byte[] empty = new byte[16]; BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty, 0);
                BitConverter.GetBytes(16).CopyTo(empty, 8);
                File.WriteAllBytes(path, ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty", empty), new Dictionary<int, byte[]>(),
                    [new("model.anm", original), new("other.bin", [7, 8, 9])]));
            }
            else File.WriteAllBytes(path, original);
            var target = new AnimationSaveTarget(path, nested ? "model.anm" : null, original);
            var set = new FigureAnimationSet(scene.Model, original, source.Parents, source.Bones, source.BindPositions) { SaveTarget = target };
            var temporaryScene = scene with { Animations = set };
            var parent = new AnimationEditorDialog(temporaryScene, template);
            await parent.WorkingEditor.CreateAnimationAsync(template, "parentedit", 3, false);
            var child = new AnimationEditorDialog(temporaryScene with { Animations = parent.WorkingEditor.Preview() }, template);
            await child.WorkingEditor.CreateAnimationAsync(template, "childcopy", 3, false);
            await child.SaveToSourceAsync(false, checkIssues: false);
            Check(child.SavedAnimationSet is not null, "child did not return saved result");
            await parent.AdoptSavedChild(child);
            Check(parent.SavedAnimationSet is not null && parent.SavedAnimationSet.Names.Contains("parentedit") &&
                parent.SavedAnimationSet.Names.Contains("childcopy"), "parent lost its own or child clip");
            var view = new UnitModelPreviewView(); view.ShowScene(temporaryScene);
            await view.AdoptSavedAnimationAsync(parent.SavedAnimationSet!, template);
            Check(view.ActiveAnimationName == template, "main preview did not load saved clip");
            byte[] firstSave = parent.SavedAnimationSet!.ExportBytes();
            var reopen = new AnimationEditorDialog(temporaryScene with { Animations = parent.SavedAnimationSet }, template);
            await reopen.WorkingEditor.CreateAnimationAsync(template, "secondcopy", 3, false);
            await reopen.SaveToSourceAsync(false, checkIssues: false);
            byte[] Current() => nested ? ResArchiveDocument.Open(path).GetResourceBytes("model.anm").ToArray() : File.ReadAllBytes(path);
            Check(reopen.SavedAnimationSet is not null && Current().SequenceEqual(reopen.SavedAnimationSet.ExportBytes()) &&
                reopen.SavedAnimationSet.Names.Contains("secondcopy"), "second save/reopen failed");
            Check(nested == false || ResArchiveDocument.Open(path).GetResourceBytes("other.bin").Span.SequenceEqual(new byte[] { 7, 8, 9 }),
                "unrelated RES resource changed");
            bool conflict = false;
            try { await target.SaveAsync(original, firstSave, false); } catch (IOException) { conflict = true; }
            Check(conflict, "external-change guard was disabled");
            child.Close(); parent.Close(); reopen.Close();
        }
        Console.WriteLine("ANM DIALOG PASS: child return, parent unsaved edits, main view, reopen and second save, standalone/RES, unrelated resource, conflict");
    }
}
