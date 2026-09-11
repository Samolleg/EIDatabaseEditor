using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;
public partial class ResFileManagerDialog
{
    private readonly List<string> _convertedMmpFolders = [];

    private async Task<int> StageConvertedMmpAsync(string folder, IReadOnlyList<MmpBatchItem> rows, CancellationToken token)
    {
        var additions = new List<ResFileEdit>();
        foreach (var row in rows.Where(r => r.Success))
        {
            string name = MmpBatchConverter.MapResourceName(row.Name);
            if (_archive!.Contains(name) || _edits.ContainsKey(name))
                throw new IOException($"Ресурс {name} уже существует или подготовлен. Замена не выполняется.");
            var imported = await ResFileManagerArchive.InspectImportAsync(Path.Combine(folder, "MMP", name.Replace('\\', Path.DirectorySeparatorChar)), token);
            additions.Add(new(ResFileEditKind.AddOrReplace, name, imported));
        }
        token.ThrowIfCancellationRequested();
        if (additions.Count == 0) return 0;
        CaptureUndo();
        foreach (var edit in additions) _edits.Add(edit.ResourceName, edit);
        // Keep imported bytes alive for Save and Undo/Redo until this manager closes.
        _convertedMmpFolders.Add(folder);
        RebuildRows();
        ShowMessage($"Добавлено map-текстур: {additions.Count}. Нажмите «Перезаписать текущий RES…» для сохранения в этот архив.", false);
        return additions.Count;
    }

