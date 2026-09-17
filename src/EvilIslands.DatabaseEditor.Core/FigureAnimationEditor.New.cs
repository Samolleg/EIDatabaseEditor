using System.Numerics;
using System.Text.RegularExpressions;
namespace EvilIslands.DatabaseEditor.Core;
public sealed partial class FigureAnimationEditor
{
    public async Task CreateAnimationAsync(string template,string name,int frames,bool bindPose)
    {
        if(!Regex.IsMatch(name,"^[A-Za-z][A-Za-z0-9_-]{0,63}$"))throw new ArgumentException("ID: латинская буква, затем латиница, цифры, _ или -, до64 символов.");
        if(frames is <1 or >10000)throw new ArgumentException("Длина: от1 до10000 кадров.");
        if(Preview().Names.Contains(name,StringComparer.OrdinalIgnoreCase))throw new ArgumentException("Такой ID анимации уже существует.");
        var clip=await Preview().LoadAsync(template);var raw=RawTracks(template);
        var additions=new List<AddedResResource>();
        foreach(var p in raw)
        {
            byte[] bytes;
            if(bindPose)
            {
                var track=clip.Tracks[p.Key];int vertices=track.Morphs.Length>0?track.Morphs[0].Length:0;
                bytes=EncodeTrack(new FigureAnimationTrack(Enumerable.Repeat(Quaternion.Identity,frames).ToArray(),
                    _source.Parents.GetValueOrDefault(p.Key)==""?new[]{_source.Bones[p.Key]}:Array.Empty<Vector3>(),
                    vertices>0?new[]{new Vector3[vertices]}:Array.Empty<Vector3[]>()));
            }
            else bytes=TransformTrack(p.Value,0,clip.FrameCount-1,frames);
            additions.Add(new(name+"\\"+p.Key,bytes));
        }
        var archive=ResArchiveDocument.Parse("animation",_bytes);
        var result=ResArchiveWriter.Rebuild(archive,new Dictionary<int,byte[]>(),additions);
        if((await _source.WithBytes(result).LoadAsync(name)).FrameCount!=frames)throw new InvalidDataException("Не совпала длина новой анимации.");
        Accept(result);
    }
}
