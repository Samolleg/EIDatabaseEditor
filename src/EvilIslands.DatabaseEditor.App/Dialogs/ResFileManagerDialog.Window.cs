using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace EvilIslands.DatabaseEditor.App.Dialogs;
public partial class ResFileManagerDialog
{
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void InitializeWindowBounds()
    {
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowBoundsHook);
            var info = new MonitorData { Size = Marshal.SizeOf<MonitorData>() };
            if (!GetMonitorInfoW(MonitorFromWindow(handle, 2), ref info)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double w = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
            double h = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
            MinWidth = Math.Min(MinWidth, w); MinHeight = Math.Min(MinHeight, h);
            Width = Math.Min(Width, w); Height = Math.Min(Height, h);
            Left = info.Work.Left / dpi.DpiScaleX + (w - Width) / 2;
            Top = info.Work.Top / dpi.DpiScaleY + (h - Height) / 2;
            if (w < 1100) { FoldersColumn.Width = new GridLength(200); _detailsWidth = 240; SetDetailsVisible(_layout.ResManagerDetailsVisible); }
        };
    }
    private IntPtr WindowBoundsHook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg != 0x24 || lp == IntPtr.Zero) return IntPtr.Zero;
        var info = new MonitorData { Size = Marshal.SizeOf<MonitorData>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info)) return IntPtr.Zero;
        var mm = Marshal.PtrToStructure<MinMaxData>(lp);
        mm.MaxPosition = new PointData { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        mm.MaxSize = new PointData { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        mm.MaxTrack = mm.MaxSize;
        mm.MinTrack.X = Math.Min(mm.MinTrack.X, mm.MaxSize.X); mm.MinTrack.Y = Math.Min(mm.MinTrack.Y, mm.MaxSize.Y);
        Marshal.StructureToPtr(mm, lp, false); handled = true; return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointData { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectData { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorData { public int Size; public RectData Monitor,Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxData { public PointData Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorData info);
}
