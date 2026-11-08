using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class JointPointDragRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static async Task Verify(UnitModelScene scene, FigureAnimationClip clip, string[] parts)
    {
        var source = scene.Animations!;
        var view = new UnitModelPreviewView { AllowAnimationEditing = false };
        view.ShowScene(scene); await view.SelectAnimationAsync(clip.Name);
        view.SetAnimationFrame(1);
        view.Measure(new Size(900,700)); view.Arrange(new Rect(0,0,900,700)); view.UpdateLayout();
        var before = source.Pose(clip,1);
        if (parts.Contains("ll3") && parts.Contains("rl3"))
        {
            var root = parts.First(p => source.Parents[p].Length == 0);
            var below = before[root].Position - Vector3.UnitZ * 100;
            view.GroundEnabled = true;
            var floor = view.GroundLevel;
            if (floor != 0) throw new Exception("Ground must expose absolute ANM zero, not follow the bind mesh.");
            var viewport = (System.Windows.Controls.Viewport3D)typeof(UnitModelPreviewView)
                .GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            Check(viewport.Children.Count == 3, "visible ground was not added to preview");
            var groundedRoot = view.ClampToGround(root, 1, below);
            var groundedFoot = view.ClampToGround("ll3", 1, below);
            Check(groundedRoot.Z > below.Z && groundedFoot.Z >= floor && groundedRoot.X == below.X && groundedRoot.Y == below.Y,
                "ground did not support root/foot or blocked travel along surface");
            view.GroundEnabled = false;
            Check(viewport.Children.Count == 2, "disabled ground is still drawn");
            Check(view.ClampToGround(root, 1, below) == below, "disabled ground still restricts movement");
        }
        IReadOnlyDictionary<string, Quaternion>? committed = null;
        int commits = 0;
        view.LimbEdited += (frame, rotations) => { Check(frame == 1,"point drag changed frame"); committed = rotations; commits++; };
        int checkedParts = 0;
        foreach (string part in parts.Where(p => FigureLimbIk.SupportsJoint(source,clip,p) && !FigureLimbIk.Supports(source,clip,p)))
        {
            string parent = source.Parents[part];
            if (Vector3.Distance(before[part].Position,before[parent].Position)<.0001f) continue;
            view.SetSkeleton(parts, part); view.UpdateLayout();
            var canvas = (Canvas)typeof(UnitModelPreviewView).GetField("_skeletonCanvas",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            var dot = canvas.Children.OfType<Ellipse>().Single(e => Equals(e.Tag,part));
            var point = new Point(Canvas.GetLeft(dot)+9,Canvas.GetTop(dot)+9);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(canvas,point)?.VisualHit;
            Check(ReferenceEquals(hit,dot),$"joint hit mismatch: {part}, hit={hit}, canvas={canvas.RenderSize}, dot={dot.RenderSize}, point={point}");
            Check(view.BeginPointGesture(part,point),"point unavailable: "+part);
            view.MoveJointGesture(point+new System.Windows.Vector(23,17));
            var live = view.CurrentJointPose;
            Check(Vector3.Distance(live[part].Position,before[part].Position)>.000001f,"point did not move: "+part);
            foreach (var node in source.Parents.Where(p=>p.Value.Length>0))
                Check(Math.Abs(Vector3.Distance(live[node.Key].Position,live[node.Value].Position)-Vector3.Distance(before[node.Key].Position,before[node.Value].Position))<.0001f,"bone stretched");
            view.EndJointDrag();
            Check(view.CurrentJointPose[part]==before[part] && commits==checkedParts,"cancel committed point");
            view.BeginPointGesture(part,point); view.MoveJointGesture(point+new System.Windows.Vector(23,17)); view.CommitJointGesture();
            Check(commits==++checkedParts && committed!.Count==1 && committed.ContainsKey(parent),"point commit did not affect parent once");
            var editor = new FigureAnimationEditor(source); var bytes=editor.ExportBytes();
            await editor.SetRotationsAsync(clip.Name,1,committed!);
            var saved=await editor.Preview().LoadAsync(clip.Name);
            var savedPose=editor.Preview().Pose(saved,1);
            Check(Vector3.Distance(savedPose[part].Position,live[part].Position)<.0001f,"saved point differs from live");
            foreach(var pair in clip.Tracks)
                foreach(int f in new[]{0,2}) Check(Math.Abs(Quaternion.Dot(pair.Value.Rotation(f),saved.Tracks[pair.Key].Rotation(f)))>.9999f,"neighbor frame changed");
            var changed=editor.ExportBytes(); editor.Undo(); Check(bytes.SequenceEqual(editor.ExportBytes()),"point Undo");
            editor.Redo(); Check(changed.SequenceEqual(editor.ExportBytes()),"point Redo");
        }
        foreach(string root in parts.Where(p=>source.Parents[p].Length==0))
        {
            view.SetSkeleton(parts,root);
            Check(view.BeginPointGesture(root,new Point(100,100)),"root point unavailable");
            view.MoveJointGesture(new Point(123,117));
            var pose=view.CurrentJointPose; var shift=pose[root].Position-before[root].Position;
            Check(shift.Length()>.000001f,"root did not move");
            foreach(string part in parts) Check(Vector3.Distance(pose[part].Position-before[part].Position,shift)<.0001f,"root deformed hierarchy");
            view.EndJointDrag(); Check(view.CurrentJointPose[root]==before[root],"root cancel");
        }
        if (parts.Contains("bd"))
        {
            var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bd" };
            string? rejection = null;
            view.PoseRejected = message => rejection = message;
            view.PoseFilter = (frame, rotations, positions) => FigureAnimationEditor.ConstrainPreview(
                source, clip, frame, rotations, positions, false, new Dictionary<string, Vector3>(), null, locked);
            view.SetSkeleton(parts, "bd");
            Check(view.BeginPointGesture("bd", new Point(100, 100)), "locked-root gesture unavailable");
            view.MoveJointGesture(new Point(123, 117));
            Check(rejection?.Contains("bd", StringComparison.OrdinalIgnoreCase) == true, "locked gesture rejection missing");
            Check(view.CurrentJointPose["bd"] == before["bd"], "locked gesture changed root pose");
            view.CommitJointGesture();
            Check(commits == checkedParts, "rejected gesture committed");
            locked.Clear(); rejection = null;
            Check(view.BeginPointGesture("bd", new Point(100, 100)), "gesture did not reset after lock rejection");
            view.MoveJointGesture(new Point(123, 117));
            Check(rejection is null && Vector3.Distance(view.CurrentJointPose["bd"].Position, before["bd"].Position) > .000001f,
                "unlocked gesture failed after rejection");
            view.EndJointDrag();
        }
        if (parts.Contains("rh2")) Check(checkedParts>0,"no humanoid joint coverage");
        Console.WriteLine($"POINT DRAG PASS {scene.Model}: {checkedParts} joints, WPF hit-test, lengths, frame1/export, cancel, Undo/Redo, root");
    }
}
