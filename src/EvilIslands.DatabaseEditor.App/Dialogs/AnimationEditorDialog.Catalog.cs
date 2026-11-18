using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;
namespace EvilIslands.DatabaseEditor.App.Dialogs;
internal sealed class AnimationLabelConverter(Func<string> model):IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is string id&&id!="Исходная поза"?AnimationNames.Shared.Label(model(),id):value;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
    internal static DataTemplate Template(Func<string> model)
    {var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding{Converter=new AnimationLabelConverter(model)});return new DataTemplate{VisualTree=text};}
}
internal sealed partial class AnimationEditorDialog
{
    private void InitializeCatalog(StackPanel sidebar)
    {
        var panel=new StackPanel();panel.Children.Add(Paragraph("РУССКОЕ НАЗВАНИЕ · "+_name));
        var title=new TextBox{Text=AnimationNames.Shared.Get(_scene.Model,_name),ToolTip="Ваша подпись сохраняется для этой модели и ID. Игровой ID не меняется."};panel.Children.Add(title);
        var save=ActionButton("Сохранить название",()=>{});save.Click+=async(_,_)=>await Run(()=>{AnimationNames.Shared.Save(_scene.Model,_name,title.Text);_incomingClip.Items.Refresh();_message.Text="Название сохранено.";return Task.CompletedTask;});panel.Children.Add(save);
        var create=ActionButton("＋ Новая анимация…",()=>{});create.Click+=async(_,_)=>await Run(()=>CreateAnimation());panel.Children.Add(create);
        var align=ActionButton("Выравнять высоту таза…",()=>{});
        align.IsEnabled=_scene.Animations?.Bones.ContainsKey("hp")==true;
        align.ToolTip="Поднять или опустить всю дорожку hp до высоты выбранной анимации этой модели. Движения между кадрами сохраняются; одно действие Undo.";
        align.Click+=async(_,_)=>await Run(AlignRootHeight);panel.Children.Add(align);
        var copyBar=new WrapPanel{Margin=new Thickness(4,0,4,6)};
        AddSaveButtons(copyBar);
        var copy=ActionButton("＋ Создать копию…",()=>{});
        copy.ToolTip="Создать отдельную анимацию под новым ID из текущего клипа со всеми внесёнными правками.";
        copy.Click+=async(_,_)=>await Run(()=>CreateAnimation(true));
        copyBar.Children.Add(copy);copyBar.Children.Add(Paragraph("Из текущей анимации: "+_name));
        DockPanel.SetDock(copyBar,Dock.Top);Body.Children.Insert(0,copyBar);
        sidebar.Children.Insert(0,panel);
        _incomingClip.ItemTemplate=AnimationLabelConverter.Template(()=>_incomingMotion?.Model??_scene.Model);
    }
    private sealed class CatalogWindow():LuaToolWindow("Новая анимация") {public void Add(UIElement panel)=>Body.Children.Add(panel);}
    private sealed class RootHeightWindow():LuaToolWindow("Высота таза анимации") {public void Add(UIElement panel)=>Body.Children.Add(panel);}

    internal static async Task<string> SuggestHeightReference(FigureAnimationSet animations, string current)
    {
        string? idle = animations.Names.FirstOrDefault(name => name.Equals("cidle09", StringComparison.OrdinalIgnoreCase));
        if (idle is null || idle.Equals(current, StringComparison.OrdinalIgnoreCase)) return current;
        var currentClip = await animations.LoadAsync(current);
        var idleClip = await animations.LoadAsync(idle);
        if (!idleClip.Tracks.TryGetValue("hp", out var idleRoot) || idleRoot.Positions.Length == 0) return current;
        if (!currentClip.Tracks.TryGetValue("hp", out var currentRoot) || currentRoot.Positions.Length == 0) return idle;
        if (!animations.Bones.TryGetValue("ll1", out var upper) ||
            !animations.Bones.TryGetValue("ll2", out var lower) ||
            !animations.Bones.TryGetValue("ll3", out var foot)) return current;
        float leg = upper.Length() + lower.Length() + foot.Length();
        return leg > 0 && currentRoot.Position(0).Z < idleRoot.Position(0).Z - leg * .5f ? idle : current;
    }

