using System.Numerics;
using System.Text.RegularExpressions;
using System.Globalization;
namespace EvilIslands.DatabaseEditor.Core;
public sealed partial class FigureAnimationEditor
{
    public static string SuggestCopyId(string template, IEnumerable<string> existing)
    {
        string stem = Regex.Replace(template, "[^A-Za-z0-9_]", "_").Trim('_');
        if (stem.Length == 0) stem = "clip";
        if (!char.IsAsciiLetter(stem[0])) stem = "a" + stem;
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        for (int number = 1; number < int.MaxValue; number++)
        {
            string ending = number == 1 ? "_copy" : "_copy" + number.ToString(CultureInfo.InvariantCulture);
            if (ending.Length >= 15) break;
            string candidate = stem[..Math.Min(stem.Length, 15 - ending.Length)] + ending;
            if (!names.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Не удалось подобрать свободный игровой ID.");
    }

    public static void ValidateNewId(string name)
    {
        if (name is null || !Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,14}$"))
            throw new ArgumentException("Игровой ID: латинская буква, затем латиница, цифры или _, всего до 15 символов.");
    }

    public async Task CreateAnimationAsync(string template,string name,int frames,bool bindPose)
    {
        ValidateNewId(name);
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
                    _source.Parents.GetValueOrDefault(p.Key)==""
                        ? new[]{track.Positions.Length>0?track.Position(0):_source.Bones[p.Key]}
                        : Array.Empty<Vector3>(),
                    vertices>0?new[]{new Vector3[vertices]}:Array.Empty<Vector3[]>()));
            }
            else bytes=TransformTrack(p.Value,0,clip.FrameCount-1,frames);
            additions.Add(new(p.Key,bytes));
        }
        var archive=ResArchiveDocument.Parse("animation",_bytes);
        var nested=BuildClipArchive(additions);
        var result=ResArchiveWriter.Rebuild(archive,new Dictionary<int,byte[]>(),[new(name,nested)]);
        if((await _source.WithBytes(result).LoadAsync(name)).FrameCount!=frames)throw new InvalidDataException("Не совпала длина новой анимации.");
        Accept(result);
    }

    private static byte[] BuildClipArchive(IReadOnlyList<AddedResResource> tracks)
    {
        var empty=new byte[ResArchiveDocument.HeaderSize];
        BitConverter.GetBytes(ResArchiveDocument.Signature).CopyTo(empty,0);
        BitConverter.GetBytes(ResArchiveDocument.HeaderSize).CopyTo(empty,8);
        return ResArchiveWriter.Rebuild(ResArchiveDocument.Parse("empty clip",empty),new Dictionary<int,byte[]>(),tracks);
    }

    // Old editor versions wrote clip\part at the ANM root. Keep reading those
    // files, but convert only the selected clip when it is explicitly saved.
    public void NestFlatClip(string name)
    {
        var outer=ResArchiveDocument.Parse("animation",_bytes);
        if(outer.Entries.Any(e=>e.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))return;
        string prefix=name+"\\";
        var flat=outer.Entries.Where(e=>e.Name.Replace('/','\\').StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(flat.Length==0)throw new InvalidDataException("Клип не найден: "+name);
        var tracks=flat.Select(e=>new AddedResResource(e.Name.Replace('/','\\')[prefix.Length..],outer.GetResourceBytes(e).ToArray())).ToArray();
        foreach(var track in tracks)_=FigureAnimationTrack.Decode(track.Data);
        byte[] nested=BuildClipArchive(tracks);
        byte[] result=ResArchiveWriter.Rebuild(outer,new Dictionary<int,byte[]>(),[new(name,nested)],flat.Select(e=>e.Index).ToHashSet());
        var checkedOuter=ResArchiveDocument.Parse("animation",result);
        var checkedNested=ResArchiveDocument.Parse(name,checkedOuter.GetResourceBytes(name).ToArray());
        if(checkedNested.Entries.Count!=flat.Length)throw new InvalidDataException("Не все дорожки перенесены в клип.");
        Accept(result);
    }
}
