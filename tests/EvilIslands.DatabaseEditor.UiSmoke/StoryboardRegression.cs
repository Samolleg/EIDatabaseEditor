using System.Numerics;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Reflection;
using System.Windows.Input;
using System.IO;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class StoryboardRegression
{
    internal static async Task VerifyUi(UnitModelScene scene, string name, string output)
    {
        var dialog = new AnimationEditorDialog(scene, name); await dialog.Refresh();
        T Field<T>(string key) => (T)typeof(AnimationEditorDialog).GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        var storyboard = Field<AnimationStoryboard>("_story"); var editor = Field<FigureAnimationEditor>("_editor");
        var source = await editor.Preview().LoadAsync(name); int n = source.FrameCount;
        Field<EvilIslands.DatabaseEditor.App.UnitModelPreviewView>("_preview").SetAnimationFrame(2);
        Check(Field<TextBox>("_first").Text=="2" && Field<TextBox>("_last").Text=="2","playback scrub differs from edit selection");
        static IEnumerable<DependencyObject> Children(DependencyObject item)
        {
            yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) foreach (var inner in Children(child)) yield return inner;
        }
        async Task Click(string title)
        {
            Children(dialog).OfType<Button>().Single(b => b.Content as string == title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i=0;dialog.EditingBusy && i<500;i++) await Task.Delay(10);
            Check(!dialog.EditingBusy,"UI busy");
        }
        storyboard.SelectFrame(1,false); await Click("Копировать · Ctrl+C");
        storyboard.SelectFrame(3,false); await Click("Заменить позу · Ctrl+V");
        Check(dialog.EditingFrameCount==n,"UI replace changed length"); Pose(scene.Animations!,source,1,await editor.Preview().LoadAsync(name),3);
        Check(Field<TextBox>("_first").Text=="3" && Field<TextBox>("_last").Text=="3","refresh lost selection");
        storyboard.SelectFrame(2,false); await Click("Удалить · Del"); Check(dialog.EditingFrameCount==n-1,"UI delete did not delete selected frame");
        await Click("Отменить · Ctrl+Z"); Check(dialog.EditingFrameCount==n,"UI undo delete");
        storyboard.SelectFrame(1,false); storyboard.SelectFrame(2,true); await Click("Дублировать после");
        Check(dialog.EditingFrameCount==n+2,"UI range duplicate"); await Click("Отменить · Ctrl+Z");
        string part=source.Tracks.ContainsKey("lh2")?"lh2":AnimationEditorDialog.EditableParts(scene,source).First();
        dialog.SelectJoint(part,1);
        var locked=Field<CheckBox>("_partLocked"); locked.IsChecked=true; Check(editor.LockedParts.Contains(part),"lock checkbox disconnected");
        locked.IsChecked=false; Check(!editor.LockedParts.Contains(part),"unlock checkbox disconnected");
        await dialog.StoryboardKeyAction(Key.L,ModifierKeys.Control,true)!(); Check(editor.LockedParts.Contains(part),"Ctrl+L from part selector");
        await dialog.StoryboardKeyAction(Key.L,ModifierKeys.Control)!(); Check(!editor.LockedParts.Contains(part),"Ctrl+L unlock");
        var bend=Field<CheckBox>("_bendUnlocked");
        if (part=="lh2")
        {
            Check(bend.IsChecked==false,"bends not restricted by default"); bend.IsChecked=true; Check(editor.UnlockedLimbs.Contains("lh3"),"bend checkbox disconnected");
            dialog.SelectJoint("rh2",1); Check(bend.IsChecked==false,"unlock leaked to other limb");
            dialog.SelectJoint("lh2",1); Check(bend.IsChecked==true,"per-limb unlock lost"); bend.IsChecked=false;
            await dialog.StoryboardKeyAction(Key.U,ModifierKeys.Control,true)!(); Check(editor.UnlockedLimbs.Contains("lh3"),"Ctrl+U from part selector");
            await dialog.StoryboardKeyAction(Key.U,ModifierKeys.Control)!(); Check(!editor.UnlockedLimbs.Contains("lh3"),"Ctrl+U relock");
        }
        var view=(FrameworkElement)dialog.Content;
        view.Measure(new Size(1100,800)); view.Arrange(new Rect(0,0,1100,800)); view.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        var scroll=Field<ScrollViewer>("_storyScroll");
        Check(scroll.HorizontalOffset<=86 && scroll.HorizontalOffset+scroll.ViewportWidth>=172,"selected storyboard frame is offscreen after first layout");
        var image=new RenderTargetBitmap(1100,800,96,96,PixelFormats.Pbgra32); image.Render(view);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(Path.Combine(output,scene.Model+"-storyboard.png"))) png.Save(file);
        Console.WriteLine("STORYBOARD UI PASS " + scene.Model + ": click selection, replace/delete/undo/range duplicate, checkboxes, hidden render");
        editor.LockedParts.Clear(); editor.UnlockedLimbs.Clear(); while(editor.CanUndo)editor.Undo(); dialog.Close();
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception("Storyboard: " + message); }
    static void Pose(FigureAnimationSet set, FigureAnimationClip a, int fa, FigureAnimationClip b, int fb)
    {
        foreach (var p in a.Tracks)
        {
            var t = b.Tracks[p.Key];
            Check(Math.Abs(Quaternion.Dot(p.Value.Rotation(fa), t.Rotation(fb))) > .99999f, "rotation " + p.Key);
            var x = p.Value.Positions.Length > 0 ? p.Value.Position(fa) : set.Bones.GetValueOrDefault(p.Key);
            var y = t.Positions.Length > 0 ? t.Position(fb) : set.Bones.GetValueOrDefault(p.Key);
            Check(Vector3.Distance(x, y) < .00001f, "position " + p.Key);
            if (p.Value.Morphs.Length > 0) for (int v = 0; v < p.Value.Morphs[0].Length; v++) Check(Vector3.Distance(p.Value.Morph(fa,v), t.Morph(fb,v)) < .00001f, "morph");
        }
    }
    internal static async Task Verify(UnitModelScene scene, string name)
    {
        var source = scene.Animations!; var original = await source.LoadAsync(name); var e = new FigureAnimationEditor(source);
        var start = e.ExportBytes(); int n = original.FrameCount;
        await e.ReplaceFramesAsync(name, 2, await e.CopyFramesAsync(name, 1, 1));
        var replaced = await e.Preview().LoadAsync(name); Check(replaced.FrameCount == n, "replacement inserted frames");
        for (int f = 0; f < n; f++) Pose(source, original, f == 2 ? 1 : f, replaced, f);
        var replacement = e.ExportBytes(); e.Undo(); Check(start.SequenceEqual(e.ExportBytes()), "replace undo"); e.Redo(); Check(replacement.SequenceEqual(e.ExportBytes()), "replace redo");
        await e.ResetFrameAsync(name, 2, false); Pose(source, original, 2, await e.Preview().LoadAsync(name), 2);
        await e.ResetFrameAsync(name, 2, true); var bind = await e.Preview().LoadAsync(name);
        Check(bind.Tracks.Values.All(t => Math.Abs(Quaternion.Dot(t.Rotation(2), Quaternion.Identity)) > .99999f), "reset bind");
        for (int f = 0; f < n; f++) if (f != 2) Pose(source, original, f, bind, f);
        e = new FigureAnimationEditor(source);
        string? other = source.Names.FirstOrDefault(s => s != name);
        if (other is not null)
        {
            var otherClip = await source.LoadAsync(other);
            if (original.Tracks.Keys.ToHashSet().SetEquals(otherClip.Tracks.Keys))
            {
                await e.ReplaceFramesAsync(name, 1, await e.CopyFramesAsync(other, 0, 0));
                Pose(source, otherClip, 0, await e.Preview().LoadAsync(name), 1);
                Pose(source, otherClip, 0, await e.Preview().LoadAsync(other), 0);
            }
        }
        var subset = await e.CopyFramesAsync(name,0,0);
        string only = original.Tracks.Keys.First();
        subset = subset with { Tracks = new Dictionary<string,byte[]> { [only] = subset.Tracks[only] } };
        var beforeSubset = await e.Preview().LoadAsync(name);
        await e.ReplaceFramesAsync(name,2,subset);
        var afterSubset = await e.Preview().LoadAsync(name);
        foreach(var p in beforeSubset.Tracks.Where(p=>p.Key!=only)) Check(Math.Abs(Quaternion.Dot(p.Value.Rotation(2),afterSubset.Tracks[p.Key].Rotation(2)))>.99999f,"absent source track changed");
        e = new FigureAnimationEditor(source);
        await e.DeleteFramesAsync(name, 1, 1); var deleted = await e.Preview().LoadAsync(name); Check(deleted.FrameCount == n - 1, "delete count");
        for (int f = 0; f < n - 1; f++) Pose(source, original, f == 0 ? 0 : f + 1, deleted, f);
        await e.DeleteFramesAsync(name, 1, n - 2); Check((await e.Preview().LoadAsync(name)).FrameCount == 1, "one frame workflow");
        var one = e.ExportBytes();
        try { await e.DeleteFramesAsync(name,0,0); throw new Exception("last frame deleted"); } catch (ArgumentException) { }
        Check(one.SequenceEqual(e.ExportBytes()), "last frame failure mutated");
        await e.DuplicateFramesAsync(name,0,0); Pose(source, original,0,await e.Preview().LoadAsync(name),1);
        e = new FigureAnimationEditor(source);
        string root = source.Parents.First(p => p.Value == "" && original.Tracks.ContainsKey(p.Key)).Key;
        string locked = original.Tracks.ContainsKey("hd") ? "hd" : root;
        e.LockedParts.Add(locked);
        var qs = new Dictionary<string, Quaternion> { [root] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.3f) * original.Tracks[root].Rotation(1)) };
        try { await e.ApplyAuthoringAsync(name,1,qs,new Dictionary<string,Vector3>(),0,false,new Dictionary<string,Vector3>()); throw new Exception("ancestor moved locked part"); } catch (InvalidOperationException) { }
        Check(start.SequenceEqual(e.ExportBytes()), "lock failed atomically");
        try { FigureAnimationEditor.ConstrainPreview(source,original,1,qs,new Dictionary<string,Vector3>(),false,new Dictionary<string,Vector3>(),null,e.LockedParts); throw new Exception("live lock ignored"); } catch (InvalidOperationException) { }
        e.LockedParts.Clear(); await e.ApplyAuthoringAsync(name,1,qs,new Dictionary<string,Vector3>(),0,false,new Dictionary<string,Vector3>());
        Check(!start.SequenceEqual(e.ExportBytes()), "unlock ineffective");
        foreach (var end in new[] { "lh3", "rh3", "ll3", "rl3" }.Where(p => FigureLimbIk.Supports(source, original, p)))
        {
            var pose = source.Pose(original, 1); var a = pose[end[..2]+"1"].Position; var b = pose[end[..2]+"2"].Position; var c = pose[end].Position;
            var axis = Vector3.Normalize(c-a); var perpendicular = b-a-axis*Vector3.Dot(b-a,axis); if (perpendicular.LengthSquared() < 1e-7f) continue;
            var reverse = FigureLimbIk.Solve(source,original,1,end,c,a-perpendicular);
            e = new FigureAnimationEditor(source);
            bool rejected = false; try { await e.ApplyAuthoringAsync(name,1,reverse,new Dictionary<string,Vector3>(),0,true,new Dictionary<string,Vector3>()); } catch (ArgumentException) { rejected = true; }
            if (!rejected) continue;
            e.UnlockedLimbs.Add(end);
            _ = FigureAnimationEditor.ConstrainPreview(source,original,1,reverse,new Dictionary<string,Vector3>(),true,new Dictionary<string,Vector3>(),e.UnlockedLimbs);
            await e.ApplyAuthoringAsync(name,1,reverse,new Dictionary<string,Vector3>(),0,true,new Dictionary<string,Vector3>());
            e.LockedParts.Add(locked);
            var saved = e.SaveProject(scene.Model,name,new AnimationAuthoringSettings { LockedParts = [locked], UnlockedLimbs = [end] });
            var opened = new FigureAnimationEditor(source); await opened.LoadProjectAsync(saved,scene.Model,name);
            Check(opened.LockedParts.SetEquals([locked]) && opened.UnlockedLimbs.SetEquals([end]), "lock project roundtrip");
            Console.WriteLine("STORYBOARD PER-LIMB LIMIT PASS " + scene.Model + "/" + end); break;
        }
        e = new FigureAnimationEditor(source);
        for(int i=0;i<35;i++) await e.ResetFrameAsync(name,1,(i%2)==0);
        int undo=0; while(e.CanUndo) {e.Undo();undo++;} Check(undo==32,"bounded history stops edits or grows");
        Console.WriteLine("STORYBOARD CORE PASS " + scene.Model + ": replacement, cross-clip, reset, delete, duplicate, locks, history");
    }
}
