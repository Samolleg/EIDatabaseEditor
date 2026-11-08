using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using System.Numerics;
using System.Windows.Threading;
using System.Windows.Media.Media3D;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class MapObjectCatalogRegression
{
    public static int Run(string databasePath)
    {
        try
        {
            var entries = MapObjectResourceCatalog.DiscoverAsync(databasePath).GetAwaiter().GetResult();
            if (entries.Count == 0) throw new Exception("No model resources were indexed.");
            var mapObjects = entries.Where(e => e.ArchiveName.Equals("HDPack_Mapobjects.res", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (mapObjects.Length != 49) throw new Exception($"Expected 49 map objects, found {mapObjects.Length}.");
            var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dialog = new MapObjectCatalogDialog(databasePath);
            var reload = (Task)typeof(MapObjectCatalogDialog).GetMethod("ReloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null)!;
            var frame = new DispatcherFrame();
            _ = reload.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            if (!reload.IsCompleted) Dispatcher.PushFrame(frame);
            reload.GetAwaiter().GetResult();
            if (dialog.Entries.Count != entries.Count) throw new Exception("Dialog catalogue differs from archive scan.");
            var root = (DockPanel)((Border)dialog.Content).Child;
            var body = (DockPanel)root.Children[^1];
            var layout = (Grid)body.Children[0];
            layout.Measure(new Size(1200, 760)); layout.Arrange(new Rect(0, 0, 1200, 760));
            var dialogPreview = (UnitModelPreviewView)typeof(MapObjectCatalogDialog)
                .GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            if (dialogPreview.ActualHeight < 600 || dialogPreview.ActualWidth < 400 || !dialog.HasVisibleScene)
                throw new Exception($"Compact layout leaves too little room for the scene: {dialogPreview.ActualWidth:F0}×{dialogPreview.ActualHeight:F0}.");
            var search = (TextBox)typeof(MapObjectCatalogDialog).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dialog)!;
            search.Text = "nast9";
            if (dialog.VisibleEntries.Count != 1 || dialog.VisibleEntries[0].Model != "nast9")
                throw new Exception("Live model search failed.");
            search.Text = "__no_such_map_object__";
            if (dialog.VisibleEntries.Count != 0) throw new Exception("Live search did not clear unmatched entries.");
            var textureSources = TextureSources.FromDatabaseFolderAsync(databasePath).GetAwaiter().GetResult();
            var textures = TextureResourceCatalog.OpenAsync(textureSources).GetAwaiter().GetResult();
            if (textures.ResourceNames.Count == 0) throw new Exception("No MMP textures indexed beside database.");
            var textureSearch = (TextBox)typeof(MapObjectCatalogDialog).GetField("_textureSearch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dialog)!;
            textureSearch.Text = textures.ResourceNames[0];
            var primary = (ComboBox)typeof(MapObjectCatalogDialog).GetField("_primary", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dialog)!;
            if (!primary.Items.Cast<string>().Contains(textures.ResourceNames[0]))
                throw new Exception("Live MMP search failed.");
            dialog.Close();
            int changedGeometry = 0;
            var comparison = mapObjects[0];
            var neutral = MapObjectResourceCatalog.LoadAsync(comparison).GetAwaiter().GetResult();
            var low = MapObjectResourceCatalog.LoadAsync(comparison, Vector3.Zero).GetAwaiter().GetResult();
            var high = MapObjectResourceCatalog.LoadAsync(comparison, Vector3.One).GetAwaiter().GetResult();
            if (neutral.Meshes.SelectMany(m => m.Positions).Zip(low.Meshes.SelectMany(m => m.Positions), Vector3.Distance).Any(d => d > .0001f) ||
                neutral.Meshes.SelectMany(m => m.Positions).Zip(high.Meshes.SelectMany(m => m.Positions), Vector3.Distance).Any(d => d > .0001f)) changedGeometry++;
            if (changedGeometry == 0) throw new Exception("Mapper morph does not change map-object geometry.");
            var pictured = mapObjects.Single(e => e.Model.Equals("stwe3", StringComparison.OrdinalIgnoreCase));
            var picturedLow = MapObjectResourceCatalog.LoadAsync(pictured, Vector3.Zero).GetAwaiter().GetResult();
            var picturedHigh = MapObjectResourceCatalog.LoadAsync(pictured, Vector3.One).GetAwaiter().GetResult();
            bool picturedMorphs = picturedLow.Meshes.SelectMany(m => m.Positions)
                .Zip(picturedHigh.Meshes.SelectMany(m => m.Positions), Vector3.Distance).Any(d => d > .0001f);
            var scaledPictured = MapObjectResourceCatalog.Scale(picturedLow, 1.5f);
            if (picturedLow.Meshes.SelectMany(m => m.Positions)
                .Zip(scaledPictured.Meshes.SelectMany(m => m.Positions), (a, b) => Vector3.Distance(b, a * 1.5f))
                .Any(d => d > .0001f) || !picturedLow.Meshes.SelectMany(m => m.Positions)
                    .Zip(scaledPictured.Meshes.SelectMany(m => m.Positions), (a, b) => Vector3.Distance(a, b))
                    .Any(d => d > .0001f))
                throw new Exception("Preview scale did not resize the pictured FIG1 object correctly.");
            var texturedScene = MapObjectResourceCatalog.WithTexturesAsync(neutral, textures, textures.ResourceNames[0], null).GetAwaiter().GetResult();
            if (neutral.Meshes.Any(mesh => mesh.TextureSlot != 2 &&
                    (!texturedScene.Textures.TryGetValue(mesh.TextureSlot, out var image) || image.Bgra.Length == 0)))
                throw new Exception("Selected MMP was not decoded for main texture slot.");
            var secondaryGeometry = neutral with { Meshes = [neutral.Meshes[0] with { TextureSlot = 2 }] };
            var secondaryScene = MapObjectResourceCatalog.WithTexturesAsync(secondaryGeometry, textures, null, textures.ResourceNames[0])
                .GetAwaiter().GetResult();
            if (!secondaryScene.Textures.ContainsKey(2)) throw new Exception("Second MMP slot was not assigned.");
            Console.WriteLine($"First object slots: {string.Join(',', neutral.Meshes.Select(m => m.TextureSlot).Distinct())}; morph changed: {changedGeometry > 0}; MMP: {textures.ResourceNames[0]}");
            Console.WriteLine($"stwe3 changes with FIG/BON morph: {picturedMorphs}");
            Console.WriteLine("stwe3 uniform preview scale: 150% verified");
            var sizeView = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
            sizeView.Measure(new Size(900, 700)); sizeView.Arrange(new Rect(0, 0, 900, 700));
            sizeView.ShowScene(picturedLow, item: true);
            var camera = (PerspectiveCamera)typeof(UnitModelPreviewView)
                .GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sizeView)!;
            var cameraBeforeResize = camera.Position;
            sizeView.ShowScene(scaledPictured, item: true, preserveCamera: true);
            if (camera.Position != cameraBeforeResize || !sizeView.HasVisibleScene)
                throw new Exception("Scale update moved camera or cleared map-object preview.");
            var texturedView = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
            texturedView.Measure(new Size(900, 700)); texturedView.Arrange(new Rect(0, 0, 900, 700));
            texturedView.ShowScene(texturedScene, item: true);
            if (!texturedView.HasVisibleScene) throw new Exception("Textured map object was not displayed.");
            foreach (var resource in mapObjects)
            {
                string name = resource.Model;
                if (resource.BonResource is null) throw new Exception($"Missing BON for {name}.");
                var scene = MapObjectResourceCatalog.LoadAsync(resource).GetAwaiter().GetResult();
                if (scene.Meshes.Count == 0 || scene.Meshes.All(m => m.Positions.Length == 0))
                    throw new Exception($"No geometry for {name}.");
                var view = new UnitModelPreviewView { ExternalScene = true, AllowAnimationEditing = false };
                view.Measure(new Size(900, 700)); view.Arrange(new Rect(0, 0, 900, 700));
                view.ShowScene(scene, item: true);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                view.DataContext = new object();
                if (!view.HasVisibleScene) throw new Exception($"Viewer lost {name} after Loaded/DataContextChanged.");
                Console.WriteLine($"{name}: {scene.Meshes.Count} mesh parts");
            }
            app.Shutdown();
            Console.WriteLine($"Map object catalogue: {entries.Count} models; {mapObjects.Length} map objects.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