    private async Task AlignRootHeight()
    {
        var animations=_editor.Preview();
        var clip=await animations.LoadAsync(_name);
        if(!clip.Tracks.TryGetValue("hp",out var root)||root.Positions.Length==0)
            throw new ArgumentException("У этой анимации нет дорожки положения таза hp.");
        var choices=animations.Names.Where(name=>!name.Equals(_name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(choices.Length==0)throw new ArgumentException("Для этой модели нет другой опорной анимации.");
        string suggested=await SuggestHeightReference(animations,_name);
        if(suggested.Equals(_name,StringComparison.OrdinalIgnoreCase))
            suggested=choices.FirstOrDefault(name=>name.Equals("cidle09",StringComparison.OrdinalIgnoreCase))??choices[0];
        var dialog=new RootHeightWindow{Owner=this,Width=560,Height=340};
        var panel=new StackPanel{Margin=new Thickness(12)};
        panel.Children.Add(Paragraph($"Анимация {_name}: исходный hp.Z = {root.Position(0).Z:F6}. Выберите исправный клип этой же модели как опору."));
        var reference=new ComboBox{ItemsSource=choices,SelectedItem=suggested};
        reference.ItemTemplate=AnimationLabelConverter.Template(()=>_scene.Model);
        panel.Children.Add(reference);
        var details=Paragraph("");panel.Children.Add(details);
        async Task Describe()
        {
            if(reference.SelectedItem is not string name)return;
            try
            {
                var selected=await animations.LoadAsync(name);
                details.Text=selected.Tracks.TryGetValue("hp",out var other)&&other.Positions.Length>0
                    ? $"Опора hp.Z = {other.Position(0).Z:F6}; сдвиг каждого кадра по Z = {other.Position(0).Z-root.Position(0).Z:+0.000000;-0.000000;0}. X/Y, повороты и разница высот между кадрами сохранятся. Применение можно отменить."
                    : "У этого клипа нет положения таза hp. Выберите другую опору.";
            }
            catch(Exception ex){details.Text="Не удалось прочитать опорную анимацию: "+ex.Message;}
        }
        reference.SelectionChanged+=async(_,_)=>await Describe();
        await Describe();
        var apply=ActionButton("Применить ко всем кадрам",()=>dialog.DialogResult=true);panel.Children.Add(apply);
        dialog.Add(panel);
        if(dialog.ShowDialog()!=true)return;
        float difference=await _editor.AlignRootHeightAsync(_name,reference.SelectedItem as string??throw new ArgumentException("Выберите опорную анимацию."));
        await Refresh();
        _message.Text=$"Высота таза изменена на {difference:+0.000000;-0.000000;0} во всех кадрах. Сохраните ANM отдельно; Отменить вернёт прежнюю дорожку.";
    }
    private async Task CreateAnimation(bool copyOnly=false)
    {
        var dialog=new CatalogWindow(){Owner=this,Width=560,Height=550};
        var panel=new StackPanel{Margin=new Thickness(12)};
        string proposed=FigureAnimationEditor.SuggestCopyId(_name,_editor.Preview().Names);
        var id=new TextBox{Text=proposed,MaxLength=15,ToolTip="Игровой ID для ANM и ADB: до 15 символов, сначала латинская буква, затем буквы, цифры или _."};var label=new TextBox();var length=new TextBox{Text=(_loadedClip?.FrameCount??(await _editor.Preview().LoadAsync(_name)).FrameCount).ToString(),IsReadOnly=copyOnly};
        var bind=new CheckBox{Content="Начать с исходной Т-позы",Margin=new Thickness(4),
            ToolTip="Повороты будут сброшены, но стартовая высота таза сохранится из текущей анимации."};
        foreach(var item in new (string Label,Control Field)[]{("Уникальный ID (латиница)",id),("Русское название",label),("Число кадров",length)}){panel.Children.Add(Paragraph(item.Label));panel.Children.Add(item.Field);}
        panel.Children.Add(Paragraph("ID для игры: до 15 символов; первая — латинская буква, далее латиница, цифры или _. Уже существующие имена ANM не меняются."));
        if(!copyOnly)panel.Children.Add(bind);
        panel.Children.Add(Paragraph(copyOnly?"Копия текущей анимации со всеми правками и тем же числом кадров. Исходная анимация сохранится. Копия откроется в отдельном редакторе; экспорт ANM сохранит весь набор.":"Без галочки — копия текущей анимации. С Т-позой высота таза берётся из текущей анимации; если она уже уходит под землю, сначала выберите исправную опорную анимацию. Результат откроется в отдельном редакторе."));
        var ok=ActionButton("Создать",()=>dialog.DialogResult=true);panel.Children.Add(ok);dialog.Add(panel);
        if(dialog.ShowDialog()!=true)return;
        var copy=new FigureAnimationEditor(_editor.Preview());
        await copy.CreateAnimationAsync(_name,id.Text.Trim(),Integer(length),bind.IsChecked==true);
        AnimationNames.Shared.Save(_scene.Model,id.Text.Trim(),label.Text);
        var child=new AnimationEditorDialog(_scene with{Animations=copy.Preview()},id.Text.Trim()){Owner=this};
        child._saveBaseline=_saveBaseline?.ToArray();
        child._exported=Array.Empty<byte>(); // Newly added clip must be exported or saved, even before pose edits.
        child.ShowDialog();
        await AdoptSavedChild(child);
    }
}
