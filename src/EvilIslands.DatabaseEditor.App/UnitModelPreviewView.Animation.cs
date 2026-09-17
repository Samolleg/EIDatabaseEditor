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
    internal bool AllowAnimationEditing { get; init; } = true;
    private UnitModelScene? _editingScene;
    private readonly Button _editAnimation = new() { Content = "Редактор Анимаций", Margin = new Thickness(4) };
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
    internal int EditorFps { get => _fps.SelectedItem is int n ? n : 30; set => _fps.SelectedItem = value; }
    internal bool EditorLoop { get => _loop.IsChecked == true; set => _loop.IsChecked = value; }
    internal string? EditorAnimationName => _clip?.Name ?? _animations?.Names.FirstOrDefault(n=>n.Contains("idle",StringComparison.OrdinalIgnoreCase)) ?? _animations?.Names.FirstOrDefault();
    internal bool AnimationPlaying => _animationTimer.IsEnabled;
    internal string? ActiveAnimationName => _clip?.Name;
    internal double AnimationFrame => _frame.Value;
    internal void RestrictToEditedClip()
    {
        _clips.IsEnabled = false;
        _mobTools.Visibility = _itemTools.Visibility = _information.Visibility = Visibility.Collapsed;
    }
    internal void MoveEditorPlaybackTo(Panel destination)
    {
        if (_animationTools.Parent is Panel parent) parent.Children.Remove(_animationTools);
        _clips.Visibility = Visibility.Collapsed;
        if (_animationTools.Children[0] is TextBlock label) label.Visibility = Visibility.Collapsed;
        _frame.Width = 170; _fps.Width = 55;
        destination.Children.Add(_animationTools);
        if (Content is Grid layout)
        {
            layout.RowDefinitions[0].Height = new GridLength(0);
            layout.RowDefinitions[2].Height = new GridLength(0);
        }
        var fit = new Button { Content = "Вписать модель", Margin = new Thickness(4) };
        fit.Click += (_, _) => Fit(); destination.Children.Add(fit);
    }

    private void InitializeAnimationControls(DockPanel toolbar)
    {
        foreach (UIElement control in new UIElement[] { new TextBlock { Text = "Анимация", Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center }, _clips, _play, _pause, _stop, _frame, _frameText, _fps,
            new TextBlock { Text = "кадр/с", VerticalAlignment = VerticalAlignment.Center }, _loop, _editAnimation, _animationError }) _animationTools.Children.Add(control);
        _editAnimation.SetResourceReference(Button.StyleProperty,"AccentButton");
        _editAnimation.FontWeight=FontWeights.SemiBold;
        _editAnimation.ToolTip="Открыть редактор, задать русское название или создать новую анимацию";
        _clips.Width=290;
        _clips.ItemTemplate=Dialogs.AnimationLabelConverter.Template(()=>_editingScene?.Model??"");
        _editAnimation.Click += (_, _) =>
        {
            if (_editingScene is null || _animations is not {Names.Count: >0}) return;
            PauseAnimation();
            new Dialogs.AnimationEditorDialog(_editingScene, EditorAnimationName!) { Owner = Window.GetWindow(this) }.ShowDialog();
            _clips.Items.Refresh();
        };
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
        ClearMotionGuides(); _pendingEditorBounds=null;
        _clip = null; _animations = null; _animatedMeshes.Clear();
        _ghostMeshes.Clear(); _viewport.Children.Remove(_ghostVisual); _originalSet = null; _originalClip = null; _showOriginal = _showGhost = false;
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

    // Same geometry, new edited ANM: keep the visible pose while bounds are prepared.
    // Commit clip and selected frame together; never expose bind pose or frame zero.
    internal async Task ReplaceEditedAnimationAsync(FigureAnimationSet set,FigureAnimationClip clip,int frame)
    {
        PauseAnimation();
        var meshes=_animatedMeshes.Select(m=>m.Source).ToArray();
        var bounds=await Task.Run(()=>AnimationBounds(set,clip,meshes,CancellationToken.None));
        _clipCancellation?.Cancel();
        _animations=set;_clip=clip;
        if(_editingScene is not null)_editingScene=_editingScene with {Animations=set};
        _pendingEditorBounds=bounds;
        _settingFrame=true;
        _frame.Maximum=clip.FrameCount-1;_frame.Value=Math.Clamp(frame,0,clip.FrameCount-1);
        _settingFrame=false;
        _animationError.Visibility=Visibility.Collapsed;
        ApplyAnimationFrame();UpdateAnimationControls();
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
        _editAnimation.Visibility = AllowAnimationEditing ? Visibility.Visible : Visibility.Collapsed;
        _editAnimation.IsEnabled = EditorAnimationName is not null;
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

    private static void RenderPose(FigureAnimationSet set, FigureAnimationClip clip, float frame, IReadOnlyDictionary<string, FigurePose> pose, IEnumerable<AnimatedMesh> meshes)
    {
            foreach (var part in meshes)
            {
                var name = part.Source.Name;
                if (!pose.TryGetValue(name, out var node) || !set.BindPositions.TryGetValue(name, out var bind))
                    throw new InvalidDataException("Нет узла для детали: " + name);
                var track = clip.Tracks.GetValueOrDefault(name);
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
    }
    internal event Action<double>? FrameDisplayed;
    private FigureAnimationSet? _originalSet;
    private FigureAnimationClip? _originalClip;
    private bool _showOriginal, _showGhost;
    private readonly ModelVisual3D _ghostVisual = new();
    private readonly List<AnimatedMesh> _ghostMeshes = [];
    internal bool ShowingOriginal => _showOriginal;
    internal int GhostMeshCount => _ghostMeshes.Count;
    internal void SetComparison(FigureAnimationSet original, FigureAnimationClip clip, bool before, bool ghost)
    {
        _originalSet = original; _originalClip = clip; _showOriginal = before; _showGhost = ghost;
        _ghostMeshes.Clear(); _viewport.Children.Remove(_ghostVisual);
        if (ghost && !before)
        {
            var group = new Model3DGroup();
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 70, 190, 255)); brush.Freeze();
            var material = new EmissiveMaterial(brush); material.Freeze();
            foreach (var source in _animatedMeshes)
            {
                var mesh = source.Mesh.Clone(); var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                group.Children.Add(model); _ghostMeshes.Add(new(source.Source, mesh, model, source.Bind));
            }
            _ghostVisual.Content = group; _viewport.Children.Add(_ghostVisual);
        }
        ApplyAnimationFrame();
    }
    private void ApplyAnimationFrame()
    {
        if (_clip is null || _animations is null) return;
        try
        {
            float frame = (float)_frame.Value;
            var pose = _animations.Pose(_clip, frame, _gizmoPart, _gizmoRotation, _gizmoPosition, _ikRotations);
            if (_showOriginal && _originalSet is not null && _originalClip is not null)
                RenderPose(_originalSet, _originalClip, frame, _originalSet.Pose(_originalClip, frame), _animatedMeshes);
            else RenderPose(_animations, _clip, frame, pose, _animatedMeshes);
            if (_showGhost && !_showOriginal && _originalSet is not null && _originalClip is not null)
                RenderPose(_originalSet, _originalClip, frame, _originalSet.Pose(_originalClip, frame), _ghostMeshes);
            RenderOnions(frame);
            FrameDisplayed?.Invoke(frame);
            _frameText.Text = $"{frame + 1:0.0} / {_clip.FrameCount}";
            DrawSkeleton();
        }
        catch (Exception ex)
        { PauseAnimation(); _clip = null; RestoreBindPose(); UpdateAnimationControls(); _animationError.Text = "Ошибка анимации: " + ex.Message; _animationError.Visibility = Visibility.Visible; }
    }
}
