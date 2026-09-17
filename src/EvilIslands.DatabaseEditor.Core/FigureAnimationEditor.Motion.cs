using System.Numerics;
using System.Text.Json;
namespace EvilIslands.DatabaseEditor.Core;

public sealed record FootContact(string Part, int First, int Last, Vector3 Anchor);
public sealed record StoredAnimationPose(string Name, Dictionary<string, Quaternion> Rotations, Dictionary<string, Vector3> Positions);
public sealed record MotionTransfer(int Version, string Model, string Clip, byte[] Anm, Dictionary<string,string> Parents, Dictionary<string,Vector3> Bones);
public sealed record AnimationIssue(int Frame, string Part, string Message) { public override string ToString() => $"{Frame} · {Part} · {Message}"; }
public enum MotionEase { Linear, Smooth, EaseIn, EaseOut, Hold }
public sealed partial class FigureAnimationEditor
{
    public async Task NavigateHistoryAsync(string name,bool redo)
    {
        var history=redo?_redo:_undo;
        if(history.TryPeek(out var bytes)&&Contacts.Count>0)
        {
            var target=await _source.WithBytes(bytes).LoadAsync(name);
            if(Contacts.Any(c=>c.First<0||c.Last>=target.FrameCount))throw new ArgumentException("Сначала уберите контакты, выходящие за длину клипа после отмены/повтора.");
        }
        if(redo)Redo();else Undo();
    }
    public bool MotionLimits { get; set; }
    public IReadOnlyDictionary<string,Vector3> MotionPins {get;set;}=new Dictionary<string,Vector3>();
    private static Vector3 PositionAt(FigureAnimationTrack track,float frame,Vector3 fallback)=>track.Positions.Length>0?track.Position(frame):fallback;
    public IReadOnlyList<FootContact> Contacts { get; set; } = Array.Empty<FootContact>();
    public static float Ease(float t, MotionEase ease) => ease switch { MotionEase.Smooth => t*t*(3-2*t), MotionEase.EaseIn => t*t, MotionEase.EaseOut => 1-(1-t)*(1-t), MotionEase.Hold => t < 1 ? 0 : 1, _ => t };
    public static float TwistDegrees(Quaternion q, int axis)
    {
        float c = axis == 0 ? q.X : axis == 1 ? q.Y : q.Z;
        float angle = 2 * MathF.Atan2(c,q.W) * 180 / MathF.PI;
        return (angle+540)%360-180;
    }
    public static Quaternion SetTwist(Quaternion q, int axis, float degrees)
    {
        Vector3 a = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var old = Quaternion.CreateFromAxisAngle(a,TwistDegrees(q,axis)*MathF.PI/180);
        return Quaternion.Normalize(q * Quaternion.Inverse(old) * Quaternion.CreateFromAxisAngle(a,degrees*MathF.PI/180));
    }
    private async Task WriteMotion(string name, Dictionary<string, Dictionary<int, (Quaternion? Q,Vector3? P)>> edits, Dictionary<string,Dictionary<int,Vector3[]>>? morphEdits=null)
    {
        var set = Preview(); var clip = await set.LoadAsync(name); var raw = RawTracks(name);
        foreach (int f in edits.Values.SelectMany(v=>v.Keys).Distinct().ToArray())
        {
            var pins = ContactPins(f);
            foreach(var pin in MotionPins)pins[pin.Key]=pin.Value;
            if (pins.Count == 0 && !MotionLimits) continue;
            var qs = edits.Where(p=>p.Value.TryGetValue(f,out var v)&&v.Q.HasValue).ToDictionary(p=>p.Key,p=>p.Value[f].Q!.Value);
            var ps = edits.Where(p=>p.Value.TryGetValue(f,out var v)&&v.P.HasValue).ToDictionary(p=>p.Key,p=>p.Value[f].P!.Value);
            AnimationPoseConstraints.Apply(set,clip,f,qs,ps,MotionLimits,pins);
            foreach(var q in qs) { if(!edits.ContainsKey(q.Key)) edits[q.Key]=new(); edits[q.Key][f]=(q.Value,ps.TryGetValue(q.Key,out var pos)?pos:null); }
        }
        var changed = new Dictionary<string,byte[]>();
        foreach(var part in edits)
        {
            if(!raw.TryGetValue(part.Key,out var bytes)) throw new ArgumentException("Неизвестная часть: "+part.Key);
            if(part.Value.Keys.Any(f=>f<0||f>=clip.FrameCount)) throw new ArgumentException("Кадр вне клипа.");
            var r=new RawTrack(bytes); using var ms=new MemoryStream(); using var w=new BinaryWriter(ms);
            bool rq=part.Value.Values.Any(v=>v.Q.HasValue), rp=part.Value.Values.Any(v=>v.P.HasValue);
            if(rp && set.Parents.GetValueOrDefault(part.Key)!="") throw new ArgumentException("Перемещение доступно только корню.");
            w.Write(rq?clip.FrameCount:r.Rotations);
            for(int f=0;f<(rq?clip.FrameCount:r.Rotations);f++)
                if(part.Value.TryGetValue(f,out var v)&&v.Q.HasValue) { var q=Quaternion.Normalize(v.Q.Value); w.Write(q.W);w.Write(q.X);w.Write(q.Y);w.Write(q.Z); }
                else if(r.Rotations>0) w.Write(r.Rotation,Math.Min(f,r.Rotations-1)*16,16); else {w.Write(1f);w.Write(0f);w.Write(0f);w.Write(0f);}
            w.Write(rp?clip.FrameCount:r.Positions);
            for(int f=0;f<(rp?clip.FrameCount:r.Positions);f++)
                if(part.Value.TryGetValue(f,out var v)&&v.P.HasValue) {var p=v.P.Value;w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}
                else if(r.Positions>0) w.Write(r.Position,Math.Min(f,r.Positions-1)*12,12); else {var p=set.Bones.GetValueOrDefault(part.Key);w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}
            if(morphEdits is not null && morphEdits.TryGetValue(part.Key,out var morphFrames) && r.Morphs>0)
            {
                w.Write(clip.FrameCount);w.Write(r.Vertices);
                for(int f=0;f<clip.FrameCount;f++)if(morphFrames.TryGetValue(f,out var vertices)){foreach(var v in vertices){w.Write(v.X);w.Write(v.Y);w.Write(v.Z);}}else w.Write(r.Morph,Math.Min(f,r.Morphs-1)*r.Vertices*12,r.Vertices*12);
            }
            else {w.Write(r.Morphs);if(!r.ShortFooter)w.Write(r.Vertices);w.Write(r.Morph);}
            changed[part.Key]=ms.ToArray();
        }
        await ReplaceTracks(name,changed);
    }
    public Dictionary<string,Vector3> ContactPins(int frame)
    {
        var pins=new Dictionary<string,Vector3>();
        foreach(var c in Contacts.Where(c=>frame>=c.First&&frame<=c.Last))
        { if(pins.TryGetValue(c.Part,out var p)&&Vector3.Distance(p,c.Anchor)>.00001f) throw new ArgumentException("Пересекаются разные контакты одной стопы."); pins[c.Part]=c.Anchor; }
        return pins;
    }
    public async Task MoveJointFrameAsync(string name,string part,int from,int to)
    {
        var clip=await Preview().LoadAsync(name);Range(clip,from,from);Range(clip,to,to);
        if(from==to)return;
        var track=clip.Tracks[part]; var original=await _source.LoadAsync(name);
        bool root=_source.Parents.GetValueOrDefault(part)=="";
        await WriteMotion(name,new(){[part]=new(){[to]=(track.Rotation(from),root?PositionAt(track,from,_source.Bones[part]):null),[from]=(original.Tracks[part].Rotation(from),root?PositionAt(original.Tracks[part],from,_source.Bones[part]):null)}});
    }
    public async Task CurveAsync(string name,string part,int first,int last,int axis,bool translation,float start,float end,MotionEase ease)
    {
        var clip=await Preview().LoadAsync(name);Range(clip,first,last);
        if(axis is <0 or >2 || !float.IsFinite(start)||!float.IsFinite(end)) throw new ArgumentException("Некорректная кривая.");
        var frames=new Dictionary<int,(Quaternion?,Vector3?)>();var track=clip.Tracks[part];
        for(int f=first;f<=last;f++)
        {
            float value=start+(end-start)*Ease(first==last?1:(f-first)/(float)(last-first),ease);
            if(translation){var p=track.Positions.Length>0?track.Position(f):_source.Bones[part];if(axis==0)p.X=value;else if(axis==1)p.Y=value;else p.Z=value;frames[f]=(null,p);}
            else frames[f]=(SetTwist(track.Rotation(f),axis,value),null);
        }
        await WriteMotion(name,new(){[part]=frames});
    }
    public async Task LoopAsync(string name,int blend)
    {
        var clip=await Preview().LoadAsync(name); if(blend<2||blend>clip.FrameCount)throw new ArgumentException("Переход: от 2 кадров до длины клипа.");
        var edits=new Dictionary<string,Dictionary<int,(Quaternion?,Vector3?)>>();
        var morphs=new Dictionary<string,Dictionary<int,Vector3[]>>();
        foreach(var p in clip.Tracks)
        {
            var frames=new Dictionary<int,(Quaternion?,Vector3?)>();bool root=_source.Parents.GetValueOrDefault(p.Key)=="";
            for(int f=clip.FrameCount-blend;f<clip.FrameCount;f++){float t=Ease((f-(clip.FrameCount-blend))/(float)(blend-1),MotionEase.Smooth);frames[f]=(Quaternion.Slerp(p.Value.Rotation(f),p.Value.Rotation(0),t),root?Vector3.Lerp(PositionAt(p.Value,f,_source.Bones[p.Key]),PositionAt(p.Value,0,_source.Bones[p.Key]),t):null);}
            if(p.Value.Morphs.Length>0){var rows=new Dictionary<int,Vector3[]>();for(int f=clip.FrameCount-blend;f<clip.FrameCount;f++){float t=Ease((f-(clip.FrameCount-blend))/(float)(blend-1),MotionEase.Smooth);rows[f]=Enumerable.Range(0,p.Value.Morphs[0].Length).Select(v=>Vector3.Lerp(p.Value.Morph(f,v),p.Value.Morph(0,v),t)).ToArray();}morphs[p.Key]=rows;}
            edits[p.Key]=frames;
        }
        await WriteMotion(name,edits,morphs);
    }
    public async Task<StoredAnimationPose> CapturePoseAsync(string name,int frame,string title)
    {
        var clip=await Preview().LoadAsync(name);Range(clip,frame,frame);
        return new(title,clip.Tracks.ToDictionary(p=>p.Key,p=>p.Value.Rotation(frame)),clip.Tracks.Where(p=>_source.Parents.GetValueOrDefault(p.Key)=="").ToDictionary(p=>p.Key,p=>p.Value.Positions.Length>0?p.Value.Position(frame):_source.Bones[p.Key]));
    }
    public async Task ApplyStoredPoseAsync(string name,int frame,StoredAnimationPose pose,IEnumerable<string> mask)
    {
        var edits=new Dictionary<string,Dictionary<int,(Quaternion?,Vector3?)>>();
        foreach(string p in mask.Distinct())if(pose.Rotations.TryGetValue(p,out var q))edits[p]=new(){[frame]=(q,pose.Positions.TryGetValue(p,out var v)?v:null)};
        if(edits.Count==0)throw new ArgumentException("Нет совместимых выбранных частей.");await WriteMotion(name,edits);
    }
    public async Task BakeContactsAsync(string name)
    {
        var clip=await Preview().LoadAsync(name);var edits=new Dictionary<string,Dictionary<int,(Quaternion?,Vector3?)>>();
        foreach(var contact in Contacts){Range(clip,contact.First,contact.Last);if(!FigureLimbIk.Supports(_source,clip,contact.Part))throw new ArgumentException("Нет цепочки стопы.");for(int f=contact.First;f<=contact.Last;f++){if(!edits.ContainsKey(contact.Part))edits[contact.Part]=new();edits[contact.Part][f]=(clip.Tracks[contact.Part].Rotation(f),null);}}
        await WriteMotion(name,edits);
    }
    private static byte[] EncodeTrack(FigureAnimationTrack t)
    {
        using var ms=new MemoryStream();using var w=new BinaryWriter(ms);w.Write(t.Rotations.Length);foreach(var q in t.Rotations){w.Write(q.W);w.Write(q.X);w.Write(q.Y);w.Write(q.Z);}w.Write(t.Positions.Length);foreach(var p in t.Positions){w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}w.Write(t.Morphs.Length);w.Write(t.Morphs.Length>0?t.Morphs[0].Length:0);foreach(var row in t.Morphs)foreach(var p in row){w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}return ms.ToArray();
    }
    public async Task ComposeAsync(string name,FigureAnimationClip incoming,int first,int last,int overlap)
    {
        var clip=await Preview().LoadAsync(name);Range(incoming,first,last);int length=last-first+1;
        if(overlap<0||overlap>Math.Min(clip.FrameCount,length)||clip.FrameCount+length-overlap>10000)throw new ArgumentException("Неверное перекрытие/длина.");
        if(!clip.Tracks.Keys.ToHashSet().SetEquals(incoming.Tracks.Keys))throw new ArgumentException("Для сборки нужны одинаковые дорожки; сначала выполните перенос.");
        var replacement=new Dictionary<string,byte[]>();int total=clip.FrameCount+length-overlap;
        foreach(var pair in clip.Tracks)
        {
            var a=pair.Value;var b=incoming.Tracks[pair.Key];int av=a.Morphs.Length>0?a.Morphs[0].Length:0,bv=b.Morphs.Length>0?b.Morphs[0].Length:0;
            if(av!=bv)throw new ArgumentException("Несовместимые деформации клипов.");
            if((long)av*total>8_000_000)throw new ArgumentException("Слишком много кадров деформации в объединённом клипе.");
            var qs=new Quaternion[total];var ps=a.Positions.Length+b.Positions.Length>0?new Vector3[total]:Array.Empty<Vector3>();var morph=av>0?new Vector3[total][]:Array.Empty<Vector3[]>();
            bool root=_source.Parents.GetValueOrDefault(pair.Key)=="";var offset=root?PositionAt(a,clip.FrameCount-1,_source.Bones.GetValueOrDefault(pair.Key))-PositionAt(b,first,_source.Bones.GetValueOrDefault(pair.Key)):Vector3.Zero;
            for(int f=0;f<total;f++)
            {
                int sourceFrame=first+Math.Max(0,f-(clip.FrameCount-overlap));float t=f<clip.FrameCount-overlap?0:f>=clip.FrameCount?1:Ease(overlap<=1?1:(f-(clip.FrameCount-overlap))/(float)(overlap-1),MotionEase.Smooth);
                qs[f]=Quaternion.Slerp(a.Rotation(f),b.Rotation(sourceFrame),t);if(ps.Length>0)ps[f]=Vector3.Lerp(PositionAt(a,f,_source.Bones.GetValueOrDefault(pair.Key)),PositionAt(b,sourceFrame,_source.Bones.GetValueOrDefault(pair.Key))+offset,t);
                if(av>0){morph[f]=new Vector3[av];for(int v=0;v<av;v++)morph[f][v]=Vector3.Lerp(a.Morph(f,v),b.Morph(sourceFrame,v),t);}
            }
            replacement[pair.Key]=EncodeTrack(new(qs,ps,morph));
        }
        if(Contacts.Count>0)throw new ArgumentException("Сначала снимите диапазоны контактов перед изменением длины клипа.");
        await ReplaceTracks(name,replacement);
    }
    public MotionTransfer ExportMotion(string model,string clip)=>new(1,model,clip,ExportBytes(),new(_source.Parents),new(_source.Bones));
    public static FigureAnimationSet ReadMotion(MotionTransfer motion)
    {
        if(motion.Version!=1||motion.Anm is null||motion.Parents is null||motion.Bones is null||motion.Parents.Count>4096||motion.Bones.Values.Any(v=>!float.IsFinite(v.LengthSquared())))throw new InvalidDataException("Некорректный пакет движения.");
        foreach(var p in motion.Parents){var seen=new HashSet<string>();string n=p.Key;while(n!=""){if(seen.Count>=128||!motion.Bones.ContainsKey(n)||!seen.Add(n)||!motion.Parents.TryGetValue(n,out n!))throw new InvalidDataException("Повреждена иерархия скелета.");}}
        return new(motion.Model,motion.Anm,motion.Parents,motion.Bones,motion.Bones);
    }
    public async Task RetargetAsync(string name,MotionTransfer motion,IReadOnlyDictionary<string,string> mapping)
    {
        var source=ReadMotion(motion);var input=await source.LoadAsync(motion.Clip);var clip=await Preview().LoadAsync(name);
        if(mapping.Count==0||mapping.Values.Distinct().Count()!=mapping.Count)throw new ArgumentException("Нужно однозначное сопоставление частей.");
        foreach(var m in mapping)
        {
            if(!clip.Tracks.ContainsKey(m.Key)||!input.Tracks.ContainsKey(m.Value))throw new ArgumentException("Не найдена часть сопоставления.");
            string tp=_source.Parents[m.Key],sp=source.Parents[m.Value];
            if((tp=="")!=(sp=="")||(tp!=""&&(!mapping.TryGetValue(tp,out var mp)||mp!=sp)))throw new ArgumentException("Сопоставьте также родителей: "+m.Key);
        }
        float targetLength=mapping.Keys.Sum(k=>_source.Bones[k].Length()),sourceLength=mapping.Values.Sum(k=>source.Bones[k].Length());float scale=sourceLength>1e-6f?targetLength/sourceLength:1;
        var edits=new Dictionary<string,Dictionary<int,(Quaternion?,Vector3?)>>();
        foreach(var m in mapping)
        {
            var frames=new Dictionary<int,(Quaternion?,Vector3?)>();var sourceBone=source.Bones[m.Value];var targetBone=_source.Bones[m.Key];
            // Compatible bind axes only: explicit rejection avoids an invented basis for dissimilar rigs.
            if(sourceBone.LengthSquared()>1e-8f&&targetBone.LengthSquared()>1e-8f&&Vector3.Dot(Vector3.Normalize(sourceBone),Vector3.Normalize(targetBone))<.98f)throw new ArgumentException("Различаются оси исходного скелета: "+m.Key+". Нужна корректировка базиса вне этого переноса.");
            for(int f=0;f<clip.FrameCount;f++){float sf=clip.FrameCount==1?0:f*(input.FrameCount-1f)/(clip.FrameCount-1);frames[f]=(input.Tracks[m.Value].Rotation(sf),_source.Parents[m.Key]==""?(PositionAt(input.Tracks[m.Value],sf,source.Bones[m.Value])-PositionAt(input.Tracks[m.Value],0,source.Bones[m.Value]))*scale+PositionAt(clip.Tracks[m.Key],0,_source.Bones[m.Key]):null);}
            edits[m.Key]=frames;
        }
        await WriteMotion(name,edits);
    }
    public async Task<IReadOnlyList<AnimationIssue>> DiagnoseAsync(string name)
    {
        var set=Preview();var clip=await set.LoadAsync(name);var issues=new List<AnimationIssue>();float size=Math.Max(.001f,set.Bones.Values.Sum(v=>v.Length())/15);
        void Add(int f,string p,string message){if(issues.Count<2000)issues.Add(new(f,p,message));}
        foreach(var part in clip.Tracks)
        {
            float Angle(Quaternion a,Quaternion b)=>2*MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a,b)),0,1))*180/MathF.PI;
            for(int f=1;f<clip.FrameCount;f++){float angle=Angle(part.Value.Rotation(f-1),part.Value.Rotation(f));if(angle>45)Add(f,part.Key,$"Поворот {angle:0}° за кадр (порог 45°)");if(set.Parents.GetValueOrDefault(part.Key)==""&&Vector3.Distance(part.Value.Position(f),part.Value.Position(f-1))>size*.5f)Add(f,part.Key,"Скачок корня > половины масштаба скелета");}
            if(set.Parents.GetValueOrDefault(part.Key)==""&&Vector3.Distance(part.Value.Position(0),part.Value.Position(clip.FrameCount-1))>size*.05f)Add(clip.FrameCount-1,part.Key,"Смещение корня между концом и началом цикла");
            if(Angle(part.Value.Rotation(0),part.Value.Rotation(clip.FrameCount-1))>10)Add(clip.FrameCount-1,part.Key,"Разрыв поворота в цикле >10°");
        }
        foreach(var contact in Contacts)for(int f=contact.First;f<=Math.Min(contact.Last,clip.FrameCount-1);f++)if(set.Pose(clip,f).TryGetValue(contact.Part,out var pose)&&Vector3.Distance(pose.Position,contact.Anchor)>size*.01f)Add(f,contact.Part,"Скольжение в отмеченном контакте");
        return issues;
    }
    public static byte[] SerializeMotion<T>(T data)=>JsonSerializer.SerializeToUtf8Bytes(data,ProjectJson);
    public static T DeserializeMotion<T>(byte[] bytes){if(bytes.Length>256*1024*1024)throw new InvalidDataException("Файл превышает 256 МиБ.");return JsonSerializer.Deserialize<T>(bytes,ProjectJson)??throw new InvalidDataException("Пустой файл.");}
}

