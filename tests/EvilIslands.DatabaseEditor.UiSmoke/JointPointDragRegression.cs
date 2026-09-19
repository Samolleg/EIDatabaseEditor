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
        if (parts.Contains("rh2")) Check(checkedParts>0,"no humanoid joint coverage");
        Console.WriteLine($"POINT DRAG PASS {scene.Model}: {checkedParts} joints, WPF hit-test, lengths, frame1/export, cancel, Undo/Redo, root");
    }
}
