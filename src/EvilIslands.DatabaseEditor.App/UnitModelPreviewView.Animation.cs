using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App;

public sealed partial class UnitModelPreviewView
{
    private const string BindPoseLabel = "Исходная поза";
    private readonly WrapPanel _animationTools = new() { Visibility = Visibility.Collapsed };
    private readonly ComboBox _clips = new() { Width = 190, Margin = new Thickness(4), IsTextSearchEnabled = true, ToolTip = "Клип из ANM. Начните вводить его имя для поиска." };
    private readonly Button _play = new() { Content = "▶ Играть", Margin = new Thickness(4) };
    private readonly Button _pause = new() { Content = "Ⅱ Пауза", Margin = new Thickness(4) };
    private readonly Button _stop = new() { Content = "■ Стоп", Margin = new Thickness(4) };
    private readonly Slider _frame = new() { Width = 160, Minimum = 0, Margin = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Кадр анимации" };
    private readonly TextBlock _frameText = new() { MinWidth = 85, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _fps = new() { Width = 65, Margin = new Thickness(4), ItemsSource = new[] { 10, 15, 20, 25, 30, 60 }, SelectedItem = 30, ToolTip = "Скорость предпросмотра, а не скорость действия в игре" };
    private readonly CheckBox _loop = new() { Content = "Повтор", IsChecked = true, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _animationError = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 450, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _animationTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly Stopwatch _animationClock = new();
    private FigureAnimationSet? _animations;
    private FigureAnimationClip? _clip;
    private CancellationTokenSource? _clipCancellation;
    private bool _settingFrame, _settingClips;
    private double _lastTick;
    private sealed record AnimatedMesh(FigureMesh Source, MeshGeometry3D Mesh, GeometryModel3D Model, Point3DCollection Bind);
    private readonly List<AnimatedMesh> _animatedMeshes = [];
    internal Task AnimationLoadTask { get; private set; } = Task.CompletedTask;
    internal bool AnimationPlaying => _animationTimer.IsEnabled;
    internal double AnimationFrame => _frame.Value;

    private void InitializeAnimationControls(DockPanel toolbar)
    {
        foreach (UIElement control in new UIElement[] { new TextBlock { Text = "Анимация", Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center }, _clips, _play, _pause, _stop, _frame, _frameText, _fps,
            new TextBlock { Text = "кадр/с", VerticalAlignment = VerticalAlignment.Center }, _loop, _animationError }) _animationTools.Children.Add(control);
        DockPanel.SetDock(_animationTools, Dock.Top); toolbar.Children.Add(_animationTools);
        _clips.SelectionChanged += (_, _) => { if (!_settingClips) AnimationLoadTask = SelectAnimationAsync(_clips.SelectedItem as string); };
        _play.Click += (_, _) => AnimationLoadTask = PlayAnimationAsync();
        _pause.Click += (_, _) => PauseAnimation();
        _stop.Click += (_, _) => StopAnimation();
        _frame.ValueChanged += (_, _) => { if (!_settingFrame) { _lastTick = _animationClock.Elapsed.TotalSeconds; ApplyAnimationFrame(); } };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
    }

    private void ResetAnimation()
    {
        PauseAnimation();
        _clipCancellation?.Cancel(); _clipCancellation?.Dispose(); _clipCancellation = null;
        _clip = null; _animations = null; _animatedMeshes.Clear();
        _animationError.Visibility = Visibility.Collapsed;
        _settingClips = true; _clips.ItemsSource = null; _settingClips = false;
        _animationTools.Visibility = Visibility.Collapsed;
    }

    private void SetAnimations(FigureAnimationSet? animations)
    {
        _animations = animations;
        _animationTools.Visibility = animations is { Names.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _settingClips = true;
        _clips.ItemsSource = new[] { BindPoseLabel }.Concat(animations?.Names ?? []).ToArray();
        _clips.SelectedIndex = 0; _settingClips = false;
        UpdateAnimationControls();
    }

    internal async Task SelectAnimationAsync(string? name)
    {
        PauseAnimation();
        _clipCancellation?.Cancel(); _clipCancellation?.Dispose();
        _clipCancellation = new CancellationTokenSource();
        var token = _clipCancellation.Token;
        _clip = null; RestoreBindPose(); UpdateAnimationControls();
        _animationError.Visibility = Visibility.Collapsed;
        _settingClips = true; _clips.SelectedItem = name ?? BindPoseLabel; _settingClips = false;
        if (_animations is null || name is null or BindPoseLabel) return;
        _play.IsEnabled = false; _frameText.Text = "Загрузка…";
        try
        {
            var animations = _animations;
            var meshes = _animatedMeshes.Select(m => m.Source).ToArray();
            var clip = await animations.LoadAsync(name, token);
            var bounds = await Task.Run(() => AnimationBounds(animations, clip, meshes, token), token);
            token.ThrowIfCancellationRequested();
            _clip = clip;
            _settingFrame = true; _frame.Maximum = clip.FrameCount - 1; _frame.Value = 0; _settingFrame = false;
            ApplyAnimationFrame(); UpdateAnimationControls();
            SetCameraBounds(bounds); Fit();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (!token.IsCancellationRequested) { _clip = null; RestoreBindPose(); UpdateAnimationControls(); _frameText.Text = "Ошибка клипа"; _animationError.Text = "Не удалось воспроизвести " + name + ": " + ex.Message; _animationError.Visibility = Visibility.Visible; } }
    }

    internal async Task PlayAnimationAsync()
    {
        var animations = _animations;
        if (animations is null) return;
        if (_clip is null)
        {
            string? name = _clips.SelectedItem as string;
            if (name is null or BindPoseLabel) name = animations.Names.FirstOrDefault(n => n.Equals("cidle01", StringComparison.OrdinalIgnoreCase))
                ?? animations.Names.FirstOrDefault(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase)) ?? animations.Names.FirstOrDefault();
            await SelectAnimationAsync(name);
        }
        if (_clip is null || !ReferenceEquals(animations, _animations)) return;
        if (_frame.Value >= _frame.Maximum) _frame.Value = 0;
        _animationClock.Restart(); _lastTick = 0; _animationTimer.Start(); UpdateAnimationControls();
    }

    internal void PauseAnimation()
    { _animationTimer.Stop(); _animationClock.Stop(); UpdateAnimationControls(); }
    internal void StopAnimation()
    { PauseAnimation(); SetAnimationFrame(0); }
    internal void SetAnimationFrame(double frame)
    { double next = Math.Clamp(frame, 0, _frame.Maximum); if (_frame.Value == next) ApplyAnimationFrame(); else _frame.Value = next; }

    private void UpdateAnimationControls()
    {
        _play.IsEnabled = _animations is { Names.Count: > 0 } && !AnimationPlaying;
        _pause.IsEnabled = AnimationPlaying;
        _stop.IsEnabled = _frame.IsEnabled = _clip is not null;
        if (_clip is null) _frameText.Text = BindPoseLabel;
    }

    private void AdvanceAnimation()
    {
        if (_clip is null) { PauseAnimation(); return; }
        double now = _animationClock.Elapsed.TotalSeconds;
        double next = _frame.Value + (now - _lastTick) * (_fps.SelectedItem is int fps ? fps : 30);
        _lastTick = now;
        if (next > _frame.Maximum)
        {
            if (_loop.IsChecked == true && _frame.Maximum > 0) next %= _frame.Maximum;
            else { next = _frame.Maximum; PauseAnimation(); }
        }
        SetAnimationFrame(next);
    }

    private void RestoreBindPose()
    {
        foreach (var part in _animatedMeshes) { part.Model.Transform = Transform3D.Identity; part.Mesh.Positions = part.Bind; }
        if (_visual.Content is { } content) { SetCameraBounds(content.Bounds); Fit(); }
    }

    // Fit once to the whole clip, not every tick. Mesh data only: no WPF objects
    // or texture decoding on the worker. Half-frame samples cover rotation arcs.
    private static Rect3D AnimationBounds(FigureAnimationSet set, FigureAnimationClip clip, FigureMesh[] meshes, CancellationToken token)
    {
        var min = new System.Numerics.Vector3(float.PositiveInfinity);
        var max = new System.Numerics.Vector3(float.NegativeInfinity);
        for (int step = 0; step <= (clip.FrameCount - 1) * 2; step++)
        {
            token.ThrowIfCancellationRequested();
            float frame = step / 2f; var pose = set.Pose(clip, frame);
            foreach (var mesh in meshes)
            {
                var node = pose[mesh.Name]; var bind = set.BindPositions[mesh.Name];
                var track = clip.Tracks.GetValueOrDefault(mesh.Name);
                for (int v = 0; v < mesh.Positions.Length; v++)
                {
                    var delta = track is { Morphs.Length: > 0 } ? track.Morph(frame, mesh.SourceVertices[v]) : System.Numerics.Vector3.Zero;
                    var p = System.Numerics.Vector3.Transform(mesh.Positions[v] - bind + delta, node.Rotation) + node.Position;
                    min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p);
                }
            }
        }
        return new Rect3D(min.X, min.Z, -max.Y, max.X - min.X, max.Z - min.Z, max.Y - min.Y);
    }

    private void ApplyAnimationFrame()
    {
        if (_clip is null || _animations is null) return;
        try
        {
            float frame = (float)_frame.Value;
            var pose = _animations.Pose(_clip, frame);
            foreach (var part in _animatedMeshes)
            {
                var name = part.Source.Name;
                if (!pose.TryGetValue(name, out var node) || !_animations.BindPositions.TryGetValue(name, out var bind))
                    throw new InvalidDataException("Нет узла для детали: " + name);
                var track = _clip.Tracks.GetValueOrDefault(name);
                if (track is { Morphs.Length: > 0 })
                {
                    var points = new Point3DCollection(part.Source.Positions.Length);
                    for (int v = 0; v < part.Source.Positions.Length; v++)
                    {
                        var p = part.Source.Positions[v] + track.Morph(frame, part.Source.SourceVertices[v]);
                        points.Add(new Point3D(p.X, p.Z, -p.Y));
                    }
                    part.Mesh.Positions = points;
                }
                else part.Mesh.Positions = part.Bind;
                var matrix = Matrix3D.Identity;
                matrix.Translate(new Vector3D(-bind.X, -bind.Z, bind.Y));
                var q = node.Rotation;
                matrix.Rotate(new Quaternion(q.X, q.Z, -q.Y, q.W));
                matrix.Translate(new Vector3D(node.Position.X, node.Position.Z, -node.Position.Y));
                part.Model.Transform = new MatrixTransform3D(matrix);
            }
            _frameText.Text = $"{frame + 1:0.0} / {_clip.FrameCount}";
        }
        catch (Exception ex)
        { PauseAnimation(); _clip = null; RestoreBindPose(); UpdateAnimationControls(); _animationError.Text = "Ошибка анимации: " + ex.Message; _animationError.Visibility = Visibility.Visible; }
    }
}
