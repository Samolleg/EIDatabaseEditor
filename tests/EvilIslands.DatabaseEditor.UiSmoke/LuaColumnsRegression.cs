using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class LuaColumnsRegression
{
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    public static int Run(string baseline, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(baseline, output); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS inline Lua: full MaterialProp and UnitPerk columns, toggle, other schemas, centered editing, shared Undo/Redo/save; isolated files only"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Verify(string baseline, string output)
    {
        string copy = Path.GetFullPath(Path.Combine(output, "copy"));
        foreach (string name in new[] { "main.lua", "lua/tables.lua", "lua/npc_consumables.lua", "res/database.res" })
        { string target = Path.Combine(copy, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(baseline, name), target, false); }
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "settings.json")), new RecoverySnapshotStore(Path.Combine(output, "recovery")));
        var vm = (MainWindowViewModel)window.DataContext; vm.LinkedSaveJournalDirectory = Path.Combine(output, "journals"); await vm.OpenPathAsync(Path.Combine(copy, "res/database.res"));
        var grid = (DataGrid)window.FindName("RecordsGrid"); var toggle = (CheckBox)window.FindName("ShowLuaColumnsButton");
        grid.EnableColumnVirtualization = false;
        grid.EnableRowVirtualization = false;
        Check(window.FindName("LuaTab") is null, "Separate Lua tab remains");
        void Layout() { var root = (FrameworkElement)window.Content; root.Measure(new Size(1800, 1050)); root.Arrange(new Rect(0, 0, 1800, 1050)); root.UpdateLayout(); }
        string Header(DataGridColumn c) => c.Header as string ?? (c.Header as TextBlock)?.Text ?? "";
        void Select(string key) { vm.SelectTableCommand.Execute(vm.Databases.SelectMany(d => d.Tables).Single(t => t.Key == key)); Layout(); }
        Select("items.materials"); int originalColumns = grid.Columns.Count;
        Check(toggle.IsChecked != true, "Lua must start hidden"); toggle.IsChecked = true; Layout();
        Check(toggle.Parent == ((FrameworkElement)window.FindName("NewRowButton")).Parent && toggle.ActualWidth > 0, "Show Lua control is not in records toolbar");
        var materials = grid.Columns.Where(c => Header(c).StartsWith("Lua · Свойства материала")).ToArray();
        Check(materials.Length == 13, "MaterialProp requires all 13 numeric fields, got " + materials.Length);
        string[] expectedLabels = ["Регенерация", "Восстановление запаса сил", "Шанс блокирования урона", "Поглощение урона блокированием", "Шанс критического урона", "Размер критического урона", "Шанс парирования", "Скорость передвижения", "Бонус силы", "Бонус ловкости", "Бонус разума", "Заметность", "Слышимость"];
        Check(materials.Select(c => Header(c).Split('\n')[1]).SequenceEqual(expectedLabels), "Material labels/order differ from owner specification");
        Check(grid.Columns.Any(c => Header(c) == "Lua · Запрет размножения\nID магазина"), "Missing explicit shop prohibition header");
        var row = vm.SelectedTable!.Records.Single(r => r.CoreRecord.Name == "dragon red");
        var column = materials.Single(c => Header(c).Contains("Поглощение"));
        vm.SelectedRecord = row; grid.ScrollIntoView(row, column); Layout();
        var display = (TextBlock)column.GetCellContent(row);
        Check(display.Text == "5.34" && display.TextAlignment == TextAlignment.Center && display.VerticalAlignment == VerticalAlignment.Center, "Material value/alignment");
        var doc = vm.LuaWorkspace!.Documents.Single(d => Path.GetFileName(d.Path) == "tables.lua"); string before = doc.Text;
        grid.SelectedCells.Clear(); grid.CurrentCell = new DataGridCellInfo(row, column); grid.SelectedCells.Add(grid.CurrentCell);
        Check(window.TryBuildClipboardMatrix(out var copied) && copied.Count == 1 && copied[0][0] == "5.34", "Lua copy returned empty/wrong text");
        var payload = MainWindow.CreateSpreadsheetClipboardDataObject(copied);
        var parsed = SpreadsheetClipboardText.Parse((string)payload.GetData(DataFormats.UnicodeText));
        var other = vm.SelectedTable.Records.Single(r => r.CoreRecord.Name == "thin");
        grid.SelectedCells.Clear(); grid.CurrentCell = new DataGridCellInfo(other, column);
        grid.SelectedCells.Clear(); grid.SelectedCells.Add(grid.CurrentCell);
        Check(window.TryPasteLuaMatrix(parsed), "Lua paste not handled");
        grid.SelectedCells.Clear(); grid.CurrentCell = new DataGridCellInfo(other, column);
        bool built = window.TryBuildClipboardMatrix(out var pasted);
        Check(built && pasted[0][0] == "5.34", "Copied scalar not pasted: " + string.Join("/", pasted.SelectMany(r => r)) + " / docChanged=" + (doc.Text != before) + " / " + vm.NotificationMessage);
        vm.UndoCommand.Execute(null); Check(doc.Text == before, "Lua paste Undo");
        vm.RedoCommand.Execute(null); Check(doc.Text != before, "Lua paste Redo"); vm.UndoCommand.Execute(null);
        grid.CurrentCell = new DataGridCellInfo(row, column);
        Check(window.TryPasteLuaMatrix(new IReadOnlyList<string>[] { new[] { "5.36" }, new[] { "0,25" } }), "Invalid range not handled");
        Check(doc.Text == before, "Invalid second value left partial Lua changes");
        Check(window.TryPasteLuaMatrix(new IReadOnlyList<string>[] { new[] { "5.36" }, new[] { "5.37" } }), "Range paste not handled");
        Check(doc.Text != before, "Range unchanged"); vm.UndoCommand.Execute(null); Check(doc.Text == before, "Range not undone as one operation");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
        grid.SelectedItem = row; grid.CurrentCell = new DataGridCellInfo(row, column);
        Check(grid.BeginEdit(), "Inline Lua editing unavailable"); Layout();
        var edit = (TextBox)column.GetCellContent(row); Check(edit.TextAlignment == TextAlignment.Center, "Editor alignment"); edit.Text = "5.35";
        Check(grid.CommitEdit(DataGridEditingUnit.Cell, true), "Cell rejected valid number"); grid.CommitEdit(DataGridEditingUnit.Row, true);
        Check(doc.Text != before && vm.HasPendingLuaChanges, "Lua was not staged"); string changed = doc.Text;
        Check(changed == before.Replace("0, 4, 1, 5.34, 1, 0, 1.75", "0, 4, 1, 5.35, 1, 0, 1.75", StringComparison.Ordinal), "Changed unrelated source text");
        vm.UndoCommand.Execute(null); Check(doc.Text == before, "Shared Undo"); vm.RedoCommand.Execute(null); Check(doc.Text == changed, "Shared Redo");
        var shopColumn = grid.Columns.Single(c => Header(c) == "Lua · Запрет размножения\nID магазина");
        async Task ShopEdit(string value, bool valid)
        {
            grid.CurrentCell = new DataGridCellInfo(row, shopColumn); Layout();
            Check(grid.BeginEdit(), "Shop edit unavailable"); Layout();
            ((TextBox)shopColumn.GetCellContent(row)).Text = value;
            bool committed = grid.CommitEdit(DataGridEditingUnit.Cell, true);
            Check(committed == valid, "Unexpected shop validation: " + value);
            if (valid) grid.CommitEdit(DataGridEditingUnit.Row, true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (!valid) { grid.CancelEdit(DataGridEditingUnit.Cell); grid.CancelEdit(DataGridEditingUnit.Row); }
        }
        int[] Shops() => vm.LuaWorkspace!.ForRecord(EvilIslands.DatabaseEditor.Core.ModelRecord.Capture(row.TableKey, row.CoreRecord))
            .Single(g => g.Spec.Name == "ForbiddenItems").Entries.Select(e => int.Parse(e.Field.Value.Field("#2")!.Value.Text)).ToArray();
        await ShopEdit("1", true); Check(Shops().SequenceEqual(new[] { 1 }), "Add single shop");
        string singleShop = doc.Text;
        await ShopEdit("1; 3", true); Check(Shops().SequenceEqual(new[] { 1, 3 }), "Add second shop");
        vm.UndoCommand.Execute(null); Check(doc.Text == singleShop, "Multi-shop Undo"); vm.RedoCommand.Execute(null);
        string twoShops = doc.Text;
        await ShopEdit("1,3", false); Check(doc.Text == twoShops, "Invalid shop changed Lua");
        await ShopEdit("3", true); Check(Shops().SequenceEqual(new[] { 3 }), "Remove one shop");
        await ShopEdit("", true); Check(Shops().Length == 0, "Clear shops");
        // Restore the exact source so the save assertion below still covers one scalar only.
        while (doc.Text != changed) { Check(vm.UndoCommand.CanExecute(null), "Cannot restore shop tests"); vm.UndoCommand.Execute(null); }
        string dbHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vm.SourcePath)));
        await vm.SaveCurrentAsync(false); Check(!vm.HasPendingLuaChanges && File.ReadAllText(doc.Path) == changed, "Common save did not save Lua");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vm.SourcePath))) == dbHash, "Lua-only save rewrote DB");
        var backups = Directory.GetFiles(Path.GetDirectoryName(doc.Path)!, "tables.lua.bak-*");
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == before, "Missing or incorrect original backup");
        toggle.IsChecked = false; Check(grid.Columns.Count == originalColumns, "Hide changed ordinary columns"); toggle.IsChecked = true;
        grid.EnableRowVirtualization = true; grid.EnableColumnVirtualization = true;
        Select("units.prototypes");
        int perks = grid.Columns.Count(c => Header(c).StartsWith("Lua · Навыки и умения"));
        Check(perks > 20, "UnitPerk was truncated: " + perks);
        var counts = new Dictionary<string, int> { ["MaterialProp"] = 13, ["UnitPerk"] = perks };
        foreach (string key in new[] { "items.weapons", "items.armors", "items.quick_items", "spells.prototypes" })
        { Select(key); int count = grid.Columns.Count(c => Header(c).StartsWith("Lua ·")); Check(count > 0, "No columns " + key); counts[key] = count; }
        Select("items.materials"); var last = grid.Columns.Last(); grid.ScrollIntoView(vm.SelectedTable!.Records.First(), last); Layout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
        IEnumerable<T> Visuals<T>(DependencyObject node) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            { var child = VisualTreeHelper.GetChild(node, i); if (child is T found) yield return found; foreach (var nested in Visuals<T>(child)) yield return nested; }
        }
        Visuals<ScrollViewer>(grid).First().ScrollToRightEnd();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
        var renderedHeader = Visuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Single(h => h.Column == last);
        Check(Visuals<TextBlock>(renderedHeader).Any(t => t.Text == "Lua · Запрет размножения\nID магазина" && t.ActualWidth > 0 && t.ActualHeight > 0), "Final shop header is not rendered");
        var bitmap = new RenderTargetBitmap(1800, 1050, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, "inline-lua.png"))) png.Save(file);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Result = "PASS", Counts = counts, DatabaseUnchanged = true, SharedUndoRedoSave = true }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
