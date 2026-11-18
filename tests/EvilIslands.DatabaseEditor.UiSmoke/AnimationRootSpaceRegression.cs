using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class AnimationRootSpaceRegression
{
    static void Check(bool value,string message) { if (!value) throw new Exception("ROOT SPACE: " + message); }
    private static IReadOnlyList<FigureMesh> FootGeometry(FigureAnimationSet set, IReadOnlyList<FigureMesh> meshes)
    {
        string Foot(string name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(name))
            {
                if (name.Equals("ll3", StringComparison.OrdinalIgnoreCase)) return "ll3";
                if (name.Equals("rl3", StringComparison.OrdinalIgnoreCase)) return "rl3";
                if (!set.Parents.TryGetValue(name, out var parent) || parent.Length == 0) break;
                name = parent;
            }
            return "";
        }
        var feet = meshes.Where(m => m.Positions.Length > 0 && Foot(m.Name).Length > 0).ToArray();
        if (!feet.Any(m => Foot(m.Name) == "ll3") || !feet.Any(m => Foot(m.Name) == "rl3"))
            throw new InvalidDataException("Для автоматической высоты нужна геометрия обеих стоп модели, включая надетую броню. Проверьте комплект модели и экипировки.");
        if (feet.Any(m => !set.BindPositions.ContainsKey(m.Name) || !set.Bones.ContainsKey(m.Name)))
            throw new InvalidDataException("Для геометрии стоп отсутствует привязка к скелету модели.");
        return feet;
    }

    static float Sole(UnitModelScene scene, FigureAnimationSet set, FigureAnimationClip clip, int frame)
    {
        var pose=set.Pose(clip,frame); float z=float.PositiveInfinity;
        foreach(var mesh in FootGeometry(set, scene.Meshes))
        {
            var node=pose[mesh.Name];var track=clip.Tracks.GetValueOrDefault(mesh.Name);
            foreach(int v in mesh.Indices.Distinct())
            {
                var morph=track is {Morphs.Length:>0}?track.Morph(frame,mesh.SourceVertices[v]):Vector3.Zero;
                z=Math.Min(z,(Vector3.Transform(mesh.Positions[v]-set.BindPositions[mesh.Name]+morph,node.Rotation)+node.Position).Z);
            }
        }
        return z;
    }
    static void Pump(Func<Task> run)
    {
        var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { var task=run();var frame=new DispatcherFrame();task.ContinueWith(_=>Dispatcher.CurrentDispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.FromCurrentSynchronizationContext());Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    static float ViewSole(UnitModelPreviewView view)
    {
        var list=(System.Collections.IEnumerable)typeof(UnitModelPreviewView).GetField("_animatedMeshes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        float z=float.PositiveInfinity;
        foreach(var item in list)
        {
            var type=item!.GetType(); var source=(FigureMesh)type.GetProperty("Source")!.GetValue(item)!;
            if(source.Name is not ("ll3" or "rl3"))continue;
            var mesh=(MeshGeometry3D)type.GetProperty("Mesh")!.GetValue(item)!;
            var model=(GeometryModel3D)type.GetProperty("Model")!.GetValue(item)!;
            foreach(int v in mesh.TriangleIndices.Distinct())z=Math.Min(z,(float)model.Transform.Transform(mesh.Positions[v]).Y);
        }
        return z;
    }
    internal static int Run(string db,string evidence) { try { return Verify(db,evidence); } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    private static int Verify(string db,string evidence)
    {
        Directory.CreateDirectory(evidence);
        var application = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent(); typeof(System.Windows.Application).GetField("_startupUri",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(application,null); application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var rows=DatabaseDocument.Open(db).Databases.SelectMany(g=>g.Tables).SelectMany(t=>t.Records.Select(r=>ModelRecord.Capture(t.Key,r))).ToArray();
        var hero=rows.Single(r=>r.Table=="units.prototypes"&&r.Name=="Human Hero");
        var scene=UnitModelPreview.LoadAsync(db,hero,rows).GetAwaiter().GetResult();var set=scene.Animations!;
        var clip=set.LoadAsync("ubriefing21").GetAwaiter().GetResult();var raw=set.WithRootSpace(FigureRootSpace.Raw);
        Check(clip.FrameCount==185 && scene.Model=="unhufe","reproduction identity changed");
        // Independent constants read from original instructions/header, not inferred
        // by the renderer: this is the actual pre-fix six-centimeter reproduction.
        Check(Math.Abs(set.Pose(clip,0)["hp"].Position.Z-.9299875f)<.00001f,"original-backed root height");
        Check(Sole(scene,raw,clip,0)>.06f && Sole(scene,set,clip,0)<.006f,"legacy foot gap not corrected");
        Console.WriteLine($"LEGACY before={Sole(scene,raw,clip,0):F6} after={Sole(scene,set,clip,0):F6} rawHp={clip.Tracks["hp"].Position(0).Z:F6} gameHp={set.Pose(clip,0)["hp"].Position.Z:F6}");
        var stats=new List<object>();
        foreach(float tall in new[]{0f,.5f,.85f,1f})
        {
            var fields=new Dictionary<int,string>(hero.Fields){[7]=tall.ToString(System.Globalization.CultureInfo.InvariantCulture)};
            var sc=UnitModelPreview.LoadAsync(db,hero with {Fields=fields},rows).GetAwaiter().GetResult();
            foreach(string name in new[]{"ubriefing21","cidle09","cwalk01"})
            {
                var s=sc.Animations!;var c=s.LoadAsync(name).GetAwaiter().GetResult();
                float independent=(.507f+tall*(1.073f-.507f))/1.052f;
                Check(Vector3.Distance(s.Pose(c,0)["hp"].Position,c.Tracks["hp"].Position(0)*independent)<.000001f,"all XYZ factor differs");
                stats.Add(new {model=sc.Model,tall,name,c=s.RootSpace.Coefficient,sole0=Sole(sc,s,c,0)});
                var edit=new FigureAnimationEditor(s);edit.TransformAsync(name,0,c.FrameCount-1,c.FrameCount).GetAwaiter().GetResult();
                Check(edit.ExportBytes().SequenceEqual(s.ExportBytes()),"open/no-op changed legacy bytes");
            }
        }
        var officer=UnitModelPreview.LoadAsync(db,rows.Single(r=>r.Table=="units.prototypes"&&r.Name=="Human Kania Army Officer MC F11"),rows).GetAwaiter().GetResult();
        Check(officer.Model!=scene.Model,"second model not distinct");
        var oc=officer.Animations!.LoadAsync("cidle09").GetAwaiter().GetResult();
        stats.Add(new {model=officer.Model,name="cidle09",c=officer.Animations.RootSpace.Coefficient,sole0=Sole(officer,officer.Animations,oc,0)});
        var editor=new FigureAnimationEditor(set);var target=set.Pose(clip,0)["hp"].Position+new Vector3(.13f,-.17f,.07f);
        editor.SetPoseAsync("ubriefing21","hp",0,clip.Tracks["hp"].Rotation(0),set.RootSpace.ToRaw(target)).GetAwaiter().GetResult();
        var bytes=editor.ExportBytes();string path=Path.Combine(evidence,"edited-unhufe.anm");File.WriteAllBytes(path,bytes);
        // Independent resource/track decode after save; then forward transform once.
        var outer=ResArchiveDocument.Parse("roundtrip",File.ReadAllBytes(path));var nested=ResArchiveDocument.Parse("ubriefing21",outer.GetResourceBytes("ubriefing21").ToArray());
        var root=FigureAnimationTrack.Decode(nested.GetResourceBytes("hp").ToArray());
        Check(Vector3.Distance(root.Position(0),target/set.RootSpace.Coefficient)<.000001f,"raw inverse serialization");
        var reopened=set.WithSavedBytes(File.ReadAllBytes(path));var reopenedClip=reopened.LoadAsync("ubriefing21").GetAwaiter().GetResult();
        Check(Vector3.Distance(reopened.Pose(reopenedClip,0)["hp"].Position,target)<.000001f,"double coefficient after save/reload");
        var original=ResArchiveDocument.Parse("original",set.ExportBytes());
        foreach(var entry in original.Entries.Where(e=>e.Name!="ubriefing21"))Check(original.GetResourceBytes(entry).Span.SequenceEqual(outer.GetResourceBytes(entry.Name).Span),"unrelated clip changed");
        var originalClip=ResArchiveDocument.Parse("clip",original.GetResourceBytes("ubriefing21").ToArray());
        foreach(var entry in originalClip.Entries.Where(e=>e.Name!="hp"))Check(originalClip.GetResourceBytes(entry).Span.SequenceEqual(nested.GetResourceBytes(entry.Name).Span),"unrelated track changed");
        var pin=set.Pose(clip,0)["ll3"].Position;
        var project=editor.SaveProject("unhufe","ubriefing21",new AnimationAuthoringSettings{Part="hp",Pins=new(){["ll3"]=pin},Contacts=new(){new("ll3",0,1,pin)}});
        var restored=new FigureAnimationEditor(set);var same=restored.LoadProjectAsync(project,"unhufe","ubriefing21").GetAwaiter().GetResult();
        Check(same.Pins.Count==1&&same.Contacts.Count==1&&restored.ProjectCoordinateWarning is null,"same-space project lost anchors");
        var changedSpace=new FigureAnimationEditor(set.WithRootSpace(FigureRootSpace.FromTransfer(.8f)));
        var migrated=changedSpace.LoadProjectAsync(project,"unhufe","ubriefing21").GetAwaiter().GetResult();
        Check(migrated.Pins.Count==0&&migrated.Contacts.Count==0&&changedSpace.ProjectCoordinateWarning is not null&&changedSpace.ExportBytes().SequenceEqual(bytes),"project raw recovery/explicit anchor migration");
        byte[] dbBytes=scene.AnimationDatabase!.Database.ExportBytes();
        Array.Clear(dbBytes,36,4);var zeroMiddle=new FigureRootSpace(AnimationDatabase.Parse(dbBytes),float.NaN);
        Check(zeroMiddle.Coefficient==1,"original middle-zero branch");
        Array.Clear(dbBytes,32,12);BitConverter.GetBytes(1f).CopyTo(dbBytes,36);var zero=new FigureRootSpace(AnimationDatabase.Parse(dbBytes),0);
        Check(zero.ToGame(Vector3.One)==Vector3.Zero,"zero coefficient read");
        try{zero.ToRaw(Vector3.One);throw new Exception("zero division allowed");}catch(InvalidDataException){}
        BitConverter.GetBytes(float.NaN).CopyTo(dbBytes,36);var invalid=new FigureRootSpace(AnimationDatabase.Parse(dbBytes),.85f);
        try{invalid.ToGame(Vector3.One);throw new Exception("NaN allowed");}catch(InvalidDataException){}
        Check(FigureRootSpace.Raw.ToGame(target)==target&&FigureRootSpace.Raw.ToRaw(target)==target,"no-ADB raw fallback");
        // Actual isolated archive save replaces Database: every snapshot must see
        // the new descriptor, while absolute anchors are explicitly invalidated.
        string adbCopy = Path.Combine(evidence,"metadata-copy.res");
        File.WriteAllBytes(adbCopy,File.ReadAllBytes(scene.AnimationDatabase.ArchivePath));
        var savedSource = AnimationDatabaseSource.OpenAsync(adbCopy,null,scene.Model).GetAwaiter().GetResult()!;
        var linkedSet = set.WithRootSpace(new FigureRootSpace(savedSource,.85f));
        var snapshot = linkedSet.WithSavedBytes(linkedSet.ExportBytes());
        var anchored = new FigureAnimationEditor(linkedSet) { MotionPins = new Dictionary<string,Vector3>{{"ll3",pin}}, Contacts = new[]{new FootContact("ll3",0,1,pin)} };
        var draft = savedSource.CreateDraft();
        var draftHeader = (byte[])typeof(AnimationDatabase).GetField("_header",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(draft)!;
        BitConverter.GetBytes(1.1f).CopyTo(draftHeader,36);
        savedSource.SaveAsync(draft,backup:false).GetAwaiter().GetResult();
        float savedCoefficient = (.507f+.85f*(1.073f-.507f))/1.1f;
        Check(Math.Abs(snapshot.RootSpace.Coefficient-savedCoefficient)<1e-6f,"saved nested snapshot stale");
        Check(Vector3.Distance(snapshot.Pose(clip,0)["hp"].Position,clip.Tracks["hp"].Position(0)*savedCoefficient)<1e-6f,"ADB save forward twice");
        var reopenedAdb=AnimationDatabaseSource.OpenAsync(adbCopy,null,scene.Model).GetAwaiter().GetResult()!;
        Check(Math.Abs(new FigureRootSpace(reopenedAdb,.85f).Coefficient-savedCoefficient)<1e-6f,"saved ADB reload");
        Check(anchored.Preview().ExportBytes().SequenceEqual(set.ExportBytes())&&anchored.MotionPins.Count==0&&anchored.Contacts.Count==0&&anchored.ProjectCoordinateWarning is not null,"live anchor refresh/raw preservation");
        var invalidEditor = new FigureAnimationEditor(set.WithRootSpace(zero));
        byte[] invalidBefore=invalidEditor.ExportBytes();
        try { invalidEditor.RetargetAsync("ubriefing21",editor.ExportMotion("unhufe","ubriefing21"),new Dictionary<string,string>{{"hp","hp"}}).GetAwaiter().GetResult(); throw new Exception("invalid game-space writer accepted"); }
        catch(InvalidDataException) { }
        Check(invalidEditor.ExportBytes().SequenceEqual(invalidBefore)&&!invalidEditor.CanUndo,"invalid coefficient partially mutated editor");
        try { FigureRootSpace.FromTransfer(float.Epsilon).ToRaw(Vector3.One); throw new Exception("inverse overflow accepted"); } catch(InvalidDataException) { }
        // Live metadata object refresh, as used by successful ADB save.
        var staticDb=scene.AnimationDatabase.Database;var linked=new FigureRootSpace(staticDb,.85f);
        float before=linked.Coefficient; var header=typeof(AnimationDatabase).GetField("_header",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(staticDb) as byte[];
        var old=header!.AsSpan(36,4).ToArray();BitConverter.GetBytes(1.1f).CopyTo(header!,36);
        Check(linked.Coefficient!=before,"descriptor stale after metadata refresh");old.CopyTo(header!,36);
        // Hidden WPF controls only: ordinary view and actual editor use exact same
        // game-space mesh matrix and Z0 floor. Never call Show/ShowDialog.
        var view=new UnitModelPreviewView { AllowAnimationEditing=true };view.ShowScene(scene);Pump(()=>view.SelectAnimationAsync("ubriefing21"));
        var dialog=new AnimationEditorDialog(scene,"ubriefing21");Pump(()=>dialog.Refresh());
        var editing=(UnitModelPreviewView)typeof(AnimationEditorDialog).GetField("_preview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
        Check(Math.Abs(ViewSole(view)-Sole(scene,set,clip,0))<.000001f&&Math.Abs(ViewSole(editing)-ViewSole(view))<.000001f,"ordinary/editor matrix discrepancy");
        view.Measure(new System.Windows.Size(700,600)); view.Arrange(new System.Windows.Rect(0,0,700,600)); view.UpdateLayout();
        view.SetSkeleton(new[]{"hp"},"hp");
        Vector3? committed=null; var displayed=Vector3.Zero;
        view.JointEdited += (part,frame,q,pos)=>committed=pos;
        Check(view.BeginJointGesture("hp",0,true,new System.Windows.Point(100,100)),"root axis gesture start");
        view.MoveJointGesture(new System.Windows.Point(130,100)); displayed=view.CurrentJointPose["hp"].Position; view.CommitJointGesture();
        Check(committed.HasValue&&Vector3.Distance(set.RootSpace.ToGame(committed.Value),displayed)<1e-6f,"gizmo inverse not serialized raw");
        Check(Vector3.Distance(displayed,set.Pose(clip,0)["hp"].Position)>.00001f,"axis gesture did not move");
        Check(view.GroundLevel==0&&editing.GroundLevel==0&&dialog.WorkingEditor.ExportBytes().SequenceEqual(set.ExportBytes()),"opening editor/JSON modified legacy");
        Check(application.Windows.Cast<System.Windows.Window>().All(window=>!window.IsVisible),"headless root test opened a visible window");
        view.PauseAnimation();editing.PauseAnimation(); application.Shutdown();
        File.WriteAllText(Path.Combine(evidence,"root-space.json"),JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("ROOT SPACE PASS: original correction, morph/model/armor, raw no-op/edit-save-reopen, untouched tracks, project migration, zero/invalid/noADB, metadata refresh, hidden ordinary/editor matrices.");return 0;
    }
}



