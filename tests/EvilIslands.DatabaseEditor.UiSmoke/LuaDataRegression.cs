using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class LuaDataRegression
{
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static void Fails<T>(Action action, string label) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected rejection: " + label); }
    public static int Run(string baseline, string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Synthetic(output);
            string working = Path.Combine(output, "working"); Directory.CreateDirectory(Path.Combine(working, "lua"));
            var paths = new[] { "main.lua", "lua/tables.lua", "lua/npc_consumables.lua", "lua/npc_consumables_catalog.lua" };
            var baselineHashes = paths.ToDictionary(p => p, p => Hash(Path.Combine(baseline, p)));
            foreach (string path in paths) File.Copy(Path.Combine(baseline, path), Path.Combine(working, path), false);
            var workspace = LuaDataWorkspace.Open(working);
            Check(workspace.Documents.Count == 2, "Two Lua documents required: " + string.Join(";", workspace.Problems));
            foreach (var doc in workspace.Documents) foreach (var table in doc.Tables) Check(table.Value.Error is null, table.Key + ": " + table.Value.Error);
            Check(!workspace.HasChanges, "No-op must be byte exact");
            List<object> report = [];
            foreach (var spec in LuaDataWorkspace.Bindings)
            {
                var doc = workspace.Documents.Single(d => Path.GetFileName(d.Path) == spec.FileName);
                Check(doc.Tables.ContainsKey(spec.Name), "Missing assignment " + spec.Name);
                var table = doc.Tables[spec.Name].Value!; if (spec.Child is not null) table = table.Field(spec.Child)!.Value;
                Check(table.Fields.Count > 0, "Empty known table " + spec.Name);
                var first = table.Fields.First(); string key = spec.IsArray ? first.Value.Field("#1")!.Value.Text : first.Key;
                string dbTable = spec.Category switch { "unit" => "units.prototypes", "spell" => "spells.prototypes", "item" => "items.weapons", _ => "items." + spec.Category };
                var record = new ModelRecord(dbTable, spec.Category == "spell" ? "Display name differs from code" : key, new Dictionary<int, string> { [1] = key }, new Dictionary<int, string[]>());
                var group = workspace.ForRecord(record).Single(g => g.Spec.Name == spec.Name);
                Check(group.Entries.Count > 0, "Not linked " + spec.Name + " / " + key);
                var entry = group.Entries.First(); var scalar = spec.Name == "NpcCombatConsumables" ? entry.Field.Value.Fields.First().Value.Field("charges")!.Value
                    : spec.IsArray ? entry.Field.Value.Field("#2")!.Value
                    : spec.Name == "DeathFearExcludedPrototypes" ? entry.Field.Value : entry.Field.Value.Field("displayName")!.Value;
                string replacement = scalar.Kind switch { LuaLiteralKind.Boolean => scalar.Text == "true" ? "false" : "true", LuaLiteralKind.String => LuaLiteralReader.Quote(scalar.Text + " test"), _ => scalar.Text == "2" ? "3" : "2" };
                string old = doc.Text;
                var oldFieldCount = table.Fields.Count;
                workspace.Edit(entry, [new(scalar.Start, scalar.End - scalar.Start, replacement)], "target regression");
                string expected = old.Remove(scalar.Start, scalar.End - scalar.Start).Insert(scalar.Start, replacement);
                Check(doc.Text == expected, "Non-target text changed " + spec.Name);
                workspace.Undo(doc); Check(doc.Text == old, "Undo " + spec.Name);
                workspace.Redo(doc); Check(doc.Text == expected, "Redo " + spec.Name);
                Fails<InvalidOperationException>(() => workspace.Remove(entry), "stale entry");
                report.Add(new { spec.Name, Rows = oldFieldCount, Key = key, LinkedRows = group.Entries.Count, ModifiedOnlyScalar = true });
            }
            foreach (var doc in workspace.Documents)
            {
                byte[] before = File.ReadAllBytes(doc.Path), after = doc.Bytes;
                string backup = workspace.Save(doc);
                Check(File.ReadAllBytes(backup).SequenceEqual(before), "Backup mismatch");
                Check(File.ReadAllBytes(doc.Path).SequenceEqual(after), "Save/read mismatch");
                Check(!doc.HasChanges && !doc.CanUndo, "Save must establish new baseline");
                var reopened = LuaSourceDocument.Open(doc.Path, doc.Tables.Keys.ToArray());
                Check(reopened.Tables.All(t => t.Value.Value is not null), "Round-trip parse failed");
            }
            AddRemove(workspace);
            Dialogs(workspace, output);
            AuditDatabase(workspace, Path.Combine(baseline, "res", "database.res"), output);
            foreach (var path in paths) Check(Hash(Path.Combine(baseline, path)) == baselineHashes[path], "Baseline modified " + path);
            Check(Hash(Path.Combine(working, "main.lua")) == baselineHashes["main.lua"] && Hash(Path.Combine(working, "lua/npc_consumables_catalog.lua")) == baselineHashes["lua/npc_consumables_catalog.lua"], "Unrelated sources changed");
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Result = "PASS", Tables = report, BaselineHashes = baselineHashes, Checks = new[] { "12 real table bindings", "target-only scalar replacement", "source byte preservation", "backup/reopen", "undo/redo", "duplicate rows", "add/remove", "external conflicts", "UTF-8/BOM/CP1251/mixed newlines", "literal-only rejection", "WPF editor controls/render", "main/catalog untouched" } }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(); Console.WriteLine("PASS Lua bindings, targeted save, source preservation, undo, errors, WPF dialogs; live files never written"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Synthetic(string output)
    {
        string text = "-- русский комментарий\r\nlocal ignored = [=[ UnitStat = {evil()} ]=]\nfunction test() UnitStat = {42} end\r\nUnitStat = { {\"A\", 1, 2}, -- first\n{\"A\", -2.5e-1, 3}; } -- last\r\nlocal other = 42\n";
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), Encoding.GetEncoding(1251) })
        {
            string path = Path.Combine(output, "synthetic-" + encoding.WebName + "-" + encoding.GetPreamble().Length + ".lua");
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]; File.WriteAllBytes(path, bytes);
            var doc = LuaSourceDocument.Open(path, "UnitStat");
            Check(doc.Bytes.SequenceEqual(bytes) && !doc.HasChanges && doc.Save() == "", "No-op encoding");
            var root = doc.Tables["UnitStat"].Value!; Check(root.Fields.Count == 2, "Ignore comments/strings/function and retain duplicates");
            var value = root.Fields[1].Value.Field("#2")!.Value;
            doc.Apply([new(value.Start, value.End - value.Start, "0.375")], "number");
            Check(doc.Text == text.Replace("-2.5e-1", "0.375", StringComparison.Ordinal), "Exact patch encoding");
            string backup = doc.Save(); Check(File.ReadAllBytes(backup).SequenceEqual(bytes), "Backup encoding");
            doc.Apply([new(0, 0, "-- editor\n")], "conflict");
            File.AppendAllText(path, "-- external\n", encoding); byte[] external = File.ReadAllBytes(path);
            Fails<IOException>(() => doc.Save(), "external writer"); Check(File.ReadAllBytes(path).SequenceEqual(external), "Conflict overwrote external edit");
            Check(!Directory.GetFiles(output, "*.tmp").Any(), "Leftover temporary file");
        }
        foreach (string bad in new[] { "{ fn() }", "{ 1 + 2 }", "{ [call()] = 3 }", "{ x = os.execute('bad') }", "{ a = 0/0 }" }) Fails<FormatException>(() => LuaLiteralReader.Read(bad), bad);
        Check(LuaLiteralReader.Read(LuaLiteralReader.Quote("quote\" \\ slash\nкириллица 1\0")).Text == "quote\" \\ slash\nкириллица 1\0", "String escaping");
        Check(LuaLiteralReader.Read("[=[\r\nbrace } -- string]=]").Text == "brace } -- string", "Long string");
        Check(LuaLiteralReader.Assignments("X = {}\nX = {}", ["X"])["X"].Value is null, "Duplicate assignments must be read-only");
        Fails<FormatException>(() => LuaLiteralReader.Scalar(LuaLiteralKind.Number, "NaN"), "NaN");
        Fails<FormatException>(() => LuaLiteralReader.Scalar(LuaLiteralKind.Number, "+1"), "Lua has no unary plus");
        foreach (string suffix in new[] { " * 2", " / 2", " ^ 2", " .. 'text'", "[1]", " { 1 }", " or {}", " == {}" })
            Check(LuaLiteralReader.Assignments("UnitStat = {}" + suffix, ["UnitStat"])["UnitStat"].Value is null, "Expression treated as table " + suffix);
        Fails<FormatException>(() => LuaLiteralReader.Scalar(LuaLiteralKind.Boolean, "1"), "flag type");
        foreach (string input in new[] { "{1}", "{a={}}", "{}", "{1 -- comment\r\n}", "{1;}", "{ 1, -- keep\n}" })
        {
            string after = LuaEntryEditorDialog.ApplyPatches(input, LuaSourceDocument.AppendTo(input, LuaLiteralReader.Read(input), "value = 2"));
            var parsed = LuaLiteralReader.Read(after); Check(parsed.Field("value")!.Value.Text == "2", "Append separators " + input);
        }
    }
    private static ModelRecord Unit(string name) => new("units.prototypes", name, new Dictionary<int, string>(), new Dictionary<int, string[]>());
    private static void AddRemove(LuaDataWorkspace workspace)
    {
        var newRecord = Unit("Editor isolated regression");
        var death = workspace.ForRecord(newRecord).Single(g => g.Spec.Name == "DeathFearExcludedPrototypes");
        string before = death.Document!.Text; workspace.Add(death, "true");
        var added = workspace.ForRecord(newRecord).Single(g => g.Spec.Name == death.Spec.Name).Entries.Single();
        workspace.Remove(added); Check(workspace.ForRecord(newRecord).Single(g => g.Spec.Name == death.Spec.Name).Entries.Count == 0, "Remove newly added");
        workspace.Undo(death.Document); Check(workspace.ForRecord(newRecord).Single(g => g.Spec.Name == death.Spec.Name).Entries.Count == 1, "Undo remove");
        workspace.Undo(death.Document); Check(death.Document.Text == before && !death.Document.HasChanges, "Undo add restores exact text");
        var npc = workspace.ForRecord(newRecord).Single(g => g.Spec.Name == "NpcCombatConsumables");
        Fails<FormatException>(() => workspace.Add(npc, "{ healing={ formula=\"healing{e0}\", charges=8 } }"), "charge bounds");
        workspace.Add(npc, "{ healing={ formula=\"healing{e0}\", charges=3, registrationOnly=true } }");
        Check(workspace.ForRecord(newRecord).Single(g => g.Spec.Name == npc.Spec.Name).Entries.Single().Field.Value.Field("healing")!.Value.Field("registrationOnly")!.Value.Text == "true", "registrationOnly preserved");
        workspace.Undo(npc.Document!);
        var forbiddenRecord = new ModelRecord("items.weapons", "Editor isolated regression", new Dictionary<int, string>(), new Dictionary<int, string[]>());
        foreach (int shop in new[] { 1, 2, 0 }) workspace.Add(workspace.ForRecord(forbiddenRecord).Single(g => g.Spec.Name == "ForbiddenItems"), "{\"Editor isolated regression\", " + shop + "}");
        var rules = workspace.ForRecord(forbiddenRecord).Single(g => g.Spec.Name == "ForbiddenItems");
        Check(rules.Entries.Select(e => e.Field.Value.Field("#2")!.Value.Text).SequenceEqual(new[] { "1", "2", "0" }), "Shop row order/multiplicity");
        workspace.Discard();
    }
    private static void Dialogs(LuaDataWorkspace workspace, string output)
    {
        var npcTable = workspace.Documents.Single(d => Path.GetFileName(d.Path) == "npc_consumables.lua").Tables["NpcCombatConsumables"].Value!.Field("prototypes")!.Value;
        string name = npcTable.Fields.First().Key;
        var group = workspace.ForRecord(Unit(name)).Single(g => g.Spec.Name == "NpcCombatConsumables");
        var entry = group.Entries.First();
        string literal = entry.Document.Text[entry.Field.Value.Start..entry.Field.Value.End];
        var dialog = new LuaEntryEditorDialog(workspace, group, literal);
        Check(dialog.BuildResult() == literal, "UI no-op changed literal");
        var row = dialog.Rows.First(r => r.Key == "charges"); row.Value = "4";
        string edited = dialog.BuildResult(); Check(edited == literal.Remove(row.Original!.Start, row.Original.End - row.Original.Start).Insert(row.Original.Start, "4"), "UI changed surrounding text");
        var content = dialog.Content as FrameworkElement ?? throw new Exception("No dialog content");
        Render(content, 850, 660, Path.Combine(output, "lua-consumables.png"));
        var grid = Visuals<DataGrid>(content).Single(); VerifyCentered(grid);
        foreach (int column in new[] { 2, 1 })
        {
            grid.SelectedItem = row; grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[column]);
            Check(grid.BeginEdit(), "Lua cell did not enter edit mode"); grid.UpdateLayout();
            var cell = grid.Columns[column].GetCellContent(row);
            if (column == 2)
            {
                var editor = Visuals<TextBox>(cell).Single();
                Check(editor.TextAlignment == TextAlignment.Center && editor.VerticalContentAlignment == VerticalAlignment.Center, "Lua text editor alignment");
            }
            else
            {
                var editor = Visuals<ComboBox>(cell).Single();
                Check(editor.HorizontalContentAlignment == HorizontalAlignment.Center && editor.VerticalContentAlignment == VerticalAlignment.Center, "Lua type editor alignment");
            }
            grid.CancelEdit(DataGridEditingUnit.Cell);
        }
        dialog.Close();
        var fresh = new LuaEntryEditorDialog(workspace, group, "{}");
        Visuals<Button>((DependencyObject)fresh.Content).Single(b => Equals(b.Content, "Добавить расходник")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Fails<FormatException>(() => fresh.BuildResult(), "new NPC kind requires formula");
        fresh.Rows.Single(r => r.Key == "formula").Value = "healing{e0}";
        Check(LuaLiteralReader.Read(fresh.BuildResult()).Fields.Single().Value.Field("charges")!.Value.Text == "1", "new NPC kind fields");
        Visuals<Button>((DependencyObject)fresh.Content).Single(b => Equals(b.Content, "Удалить расходник")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(LuaLiteralReader.Read(fresh.BuildResult()).Fields.Count == 0, "remove NPC kind"); fresh.Close();
        var res = new ResFileManagerDialog(Path.Combine(output, "not-opened.res"), null, new EditorPreferencesStore(Path.Combine(output, "res-settings.json")));
        var files = (DataGrid)res.FindName("FilesGrid");
        files.ItemsSource = new[] { new { FileName = "1.wav", FolderPath = "sounds", FileType = "WAV", Size = 73000L, State = "Без изменений" } };
        Render((FrameworkElement)res.Content, 1250, 700, Path.Combine(output, "res-centered.png")); VerifyCentered(files); res.Close();
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "settings.json")), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        Check(window.FindName("LuaTab") is null && window.FindName("ShowLuaColumnsButton") is CheckBox, "Inline Lua toggle missing");
        var vm = (MainWindowViewModel)window.DataContext; vm.ConnectLua(workspace.DirectoryPath);
        var vmGroup = vm.LuaWorkspace!.ForRecord(Unit(name)).First(g => g.Spec.Name == "DeathFearExcludedPrototypes");
        if (vmGroup.Entries.Count == 0) vm.LuaWorkspace.Add(vmGroup, "true"); else vm.LuaWorkspace.Remove(vmGroup.Entries.First());
        Check(vm.HasPendingLuaChanges && !vm.HasUnsavedWork, "Separate Lua dirty state must not enter DB recovery/save");
        Fails<InvalidOperationException>(() => vm.ConnectLua(workspace.DirectoryPath), "reconnect loses staged changes");
        vm.LuaWorkspace.Discard(); Check(!vm.HasPendingLuaChanges, "Discard did not update VM"); window.Close();
    }
    private static void Render(FrameworkElement root, int width, int height, string path)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void VerifyCentered(DataGrid grid)
    {
        Check(grid.Items.Count > 0, "No rows to verify alignment");
        foreach (var column in grid.Columns.Where(c => c.Visibility == Visibility.Visible))
        {
            var text = Visuals<TextBlock>(column.GetCellContent(grid.Items[0])).First();
            Check(text.TextAlignment == TextAlignment.Center && text.VerticalAlignment == VerticalAlignment.Center && text.HorizontalAlignment == HorizontalAlignment.Stretch, "Cell alignment: " + column.Header);
        }
        foreach (var header in Visuals<DataGridColumnHeader>(grid).Where(h => h.Column is not null))
            Check(header.HorizontalContentAlignment == HorizontalAlignment.Center && header.VerticalContentAlignment == VerticalAlignment.Center, "Header alignment: " + header.Content);
    }

    private static void AuditDatabase(LuaDataWorkspace workspace, string path, string output)
    {
        string hash = Hash(path); var db = DatabaseDocument.Open(path);
        var tables = db.Databases.SelectMany(g => g.Tables).ToArray(); List<object> report = [];
        foreach (var spec in LuaDataWorkspace.Bindings)
        {
            var doc = workspace.Documents.Single(d => Path.GetFileName(d.Path) == spec.FileName);
            var literal = doc.Tables[spec.Name].Value!; if (spec.Child is not null) literal = literal.Field(spec.Child)!.Value;
            string Key(LuaField f) => spec.IsArray ? f.Value.Field("#1")!.Value.Text : f.Key;
            var applicable = tables.Where(t => spec.Supports(t.Key)).ToArray();
            var keys = applicable.SelectMany(t => t.Records.Select(r => spec.KeyFor(ModelRecord.Capture(t.Key, r)))).ToHashSet(StringComparer.Ordinal);
            var unknown = literal.Fields.Where(f => !keys.Contains(Key(f))).Select(Key).Distinct().ToArray();
            int linked = literal.Fields.Count - literal.Fields.Count(f => !keys.Contains(Key(f)));
            Check(linked > 0, "No real DB links for " + spec.Name);
            foreach (var f in literal.Fields) LuaDataWorkspace.Validate(spec, f.Value, Key(f));
            report.Add(new { spec.Name, LuaRows = literal.Fields.Count, LinkedRows = linked, Unmatched = unknown });
        }
        File.WriteAllText(Path.Combine(output, "db-links.json"), JsonSerializer.Serialize(new { DatabaseHash = hash, Tables = report }, new JsonSerializerOptions { WriteIndented = true }));
        var weaponTable = tables.Single(t => t.Key == "items.weapons");
        var weapon = weaponTable.Records.First(r => workspace.ForRecord(ModelRecord.Capture(weaponTable.Key, r)).Any(g => g.Spec.Name == "WeaponProp" && g.Entries.Count > 0));
        var vm = new MainWindowViewModel(); vm.ConnectLua(workspace.DirectoryPath);
        vm.SelectedRecord = new EvilIslands.DatabaseEditor.App.Models.RecordRowViewModel(weapon, weaponTable.Key, weaponTable.Title, false);
        var view = new LuaDataView { DataContext = vm }; view.Refresh();
        Check(view.Groups.Any(g => g.Spec.Name == "WeaponProp" && g.Entries.Count > 0), "UI selected weapon did not bind");
        Render(view, 1250, 580, Path.Combine(output, "lua-tab.png")); VerifyCentered(Visuals<DataGrid>(view).Single());
        var spellTable = tables.Single(t => t.Key == "spells.prototypes");
        var spell = spellTable.Records.First(r => ModelRecord.Capture(spellTable.Key, r).Text(1) == "powerhit");
        vm.SelectedRecord = new EvilIslands.DatabaseEditor.App.Models.RecordRowViewModel(spell, spellTable.Key, spellTable.Title, false); view.Refresh();
        Check(view.Groups.Single(g => g.Spec.Name == "MeleeActiveSpells").Entries.Count == 1, "UI spell code binding");
        var runeTable = tables.Single(t => t.Key == "spells.modifiers");
        var rune = runeTable.Records.First(r => ModelRecord.Capture(runeTable.Key, r).Text(1) == "e3");
        Check(workspace.ForRecord(ModelRecord.Capture(runeTable.Key, rune)).Single(g => g.Spec.Name == "ForbiddenItems").Entries.Count == 2, "Rune shop prohibitions by code and multiple shops");
        var group = workspace.ForRecord(ModelRecord.Capture(spellTable.Key, spell)).Single(g => g.Spec.Name == "MeleeActiveSpells");
        var entry = group.Entries.Single(); var dialog = new LuaEntryEditorDialog(workspace, group, entry.Document.Text[entry.Field.Value.Start..entry.Field.Value.End]);
        Render(dialog.Content as FrameworkElement ?? throw new Exception("No content"), 850, 660, Path.Combine(output, "lua-spell.png")); dialog.Close();
        Check(Hash(path) == hash, "DB changed during linking");
        File.WriteAllText(Path.Combine(output, "db-links.json"), JsonSerializer.Serialize(new { DatabaseHash = hash, Tables = report }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