    private void CleanupConvertedMmp()
    {
        foreach (string folder in _convertedMmpFolders)
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { /* A preview reader may still be closing; leave only temporary data. */ }
            catch (UnauthorizedAccessException) { }
        _convertedMmpFolders.Clear();
    }
    private void ConvertMmp_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null || _isBusy) return;
        if (_edits.Count>0) { ShowMessage("Сначала сохраните изменения архива или отмените их: конвертация читает исходный RES.",true); return; }
        var archive=_archive;
        string[] selected=GetSelectedRows().Where(r=>r.ResourceName.EndsWith(".mmp",StringComparison.OrdinalIgnoreCase)).Select(r=>r.ResourceName).ToArray();
        string[] all=archive.Entries.Where(r=>r.Name.EndsWith(".mmp",StringComparison.OrdinalIgnoreCase)).Select(r=>r.Name).ToArray();
        CreateMmpConversionDialog(archive, selected, all).ShowDialog();
    }
    private Window CreateMmpConversionDialog(ResFileManagerArchive archive, string[] selected, string[] all)
    {
        var dialog=new Window { Owner=this,Title="Пакетная конвертация MMP",Width=720,Height=660,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=Background,Foreground=Foreground };
        var root=new StackPanel { Margin=new Thickness(20) };dialog.Content=new ScrollViewer { Content=root, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock {Text="MMP с DXT-сжатием и полной mip-цепочкой",FontSize=19,Margin=new Thickness(0,0,0,12)});
        var scope=new ComboBox {ItemsSource=new[]{ $"Выбранные MMP: {selected.Length}",$"Все MMP архива: {all.Length}"},SelectedIndex=selected.Length>0?0:1};root.Children.Add(scope);
        var format=new ComboBox {ItemsSource=new[]{"Авто: DXT1; при плавной прозрачности DXT5","DXT1 — пропуск полупрозрачных","DXT3 — плавная прозрачность","DXT5 — плавная прозрачность","DXT1 — явно упростить прозрачность (порог 50%)","Map x2: атлас 2×2, DXT1, alpha ≥128 (подтверждённые UV x2)"},SelectedIndex=0,Margin=new Thickness(0,10,0,0)};root.Children.Add(format);
        var output=new ComboBox {ItemsSource=new[]{"Новый RES в отдельной папке","Папка MMP","Добавить в открытый RES (сохранить затем)"},SelectedIndex=2,Margin=new Thickness(0,10,0,0)};root.Children.Add(output);
        root.Children.Add(new TextBlock {Text="Имена: имя.x2.mmp → имя.x2map.mmp. Оригиналы сохраняются. Существующие map-файлы пропускаются.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});
        root.Children.Add(new TextBlock {Text="PNT3 может быть слоем составной текстуры. Конвертация не собирает доспех, не исправляет UV и не гарантирует отображение объекта. Исходный архив не изменяется. DXT — сжатие с потерями.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});
        var status=new TextBlock {Text="Выберите параметры и начните экспорт.",TextWrapping=TextWrapping.Wrap};root.Children.Add(status);
        var bar=new ProgressBar {Height=18,Margin=new Thickness(0,10,0,10)};root.Children.Add(bar);
        var report=new TextBox {IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(report);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};root.Children.Add(actions);
        var start=new Button {Content="Конвертировать…",Padding=new Thickness(16,8,16,8)};
        var cancel=new Button {Content="Отмена",Padding=new Thickness(16,8,16,8),Margin=new Thickness(10,0,0,0),IsEnabled=false};actions.Children.Add(start);actions.Children.Add(cancel);
        CancellationTokenSource? cancellation=null;bool running=false;
        cancel.Click+=(_,_)=>cancellation?.Cancel();
        dialog.Closing+=(_,args)=>{if(running){args.Cancel=true;cancellation?.Cancel();}};
        start.Click+=async (_,_)=>
        {
            bool intoCurrent = output.SelectedIndex == 2;
            string parent = Path.GetTempPath();
            if (!intoCurrent)
            {
                var picker=new OpenFolderDialog {Title="Выберите родительскую папку для нового результата"};if(picker.ShowDialog(dialog)!=true)return;
                parent=picker.FolderName;
            }
            string destination=Path.Combine(parent,"MMP-export-"+Guid.NewGuid().ToString("N"));
            var names=scope.SelectedIndex==0?selected:all;
            if(names.Length==0){status.Text="Нет выбранных MMP.";return;}
            var choice=(MmpDxtChoice)format.SelectedIndex;bool asRes=output.SelectedIndex==0;
            cancellation=new();running=true;start.IsEnabled=scope.IsEnabled=format.IsEnabled=output.IsEnabled=false;cancel.IsEnabled=true;report.Clear();bar.Maximum=names.Length;
            try
            {
                var rows=await MmpBatchConverter.RunAsync(archive,names,destination,asRes,choice,Path.Combine(AppContext.BaseDirectory,"third-party","DirectXTex","texconv.exe"),
                    new Progress<MmpBatchProgress>(p=>{bar.Value=p.Completed;status.Text=$"{p.Completed} / {p.Total}: {p.Name}";}),cancellation.Token);
                int added = intoCurrent ? await StageConvertedMmpAsync(destination, rows, cancellation.Token) : 0;
                report.Text=string.Join(Environment.NewLine,rows.Select(r=>$"{(r.Success?"OK":"ОШИБКА")} {r.Name}: {r.Detail}"));
                status.Text=intoCurrent ? $"Подготовлено в открытом RES: {added}, ошибок: {rows.Count(r=>!r.Success)}. Закройте это окно и сохраните архив штатной кнопкой." : $"Готово: {rows.Count(r=>r.Success)}, ошибок: {rows.Count(r=>!r.Success)}. Результат: {destination}";
            }
            catch(OperationCanceledException){status.Text="Отменено. Готовый результат не создан.";}
            catch(Exception ex){status.Text="Экспорт не завершён: "+ex.Message;}
            finally
            {
                if (intoCurrent && !_convertedMmpFolders.Contains(destination) && Directory.Exists(destination)) Directory.Delete(destination,true);
                running=false;cancellation.Dispose();cancellation=null;start.IsEnabled=scope.IsEnabled=format.IsEnabled=output.IsEnabled=true;cancel.IsEnabled=false;
            }
        };
        return dialog;
    }
}
