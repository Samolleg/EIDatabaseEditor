using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class TextureRegression
{
    private static int _passed;
    private static readonly List<object> Evidence = [];
    internal static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("source database and new output folder required");
        string source = Path.GetFullPath(args[0]);
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Output folder must be new.");
        Directory.CreateDirectory(root);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task task = CheckAsync(source, root, args);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try
        {
            task.GetAwaiter().GetResult();
            Console.WriteLine($"Texture regressions PASS={_passed}, FAIL=0");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(Evidence, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pass(string name, object details)
    {
        _passed++;
        Evidence.Add(new { name, passed = true, details });
        Console.WriteLine("PASS " + name);
    }
    private static byte[] Mmp()
    {
        byte[] data = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x00504D4D);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x8888);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 32);
        return data;
    }
    private static void Res(string path, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] empty = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(empty, ResArchiveDocument.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 16);
        var archive = ResArchiveDocument.Parse("synthetic", empty);
        byte[] bytes = ResArchiveWriter.Rebuild(archive, new Dictionary<int, byte[]>(),
            entries.Select(entry => new AddedResResource(entry.Name, entry.Data)).ToArray());
        File.WriteAllBytes(path, bytes);
        _ = ResArchiveDocument.Open(path);
    }
    private static TextureRecord Record(string table, int index, string name, params (int Id, string Value)[] fields) =>
        new(table, index, name, fields.ToDictionary(x => x.Id, x => x.Value),
            new Dictionary<int, IReadOnlyList<string>>());

    private static async Task CheckAsync(string source, string root, string[] args)
    {
        string sourceHash = ResArchiveDocument.Open(source).Sha256;
        string game = Path.Combine(root, "game");
        string mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(mod);
        string first = Path.Combine(mod, "first.res");
        string later = Path.Combine(mod, "later.res");
        string config = Path.Combine(mod, "config.ini");
        Res(first, ("same.mmp", Mmp()), ("broken.mmp", [1, 2, 3]));
        Res(later, ("same.mmp", Mmp()), ("broken.mmp", Mmp()));
        Res(Path.Combine(game, "Res", "textures.res"), ("group.mmp", Mmp()));
        Res(Path.Combine(game, "Res", "redress.res"), ("redress.mmp", Mmp()));
        Res(Path.Combine(game, "Res", "menus.res"), ("menu.mmp", Mmp()));
        File.WriteAllText(config, "[RES]\ntextures.res=first.res\ntextures.res=later.res\nredress.res=later.res\n");
        var configured = TextureSources.FromGame(game, config);
        var catalog = await TextureResourceCatalog.OpenAsync(configured);
        Require(catalog.Complete, string.Join("\n", catalog.Problems));
        TextureCheck found = catalog.Check("SaMe.MMP");
        Require(found.State == TextureCheckState.Found && found.Source == first && found.Matches.Count == 2, "First-hit priority/case matching failed.");
        Require(catalog.Check("broken.mmp").State == TextureCheckState.Corrupt, "Corrupt winner must not fall back to a later version.");
        Require(catalog.Check("absent.mmp").State == TextureCheckState.Missing, "Complete sources must report absent resource.");
        Pass("configured-priority-and-corrupt-winner", new { resources = catalog.ResourceCount });
        File.WriteAllText(config, "[RES]\ntextures.res=later.res\ntextures.res=first.res\n");
        Require(!catalog.IsCurrent(), "Changed configuration must invalidate its catalogue.");
        var reordered = await TextureResourceCatalog.OpenAsync(TextureSources.FromGame(game, config));
        Require(reordered.IsCurrent() && reordered.Check("same.mmp").Source == later &&
            reordered.Check("broken.mmp").State == TextureCheckState.Found, "Reload must apply the new configuration order.");
        Pass("configuration-change-and-reload", new { reordered = true });

        var manual = await TextureResourceCatalog.OpenAsync(TextureSources.Manual([first]));
        Require(manual.Check("same.mmp").State == TextureCheckState.Incomplete &&
            manual.Check("absent.mmp").State == TextureCheckState.Incomplete, "Partial selection must not assert runtime winner or missing.");
        string unavailable = Path.Combine(root, "missing.res");
        var partial = await TextureResourceCatalog.OpenAsync(new([unavailable, first], [], true, false, []));
        Require(!partial.Complete && partial.Problems.Count == 1 && partial.Check("same.mmp").State == TextureCheckState.Incomplete, "Unavailable source must invalidate complete coverage.");
        File.SetLastWriteTimeUtc(first, File.GetLastWriteTimeUtc(first).AddMinutes(1));
        Require(!catalog.IsCurrent() && catalog.Check("same.mmp", catalog.IsCurrent()).State == TextureCheckState.Incomplete, "Changed archives must invalidate results.");
        Pass("partial-and-changed-sources", new { unavailableDetected = true });

        string alias = Path.Combine(root, "aliases.res");
        Res(alias, ("a.mmp", Mmp()), ("b.mmp", Mmp()));
        var original = ResArchiveDocument.Open(alias);
        byte[] aliased = File.ReadAllBytes(alias);
        ResEntry a = original.Entries.Single(e => e.Name == "a.mmp");
        ResEntry b = original.Entries.Single(e => e.Name == "b.mmp");
        BinaryPrimitives.WriteUInt32LittleEndian(aliased.AsSpan((int)b.DirectoryOffset + 8), (uint)a.Offset);
        File.WriteAllBytes(alias, aliased);
        var aliases = await TextureResourceCatalog.OpenAsync(new([alias], [], true, false, []));
        Require(aliases.Complete && aliases.Check("a.mmp").State == TextureCheckState.Found &&
            aliases.Check("b.mmp").State == TextureCheckState.Found, "Exact payload aliases must be readable.");
        string emptyRes = Path.Combine(root, "empty.res");
        Res(emptyRes);
        var emptyCatalog = await TextureResourceCatalog.OpenAsync(new([emptyRes], [], true, false, []));
        Require(emptyCatalog.Complete && emptyCatalog.ResourceCount == 0, "Empty RES is a valid empty catalog.");
        string loose = Path.Combine(root, "loose");
        Directory.CreateDirectory(loose);
        File.WriteAllBytes(Path.Combine(loose, "loose.mmp"), Mmp());
        var looseCatalog = await TextureResourceCatalog.OpenAsync(new([], [loose], true, false, []));
        Require(looseCatalog.Check("loose.mmp").State == TextureCheckState.Found, "Loose texture not found.");
        File.WriteAllBytes(Path.Combine(loose, "new.mmp"), Mmp());
        Directory.SetLastWriteTimeUtc(loose, Directory.GetLastWriteTimeUtc(loose).AddMinutes(1));
        Require(!looseCatalog.IsCurrent(), "New loose entries must invalidate catalog.");
        Pass("aliases-empty-and-loose", new { aliases = aliases.ResourceCount });

        var spell = Record("spells.prototypes", 0, "synthetic", (18, "7"));
        var material = Record("items.materials", 0, "material", (1, "metal"), (2, "me"));
        var weapon = Record("items.weapons", 0, "weapon", (2, "0"), (3, "metal"), (5, "2"), (6, "1"));
        var armor = Record("items.armors", 0, "armor", (2, "0"), (3, "metal"), (5, "2"), (6, "1"));
        var quick = Record("items.quick_items", 0, "quick", (3, "none"), (5, "9"));
        TextureRecord[] records = [spell, material, weapon, armor, quick];
        Require(DatabaseTextureReferences.Resolve(spell, records, false).Single().Name == "spell0007.mmp", "Spell rule differs from engine.");
        Require(DatabaseTextureReferences.Resolve(weapon, records, false).Single().Name == "unhumasw_02.me.1.mmp", "Weapon/material rule differs from engine.");
        Require(DatabaseTextureReferences.Resolve(armor, records, false).Single().Name == "unhumahl_02.me.1.mmp", "Armor/material rule differs from engine.");
        Require(DatabaseTextureReferences.Resolve(quick, records, false).Single().Name == "qitem0009.mmp", "Quick item rule differs from engine.");
        var variants = DatabaseTextureReferences.Resolve(spell, records, true);
        Require(variants.Count == 3 && variants.All(r => r.NeedsContext) && variants[1].Name == "spell0007.x2.mmp" && variants[2].Name == "spell0007.x4.mmp", "Addon variants must remain explicit/context-dependent.");
        Require(DatabaseTextureReferences.Resolve(Record("spells.prototypes", 1, "sentinel", (18, "2147483647")), records, false).Single().NeedsContext, "Sentinel must not become fabricated missing file.");
        var race = Record("units.races", 0, "race", (30, "mask")) with
        { Lists = new Dictionary<int, IReadOnlyList<string>> { [31] = ["skin"] } };
        var unit = Record("units.prototypes", 0, "unit", (1, "race"), (3, "3"));
        var units = DatabaseTextureReferences.Resolve(unit, [race, unit], false);
        Require(units.Any(r => r.Name == "skin.mmp") && units.Any(r => r.Name == "maskskin_03.mmp") && units.All(r => r.NeedsContext), "Creature layers must not pretend complete model coverage.");
        Pass("engine-reference-rules-and-context", new { variants = variants.Count, creatureReferences = units.Count });

        using var vm = new MainWindowViewModel();
        await vm.OpenPathAsync(source);
        Require(vm.HasDocument, "Reference database did not open.");
        var spells = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "spells.prototypes");
        vm.SelectTableCommand.Execute(spells);
        vm.SelectedRecord = spells.Records.First(r => r.CoreRecord.Fields.Any(f => f.Id == 18 && int.TryParse(f.EditorText, out int n) && n >= 0 && n < 1000));
        var field = vm.SelectedRecord.GetCell(18)!;
        int initial = int.Parse(field.EditorText, CultureInfo.InvariantCulture);
        string icons = Path.Combine(root, "icons.res");
        Res(icons, ($"spell{initial:D4}.mmp", Mmp()), ($"spell{initial + 1:D4}.mmp", Mmp()));
        await vm.ConnectTextureSourcesAsync(new([icons], [], true, false, []));
        await vm.RecheckTexturesAsync();
        Require(vm.TextureRows.Single().State == TextureCheckState.Found, "ViewModel must display resolved texture.");
        field.EditorText = (initial + 1).ToString(CultureInfo.InvariantCulture);
        await Task.Delay(350);
        await vm.RecheckTexturesAsync();
        Require(vm.TextureRows.Single().ResourceName == $"spell{initial + 1:D4}.mmp", "Edit must update reference.");
        vm.UndoCommand.Execute(null);
        await vm.RecheckTexturesAsync();
        Require(vm.TextureRows.Single().ResourceName == $"spell{initial:D4}.mmp", "Undo must restore reference.");
        vm.TextureShowAll = true;
        await vm.RecheckTexturesAsync();
        Require(vm.TextureRows.Count > 100 && vm.TextureRows.Any(r => r.State == TextureCheckState.Context), "Whole database check must include coverage gaps.");
        var target = vm.TextureRows.First(r => r.TableKey == "items.weapons");
        vm.NavigateToTexture(target);
        Require(vm.SelectedRecord?.TableKey == target.TableKey && vm.SelectedRecord.SourceRecordIndex == target.RecordIndex, "Result navigation failed.");
        Require(ResArchiveDocument.Open(source).Sha256 == sourceHash, "Input database was modified.");
        Pass("viewmodel-edit-undo-whole-database-navigation", new { rows = vm.TextureRows.Count, sourceUnchanged = true });

        if (args.Length >= 4)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var real = await TextureResourceCatalog.OpenAsync(TextureSources.FromGame(Path.GetFullPath(args[2]), Path.GetFullPath(args[3])));
            var summary = new { resources = real.ResourceCount, complete = real.Complete, problems = real.Problems, elapsedMs = watch.ElapsedMilliseconds };
            Evidence.Add(new { name = "real-resource-readonly-inventory", summary });
            Console.WriteLine(JsonSerializer.Serialize(summary));
            Require(real.ResourceCount > 0 && real.Complete && real.IsCurrent(), "Real configured catalog did not complete.");
            Pass("real-configured-catalog", new { real.ResourceCount });
        }
    }
}
