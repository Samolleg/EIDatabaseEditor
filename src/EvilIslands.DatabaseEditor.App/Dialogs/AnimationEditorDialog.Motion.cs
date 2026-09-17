using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;
namespace EvilIslands.DatabaseEditor.App.Dialogs;
internal sealed partial class AnimationEditorDialog
{
    private readonly WrapPanel _motionTools=new();
    private readonly MotionGraph _tracksGraph=new(),_curveGraph=new(){Curve=true};
    private readonly CheckBox _trajectoryCheck=new(){Content="Траектория выбранной части",Margin=new(4)},_onionCheck=new(){Content="Соседние позы −3, −1, +1, +3",Margin=new(4)};
    private readonly ComboBox _channel=new(){ItemsSource=new[]{"Поворот X","Поворот Y","Поворот Z","Корень X","Корень Y","Корень Z"},SelectedIndex=0,Width=135},_ease=new(){ItemsSource=new[]{"Линейно","Плавно","Разгон","Торможение","Ступень"},SelectedIndex=1,Width=130};
    private readonly TextBox _curveStart=Number(),_curveEnd=Number(),_transition=Number(),_poseName=new(){Text="Новая поза",Width=260},_mapping=new(){AcceptsReturn=true,Height=90,Width=270,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly ComboBox _library=new(){Width=270,DisplayMemberPath="Name"},_mask=new(){Width=270,ItemsSource=new[]{"Все части","Только руки","Только ноги","Выбранная часть"},SelectedIndex=0},_incomingClip=new(){Width=270};
    private readonly List<StoredAnimationPose> _poses=[];
    private readonly ListBox _contactsList=new(){Height=65,Width=270},_issuesList=new(){Height=120};
    private readonly TextBox _incomingFirst=Number(),_incomingLast=Number();
    private MotionTransfer? _incomingMotion;
    private bool _refreshingMotion;
    private void InitializeMotion(StackPanel sidebar)
    {
        var expander=new Expander{Header="Движение и монтаж · 10 инструментов",Content=_motionTools,IsExpanded=false,Margin=new(3)};sidebar.Children.Insert(0,expander);
        void Label(string text)=>_motionTools.Children.Add(Paragraph(text));
        void Add(params UIElement[] controls){foreach(var c in controls)_motionTools.Children.Add(c);}
        void Button(string text,Func<Task> action){var b=ActionButton(text,()=>{});b.Click+=async(_,_)=>await Run(action);Add(b);}
        Label("КРИВАЯ · выбранная часть и диапазон");Add(_channel,_ease);Label("Начальное и конечное значения");Add(_curveStart,_curveEnd);
        Button("Применить кривую",async()=>{var r=SelectedRange();await _editor.CurveAsync(_name,SelectedPart(),r.First,r.Last,_channel.SelectedIndex%3,_channel.SelectedIndex>=3,Value(_curveStart),Value(_curveEnd),(MotionEase)_ease.SelectedIndex);await Refresh();});
        Add(_trajectoryCheck,_onionCheck);
        Label("ЦИКЛ / ПЕРЕХОД · число кадров");_transition.Text="4";Add(_transition);
        Button("Свести конец с началом",async()=>{await _editor.LoopAsync(_name,Integer(_transition));await Refresh();});
        Label("БИБЛИОТЕКА ПОЗ");Add(_poseName,_library,_mask);
        Button("Запомнить позу",async()=>{if(_poses.Count>=500)throw new ArgumentException("Лимит библиотеки: 500 поз.");_poses.Add(await _editor.CapturePoseAsync(_name,CurrentFrame,_poseName.Text));RefreshLibrary();});
        Button("Применить позу",async()=>{if(_library.SelectedItem is not StoredAnimationPose pose)throw new ArgumentException("Выберите позу.");IEnumerable<string> parts=EditableParts(_scene,_loadedClip!);parts=_mask.SelectedIndex switch{1=>parts.Where(p=>p.StartsWith("lh")||p.StartsWith("rh")||p.Contains("hand")),2=>parts.Where(p=>p.StartsWith("ll")||p.StartsWith("rl")||p.Contains("leg")),3=>new[]{SelectedPart()},_=>parts};await _editor.ApplyStoredPoseAsync(_name,CurrentFrame,pose,parts);await Refresh();});
        Button("Удалить позу",()=>{if(_library.SelectedItem is StoredAnimationPose p)_poses.Remove(p);RefreshLibrary();return Task.CompletedTask;});
        Button("Сохранить библиотеку…",()=>{SaveData("Библиотека поз|*.eiposes","poses.eiposes",FigureAnimationEditor.SerializeMotion(_poses));return Task.CompletedTask;});
        Button("Открыть библиотеку…",()=>{var bytes=ReadData("Библиотека поз|*.eiposes");if(bytes is null)return Task.CompletedTask;var poses=FigureAnimationEditor.DeserializeMotion<List<StoredAnimationPose>>(bytes);if(poses.Count>500||poses.Any(p=>p.Rotations is null||p.Positions is null||p.Rotations.Any(q=>!float.IsFinite(q.Value.LengthSquared())||q.Value.LengthSquared()<.0001f)||p.Positions.Any(v=>!float.IsFinite(v.Value.LengthSquared()))))throw new InvalidDataException("Некорректные позы.");_poses.Clear();_poses.AddRange(poses);RefreshLibrary();return Task.CompletedTask;});
        Label("КОНТАКТЫ · диапазон шкалы");Add(_contactsList);
        foreach(string foot in new[]{"ll3","rl3"})Button(foot=="ll3"?"Левая стопа: контакт":"Правая стопа: контакт",async()=>{var r=SelectedRange();if(!FigureLimbIk.Supports(_editor.Preview(),_loadedClip!,foot))throw new ArgumentException("Нет цепочки стопы.");if(_editor.Contacts.Any(c=>c.Part==foot&&c.First<=r.Last&&r.First<=c.Last))throw new ArgumentException("Контакт этой стопы уже есть в диапазоне.");var contact=new FootContact(foot,r.First,r.Last,_editor.Preview().Pose(_loadedClip!,r.First)[foot].Position);_editor.Contacts=_editor.Contacts.Append(contact).ToArray();await RefreshMotion();});
        Button("Убрать контакт",async()=>{int i=_contactsList.SelectedIndex;if(i>=0)_editor.Contacts=_editor.Contacts.Where((_,n)=>i!=n).ToArray();await RefreshMotion();});
        Button("Закрепить диапазоны",async()=>{await _editor.BakeContactsAsync(_name);await Refresh();});
        Label("СБОРКА КЛИПОВ · диапазон источника");Add(_incomingClip,_incomingFirst,_incomingLast);_incomingFirst.Text="0";
        Button("Добавить клип с переходом",async()=>{var clip=await IncomingClip();await _editor.ComposeAsync(_name,clip,Integer(_incomingFirst),Integer(_incomingLast),Integer(_transition));await Refresh();});
        Button("Экспорт движения…",()=>{SaveData("Движение и скелет|*.eimotion",_scene.Model+".eimotion",FigureAnimationEditor.SerializeMotion(_editor.ExportMotion(_scene.Model,_name)));return Task.CompletedTask;});
        Button("Источник движения…",async()=>{var bytes=ReadData("Движение и скелет|*.eimotion");if(bytes is null)return;var motion=FigureAnimationEditor.DeserializeMotion<MotionTransfer>(bytes);var set=FigureAnimationEditor.ReadMotion(motion);var clip=await set.LoadAsync(motion.Clip);_incomingMotion=motion;_incomingClip.ItemsSource=set.Names;_incomingClip.SelectedItem=motion.Clip;_incomingFirst.Text="0";_incomingLast.Text=(clip.FrameCount-1).ToString();_mapping.Text=string.Join(Environment.NewLine,_loadedClip!.Tracks.Keys.Where(p=>clip.Tracks.ContainsKey(p)&&set.Parents.ContainsKey(p)&&_scene.Animations!.Parents.ContainsKey(p)).Select(p=>p+"="+p));});
        Label("ПЕРЕНОС · цель=источник, по строке");Add(_mapping);
        Button("Перенести на эту модель",async()=>{if(_incomingMotion is null)throw new ArgumentException("Откройте .eimotion другой модели.");var map=new Dictionary<string,string>();foreach(var row in _mapping.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){var pair=row.Split('=',2,StringSplitOptions.TrimEntries);if(pair.Length!=2||!map.TryAdd(pair[0],pair[1]))throw new ArgumentException("Формат сопоставления: цель=источник.");}await _editor.RetargetAsync(_name,_incomingMotion with{Clip=_incomingClip.SelectedItem as string??_incomingMotion.Clip},map);await Refresh();});
        Button("Проверить анимацию",ShowIssues);
        if(_timeline.Parent is StackPanel footer)
        {
            var tabs=new StackPanel();var headers=new WrapPanel();var content=new ContentControl{Height=110};
            var tracks=new ScrollViewer{Content=_tracksGraph,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(22,27,24))};
            foreach(var tab in new (string Label,UIElement View)[]{("Дорожки суставов",tracks),("Кривая",_curveGraph),("Проверка",_issuesList)})
            {var button=ActionButton(tab.Label,()=>content.Content=tab.View);button.Height=26;button.MinHeight=0;button.FontSize=11;button.Padding=new Thickness(6,2,6,2);headers.Children.Add(button);}
            content.Content=tracks;tabs.Children.Add(headers);tabs.Children.Add(content);footer.Children.Insert(0,tabs);
        }
        _tracksGraph.Seek+=(part,f)=>SelectJoint(part,f);
        _tracksGraph.MoveFrame+=async(part,from,to)=>await Run(async()=>{await _editor.MoveJointFrameAsync(_name,part,from,to);await Refresh();});
        _curveGraph.Seek+=(part,f)=>SelectJoint(part,f);
        _curveGraph.EditValue+=async(frame,value)=>await Run(async()=>{await _editor.CurveAsync(_name,SelectedPart(),frame,frame,_channel.SelectedIndex%3,_channel.SelectedIndex>=3,value,value,MotionEase.Linear);await Refresh();});
        _incomingClip.SelectionChanged+=async(_,_)=>{if(!_busy)await Run(async()=>{var clip=await IncomingClip();_incomingFirst.Text="0";_incomingLast.Text=(clip.FrameCount-1).ToString();});};
        _channel.SelectionChanged+=async(_,_)=>{if(!_refreshingMotion)await Run(RefreshMotion);};
        _part.SelectionChanged+=async(_,_)=>{if(!_busy&&!_refreshingMotion)await Run(RefreshMotion);};
        foreach(var check in new[]{_trajectoryCheck,_onionCheck}){check.Checked+=(_,_)=>UpdateGuides();check.Unchecked+=(_,_)=>UpdateGuides();}
        _issuesList.SelectionChanged+=(_,_)=>{if(_issuesList.SelectedItem is AnimationIssue issue){if(_loadedClip!.Tracks.ContainsKey(issue.Part))SelectJoint(issue.Part,issue.Frame);else _preview.SetAnimationFrame(issue.Frame);}};
    }
    private string SelectedPart()=>_part.SelectedItem as string??throw new ArgumentException("Выберите часть.");
    private static float Value(TextBox box)=>float.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out float n)&&float.IsFinite(n)?n:throw new ArgumentException("Введите конечное число с точкой.");
    private static int Integer(TextBox box)=>int.TryParse(box.Text,out int n)?n:throw new ArgumentException("Введите целое число.");
    private void RefreshLibrary(){_library.ItemsSource=null;_library.ItemsSource=_poses;_library.SelectedIndex=_poses.Count-1;}
    private async Task<FigureAnimationClip> IncomingClip(){var name=_incomingClip.SelectedItem as string??throw new ArgumentException("Выберите клип.");return await (_incomingMotion is null?_editor.Preview():FigureAnimationEditor.ReadMotion(_incomingMotion)).LoadAsync(name);}
    private void UpdateGuides(){if(_loadedClip is not null&&!_loadingSettings)_preview.SetMotionGuides(_part.SelectedItem as string,_trajectoryCheck.IsChecked==true,_onionCheck.IsChecked==true);}
    private async Task RefreshMotion()
    {
        if(_loadedClip is null)return;_refreshingMotion=true;
        try
        {
            var original=_originalClip??await _scene.Animations!.LoadAsync(_name);var parts=EditableParts(_scene,_loadedClip);
            _tracksGraph.Count=_curveGraph.Count=_loadedClip.FrameCount;_tracksGraph.Parts=parts;_tracksGraph.Height=Math.Max(100,parts.Length*22);
            _tracksGraph.Changed=parts.Select(p=>Enumerable.Range(0,_loadedClip.FrameCount).Select(f=>!original.Tracks.TryGetValue(p,out var old)||Math.Abs(Quaternion.Dot(old.Rotation(f),_loadedClip.Tracks[p].Rotation(f)))<.99999f||Vector3.Distance(old.Position(f),_loadedClip.Tracks[p].Position(f))>.00001f).ToArray()).ToArray();_tracksGraph.InvalidateVisual();
            if(_part.SelectedItem is string part){var track=_loadedClip.Tracks[part];int axis=_channel.SelectedIndex%3;_curveGraph.Parts=new[]{part};_curveGraph.Values=Enumerable.Range(0,_loadedClip.FrameCount).Select(f=>_channel.SelectedIndex<3?FigureAnimationEditor.TwistDegrees(track.Rotation(f),axis):axis==0?track.Position(f).X:axis==1?track.Position(f).Y:track.Position(f).Z).ToArray();_curveStart.Text=_curveGraph.Values[0].ToString("R",CultureInfo.InvariantCulture);_curveEnd.Text=_curveGraph.Values[^1].ToString("R",CultureInfo.InvariantCulture);}_curveGraph.InvalidateVisual();
            _contactsList.ItemsSource=_editor.Contacts.Select(c=>$"{PartLabel(c.Part)}: {c.First}–{c.Last}").ToArray();
            if(_incomingClip.ItemsSource is null){_incomingClip.ItemsSource=_scene.Animations!.Names;_incomingClip.SelectedItem=_name;_incomingLast.Text=(_loadedClip.FrameCount-1).ToString();}
            UpdateGuides();
        }
        finally{_refreshingMotion=false;}
    }
    private async Task ShowIssues(){var issues=await _editor.DiagnoseAsync(_name);_issuesList.ItemsSource=issues;_message.Text=issues.Count==0?"По заданным порогам проблем не найдено.":$"Замечаний: {issues.Count}. Вкладка «Проверка»: щёлкните для перехода. Для нециклического действия разрыв цикла допустим.";}
    private async Task<bool> CheckBeforeExport(){await ShowIssues();return _issuesList.Items.Count==0||MessageBox.Show(this,"Найдены замечания. Экспортировать несмотря на них? Список доступен на вкладке «Проверка».",Title,MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;}
    private byte[]? ReadData(string filter){var picker=new OpenFileDialog{Filter=filter};if(picker.ShowDialog(this)!=true)return null;if(new FileInfo(picker.FileName).Length>256L*1024*1024)throw new InvalidDataException("Файл превышает 256 МиБ.");return File.ReadAllBytes(picker.FileName);}
    private void SaveData(string filter,string name,byte[] bytes){var picker=new SaveFileDialog{Filter=filter,FileName=name};if(picker.ShowDialog(this)!=true)return;string tmp=picker.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var stream=new FileStream(tmp,FileMode.CreateNew)){stream.Write(bytes);stream.Flush(true);}if(!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(bytes))throw new IOException("Проверка записи не прошла.");if(File.Exists(picker.FileName))File.Replace(tmp,picker.FileName,picker.FileName+".bak");else File.Move(tmp,picker.FileName);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
}
