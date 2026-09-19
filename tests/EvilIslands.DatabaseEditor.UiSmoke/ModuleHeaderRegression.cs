using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ModuleHeaderRegression
{
    private sealed class Codec : IDocumentCodec
    {
        public Result Validate(DocumentSnapshot snapshot) => Result.Ok;
        public PreparedDocument Prepare(DocumentSnapshot snapshot, string kind) => new(Result.Ok, snapshot.State);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    internal static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database, output);
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background)); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS module header: reclaim/restore82px, global history, title order and compact layout"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Verify(string database, string output)
    {
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output,"settings.json")), new RecoverySnapshotStore(Path.Combine(output,"recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(database);
        var root = (FrameworkElement)window.FindName("MainRoot");
        var header = (Border)window.FindName("DatabaseContentHeader");
        var workspace = (Grid)header.Parent;
        var undo = (Button)window.FindName("TitleUndoButton"); var redo = (Button)window.FindName("TitleRedoButton");
        var layout = (Button)window.FindName("ResetLayoutButton");
        Check(ReferenceEquals(undo.Command,vm.UndoCommand) && ReferenceEquals(redo.Command,vm.RedoCommand),"History binding changed");
        var title = (Panel)undo.Parent;
        Check(ReferenceEquals(title,redo.Parent) && ReferenceEquals(title,layout.Parent) && title.Children.IndexOf(undo)<title.Children.IndexOf(redo) && title.Children.IndexOf(redo)<title.Children.IndexOf(layout),"Wrong title order");
        void Measure() { root.Measure(new Size(1180,680)); root.Arrange(new Rect(0,0,1180,680)); root.UpdateLayout(); }
        foreach (int cycle in Enumerable.Range(0,3))
        {
            vm.NavigateCommand.Execute(vm.NavigationItems.Single(n=>n.Section==WorkspaceSection.Overview)); Measure();
            Check(header.Visibility==Visibility.Visible && Math.Abs(workspace.RowDefinitions[0].ActualHeight-82)<.1,"Header did not restore");
            double previous=workspace.RowDefinitions[3].ActualHeight;
            vm.NavigateCommand.Execute(vm.NavigationItems.Single(n=>n.Section==WorkspaceSection.Modules)); Measure();
            Check(header.Visibility==Visibility.Collapsed && workspace.RowDefinitions[0].ActualHeight==0,"Header left blank space");
            Check(Math.Abs(workspace.RowDefinitions[3].ActualHeight-previous-82)<.1,"Module did not gain header height");
        }
        vm.ModuleDocuments.RegisterCodec("fixture","header",1,new Codec());
        var doc=vm.ModuleDocuments.Open(new("fixture","header",1,"Header test","before",[1]));
        Check(vm.ApplyModuleTransaction(new(Guid.NewGuid(),vm.ModuleWorkspace.Snapshot().Stamp,"Edit",[new UpdateDocument(doc.DocumentId,vm.ModuleDocuments.Get(doc.DocumentId)!.Revision,"after",[2])])).Succeeded,"Edit failed");
        Check(undo.IsEnabled && undo.Command!.CanExecute(null),"Title Undo disabled after module edit");
        undo.Command.Execute(null); Check(vm.ModuleDocuments.Get(doc.DocumentId)!.Text=="before","Title Undo did not use shared history");
        Check(redo.IsEnabled && redo.Command!.CanExecute(null),"Title Redo disabled");
        redo.Command.Execute(null); Check(vm.ModuleDocuments.Get(doc.DocumentId)!.Text=="after","Title Redo failed");
        Measure();
        var bitmap=new RenderTargetBitmap(1180,680,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(output,"module-header.png"))) png.Save(file);
        Check(!window.IsVisible,"Unexpected interactive window");
        vm.Dispose();
    }
}
