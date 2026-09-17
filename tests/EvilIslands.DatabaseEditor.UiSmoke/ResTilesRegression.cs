using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Services;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class ResTilesRegression
{
    static void Check(bool b, string message) { if (!b) throw new Exception(message); }
    static IEnumerable<T> Visuals<T>(DependencyObject p) where T : DependencyObject
    { for (int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++) { var c=VisualTreeHelper.GetChild(p,i); if(c is T t) yield return t; foreach(var x in Visuals<T>(c)) yield return x; } }
    public static int Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var app=new EvilIslands.DatabaseEditor.App.App(); app.InitializeComponent(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task=Verify(source,output); var frame=new DispatcherFrame();
        _=task.ContinueWith(_=>frame.Dispatcher.BeginInvoke(()=>frame.Continue=false));
        if(!task.IsCompleted) Dispatcher.PushFrame(frame);
        try {task.GetAwaiter().GetResult(); Console.WriteLine("PASS real RES tile previews, selection roundtrip, search, bounded page, source unchanged");return 0;} catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static async Task Verify(string source,string output)
    {
        var hash=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
        var dialog=new ResFileManagerDialog(source,null,new EditorPreferencesStore(Path.Combine(output,"prefs.json")));
        await (Task)typeof(ResFileManagerDialog).GetMethod("LoadArchiveAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,[source])!;
        var root=(FrameworkElement)dialog.Content;
        void Layout(){root.Measure(new Size(1350,850));root.Arrange(new Rect(0,0,1350,850));root.UpdateLayout();}
        Layout();
        var grid=(DataGrid)dialog.FindName("FilesGrid");var tiles=(ListBox)dialog.FindName("TilesList"); var toggle=(CheckBox)dialog.FindName("TileMode");
        var selected=grid.Items[0];grid.SelectedItem=selected;toggle.IsChecked=true;Layout();
        Check(tiles.SelectedItems.Contains(selected),"Selection lost switching to tiles");
        Check(tiles.Items.Count>1 && tiles.Items.Count<=60,"Page is not bounded");
        foreach(var img in Visuals<Image>(tiles)) img.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        for(int i=0;i<100 && Visuals<Image>(tiles).Count(x=>x.Source!=null)<tiles.Items.Count;i++) await Task.Delay(100);
        int decoded=Visuals<Image>(tiles).Count(x=>x.Source!=null);
        Check(decoded==tiles.Items.Count,"Some real MMP thumbnails failed: "+decoded+"/"+tiles.Items.Count);Layout();
        var bmp=new RenderTargetBitmap(1350,850,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var file=File.Create(Path.Combine(output,"tiles.png")))png.Save(file);
        toggle.IsChecked=false;Check(grid.SelectedItems.Contains(selected),"Selection lost switching to list");toggle.IsChecked=true;
        ((TextBox)dialog.FindName("SearchBox")).Text="no-such-texture-123456";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Check(tiles.Items.Count==0,"Search not applied to tiles");
        Check(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))),"Source changed");
        File.WriteAllText(Path.Combine(output,"result.txt"),"PASS thumbnails="+decoded+"; selection/search/page/source checks passed");
        dialog.Close();
    }
}
