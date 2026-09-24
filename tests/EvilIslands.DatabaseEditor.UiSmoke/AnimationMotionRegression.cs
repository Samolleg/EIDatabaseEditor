using System.Numerics;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using System.Windows;
namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class AnimationMotionRegression
{
    private static void Check(bool ok,string why){if(!ok)throw new Exception("MOTION: "+why);}
    private static bool Same(Quaternion a,Quaternion b)=>Math.Abs(Quaternion.Dot(a,b))>.99998f;
    internal static async Task Verify(UnitModelScene scene,string name)
    {
        var source=scene.Animations!;var clip=await source.LoadAsync(name);var editor=new FigureAnimationEditor(source);var original=editor.ExportBytes();
        string part=clip.Tracks.ContainsKey("hd")?"hd":clip.Tracks.Keys.First();int last=Math.Min(5,clip.FrameCount-1);
        foreach(var ease in Enum.GetValues<MotionEase>())
        {
            await editor.CurveAsync(name,part,0,last,2,false,0,30,ease);var curve=await editor.Preview().LoadAsync(name);
            for(int f=0;f<=last;f++)Check(Math.Abs(FigureAnimationEditor.TwistDegrees(curve.Tracks[part].Rotation(f),2)-30*FigureAnimationEditor.Ease(f/(float)last,ease))<.002f,"curve interpolation");
            for(int f=last+1;f<clip.FrameCount;f++)Check(Same(curve.Tracks[part].Rotation(f),clip.Tracks[part].Rotation(f)),"curve outside range");
            editor.Undo();Check(original.SequenceEqual(editor.ExportBytes()),"curve undo");
        }
        await editor.CurveAsync(name,part,1,1,2,false,42,42,MotionEase.Linear);var key=(await editor.Preview().LoadAsync(name)).Tracks[part].Rotation(1);
        await editor.MoveJointFrameAsync(name,part,1,2);var moved=await editor.Preview().LoadAsync(name);Check(Same(moved.Tracks[part].Rotation(2),key)&&Same(moved.Tracks[part].Rotation(1),clip.Tracks[part].Rotation(1)),"move joint key");editor.Undo();editor.Undo();
        var pose=await editor.CapturePoseAsync(name,0,"test pose");var library=FigureAnimationEditor.DeserializeMotion<List<StoredAnimationPose>>(FigureAnimationEditor.SerializeMotion(new List<StoredAnimationPose>{pose}));
        await editor.ApplyStoredPoseAsync(name,2,library[0],new[]{part});var pasted=await editor.Preview().LoadAsync(name);Check(Same(pasted.Tracks[part].Rotation(2),clip.Tracks[part].Rotation(0)),"masked library paste");foreach(var other in clip.Tracks.Where(p=>p.Key!=part))Check(Same(other.Value.Rotation(2),pasted.Tracks[other.Key].Rotation(2)),"mask modified other joint");editor.Undo();
        await editor.LoopAsync(name,Math.Min(4,clip.FrameCount));var loop=await editor.Preview().LoadAsync(name);foreach(var p in loop.Tracks){Check(Same(p.Value.Rotation(0),p.Value.Rotation(loop.FrameCount-1)),"loop rotation seam");if(p.Value.Morphs.Length>0)Check(Vector3.Distance(p.Value.Morph(0,0),p.Value.Morph(loop.FrameCount-1,0))<.00001,"loop morph seam");}editor.Undo();
        await editor.ComposeAsync(name,clip,0,last,2);var mix=await editor.Preview().LoadAsync(name);Check(mix.FrameCount==clip.FrameCount+last-1,"composition length");foreach(var p in clip.Tracks)Check(Same(mix.Tracks[p.Key].Rotation(mix.FrameCount-1),p.Value.Rotation(last)),"composition incoming tail");editor.Undo();
        var motion=FigureAnimationEditor.DeserializeMotion<MotionTransfer>(FigureAnimationEditor.SerializeMotion(editor.ExportMotion(scene.Model,name)));
        var mapping=clip.Tracks.Keys.Where(source.Parents.ContainsKey).ToDictionary(p=>p,p=>p);
        await editor.RetargetAsync(name,motion,mapping);var retarget=await editor.Preview().LoadAsync(name);foreach(var p in mapping.Keys)for(int f=0;f<clip.FrameCount;f++)Check(Same(clip.Tracks[p].Rotation(f),retarget.Tracks[p].Rotation(f)),"identity retarget");if(editor.CanUndo)editor.Undo();
        string rootPart=mapping.Keys.First(p=>source.Parents[p]=="");
        var large=motion with {Bones=motion.Bones.ToDictionary(p=>p.Key,p=>p.Value*2)};
        await editor.RetargetAsync(name,large,mapping);var scaled=await editor.Preview().LoadAsync(name);
        var rootTrack=clip.Tracks[rootPart];Vector3 rootStart=rootTrack.Positions.Length>0?rootTrack.Position(0):source.Bones[rootPart];
        for(int f=0;f<clip.FrameCount;f++){var delta=rootTrack.Positions.Length>0?(rootTrack.Position(f)-rootTrack.Position(0))*.5f:Vector3.Zero;Check(Vector3.Distance(scaled.Tracks[rootPart].Position(f),rootStart+delta)<.00001f,"scaled retarget root");}
        Check(source.Bones.All(p=>editor.Preview().Bones[p.Key]==p.Value),"retarget changed target dimensions");if(editor.CanUndo)editor.Undo();
        var wrongBones=new Dictionary<string,Vector3>(motion.Bones);string? axisPart=mapping.Keys.FirstOrDefault(p=>wrongBones[p].LengthSquared()>1e-8f);if(axisPart is not null){wrongBones[axisPart]=-wrongBones[axisPart];var axisBefore=editor.ExportBytes();
        try{await editor.RetargetAsync(name,motion with {Bones=wrongBones},mapping);throw new Exception("incompatible axes accepted");}catch(ArgumentException){}Check(axisBefore.SequenceEqual(editor.ExportBytes()),"axes failure mutation");}
        var badMap=new Dictionary<string,string>(mapping);string child=mapping.Keys.First(p=>source.Parents[p]!="");badMap.Remove(source.Parents[child]);var before=editor.ExportBytes();try{await editor.RetargetAsync(name,motion,badMap);throw new Exception("invalid hierarchy accepted");}catch(ArgumentException){}Check(before.SequenceEqual(editor.ExportBytes()),"retarget failure mutation");
        if(FigureLimbIk.Supports(source,clip,"ll3"))
        {
            var rangeEditor=new FigureAnimationEditor(source);var one=await rangeEditor.CopyFramesAsync(name,0,0);await rangeEditor.PasteFramesAsync(name,0,one);await rangeEditor.PasteFramesAsync(name,0,one);
            var rangeContact=new FootContact("ll3",0,2,source.Pose(clip,0)["ll3"].Position);rangeEditor.Contacts=new[]{rangeContact};var rangeBefore=rangeEditor.ExportBytes();
            await rangeEditor.BakeContactsAsync(name);var rangeClip=await rangeEditor.Preview().LoadAsync(name);for(int f=0;f<=2;f++)Check(Vector3.Distance(rangeEditor.Preview().Pose(rangeClip,f)["ll3"].Position,rangeContact.Anchor)<.0001f,"range contact drift");rangeEditor.Undo();Check(rangeBefore.SequenceEqual(rangeEditor.ExportBytes()),"contact bake undo");
            rangeEditor.Contacts=new[]{rangeContact with {Anchor=rangeContact.Anchor+new Vector3(100,100,100)}};
            try{await rangeEditor.BakeContactsAsync(name);throw new Exception("unreachable contact accepted");}catch(ArgumentException){}Check(rangeBefore.SequenceEqual(rangeEditor.ExportBytes()),"contact failure mutation");
            rangeEditor.Contacts=new[]{rangeContact with {First=clip.FrameCount+1,Last=clip.FrameCount+1}};
            try{await rangeEditor.NavigateHistoryAsync(name,false);throw new Exception("history invalidated contacts");}catch(ArgumentException){}Check(rangeBefore.SequenceEqual(rangeEditor.ExportBytes()),"history guard mutation");
            var c=new FootContact("ll3",0,0,source.Pose(clip,0)["ll3"].Position);editor.Contacts=new[]{c};
            var settings=new AnimationAuthoringSettings{Part=part,First=0,Last=last,Contacts=new(){c},Poses=library,Trajectory=true,Onion=true};
            var saved=editor.SaveProject(scene.Model,name,settings);var reopened=new FigureAnimationEditor(source);var settingsBack=await reopened.LoadProjectAsync(saved,scene.Model,name);Check(reopened.Contacts.Count==1&&settingsBack.Poses.Count==1&&settingsBack.Trajectory&&settingsBack.Onion,"project motion settings");
            string root=mapping.Keys.First(p=>source.Parents[p]=="");var start=source.Pose(clip,0)[root].Position;
            await editor.CurveAsync(name,root,0,0,2,true,start.Z-.0001f,start.Z-.0001f,MotionEase.Linear);var fixedClip=await editor.Preview().LoadAsync(name);Check(Vector3.Distance(editor.Preview().Pose(fixedClip,0)["ll3"].Position,c.Anchor)<.0001f,"contact did not constrain curve");editor.Undo();editor.Contacts=Array.Empty<FootContact>();
        }
        await editor.CurveAsync(name,part,1,1,2,false,170,170,MotionEase.Linear);var issues=await editor.DiagnoseAsync(name);Check(issues.Any(i=>i.Part==part&&i.Frame is 1 or 2),"jump not detected");editor.Undo();
        var creator=new FigureAnimationEditor(source);var initial=creator.ExportBytes();
        foreach(bool bind in new[]{false,true})
        {
            string created=bind?"user_bind":"user_copy";await creator.CreateAnimationAsync(name,created,9,bind);
            var added=await creator.Preview().LoadAsync(created);Check(added.FrameCount==9,"new clip length");
            var oldArchive=ResArchiveDocument.Parse("old",initial);var newArchive=ResArchiveDocument.Parse("new",creator.ExportBytes());
            foreach(var entry in oldArchive.Entries)Check(oldArchive.GetResourceBytes(entry).Span.SequenceEqual(newArchive.GetResourceBytes(entry.Name).Span),"new clip modified original");
            foreach(var track in added.Tracks)Check(Same(track.Value.Rotation(0),bind?Quaternion.Identity:clip.Tracks[track.Key].Rotation(0)),"new clip pose");
            var beforeDuplicate=creator.ExportBytes();try{await creator.CreateAnimationAsync(name,created.ToUpperInvariant(),9,false);throw new Exception("duplicate accepted");}catch(ArgumentException){}Check(beforeDuplicate.SequenceEqual(creator.ExportBytes()),"duplicate mutation");
            creator.Undo();Check(initial.SequenceEqual(creator.ExportBytes()),"new clip undo");
        }
        try{await creator.CreateAnimationAsync(name,"../bad",9,false);throw new Exception("invalid name accepted");}catch(ArgumentException){}
        Console.WriteLine("CATALOG PASS: copy/bind new clips, exact original payloads, duplicate/name guards, undo");
        await AnimationSaveRegression.Verify(source, name);
        var view=new UnitModelPreviewView{AllowAnimationEditing=false};view.ShowScene(scene);Check(view.EditorAnimationName is not null,"editor unavailable from bind pose");await view.SelectAnimationAsync(name);view.SetSkeleton(AnimationEditorDialog.EditableParts(scene,clip),part);view.SetMotionGuides(part,true,true);view.SetAnimationFrame(3);Check(view.OnionCount==4&&view.TrajectoryCount==clip.FrameCount,"trajectory/onions missing");view.SetMotionGuides(part,false,false);Check(view.OnionCount==0&&view.TrajectoryCount==0,"guide cleanup");
        Console.WriteLine("MOTION TEN PASS "+scene.Model+": joint transfer, five curves, trajectory/onions, loop, masked library, timed contacts, composition, mapped retarget+rollback, diagnosis, project persistence");
    }
}
