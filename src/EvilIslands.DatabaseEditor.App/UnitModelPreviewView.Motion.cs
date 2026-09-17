using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using EvilIslands.DatabaseEditor.Core;
using V3=System.Numerics.Vector3;
namespace EvilIslands.DatabaseEditor.App;
public sealed partial class UnitModelPreviewView
{
    private readonly List<(int Offset,ModelVisual3D Visual,List<AnimatedMesh> Meshes)> _onions=[];
    private V3[] _trajectory=[];
    private string? _trajectoryPart;
    internal int OnionCount => _onions.Count;
    internal int TrajectoryCount => _trajectory.Length;
    internal void SetMotionGuides(string? part,bool trajectory,bool onion)
    {
        ClearMotionGuides(); _trajectoryPart=part;
        if(_clip is null||_animations is null)return;
        if(trajectory&&part is not null&&_animations.Parents.ContainsKey(part))
            _trajectory=Enumerable.Range(0,_clip.FrameCount).Select(f=>_animations.Pose(_clip,f)[part].Position).ToArray();
        if(onion)foreach(int offset in new[]{-3,-1,1,3})
        {
            var visual=new ModelVisual3D();var group=new Model3DGroup();var meshes=new List<AnimatedMesh>();
            var brush=new SolidColorBrush(offset<0?Color.FromArgb(32,255,150,60):Color.FromArgb(32,40,180,255));brush.Freeze();var material=new EmissiveMaterial(brush);material.Freeze();
            foreach(var source in _animatedMeshes){var geometry=source.Mesh.Clone();var model=new GeometryModel3D(geometry,material){BackMaterial=material};group.Children.Add(model);meshes.Add(new(source.Source,geometry,model,source.Bind));}
            visual.Content=group;_viewport.Children.Add(visual);_onions.Add((offset,visual,meshes));
        }
        ApplyAnimationFrame();
    }
    private void ClearMotionGuides(){foreach(var onion in _onions)_viewport.Children.Remove(onion.Visual);_onions.Clear();_trajectory=[];}
    private void RenderOnions(float frame)
    {
        if(_animations is null||_clip is null)return;
        foreach(var onion in _onions)
        {
            float f=frame+onion.Offset;
            if(_showOriginal||f<0||f>=_clip.FrameCount){if(_viewport.Children.Contains(onion.Visual))_viewport.Children.Remove(onion.Visual);continue;}
            if(!_viewport.Children.Contains(onion.Visual))_viewport.Children.Add(onion.Visual);
            RenderPose(_animations,_clip,f,_animations.Pose(_clip,f),onion.Meshes);
        }
    }
    private void DrawTrajectory()
    {
        if(_showOriginal||_trajectory.Length==0)return;
        var points=new PointCollection();
        for(int f=0;f<_trajectory.Length;f++)if(ProjectJoint(_trajectory[f]) is Point p)points.Add(p);
        _skeletonCanvas.Children.Add(new Polyline{Points=points,Stroke=Brushes.DeepSkyBlue,StrokeThickness=1.5,IsHitTestVisible=false});
        int step=Math.Max(1,_trajectory.Length/80);
        for(int f=0;f<_trajectory.Length;f+=step)
        {
            if(ProjectJoint(_trajectory[f]) is not Point p)continue;int frame=f;
            var dot=new Ellipse{Width=6,Height=6,Fill=Brushes.DeepSkyBlue,Cursor=Cursors.Hand,ToolTip="Кадр "+frame};
            System.Windows.Controls.Canvas.SetLeft(dot,p.X-3);System.Windows.Controls.Canvas.SetTop(dot,p.Y-3);
            dot.MouseLeftButtonDown+=(_,e)=>{e.Handled=true;PauseAnimation();SetAnimationFrame(frame);if(_trajectoryPart is not null)JointSelected?.Invoke(_trajectoryPart,frame);};_skeletonCanvas.Children.Add(dot);
        }
    }
}
