using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Immutable PAK snapshot; all codecs and writes belong to EIRebuild64Pack.</summary>
public sealed class PakArchive : IDisposable
{
    public const string UtilitySha256 = PakUtilityIdentity.Sha256;
    private readonly string _directory;
    private readonly string _snapshot;
    private readonly Dictionary<string, Row> _rows;
    private bool _disposed;
    private sealed record Row(string Name, string Hex, uint Method, long Size,string DiskPath);
    private string? _materialized;
    public IReadOnlyList<ResEntry> Entries { get; }
    public string Sha256 { get; }
    public long Length { get; }
    public IReadOnlyList<string> Deleted => _rows.Values.Where(r => r.Method == 2).Select(r => r.Name).ToArray();
    private static Encoding Legacy { get { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); } }

    public static bool IsPak(string path)
    {
        if(Path.GetExtension(path).Equals(".pak",StringComparison.OrdinalIgnoreCase))return true;
        if(!File.Exists(path))return false;
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        Span<byte> signature=stackalloc byte[8];return input.Read(signature)==8&&IsPak(signature);
    }
    public static bool IsPak(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual("EIPAK\r\n\0"u8);
    public static string LookupKey(string name)
    {
        byte[] bytes = Legacy.GetBytes(name);
        var special = new Dictionary<byte, byte> { [0x80]=0x90,[0x81]=0x83,[0x8a]=0x9a,[0x8c]=0x9c,[0x8d]=0x9d,[0x8e]=0x9e,[0x8f]=0x9f,[0xa1]=0xa2,[0xa3]=0xbc,[0xa5]=0xb4,[0xa8]=0xb8,[0xaa]=0xba,[0xaf]=0xbf,[0xb2]=0xb3,[0xbd]=0xbe };
        for (int i=0;i<bytes.Length;i++) { byte b=bytes[i]; bytes[i]=(b>=0x41&&b<=0x5a)||(b>=0xc0&&b<=0xdf)?(byte)(b+32):special.GetValueOrDefault(b,b); }
        return Convert.ToHexString(bytes);
    }
    public static PakArchive Open(string path, CancellationToken token = default)
    {
        RecoverFor(path);
        // A single shared-delete handle supplies both hash and bytes: replacement
        // while opening cannot combine metadata and payload from different files.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return new PakArchive(source, token);
    }
    internal static PakArchive FromBytes(byte[] bytes, CancellationToken token = default)
    { using var source = new MemoryStream(bytes, false); return new PakArchive(source, token); }
    private PakArchive(Stream source, CancellationToken token)
    {
        _directory = Path.Combine(Path.GetTempPath(), "ei-pak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory); _snapshot = Path.Combine(_directory,"snapshot.pak");
        try {
            using (var destination = File.Create(_snapshot)) source.CopyTo(destination);
            token.ThrowIfCancellationRequested();
            Length = new FileInfo(_snapshot).Length;
            using (var input=File.OpenRead(_snapshot)) Sha256=Convert.ToHexString(SHA256.HashData(input));
            using JsonDocument json=JsonDocument.Parse(Run(["list",_snapshot,"--json"],token));
            _rows=new Dictionary<string,Row>(StringComparer.Ordinal);
            foreach(var item in json.RootElement.EnumerateArray()) {
                string hex=item.GetProperty("name_hex").GetString()!;
                string name=Legacy.GetString(Convert.FromHexString(hex));
                var row=new Row(name,hex,item.GetProperty("method").GetUInt32(),item.GetProperty("size").GetInt64(),item.GetProperty("path").GetString()!);
                _rows.Add(LookupKey(name),row);
            }
            Entries=_rows.Values.Where(r=>r.Method!=2).Select((r,i)=>new ResEntry(i,r.Name,0,checked((int)r.Size),0,checked((ushort)(r.Hex.Length/2)),0)).ToArray();
        } catch { Dispose(); throw; }
    }
    public byte[] Read(string name, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!_rows.TryGetValue(LookupKey(name),out var row)||row.Method==2)throw new KeyNotFoundException(name);
        if(_materialized is not null)return File.ReadAllBytes(Path.Combine(_materialized,row.DiskPath.Replace('/',Path.DirectorySeparatorChar)));
        // Unique extraction paths make parallel preview requests independent.
        string output=Path.Combine(_directory,Guid.NewGuid().ToString("N"));
        try { Run(["extract",_snapshot,row.Name,output],token); return File.ReadAllBytes(output); }
        finally { if(File.Exists(output))File.Delete(output); }
    }
    public static byte[] Read(string path,string name,CancellationToken token=default)
    { using var archive=Open(path,token);return archive.Read(name,token); }
    public static IReadOnlyList<string> DeletedNames(string path,CancellationToken token=default)
    { using var archive=Open(path,token);return archive.Deleted; }
    internal byte[] OriginalBytes() => File.ReadAllBytes(_snapshot);
    internal void Materialize()
    {
        if(_materialized is not null)return;
        string folder=Path.Combine(_directory,"unpacked");Run(["unpack",_snapshot,folder],CancellationToken.None);_materialized=folder;
    }
    internal byte[] Rebuild(IReadOnlyDictionary<int,byte[]> replacements,IReadOnlyList<AddedResResource> additions,IReadOnlySet<int>? removals)
    {
        string target=Path.Combine(_directory,Guid.NewGuid().ToString("N")+".pak");
        File.Copy(_snapshot,target);
        var changes=new Dictionary<string,byte[]?>();
        foreach(var entry in Entries) {
            if(removals?.Contains(entry.Index)==true)changes.Add(entry.Name,null);
            else if(replacements.TryGetValue(entry.Index,out var bytes))changes.Add(entry.Name,bytes);
        }
        foreach(var entry in additions)changes.Add(entry.Name,entry.Data);
        try { ApplyChanges([(target,Sha256,changes)]);return File.ReadAllBytes(target); }
        finally { if(File.Exists(target))File.Delete(target); }
    }
    public static void PublishImages(IReadOnlyList<(string Path,string? ExpectedHash,byte[] Bytes)> images)
        => PublishMixedImages(images);
    public static void ApplyChanges(IReadOnlyList<(string Path,string? ExpectedHash,Dictionary<string,byte[]?> Changes)> archives)
        => ApplyChanges(archives,[]);

    public static void PublishMixedImages(IReadOnlyList<(string Path,string? ExpectedHash,byte[] Bytes)> images)
    {
        // Every image was serialized and verified by the native utility. Its
        // files transaction verifies .pak payloads again, replaces exact bytes,
        // and shares one recovery manifest with loose MOB/script participants.
        ApplyChanges([],images);
    }

    internal static byte[] MergeImages(string path,string expectedHash,IReadOnlyList<byte[]> images)
    {
        using var baseline=Open(path);baseline.Materialize();
        if(!baseline.Sha256.Equals(expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Shared archive changed since opening.");
        var original=baseline.Entries.ToDictionary(e=>LookupKey(e.Name),e=>baseline.Read(e.Name));
        var edits=new Dictionary<string,(string Name,byte[]? Bytes)>(StringComparer.Ordinal);
        foreach(var image in images){
            using var prepared=FromBytes(image);prepared.Materialize();
            var candidates=prepared.Entries.Select(e=>(e.Name,Bytes:(byte[]?)prepared.Read(e.Name))).Concat(prepared.Deleted.Select(n=>(Name:n,Bytes:(byte[]?)null)));
            foreach(var row in candidates){
                string key=LookupKey(row.Name);
                if(row.Bytes is null ? baseline.Deleted.Any(n=>LookupKey(n)==key) : original.TryGetValue(key,out var before)&&before.AsSpan().SequenceEqual(row.Bytes))continue;
                if(edits.TryGetValue(key,out var prior)&&!(prior.Bytes is null&&row.Bytes is null||prior.Bytes is not null&&row.Bytes is not null&&prior.Bytes.AsSpan().SequenceEqual(row.Bytes)))throw new IOException("Conflicting edits to shared archive resource: "+row.Name);
                edits[key]=row;
            }
        }
        string temporary=Path.Combine(baseline._directory,"merged.pak");File.Copy(baseline._snapshot,temporary);
        ApplyChanges([(temporary,baseline.Sha256,edits.Values.ToDictionary(e=>e.Name,e=>e.Bytes))]);
        return File.ReadAllBytes(temporary);
    }
    private static void ApplyChanges(IReadOnlyList<(string Path,string? ExpectedHash,Dictionary<string,byte[]?> Changes)> archives,
        IReadOnlyList<(string Path,string? ExpectedHash,byte[] Bytes)> files)
    {
        string temp=Path.Combine(Path.GetTempPath(),"ei-pak-write-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var group=new List<object>();int number=0;
            foreach(var archive in archives) {
                if(!IsPak(archive.Path)||DatabaseSafetyPolicy.ContainsReparsePoint(archive.Path))throw new IOException("PAK target must be a regular .pak path.");
                var rows=new List<object>();
                foreach(var change in archive.Changes) {
                    string hex=Convert.ToHexString(Legacy.GetBytes(change.Key));
                    if(change.Value is null)rows.Add(new { op="delete",name_hex=hex });
                    else {string file=Path.Combine(temp,(number++)+".payload");File.WriteAllBytes(file,change.Value);rows.Add(new { op="put",name_hex=hex,file });}
                }
                group.Add(new { pak=Path.GetFullPath(archive.Path),expected_sha256=archive.ExpectedHash?.ToLowerInvariant(),changes=rows });
            }
            var loose=new List<object>();
            foreach(var item in files){
                if(DatabaseSafetyPolicy.ContainsReparsePoint(item.Path))throw new IOException("Unsafe loose target.");
                string payload=Path.Combine(temp,(number++)+".payload");File.WriteAllBytes(payload,item.Bytes);
                loose.Add(new { target=Path.GetFullPath(item.Path),file=payload,sha256=Convert.ToHexString(SHA256.HashData(item.Bytes)).ToLowerInvariant(),expected_sha256=item.ExpectedHash?.ToLowerInvariant() });
            }
            string request=Path.Combine(temp,"transaction.json");File.WriteAllText(request,JsonSerializer.Serialize(new { archives=group,files=loose }),new UTF8Encoding(false));
            Run(["txn",request],CancellationToken.None);
        } finally { Directory.Delete(temp,true); }
    }
    public static void Recover(string folder) => Run(["recover",Path.GetFullPath(folder)],CancellationToken.None);
    public static void RecoverFor(string path)
    {
        for(var directory=Directory.GetParent(Path.GetFullPath(path));directory is not null;directory=directory.Parent)
            if(directory.EnumerateFiles("pak-txn-*.json",SearchOption.TopDirectoryOnly).Any())Recover(directory.FullName);
    }
    public static void Create(string path,Dictionary<string,byte[]> resources) => ApplyChanges([(path,null,resources.ToDictionary(p=>p.Key,p=>(byte[]?)p.Value))]);
    public static byte[] ConvertResImage(byte[] image)
    {
        if(IsPak(image))return image;
        var source=ResArchiveDocument.Parse("source.res",image);
        string directory=Path.Combine(Path.GetTempPath(),"ei-pak-convert-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {string target=Path.Combine(directory,"image.pak");Create(target,source.Entries.ToDictionary(e=>e.Name,e=>source.GetResourceBytes(e).ToArray()));return File.ReadAllBytes(target);}
        finally {Directory.Delete(directory,true);}
    }
    private static string Run(IReadOnlyList<string> args,CancellationToken token)
    {
        string utility=Path.Combine(AppContext.BaseDirectory,"EIRebuild64Pack.exe");
        if (!File.Exists(utility))
            throw new IOException("В комплекте редактора отсутствует EIRebuild64Pack.exe. Восстановите полный комплект редактора.");
        using (var file = File.OpenRead(utility))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(UtilitySha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Утилита PAK не соответствует этой сборке редактора. Восстановите полный комплект редактора, включая EIRebuild64Pack.exe.");
        var info=new ProcessStartInfo(utility){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Cannot start PAK utility.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        // Writes run to completion; cancellation must never interrupt a committed txn.
        process.WaitForExit();Task.WaitAll(stdout,stderr);
        if(process.ExitCode!=0)throw new IOException(stderr.Result+stdout.Result);
        token.ThrowIfCancellationRequested();return stdout.Result;
    }
    public void Dispose(){if(_disposed)return;_disposed=true;try{Directory.Delete(_directory,true);}catch(IOException){}catch(UnauthorizedAccessException){}GC.SuppressFinalize(this);}
    ~PakArchive(){Dispose();}
}
