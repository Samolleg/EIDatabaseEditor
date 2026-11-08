using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PortableResourcesRegression
{
    public static int Run(string root)
    {
        string database = Path.Combine(root, "MODS", "Dejavu", "res", "database.res");
        var sources = TextureSources.FromDatabaseFolderAsync(database).GetAwaiter().GetResult();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check(sources.GameFolder == Path.Combine(root, "Base"), "Portable data root");
        Check(sources.ModProfile && sources.ConfigurationPath == Path.Combine(root, "MODS", "Dejavu", "config.ini"), "Mod profile");
        Check(sources.Archives[0] == Path.Combine(root, "MODS", "Dejavu", "res", "Spells.x2.res"), "Override order");
        Check(sources.Archives.Contains(Path.Combine(root, "Base", "Res", "textures.res")), "Base textures");
        Check(sources.Archives.All(p => p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Foreign installation resource");
        var figures = UnitModelPreview.FigureArchives(database, sources).ToArray();
        Check(figures.Last() == Path.Combine(root, "Base", "Res", "figures.res"), "Base figures");
        Check(figures.All(File.Exists), "Missing figure archive");
        var catalog = TextureResourceCatalog.OpenAsync(sources).GetAwaiter().GetResult();
        Check(catalog.Problems.Count == 0 && catalog.ResourceCount > 0, "Texture indexing");
        Check(DatabaseDocument.Open(database).Databases.Count > 0, "Database loading");
        Check(LuaDataWorkspace.FindNearby(database) == Path.Combine(root, "MODS", "Dejavu", "lua"), "Lua discovery");
        Check(DatabaseSafetyPolicy.IsLikelyGameInstallationPath(database), "Game safety policy");
        Console.WriteLine($"PASS portable profile, override priority, base figures/textures, DB, Lua, safety; {catalog.ResourceCount} textures");

        string fixture = Path.Combine(Path.GetTempPath(), "dbe-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "Res"));
        File.WriteAllText(Path.Combine(fixture, "game.exe"), "fixture");
        string db = Path.Combine(fixture, "Res", "database.res");
        File.WriteAllText(Path.Combine(fixture, "config.ini"), "[RES]\ndatabase.res=Res\\database.res\ntextures.res=Res\\custom.res\n");
        var legacy = TextureSources.FromDatabaseFolderAsync(db).GetAwaiter().GetResult();
        Check(legacy.GameFolder == fixture && legacy.Archives[0] == Path.Combine(fixture, "Res", "custom.res"), "Legacy discovery regression");
        Console.WriteLine("PASS legacy installation discovery unchanged");
        return 0;
    }
}
