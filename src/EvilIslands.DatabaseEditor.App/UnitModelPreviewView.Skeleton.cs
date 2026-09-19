using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using EvilIslands.DatabaseEditor.Core;
using V3 = System.Numerics.Vector3;
using Q = System.Numerics.Quaternion;

namespace EvilIslands.DatabaseEditor.App;

public sealed partial class UnitModelPreviewView
{
    private readonly Canvas _skeletonCanvas = new() { ClipToBounds = true };
    private string[] _skeletonParts = [];
    private string? _selectedJoint, _gizmoPart;
    private Q? _gizmoRotation;
    private V3? _gizmoPosition;
    private Point? _gizmoStart;
    private int _gizmoAxis, _gizmoFrame;
    private bool _gizmoTranslate;
    private IReadOnlyDictionary<string, Q>? _ikRotations;
    private bool _ikGesture;
    private bool _pointJointGesture, _pointRootGesture;
    private bool _ikPoleGesture;
    private V3 _ikFixedEndpoint;
    internal event Action<int, IReadOnlyDictionary<string, Q>>? LimbEdited;
    internal Func<int, IReadOnlyDictionary<string, Q>, IReadOnlyDictionary<string, V3>, IReadOnlyDictionary<string, Q>>? PoseFilter;
    internal Action<string>? PoseRejected;
    private Q _originalRotation;
    private V3 _originalPosition;
    internal event Action<string, int>? JointSelected;
    internal event Action<string, int, Q, V3?>? JointEdited;
    internal void SetSkeleton(string[] parts, string? selected)
    { _skeletonParts = parts; _selectedJoint = selected; DrawSkeleton(); }
    private static readonly V3[] Axes = [V3.UnitX, V3.UnitY, V3.UnitZ];
    private static readonly Brush[] AxisBrushes = [Brushes.OrangeRed, Brushes.LimeGreen, Brushes.DeepSkyBlue];

