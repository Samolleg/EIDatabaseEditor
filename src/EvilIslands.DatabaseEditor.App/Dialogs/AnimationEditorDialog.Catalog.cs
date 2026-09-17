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
        var create=ActionButton("＋ Новая анимация…",()=>{});create.Click+=async(_,_)=>await Run(CreateAnimation);panel.Children.Add(create);
        sidebar.Children.Insert(0,panel);
        _incomingClip.ItemTemplate=AnimationLabelConverter.Template(()=>_incomingMotion?.Model??_scene.Model);
    }
    private sealed class CatalogWindow():LuaToolWindow("Новая анимация") {public void Add(UIElement panel)=>Body.Children.Add(panel);}
    private async Task CreateAnimation()
    {
        var dialog=new CatalogWindow(){Owner=this,Width=560,Height=550};
        var panel=new StackPanel{Margin=new Thickness(12)};
        var id=new TextBox{Text="new_animation"};var label=new TextBox();var length=new TextBox{Text=(_loadedClip?.FrameCount??30).ToString()};
        var bind=new CheckBox{Content="Начать с исходной Т-позы",Margin=new Thickness(4)};
        foreach(var item in new (string Label,Control Field)[]{("Уникальный ID (латиница)",id),("Русское название",label),("Число кадров",length)}){panel.Children.Add(Paragraph(item.Label));panel.Children.Add(item.Field);}
        panel.Children.Add(bind);panel.Children.Add(Paragraph("Без галочки — копия текущей анимации. Результат откроется в отдельном редакторе; экспорт ANM сохранит весь набор."));
        var ok=ActionButton("Создать",()=>dialog.DialogResult=true);panel.Children.Add(ok);dialog.Add(panel);
        if(dialog.ShowDialog()!=true)return;
        var copy=new FigureAnimationEditor(_editor.Preview());
        await copy.CreateAnimationAsync(_name,id.Text.Trim(),Integer(length),bind.IsChecked==true);
        AnimationNames.Shared.Save(_scene.Model,id.Text.Trim(),label.Text);
        var child=new AnimationEditorDialog(_scene with{Animations=copy.Preview()},id.Text.Trim()){Owner=this};
        child._exported=Array.Empty<byte>(); // Newly added clip must be exported or saved, even before pose edits.
        child.ShowDialog();
    }
}
