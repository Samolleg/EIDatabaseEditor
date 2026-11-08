using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App;

public sealed partial class UnitModelPreviewView
{
    private readonly ModelVisual3D _groundVisual = new();
    private bool _groundEnabled;
    private float _groundLevel;

    internal bool GroundEnabled
    {
        get => _groundEnabled;
        set
        {
            _groundEnabled = value;
            if (_viewport.Children.Contains(_groundVisual)) _viewport.Children.Remove(_groundVisual);
            if (value && _groundVisual.Content is not null) _viewport.Children.Insert(0, _groundVisual);
        }
    }

    internal float GroundLevel => _groundLevel;

    private void UpdateGround(UnitModelScene scene)
    {
        var vertices = scene.Meshes.SelectMany(m => m.Positions).ToArray();
        if (vertices.Length == 0) { _groundVisual.Content = null; GroundEnabled = _groundEnabled; return; }
        // ANM root positions are absolute. A floor below the bind mesh (whose hip
        // is often at zero) conceals a missing root height in a saved animation.
        _groundLevel = 0;
        float minX = vertices.Min(p => p.X), maxX = vertices.Max(p => p.X);
        float minY = vertices.Min(p => p.Y), maxY = vertices.Max(p => p.Y);
        float size = Math.Max(2, Math.Max(maxX - minX, maxY - minY) * 2.5f);
        float centerX = (minX + maxX) / 2, centerY = (minY + maxY) / 2;
        var group = new Model3DGroup();
        AddGroundTile(group, centerX, centerY, size, _groundLevel,
            new SolidColorBrush(Color.FromArgb(120, 43, 61, 52)));
        var line = new SolidColorBrush(Color.FromRgb(89, 112, 96));
        for (int i = -5; i <= 5; i++)
        {
            float offset = i * size / 10;
            AddGroundTile(group, centerX + offset, centerY, size / 500, _groundLevel + .002f, line, size);
            AddGroundTile(group, centerX, centerY + offset, size, _groundLevel + .002f, line, size / 500);
        }
        _groundVisual.Content = group;
        GroundEnabled = _groundEnabled;
    }

    private static void AddGroundTile(Model3DGroup group, float x, float y, float width, float level, Brush brush, float? depth = null)
    {
        float halfX = width / 2, halfY = (depth ?? width) / 2;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(x - halfX, level, -(y - halfY)), new(x + halfX, level, -(y - halfY)),
                new(x + halfX, level, -(y + halfY)), new(x - halfX, level, -(y + halfY))
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        var material = new DiffuseMaterial(brush);
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    // The floor is a preview/authoring aid: never alter imported clip frames merely by displaying it.
    internal Vector3 ClampToGround(string part, int frame, Vector3 target)
    {
        if (!_groundEnabled || _animations is null || _clip is null) return target;
        var pose = _animations.Pose(_clip, frame);
        if (!pose.TryGetValue(part, out var joint)) return target;
        var feet = new[] { "ll3", "rl3" }.Where(pose.ContainsKey).ToArray();
        if (feet.Length == 0) return target;
        var firstPose = _animations.Pose(_clip, 0);
        float clearance = Math.Max(0, feet.Where(firstPose.ContainsKey)
            .Select(foot => firstPose[foot].Position.Z - _groundLevel).DefaultIfEmpty(0).Min());
        if (feet.Contains(part, StringComparer.OrdinalIgnoreCase))
            return target with { Z = Math.Max(target.Z, _groundLevel + clearance) };
        if (_animations.Parents.GetValueOrDefault(part) != "") return target;
        float lowest = feet.Min(foot => pose[foot].Position.Z);
        float minimum = joint.Position.Z + _groundLevel + clearance - lowest;
        return target with { Z = Math.Max(target.Z, minimum) };
    }
}
