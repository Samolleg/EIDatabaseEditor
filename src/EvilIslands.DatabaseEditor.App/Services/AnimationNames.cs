using System.IO;
using System.Text.Json;
namespace EvilIslands.DatabaseEditor.App.Services;
internal sealed class AnimationNames
{
    private readonly string _path;
    public static AnimationNames Shared {get;}=new();
    public AnimationNames(string? path=null)=>_path=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Редактор БД от Атамана","animation-names.json");
    private Dictionary<string,string> Read()=>File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllBytes(_path))??new():new();
    public string Get(string model,string clip){try{return Read().GetValueOrDefault(model+"/"+clip,"");}catch(IOException){return "";}catch(JsonException){return "";}}
    public IReadOnlyDictionary<string,string> ForModel(string model)
    {
        try
        {
            string prefix=model+"/";
            return Read().Where(pair=>pair.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                .GroupBy(pair=>pair.Key[prefix.Length..],StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group=>group.Key,group=>group.Last().Value,StringComparer.OrdinalIgnoreCase);
        }
        catch(IOException){return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);}
        catch(JsonException){return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);}
    }
    public string Label(string model,string clip){var name=Get(model,clip);return clip+" — "+(name.Length==0?"название не задано":name);}
    public void Save(string model,string clip,string name)
    {
        if(name.Length>160)throw new ArgumentException("Название: не более160 символов.");
        var names=Read();string key=model+"/"+clip;
        if(string.IsNullOrWhiteSpace(name))names.Remove(key);else names[key]=name.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);string tmp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(tmp,JsonSerializer.SerializeToUtf8Bytes(names));if(File.Exists(_path))File.Replace(tmp,_path,_path+".bak");else File.Move(tmp,_path);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
}
