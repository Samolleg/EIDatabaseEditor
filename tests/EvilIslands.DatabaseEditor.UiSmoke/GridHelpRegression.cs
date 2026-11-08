using System.IO;
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

internal static class GridHelpRegression
{
    public static int Run(string baseline, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(baseline, output);
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background));
        Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS compact DB/Lua: semantic hover, projection/unknown help, copy/edit/undo, layout, no interactive window"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
    private static async Task Verify(string baseline, string output)
    {
        string copy = Path.GetFullPath(Path.Combine(output, "copy"));
        foreach (string name in new[] { "main.lua", "lua/tables.lua", "lua/npc_consumables.lua", "res/database.res" })
        {
            string target = Path.Combine(copy,name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(baseline,name),target,false);
        }
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output,"settings.json")),new RecoverySnapshotStore(Path.Combine(output,"recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(Path.Combine(copy,"res/database.res"));
        vm.SelectTableCommand.Execute(vm.Databases.SelectMany(d=>d.Tables).Single(t=>t.Key=="items.weapons"));
        var grid=(DataGrid)window.FindName("RecordsGrid");
        ((CheckBox)window.FindName("ShowLuaColumnsButton")).IsChecked=true;
        var root=(FrameworkElement)window.FindName("MainRoot");
        void Layout() {root.Measure(new Size(1440,900));root.Arrange(new Rect(0,0,1440,900));root.UpdateLayout();}
        void Render(string name) {Layout();var bitmap=new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));png.Save(file);}
        Layout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
        var row=vm.SelectedTable!.Records.First();
        vm.SelectedRecord=row;
        var price=grid.Columns.Single(c=>c.Header is SpreadsheetColumnViewModel {Title:"Цена"});
        grid.ScrollIntoView(row,price);Layout();
        var priceText=(TextBlock)price.GetCellContent(row);
        string help=priceText.ToolTip?.ToString()??"";
        Check(help.Contains("стоимости") && help.Contains("БД ·") && help!=priceText.Text,"DB hover still shows value/path instead of meaning");
        Check(grid.RowHeight==28 && priceText.ActualHeight<=28,"Compact DB row layout");
        var priceField=row.Cells[7] ?? throw new Exception("Missing price field");
        string originalPrice=priceField.EditorText;
        priceField.EditorText="invalid number";Layout();
        Check(priceField.HasError && priceText.ToolTip?.ToString()==priceField.ErrorMessage,"Validation error hidden by semantic help");
        priceField.EditorText=originalPrice;Layout();
        Check(priceText.ToolTip?.ToString()==help,"Semantic help did not return after error correction");
        Render("db-compact.png");
        var lua=grid.Columns.OfType<DataGridTextColumn>().Single(c=>c.Header is string s && s.StartsWith("Lua · Свойства оружия") && s.EndsWith("шанс критического урона"));
        grid.ScrollIntoView(row,lua);Layout();
        var luaText=(TextBlock)lua.GetCellContent(row);
        help=luaText.ToolTip?.ToString()??"";
        Check(help.Contains("критического попадания") && help.Contains("WeaponProp") && help!=luaText.Text,"Lua semantic tooltip");
        Check(lua.Width.Value<150,"Lua width not compact");
        var helpBefore=help;
        grid.SelectedCells.Clear();grid.CurrentCell=new DataGridCellInfo(row,lua);grid.SelectedCells.Add(grid.CurrentCell);
        Check(window.TryBuildClipboardMatrix(out var matrix)&&matrix[0][0]==luaText.Text,"Copy must still return value");
        var doc=vm.LuaWorkspace!.Documents.Single(d=>Path.GetFileName(d.Path)=="tables.lua");string before=doc.Text;
        Check(window.TryPasteLuaMatrix(new IReadOnlyList<string>[] { new[] { "17.25" } }),"Lua edit not handled");
        Check(doc.Text!=before,"Lua edit did not modify draft");Layout();
        Check(((TextBlock)lua.GetCellContent(row)).ToolTip?.ToString()==helpBefore,"Help depends on cell value");
        vm.UndoCommand.Execute(null);Check(doc.Text==before,"Lua undo lost original data");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);Layout();
        Check(grid.BeginEdit(),"Compact Lua editor unavailable");Layout();
        Check(lua.GetCellContent(row) is TextBox edit && edit.ActualHeight<=28 && edit.ActualHeight>15,"Editor clipped in compact row");
        grid.CancelEdit(DataGridEditingUnit.Cell);grid.CancelEdit(DataGridEditingUnit.Row);
        Render("lua-compact.png");
        var unknown=new SpreadsheetColumnViewModel(99,"field-99","Неизвестное поле","Int32",true,false,null,99,true);
        Check(unknown.HeaderToolTip.Contains("пока не описано"),"Unknown semantics invented");
        var projection=new SpreadsheetColumnViewModel(14,"field-14[0]","Магазин 1","Boolean",true,false,
            new(EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit,0,"Магазин 1"),0,true,"Магазины");
        Check(projection.HeaderToolTip.Contains("остальные флаги не меняются"),"Projection help missing");
        File.WriteAllText(Path.Combine(output,"tooltips.txt"),priceText.ToolTip+"\n\n"+helpBefore+"\n\n"+unknown.HeaderToolTip+"\n\n"+projection.HeaderToolTip);
        Check(!window.IsVisible,"Interactive app launched");vm.Dispose();
    }
}
