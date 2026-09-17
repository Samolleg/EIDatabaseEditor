using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed class AnimationTimeline : FrameworkElement
{
    internal int Count { get; set; } = 1;
    internal int First { get; set; }
    internal int Last { get; set; }
    internal double Frame { get; set; }
    internal event Action<int, int>? RangeChanged;
    internal event Action<int>? Seek;
    private int _anchor;
    private bool _selecting;
    public AnimationTimeline()
    {
        Height = 76; MinWidth = 150; Focusable = true;
        ToolTip = "Щелчок — кадр; Shift + перетаскивание — диапазон. Стрелки ←/→ — соседний кадр.";
    }
    private double X(double frame) => 12 + frame * Math.Max(1, ActualWidth - 24) / Math.Max(1, Count - 1);
    private int At(double x) => Math.Clamp((int)Math.Round((x - 12) * Math.Max(1, Count - 1) / Math.Max(1, ActualWidth - 24)), 0, Count - 1);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 29, 26)), null, new Rect(RenderSize));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 92, 61)), null, new Rect(X(First) - 3, 23, Math.Max(6, X(Last) - X(First) + 6), 25));
        int step = Math.Max(1, (int)Math.Ceiling(Count / Math.Max(1, ActualWidth / 50)));
        void Text(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.WhiteSmoke, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        for (int f = 0; f < Count; f += step) { double x = X(f); dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, 22), new Point(x, 49)); Text(f.ToString(), x - 3, 5); }
        dc.DrawLine(new Pen(Brushes.Gold, 2), new Point(X(Frame), 20), new Point(X(Frame), 52));
        Text($"Кадр {(int)Frame} · Выделено {First}–{Last} ({Last - First + 1}) · Shift + мышь: диапазон", 12, 55);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); _anchor = At(e.GetPosition(this).X); _selecting = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        CaptureMouse(); Update(e.GetPosition(this).X); e.Handled = true;
    }
    private void Update(double x)
    {
        int frame = At(x);
        if (_selecting) { First = Math.Min(_anchor, frame); Last = Math.Max(_anchor, frame); RangeChanged?.Invoke(First, Last); }
        else { Frame = frame; Seek?.Invoke(frame); }
        InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Update(e.GetPosition(this).X); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { Update(e.GetPosition(this).X); ReleaseMouseCapture(); e.Handled = true; } base.OnMouseLeftButtonUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) { Frame = Math.Clamp((int)Frame + (e.Key == Key.Left ? -1 : 1), 0, Count - 1); Seek?.Invoke((int)Frame); InvalidateVisual(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
