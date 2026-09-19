using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace EvilIslands.DatabaseEditor.App;

public sealed partial class UnitModelPreviewView
{
    private readonly ModelVisual3D _lighting = new();
    private readonly SolidColorBrush _sceneBackground = new(Color.FromRgb(22, 26, 24));
    private readonly TextBlock _cameraReadout = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private int _lightKind;
    private double _lightPower = 1, _ambientPower = .57;
    private readonly double[] _lightPosition = [1, 2, 3];

    private void InitializeViewSettings(DockPanel toolbar)
    {
        var panel = new StackPanel();
        var controls = new WrapPanel();
        var type = new ComboBox { Width = 160, Margin = new Thickness(4), ItemsSource = new[] { "Направленный", "Точечный", "Равномерный" }, SelectedIndex = 0 };
        type.SelectionChanged += (_, _) => { _lightKind = type.SelectedIndex; UpdateLighting(); };
        controls.Children.Add(type);
        void Number(string label, double initial, double min, double max, Action<double> apply)
        {
            controls.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
            var input = new TextBox { Text = initial.ToString(CultureInfo.InvariantCulture), Width = 64, Padding = new Thickness(4), Margin = new Thickness(2), ToolTip = $"{min} … {max}; Enter — применить" };
            void Commit()
            {
                if (double.TryParse(input.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= min && value <= max)
                { apply(value); input.ClearValue(Border.BorderBrushProperty); }
                else input.BorderBrush = Brushes.IndianRed;
            }
            input.LostKeyboardFocus += (_, _) => Commit();
            input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
            controls.Children.Add(input);
        }
        Number("Сила", 1, 0, 4, v => { _lightPower = v; UpdateLighting(); });
        Number("Заполнение", .57, 0, 1, v => { _ambientPower = v; UpdateLighting(); });
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            Number("XYZ"[i].ToString(), _lightPosition[i], -50, 50, v => { _lightPosition[axis] = v; UpdateLighting(); });
        }
        Number("FoV°", 40, 10, 120, v => { _camera.FieldOfView = v; UpdateCamera(); });
        panel.Children.Add(controls);
        panel.Children.Add(new TextBlock { Text = "Свет: XYZ относительно центра, в радиусах модели. Камера: координаты просмотра, Y вверх; вращение мышью, расстояние колёсиком.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        panel.Children.Add(_cameraReadout);
        var output = new WrapPanel();
        var background = new ComboBox { Width = 160, Margin = new Thickness(4), ItemsSource = new[] { "Тёмный фон", "Серый фон", "Белый фон", "Чёрный фон" }, SelectedIndex = 0 };
        background.SelectionChanged += (_, _) => _sceneBackground.Color = background.SelectedIndex switch { 1 => Colors.Gray, 2 => Colors.White, 3 => Colors.Black, _ => Color.FromRgb(22, 26, 24) };
        output.Children.Add(background);
        var capture = new Button { Content = "Сохранить кадр PNG…", Margin = new Thickness(4) };
        capture.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Изображение PNG|*.png", FileName = "model.png" };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            try { using var stream = System.IO.File.Create(picker.FileName); CaptureFrame().Save(stream); }
            catch (Exception ex) { _status.Text = "Не удалось сохранить кадр: " + ex.Message; }
        };
        output.Children.Add(capture); panel.Children.Add(output);
        var expander = new Expander { Header = "Свет и камера", Content = panel, Margin = new Thickness(4) };
        DockPanel.SetDock(expander, Dock.Top); toolbar.Children.Add(expander);
    }

    internal PngBitmapEncoder CaptureFrame()
    {
        int width = Math.Max(1, (int)Math.Ceiling(_viewport.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(_viewport.ActualHeight));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(_sceneBackground, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Render(_viewport);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); return encoder;
    }

    private void UpdateLighting()
    {
        static Color Power(double p) => Color.FromScRgb(1, (float)p, (float)p, (float)p);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Power(_lightKind == 2 ? _lightPower : _ambientPower)));
        var offset = new Vector3D(_lightPosition[0], _lightPosition[1], _lightPosition[2]);
        if (offset.LengthSquared < .000001) offset = new Vector3D(0, 0, 1);
        if (_lightKind == 0) lights.Children.Add(new DirectionalLight(Power(_lightPower), -offset));
        if (_lightKind == 1) lights.Children.Add(new PointLight(Power(_lightPower), _center + offset * _radius) { ConstantAttenuation = 1 });
        _lighting.Content = lights;
    }

    private void UpdateCameraReadout()
    {
        var p = _camera.Position - _center;
        _cameraReadout.Text = FormattableString.Invariant($"Камера относительно центра: X {p.X:F3} · Y {p.Y:F3} · Z {p.Z:F3}    Углы: X {_pitch * 180 / Math.PI:F1}° · Y {_yaw * 180 / Math.PI:F1}° · Z 0°    FoV {_camera.FieldOfView:F0}°");
    }
}
