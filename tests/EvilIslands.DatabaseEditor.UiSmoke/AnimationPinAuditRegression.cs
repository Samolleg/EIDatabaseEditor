using System.IO;
using System.Numerics;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationPinAuditRegression
{
    private static void Check(bool pass, string message)
    { if (!pass) throw new Exception("PIN AUDIT: " + message); }

    internal static int Run(string database)
    { Verify(database).GetAwaiter().GetResult(); return 0; }

    private static async Task Verify(string database)
    {
        var rows = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables)
            .SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var scene = await UnitModelPreview.LoadAsync(database,
            rows.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), rows);
        var source = scene.Animations ?? throw new InvalidDataException("Нет ANM для Human Hero.");
        string walkName = source.Names.First(n => n.StartsWith("cwalk", StringComparison.OrdinalIgnoreCase));
        var walk = await source.LoadAsync(walkName);
        var anchor = source.Pose(walk, 0)["ll3"].Position;
        var editor = new FigureAnimationEditor(source)
        { MotionPins = new Dictionary<string, Vector3> { ["ll3"] = anchor } };
        byte[] before = editor.ExportBytes();
        try
        {
            await editor.TransformAsync(walkName, walk.FrameCount / 2, walk.FrameCount / 2, walk.FrameCount);
            throw new Exception("TransformAsync accepted a pinned foot");
        }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("фиксац", StringComparison.OrdinalIgnoreCase), "Transform explanation"); }
        Check(editor.ExportBytes().SequenceEqual(before) && !editor.CanUndo && editor.MotionPins.Count == 1,
            "Transform rejection changed bytes, history or pins");
        editor.MotionPins = new Dictionary<string, Vector3>();
        await editor.TransformAsync(walkName, walk.FrameCount / 2, walk.FrameCount / 2, walk.FrameCount);
        byte[] transformed = editor.ExportBytes();
        Check(!transformed.SequenceEqual(before) && editor.CanUndo, "Transform after unpin");
        editor.Undo(); Check(editor.ExportBytes().SequenceEqual(before), "Transform undo");
        editor.Redo(); Check(editor.ExportBytes().SequenceEqual(transformed), "Transform redo");

        string idleName = "cidle09";
        var idle = await source.LoadAsync(idleName);
        var align = new FigureAnimationEditor(source);
        await align.CreateAnimationAsync(idleName, "pin_ref", idle.FrameCount, false);
        var originalRoot = idle.Tracks["hp"];
        await align.SetPoseAsync("pin_ref", "hp", 0, originalRoot.Rotation(0),
            originalRoot.Position(0) + new Vector3(0, 0, .1f));
        byte[] baseline = align.ExportBytes();
        var idleAnchor = align.Preview().Pose(await align.Preview().LoadAsync(idleName), 0)["ll3"].Position;
        align.MotionPins = new Dictionary<string, Vector3> { ["ll3"] = idleAnchor };
        int undoBefore = CountUndo(align);
        try { await align.AlignRootHeightAsync(idleName, "pin_ref"); throw new Exception("AlignRootHeightAsync accepted a pinned foot"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("фиксац", StringComparison.OrdinalIgnoreCase), "Align explanation"); }
        Check(align.ExportBytes().SequenceEqual(baseline) && CountUndo(align) == undoBefore && align.MotionPins.Count == 1,
            "Align rejection changed bytes, history or pins");
        align.MotionPins = new Dictionary<string, Vector3>();
        float shift = await align.AlignRootHeightAsync(idleName, "pin_ref");
        byte[] aligned = align.ExportBytes();
        Check(Math.Abs(shift - .1f) < .0001f && !aligned.SequenceEqual(baseline), "Align after unpin");
        align.Undo(); Check(align.ExportBytes().SequenceEqual(baseline), "Align undo");
        align.Redo(); Check(align.ExportBytes().SequenceEqual(aligned), "Align redo");

        // The same invariant protects direct pose edits and track-based UI operations.
        var direct = new FigureAnimationEditor(source)
        { MotionPins = new Dictionary<string, Vector3> { ["ll3"] = anchor } };
        var root = walk.Tracks["hp"];
        try
        {
            await direct.SetPoseAsync(walkName, "hp", 0, root.Rotation(0), root.Position(0) + new Vector3(0, 0, .1f));
            throw new Exception("SetPoseAsync accepted a pinned foot");
        }
        catch (InvalidOperationException) { }
        Check(direct.ExportBytes().SequenceEqual(before) && !direct.CanUndo, "direct pose rejection atomicity");
        Console.WriteLine("PIN AUDIT PASS: cwalk range, root +0.1, direct pose, atomic rejection, unpin, undo/redo");
    }

    private static int CountUndo(FigureAnimationEditor editor)
    {
        int count = 0;
        // Count without consuming history by traversing reversible states.
        while (editor.CanUndo) { editor.Undo(); count++; }
        while (editor.CanRedo) editor.Redo();
        return count;
    }
}
