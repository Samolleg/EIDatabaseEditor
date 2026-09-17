using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace EvilIslands.DatabaseEditor.App.Dialogs;
internal sealed class MotionGraph : FrameworkElement
{
    internal string[] Parts {get;set;}=[];
    internal bool[][] Changed {get;set;}=[];
    internal float[] Values {get;set;}=[];
    internal int Count {get;set;}=1;
    internal bool Curve {get;set;}
    internal event Action<string,int>? Seek;
    internal event Action<string,int,int>? MoveFrame;
    internal event Action<int,float>? EditValue;
    private int _from,_row;private Point _start;
    public MotionGraph(){MinWidth=200;Height=110;ToolTip="Дорожки: щелчок выбирает часть/кадр; перетаскивание переносит правку. Кривая: тяните точку по вертикали.";}
    private double Left=>Curve?35:130;
    private double X(int frame)=>Left+frame*Math.Max(1,ActualWidth-Left-10)/Math.Max(1,Count-1);
    private int At(double x)=>Math.Clamp((int)Math.Round((x-Left)*Math.Max(1,Count-1)/Math.Max(1,ActualWidth-Left-10)),0,Math.Max(0,Count-1));
    private (float Min,float Max) Bounds(){float min=Values.Length==0?0:Values.Min(),max=Values.Length==0?1:Values.Max();if(max-min<.001f){min-=1;max+=1;}float pad=(max-min)*.1f;return(min-pad,max+pad);}
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22,27,24)),null,new Rect(RenderSize));
        void Text(string text,double x,double y)=>dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,Brushes.WhiteSmoke,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        if(Curve)
        {
            var bounds=Bounds();double Y(float value)=>10+(bounds.Max-value)/(bounds.Max-bounds.Min)*Math.Max(1,ActualHeight-25);
            Text(bounds.Max.ToString("0.##"),0,0);Text(bounds.Min.ToString("0.##"),0,ActualHeight-15);
            for(int f=1;f<Values.Length;f++)dc.DrawLine(new Pen(Brushes.DeepSkyBlue,1.5),new Point(X(f-1),Y(Values[f-1])),new Point(X(f),Y(Values[f])));
            for(int f=0;f<Values.Length;f+=Math.Max(1,Values.Length/100))dc.DrawEllipse(Brushes.Gold,null,new Point(X(f),Y(Values[f])),2,2);
        }
        else for(int row=0;row<Parts.Length;row++)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0,row*22,Left-5,22)));Text(AnimationEditorDialog.PartLabel(Parts[row]),3,row*22+3);dc.Pop();
            dc.DrawLine(new Pen(Brushes.DimGray,1),new Point(Left,row*22+12),new Point(ActualWidth,row*22+12));
            if(row<Changed.Length)for(int f=0;f<Changed[row].Length;f++)if(Changed[row][f])dc.DrawRectangle(Brushes.Gold,null,new Rect(X(f)-2,row*22+8,4,8));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _start=e.GetPosition(this);_from=At(_start.X);_row=(int)(_start.Y/22);CaptureMouse();e.Handled=true;
        if(!Curve&&_row<Parts.Length)Seek?.Invoke(Parts[_row],_from);
        if(Curve&&Parts.Length>0)Seek?.Invoke(Parts[0],_from);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(!IsMouseCaptured)return;var p=e.GetPosition(this);ReleaseMouseCapture();e.Handled=true;
        if(Curve&&Math.Abs(p.Y-_start.Y)>3){var b=Bounds();EditValue?.Invoke(_from,b.Max-(float)Math.Clamp((p.Y-10)/Math.Max(1,ActualHeight-25),0,1)*(b.Max-b.Min));}
        else if(!Curve&&_row<Parts.Length&&(p-_start).Length>4)MoveFrame?.Invoke(Parts[_row],_from,At(p.X));
    }
}
