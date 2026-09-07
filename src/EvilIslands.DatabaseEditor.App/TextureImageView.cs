using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EvilIslands.DatabaseEditor.App;

public sealed class TextureImageView : Border
{
    public static readonly DependencyProperty BitmapProperty = DependencyProperty.Register(
        nameof(Bitmap), typeof(BitmapSource), typeof(TextureImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(TextureImageView), new PropertyMetadata(0d, Changed));
    public BitmapSource? Bitmap { get => (BitmapSource?)GetValue(BitmapProperty); set => SetValue(BitmapProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly ScrollViewer _scroll;

    public TextureImageView()
    {
        Height = 210;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(80, 80, 80)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var light = new SolidColorBrush(Color.FromRgb(112, 112, 112));
        drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var checker = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16) };
        checker.Freeze();
        _scroll = new ScrollViewer
        {
            Background = checker, Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Child = _scroll;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        SizeChanged += (_, _) => UpdateImage();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TextureImageView)d).UpdateImage();
    private void UpdateImage()
    {
        _image.Source = Bitmap;
        if (Bitmap is null) { _image.Width = 0; _image.Height = 0; return; }
        double scale = Zoom > 0 && double.IsFinite(Zoom) ? Math.Clamp(Zoom, 0.125, 8) :
            Math.Min(Math.Max(1, ActualWidth - 20) / Bitmap.PixelWidth, (Height - 20) / Bitmap.PixelHeight);
        _image.Width = Bitmap.PixelWidth * scale;
        _image.Height = Bitmap.PixelHeight * scale;
    }
}

