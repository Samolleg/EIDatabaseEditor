using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class ViewerRegression
{
    public static int Run(string database, string output)
    {
        Directory.CreateDirectory(output);
        var records = DatabaseDocument.Open(database).Databases.SelectMany(g => g.Tables).SelectMany(t => t.Records.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var targets = records.Where(r => (r.Table == "units.prototypes" || ItemModelPreview.Supports(r.Table)) && new[] { "Human Hero", "Human Kania Police KHelm A10", "cheat dagger", "kania high2 helm" }.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        File.WriteAllLines(Path.Combine(output, "helmet-records.txt"), records.Where(r => r.Table == "units.prototypes" && r.Name.Contains("Khelm", StringComparison.OrdinalIgnoreCase)).Select(r => r.Name));
        var hero = records.First(r => r.Table == "units.prototypes" && r.Name == "Human Hero");
        var fields = hero.Fields.ToDictionary(p => p.Key, p => p.Value); fields[33] = "cheat dagger.bronze";
        targets = targets.Append(hero with { Name = "Hero-dagger-fixture", Fields = fields }).ToArray();
        foreach (var record in targets)
        {
            bool item = ItemModelPreview.Supports(record.Table);
            var scene = item ? ItemModelPreview.LoadAsync(database, record, records, default, records.First(r => r.Table == "items.materials" && r.Name == "bronze")).GetAwaiter().GetResult()
                : UnitModelPreview.LoadAsync(database, record, records).GetAwaiter().GetResult();
            foreach (var mesh in scene.Meshes)
            {
                if (mesh.Normals.Length != mesh.Positions.Length) throw new Exception("Normals missing: " + mesh.Name);
                if (mesh.Normals.Any(n => n.Length() > .001 && Math.Abs(n.Length() - 1) > .02)) throw new Exception("Invalid packed FIG normals: " + mesh.Name);
            }
            if (record.Name == "Human Hero" && scene.Model == "unhuma")
            {
                var dagger = scene.Meshes.Single(m => m.Name == "rh3.dagger03");
                if (dagger.UV.Any(p => p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)) throw new Exception("Original dagger atlas repeats: doubled UV regression");
            }
            var view = new UnitModelPreviewView { Width = 1000, Height = 850 };
            view.Measure(new Size(1000, 850)); view.Arrange(new Rect(0, 0, 1000, 850)); view.ShowScene(scene, item);
            if (!item && scene.Animations is { } animations)
            {
                string clip = record.Name.Contains("KHelm", StringComparison.OrdinalIgnoreCase) ? "cidle12" : "cidle10";
                if (animations.Names.Contains(clip))
                {
                    var task = view.SelectAnimationAsync(clip);
                    var dispatcherFrame = new System.Windows.Threading.DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcherFrame.Dispatcher.BeginInvoke(new Action(() => dispatcherFrame.Continue = false)));
                    if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(dispatcherFrame);
                    task.GetAwaiter().GetResult(); view.SetAnimationFrame(49.6);
                }
            }
            var visual = (ModelVisual3D)Field("_visual").GetValue(view)!;
            if (((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().Any(m => ((MeshGeometry3D)m.Geometry).Normals.Count == 0)) throw new Exception("Renderer discarded normals");
            foreach (int kind in new[] { 0, 1, 2 })
            {
                Field("_lightKind").SetValue(view, kind); Method("UpdateLighting").Invoke(view, null);
                var lights = (Model3DGroup)((ModelVisual3D)Field("_lighting").GetValue(view)!).Content;
                if (kind == 1 && !lights.Children.OfType<PointLight>().Any()) throw new Exception("Point light missing");
                if (kind == 2 && lights.Children.Count != 1) throw new Exception("Flat light failed");
            }
            Field("_lightKind").SetValue(view, 0); Method("UpdateLighting").Invoke(view, null);
            var camera = (PerspectiveCamera)Field("_camera").GetValue(view)!;
            camera.FieldOfView = 60; Method("Fit").Invoke(view, null);
            double wideDistance = camera.LookDirection.Length;
            camera.FieldOfView = 30; Method("Fit").Invoke(view, null);
            if (camera.LookDirection.Length <= wideDistance) throw new Exception("FoV fitting ignored");
            camera.FieldOfView = 40; Method("Fit").Invoke(view, null);
            foreach (var expander in Children(view).OfType<Expander>()) expander.IsExpanded = true;
            view.UpdateLayout();
            using (var frameOutput = File.Create(Path.Combine(output, record.Name + "-frame.png"))) view.CaptureFrame().Save(frameOutput);
            if (record.Name == "Hero-dagger-fixture")
            {
                var secondary = scene.Meshes.Where(m => m.Name.Contains("dagger")).ToArray();
                for (int variant = 0; variant < 3; variant++)
                {
                    var meshes = secondary.Select(m => m with { UV = m.UV.Select(p => variant == 0 ? p : variant == 1 ? p / 2 : new System.Numerics.Vector2(p.X, 1 - p.Y)).ToArray() }).ToArray();
                    view.ShowScene(scene with { Meshes = meshes, Animations = null }, true); view.UpdateLayout();
                    using var isolated = File.Create(Path.Combine(output, "dagger-uv-" + variant + ".png")); view.CaptureFrame().Save(isolated);
                }
                foreach (var pair in scene.Textures)
                {
                    var t = pair.Value; var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(t.Width,t.Height,96,96,t.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32,null,t.Bgra,t.Width*4)));
                    using var texture = File.Create(Path.Combine(output,"texture-"+pair.Key+".png")); png.Save(texture);
                }
            }
            var bitmap = new RenderTargetBitmap(1000, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, record.Name + ".png"))) encoder.Save(stream);
            File.WriteAllLines(Path.Combine(output, record.Name + ".txt"), new[] { record.Table + "/" + record.Name, scene.Model, "WEAPON=" + record.Text(33), "ARMOR=" + string.Join(";", record.Lists.GetValueOrDefault(32, [])) }.Concat(scene.Meshes.Select(m => $"{m.Name}: vertices={m.Positions.Length}, normals={m.Normals.Length}; UV {m.UV.Min(p=>p.X)}..{m.UV.Max(p=>p.X)} / {m.UV.Min(p=>p.Y)}..{m.UV.Max(p=>p.Y)}; texture={m.TextureSlot}")).Concat(scene.Textures.Select(t=>$"Texture {t.Key}: {t.Value.Width}x{t.Value.Height}")).Concat(scene.Sources).Concat(scene.Notes));
            Console.WriteLine("PASS " + record.Name + " normals, lighting, camera; " + scene.Model);
        }
        if (targets.Length < 2) throw new Exception("Required records missing");
        app.Shutdown(); return 0;
    }
    private static FieldInfo Field(string name) => typeof(UnitModelPreviewView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(UnitModelPreviewView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Children(child)) yield return descendant; }
    }
}