    private Point? ProjectJoint(V3 p)
    {
        var world = new Point3D(p.X, p.Z, -p.Y);
        var forward = _camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var d = world - _camera.Position; double depth = Vector3D.DotProduct(d, forward);
        if (depth <= _camera.NearPlaneDistance || _viewport.ActualWidth <= 0) return null;
        double scale = _viewport.ActualWidth / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360) * depth);
        return new Point(_viewport.ActualWidth / 2 + Vector3D.DotProduct(d, right) * scale,
            _viewport.ActualHeight / 2 - Vector3D.DotProduct(d, up) * scale);
    }
    private void DrawSkeleton()
    {
        _skeletonCanvas.Children.Clear();
        DrawTrajectory();
        if (_showOriginal || _skeletonParts.Length == 0 || _animations is null || _clip is null) return;
        var pose = _animations.Pose(_clip, (float)AnimationFrame, _gizmoPart, _gizmoRotation, _gizmoPosition, _ikRotations);
        var visible = _skeletonParts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _skeletonParts)
        {
            if (!pose.TryGetValue(name, out var node) || ProjectJoint(node.Position) is not Point p) continue;
            if (_animations.Parents.TryGetValue(name, out var parent) && visible.Contains(parent) && pose.TryGetValue(parent, out var ancestor) && ProjectJoint(ancestor.Position) is Point a)
                _skeletonCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = p.X, Y2 = p.Y, Stroke = Brushes.Gold, StrokeThickness = 2, IsHitTestVisible = false });
            bool movable = parent == "" || FigureLimbIk.Supports(_animations, _clip, name) ||
                (FigureLimbIk.SupportsJoint(_animations, _clip, name) && pose.TryGetValue(parent!, out var pivot) && V3.DistanceSquared(node.Position, pivot.Position) >= 1e-10f);
            var dot = new Ellipse { Width = 18, Height = 18, Tag = name, Fill = name == _selectedJoint ? Brushes.Orange : FigureLimbIk.Supports(_animations, _clip, name) ? Brushes.Cyan : Brushes.White, Stroke = Brushes.Black, StrokeThickness = 2,
                Cursor = movable ? Cursors.SizeAll : Cursors.Hand, ToolTip = Dialogs.AnimationEditorDialog.PartLabel(name) + " — " + (movable ? JointDragHint(name) : "точка без подвижной родительской кости; используйте кольца вращения") };
            Panel.SetZIndex(dot, name == _selectedJoint ? 20 : 10); // Points remain reachable where rings/arrows cross them.
            Canvas.SetLeft(dot, p.X - 9); Canvas.SetTop(dot, p.Y - 9);
            dot.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; PauseAnimation(); _selectedJoint = name;
                JointSelected?.Invoke(name, (int)AnimationFrame); DrawSkeleton();
                if (BeginPointGesture(name, e.GetPosition(_skeletonCanvas)))
                { _skeletonCanvas.Focusable = true; _skeletonCanvas.Focus(); _skeletonCanvas.CaptureMouse(); }
            };
            _skeletonCanvas.Children.Add(dot);
        }
        if (_selectedJoint is not string selected || !pose.TryGetValue(selected, out var joint) || ProjectJoint(joint.Position) is not Point center) return;
        // Local-axis rings; screen motion rotates around the chosen local axis.
        for (int axis = 0; axis < 3; axis++)
        {
            int index = axis; double radius = 32 + 13 * axis;
            var ring = new Ellipse { Width = radius * 2, Height = radius * 2, Stroke = AxisBrushes[axis], StrokeThickness = 3,
                Cursor = Cursors.SizeWE, ToolTip = $"Поворот {"XYZ"[axis]}: тяните вправо/влево (1° на пиксель). Esc — отменить." };
            Canvas.SetLeft(ring, center.X - radius); Canvas.SetTop(ring, center.Y - radius);
            ring.MouseLeftButtonDown += (_, e) => BeginJointDrag(e, index, false);
            _skeletonCanvas.Children.Add(ring);
            if (_animations.Parents.GetValueOrDefault(selected) != "") continue;
            float length = (float)(_radius * .35);
            if (ProjectJoint(joint.Position + Axes[axis] * length) is not Point end || (end - center).Length < 10) continue;
            var arrow = new Line { X1 = center.X, Y1 = center.Y, X2 = end.X, Y2 = end.Y, Stroke = AxisBrushes[axis], StrokeThickness = 5,
                Cursor = Cursors.SizeAll, ToolTip = $"Перемещение корня по {"XYZ"[axis]}; Esc — отменить" };
            arrow.MouseLeftButtonDown += (_, e) => BeginJointDrag(e, index, true); _skeletonCanvas.Children.Add(arrow);
            var label = new TextBlock { Text = "▶ " + "XYZ"[axis], Foreground = AxisBrushes[axis], FontWeight = FontWeights.Bold, Cursor = Cursors.SizeAll };
            Canvas.SetLeft(label, end.X); Canvas.SetTop(label, end.Y);
            label.MouseLeftButtonDown += (_, e) => BeginJointDrag(e, index, true); _skeletonCanvas.Children.Add(label);
        }
        if (FigureLimbIk.Supports(_animations, _clip, selected) && ProjectJoint(pose[selected[..2] + "2"].Position) is Point elbow)
        {
            Point handle = elbow + new Vector(70, -35);
            _skeletonCanvas.Children.Add(new Line { X1 = elbow.X, Y1 = elbow.Y, X2 = handle.X, Y2 = handle.Y, Stroke = Brushes.Gold, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false });
            var bend = new Border { Background = Brushes.DarkGoldenrod, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 6, 3), Cursor = Cursors.SizeAll,
                ToolTip = "Направление локтя/колена: тяните при неподвижной кисти/стопе. Для другой плоскости поверните камеру.",
                Child = new TextBlock { Text = "◆ Сгиб", Foreground = Brushes.White } };
            Canvas.SetLeft(bend, handle.X); Canvas.SetTop(bend, handle.Y);
            bend.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (BeginBendGesture(selected, e.GetPosition(_skeletonCanvas)))
                { _skeletonCanvas.Focusable = true; _skeletonCanvas.Focus(); _skeletonCanvas.CaptureMouse(); }
            };
            _skeletonCanvas.Children.Add(bend);
        }
    }
    private void BeginJointDrag(MouseButtonEventArgs e, int axis, bool translate)
    {
        e.Handled = true;
        if (_selectedJoint is not string part || !BeginJointGesture(part, axis, translate, e.GetPosition(_skeletonCanvas))) return;
        _skeletonCanvas.Focusable = true; _skeletonCanvas.Focus(); _skeletonCanvas.CaptureMouse();
    }
    internal bool BeginJointGesture(string part, int axis, bool translate, Point start)
    {
        if (_clip is null || _animations is null || axis < 0 || axis > 2 || !_skeletonParts.Contains(part) || !_clip.Tracks.TryGetValue(part, out var track)) return false;
        if (translate && _animations.Parents.GetValueOrDefault(part) != "") return false;
        PauseAnimation(); _gizmoFrame = (int)AnimationFrame; SetAnimationFrame(_gizmoFrame);
        _gizmoPart = part; _gizmoAxis = axis; _gizmoTranslate = translate;
        _originalRotation = track.Rotation(_gizmoFrame);
        _originalPosition = track.Positions.Length > 0 ? track.Position(_gizmoFrame) : _animations.Bones.GetValueOrDefault(part);
        _gizmoStart = start;
        _animationTools.IsEnabled = false;
        return true;
    }
    private string JointDragHint(string part) => _animations!.Parents.GetValueOrDefault(part) == ""
        ? "тяните: перемещение модели в плоскости экрана"
        : FigureLimbIk.Supports(_animations, _clip!, part) ? "тяните: сгибание руки или ноги"
        : "тяните: поворот родительской кости вместе с её дочерними частями; кольца вращают выбранную часть";

    internal bool BeginPointGesture(string part, Point start)
    {
        if (_animations is null || _clip is null || !_skeletonParts.Contains(part)) return false;
        if (FigureLimbIk.Supports(_animations, _clip, part)) return BeginLimbGesture(part, start);
        bool root = _animations.Parents.GetValueOrDefault(part) == "";
        if (!root)
        {
            try { FigureLimbIk.SolveJoint(_animations, _clip, (int)AnimationFrame, part, _animations.Pose(_clip, (float)AnimationFrame)[part].Position); }
            catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException)
            { PoseRejected?.Invoke(ex.Message); return false; }
        }
        if (!BeginJointGesture(part, 0, root, start)) return false;
        _pointRootGesture = root; _pointJointGesture = !root;
        _originalPosition = _animations.Pose(_clip, _gizmoFrame)[part].Position;
        return true;
    }
    internal bool BeginLimbGesture(string part, Point start)
    {
        if (_animations is null || _clip is null || !_skeletonParts.Contains(part) || !FigureLimbIk.Supports(_animations, _clip, part)) return false;
        if (!BeginJointGesture(part, 0, false, start)) return false;
        _ikGesture = true; _originalPosition = _animations.Pose(_clip, _gizmoFrame)[part].Position;
        return true;
    }
    internal bool BeginBendGesture(string part, Point start)
    {
        if (!BeginLimbGesture(part, start)) return false;
        _ikPoleGesture = true; _ikFixedEndpoint = _originalPosition;
        _originalPosition = _animations!.Pose(_clip!, _gizmoFrame)[part[..2] + "2"].Position;
        return true;
    }
    private void InitializeSkeletonInput()
    {
        _skeletonCanvas.MouseMove += (_, e) =>
        {
            if (_gizmoStart is null) return;
            e.Handled = true; MoveJointGesture(e.GetPosition(_skeletonCanvas));
        };
        _skeletonCanvas.MouseLeftButtonUp += (_, e) => { if (_gizmoStart is not null) { e.Handled = true; CommitJointGesture(); } };
        _skeletonCanvas.KeyDown += (_, e) => { if (e.Key == Key.Escape && _gizmoStart is not null) { e.Handled = true; EndJointDrag(); } };
        _skeletonCanvas.LostMouseCapture += (_, _) => { if (_gizmoStart is not null) EndJointDrag(); };
        _skeletonCanvas.MouseWheel += (_, e) => { if (_gizmoStart is not null) e.Handled = true; };
    }
    internal void MoveJointGesture(Point point)
    {
            if (_gizmoStart is not Point start || _animations is null || _clip is null) return;
            var previousRotations = _ikRotations; var previousRotation = _gizmoRotation; var previousPosition = _gizmoPosition;
            void AcceptPreview()
            {
                if (PoseFilter is not null)
                {
                    var qs = (_ikGesture || _pointJointGesture) && _ikRotations is not null ? new Dictionary<string, Q>(_ikRotations) : new Dictionary<string, Q>();
                    var ps = new Dictionary<string, V3>();
                    if (_gizmoPart is string part) { if (_gizmoRotation is Q q) qs[part] = q; if (_gizmoPosition is V3 p) ps[part] = p; }
                    try { _ikRotations = PoseFilter(_gizmoFrame, qs, ps); }
                    catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException)
                    { _ikRotations = previousRotations; _gizmoRotation = previousRotation; _gizmoPosition = previousPosition; PoseRejected?.Invoke(ex.Message); }
                }
                ApplyAnimationFrame();
            }
            Vector movement = point - start;
            if (_ikGesture || _pointJointGesture || _pointRootGesture)
            {
                if (movement.Length < 3) { _ikRotations = null; _gizmoPosition = null; ApplyAnimationFrame(); return; }
                var forward = _camera.LookDirection; forward.Normalize();
                var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, forward);
                var world = new Point3D(_originalPosition.X, _originalPosition.Z, -_originalPosition.Y);
                double depth = Vector3D.DotProduct(world - _camera.Position, forward);
                double scale = 2 * Math.Tan(_camera.FieldOfView * Math.PI / 360) * depth / Math.Max(1, _viewport.ActualWidth);
                var offset = (right * movement.X - up * movement.Y) * scale;
                var target = _originalPosition + new V3((float)offset.X, (float)-offset.Z, (float)offset.Y);
                if (_pointRootGesture) { _gizmoPosition = target; AcceptPreview(); return; }
                try { _ikRotations = _pointJointGesture
                    ? FigureLimbIk.SolveJoint(_animations, _clip, _gizmoFrame, _gizmoPart!, target)
                    : FigureLimbIk.Solve(_animations, _clip, _gizmoFrame, _gizmoPart!, _ikPoleGesture ? _ikFixedEndpoint : target, _ikPoleGesture ? target : null); }
                catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException)
                { PoseRejected?.Invoke(ex.Message); return; }
                AcceptPreview(); return;
            }
            if (_gizmoTranslate)
            {
                var a = ProjectJoint(_originalPosition); var b = ProjectJoint(_originalPosition + Axes[_gizmoAxis]);
                if (a is null || b is null) return;
                var screenAxis = b.Value - a.Value;
                if (screenAxis.LengthSquared < 1) return;
                float amount = (float)(Vector.Multiply(movement, screenAxis) / screenAxis.LengthSquared);
                _gizmoPosition = _originalPosition + Axes[_gizmoAxis] * amount;
            }
            else _gizmoRotation = Q.Normalize(_originalRotation * Q.CreateFromAxisAngle(Axes[_gizmoAxis], (float)(movement.X * Math.PI / 180)));
            AcceptPreview();
    }
    internal void CommitJointGesture()
    {
            if (_gizmoStart is null || _gizmoPart is not string part) return;
            if (_ikGesture || _pointJointGesture)
            {
                int ikFrame = _gizmoFrame; var edits = _ikRotations;
                EndJointDrag(); if (edits is not null) LimbEdited?.Invoke(ikFrame, edits);
                return;
            }
            var rotation = _gizmoRotation ?? _originalRotation; var position = _gizmoPosition;
            int frame = _gizmoFrame; bool changed = _gizmoRotation.HasValue || position.HasValue;
            EndJointDrag();
            if (changed) JointEdited?.Invoke(part, frame, rotation, position);
    }
    internal IReadOnlyDictionary<string, FigurePose> CurrentJointPose => _animations!.Pose(_clip!, (float)AnimationFrame, _gizmoPart, _gizmoRotation, _gizmoPosition, _ikRotations);
    internal void EndJointDrag()
    {
        _ikGesture = false; _ikPoleGesture = false; _pointJointGesture = false; _pointRootGesture = false; _ikRotations = null;
        _gizmoStart = null; _gizmoPart = null; _gizmoRotation = null; _gizmoPosition = null;
        _animationTools.IsEnabled = true; _skeletonCanvas.ReleaseMouseCapture(); ApplyAnimationFrame();
    }
}
