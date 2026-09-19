using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

// Draw only visible cards; a 10,000-frame clip does not create 10,000 WPF controls.
internal sealed class AnimationStoryboard : FrameworkElement
{
    internal const double CardWidth = 86;
    private FigureAnimationSet? _set;
    private FigureAnimationClip? _clip;
    private HashSet<string> _parts = new();
    private readonly Dictionary<int, Point[]> _points = new();
    private (int A, int B)[] _edges = [];
    private string[] _keys = [];
    internal int First, Last, Current;
    internal double Offset, Viewport = 800;
    internal event Action<int, bool>? Selected;
    public AnimationStoryboard() { Height = 112; Focusable = true; ToolTip = "Щелчок — выбрать кадр; Shift — диапазон. Ctrl+C — копировать, Ctrl+V — заменить позу; Delete — удалить."; }
    internal void SetClip(FigureAnimationSet set, FigureAnimationClip clip, IEnumerable<string> parts)
    {
        _set = set; _clip = clip; _parts = parts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _keys = clip.Tracks.Keys.Where(_parts.Contains).ToArray();
        _edges = _keys.Select((k, i) => (A: i, B: Array.IndexOf(_keys, set.Parents.GetValueOrDefault(k)))).Where(e => e.B >= 0).ToArray();
        _points.Clear(); Width = clip.FrameCount * CardWidth; InvalidateVisual();
    }
    private Point[] Pose(int frame)
    {
        if (_points.TryGetValue(frame, out var result)) return result;
        var pose = _set!.Pose(_clip!, frame);
        var points = _keys.Select(k => new Point(pose[k].Position.X + .35 * pose[k].Position.Y, -pose[k].Position.Z)).ToArray();
        if (points.Length == 0) return [];
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double scale = Math.Min(66 / Math.Max(.01, maxX - minX), 70 / Math.Max(.01, maxY - minY));
        result = points.Select(p => new Point(43 + (p.X - (minX + maxX) / 2) * scale, 46 + (p.Y - (minY + maxY) / 2) * scale)).ToArray();
        if (_points.Count > 160) _points.Clear();
        return _points[frame] = result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (_clip is null) return;
        int first = Math.Max(0, (int)(Offset / CardWidth)), last = Math.Min(_clip.FrameCount - 1, (int)((Offset + Viewport) / CardWidth) + 1);
        for (int f = first; f <= last; f++)
        {
            double x = f * CardWidth; bool selected = f >= First && f <= Last;
            dc.DrawRoundedRectangle(new SolidColorBrush(selected ? Color.FromRgb(37, 81, 57) : Color.FromRgb(27, 33, 29)), new Pen(f == Current ? Brushes.Gold : Brushes.DimGray, f == Current ? 2 : 1), new Rect(x + 3, 3, 80, 104), 4, 4);
            var points = Pose(f);
            Point P(int i) => new(x + points[i].X, points[i].Y);
            foreach (var edge in _edges) dc.DrawLine(new Pen(Brushes.LightGreen, 2), P(edge.A), P(edge.B));
            foreach (var p in points) dc.DrawEllipse(Brushes.WhiteSmoke, null, new Point(x + p.X, p.Y), 2, 2);
            dc.DrawText(new FormattedText(f.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x + 8, 88));
        }
    }
    internal void SelectFrame(int frame, bool extend)
    {
        if (_clip is null) return;
        Selected?.Invoke(Math.Clamp(frame, 0, _clip.FrameCount - 1), extend);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); SelectFrame((int)(e.GetPosition(this).X / CardWidth), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            int target = e.Key switch { Key.Home => 0, Key.End => (_clip?.FrameCount ?? 1) - 1, Key.Left => Current - 1, _ => Current + 1 };
            SelectFrame(target, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
