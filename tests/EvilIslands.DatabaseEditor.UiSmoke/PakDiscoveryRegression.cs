using System.IO;
using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PakDiscoveryRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "dbe-pak-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            string mod = Path.Combine(root, "MODS", "Test");
            string res = Path.Combine(mod, "res");
            string baseline = Path.Combine(root, "Base", "Res");
            Directory.CreateDirectory(res); Directory.CreateDirectory(baseline);
            foreach (string name in new[] { "early.res", "late.res", "late.pak", "models.res", "models.pak", "database.pak" })
                File.WriteAllBytes(Path.Combine(res, name), []);
            File.WriteAllBytes(Path.Combine(baseline, "textures.res"), []);
            File.WriteAllBytes(Path.Combine(baseline, "textures.pak"), []);
            File.WriteAllBytes(Path.Combine(baseline, "figures.res"), []);
            File.WriteAllBytes(Path.Combine(baseline, "menus.res"), []);
            string config = Path.Combine(mod, "config.ini");
            File.WriteAllText(config, "[RES]\ndatabase.res=res\\database.res\ntextures.res=res\\early.res\ntextures.res=res\\late.res\nfigures.res=res\\models.res\n", Encoding.ASCII);
            var sources = TextureSources.FromGame(root, config, ["textures.res"]);
            Require(sources.Archives.SequenceEqual(new[] { Path.Combine(res, "late.pak"), Path.Combine(res, "early.res"), Path.Combine(baseline, "textures.res") }), "PAK tier or base RES order");
            Require(!sources.Archives.Contains(Path.Combine(res, "late.res")), "Shadowed RES included");
            var figures = UnitModelPreview.FigureArchives(Path.Combine(res, "database.pak"), sources).ToArray();
            Require(figures.SequenceEqual(new[] { Path.Combine(res, "models.pak"), Path.Combine(baseline, "figures.res"),Path.Combine(baseline,"menus.res") }), "Figure-kind provider order");
            var textureKinds=TextureSources.FromGame(root,config).Archives;
            Require(!textureKinds.Contains(Path.Combine(res,"models.pak"))&&textureKinds.ToList().IndexOf(Path.Combine(baseline,"textures.res"))<textureKinds.ToList().IndexOf(Path.Combine(baseline,"menus.res")),"Archive kind or texture/menu priority");
            File.AppendAllText(config, "textures.res=-\nfigures.res=-\nmenus.res=-\n");
            sources = TextureSources.FromGame(root, config, ["textures.res"]);
            Require(sources.Archives.Count == 2 && sources.Archives.All(p => p.StartsWith(mod)), "Ignore-base textures");
            Require(UnitModelPreview.FigureArchives(Path.Combine(res, "database.pak"), sources).SequenceEqual(new[] { Path.Combine(res, "models.pak") }), "Ignore-base figures");
            Require(ResourceArchivePaths.Enumerate(res).Count(p => Path.GetFileName(p).StartsWith("late.")) == 1, "Directory sibling suppression");
            byte[] empty=new byte[16];BinaryPrimitives.WriteUInt32LittleEndian(empty,ResArchiveDocument.Signature);BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8),16);
            foreach(string path in Directory.EnumerateFiles(res,"*.res"))File.WriteAllBytes(path,empty);
            byte[] mmp=new byte[80];BinaryPrimitives.WriteUInt32LittleEndian(mmp,0x00504d4d);BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(4),1);BinaryPrimitives.WriteUInt32LittleEndian(mmp.AsSpan(8),1);
            string late=Path.Combine(res,"late.pak");File.Delete(late);PakArchive.Create(late,new(){{"sample.mmp",mmp}});
            string modelPak=Path.Combine(res,"models.pak");File.Delete(modelPak);PakArchive.Create(modelPak,new(){{"model.mod",[1]}});
            string looseTexture=Path.Combine(mod,"sample.mmp"),looseModel=Path.Combine(mod,"model.mod");File.WriteAllBytes(looseTexture,mmp);File.WriteAllBytes(looseModel,[7]);
            Require(ResourceArchivePaths.FindLoose(config,"model.mod")==looseModel,"loose mod-root selection");
            PakArchive.ApplyChanges([(late,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(late))),new(){{"sample.mmp",null}})]);
            var catalog=TextureResourceCatalog.OpenAsync(sources).GetAwaiter().GetResult();Require(catalog.Check("sample.mmp").Source==looseTexture,"PAK tombstone hid higher loose texture");
            using(var session=ResourceValidationSession.OpenAsync(Path.Combine(res,"database.pak"),sources,CancellationToken.None).GetAwaiter().GetResult())Require(session.Read("model.mod",CancellationToken.None).SequenceEqual(new byte[]{7}),"model preview ignored loose override");
            var models=MapObjectResourceCatalog.DiscoverAsync(Path.Combine(res,"database.pak")).GetAwaiter().GetResult();Require(models.Single(m=>m.Model=="model").ArchivePath==mod,"map object catalog ignored loose source");
            Console.WriteLine("PASS PAK discovery: tier order, sibling suppression, base RES, logical config names, ignore-base");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
