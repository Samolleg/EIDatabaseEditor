using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class AnimationClipSearchRegression
{
    internal static int Run(string database)
    {
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Verify(database);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("ANIMATION SEARCH PASS"); return 0; }
        finally { app.Shutdown(); }
    }

    private static async Task Verify(string database)
    {
        var records = DatabaseDocument.Open(database).Databases.SelectMany(group => group.Tables)
            .SelectMany(table => table.Records.Select(record => ModelRecord.Capture(table.Key, record))).ToArray();
        var hero = records.Single(record => record.Table == "units.prototypes" && record.Name == "Human Hero");
        var scene = await UnitModelPreview.LoadAsync(database, hero, records);
        var set = scene.Animations ?? throw new Exception("ANIMATION SEARCH: ANM not loaded");
        var adb = scene.AnimationDatabase?.Database ?? throw new Exception("ANIMATION SEARCH: ADB not loaded");
        var choices = AnimationClipCatalog.Build(set.Names, adb,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [set.Names[0]] = "Проверка названия" });
        Check(choices.Count == set.Names.Count, "catalog lost ANM clips");
        var registered = choices.First(choice => adb.Entries.Any(entry => entry.Name.Equals(choice.Id, StringComparison.OrdinalIgnoreCase)));
        int index = adb.Entries.First(entry => entry.Name.Equals(registered.Id, StringComparison.OrdinalIgnoreCase)).Index;
        Check(registered.Label.Contains($"ADB №{index}", StringComparison.Ordinal), "ADB index missing from label");
        Check(AnimationClipCatalog.Search(choices, index.ToString()).Any(choice => choice.Id == registered.Id), "ADB index is not searchable");
        Check(AnimationClipCatalog.Search(choices, "проверка названия").Single().Id == set.Names[0], "Russian name is not searchable");
        Check(AnimationClipCatalog.Search(choices, set.Names[0]).Any(choice => choice.Id == set.Names[0]), "clip ID is not searchable");
        var absent = AnimationClipCatalog.Build(["newclip"], adb, new Dictionary<string, string>());
        Check(absent.Single().AdbStatus == "не прописана в ADB", "missing ADB entry not marked");
        var unavailable = AnimationClipCatalog.Build(["newclip"], null, new Dictionary<string, string>());
        Check(unavailable.Single().AdbStatus == "ADB недоступен", "unavailable ADB mislabeled as unregistered");

        var view = new UnitModelPreviewView();
        view.ShowScene(scene);
        var search = Descendants(view).OfType<TextBox>().Single(control => Equals(control.ToolTip, "Поиск анимации по ID, русскому названию или номеру ADB"));
        var clips = Descendants(view).OfType<ComboBox>().Single(control => Equals(control.ToolTip, "Клипы ANM: ID, номер записи ADB и русское название"));
        var liveResults = Descendants(view).OfType<Border>().Single(control => Equals(control.Tag, "AnimationLiveSearchResults"));
        Check(clips.Items.Count == set.Names.Count + 1, "model view does not list all clips");
        await view.SelectAnimationAsync(registered.Id);
        search.Text = "a-search-that-matches-nothing";
        Check(clips.SelectedItem as string == registered.Id && clips.Items.Count == 2,
            "search changed the previewed clip or lost its selection");
        search.Text = registered.Id;
        Check(clips.Items.Cast<string>().Contains(registered.Id), "model view ID filtering failed");
        Check(liveResults.Visibility == Visibility.Visible && !clips.IsDropDownOpen,
            "live results are not independent from the ComboBox dropdown");
        var select = Descendants(liveResults).OfType<Button>().Single(button => (button.Content as string)?.Contains(registered.Id, StringComparison.OrdinalIgnoreCase) == true);
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await view.AnimationLoadTask;
        Check(view.ActiveAnimationName == registered.Id && search.Text.Length == 0, "clicking live result did not load animation");
        search.Text = "c";
        var cResults = Descendants(liveResults).OfType<Button>().Select(button => button.Content as string).ToArray();
        search.Text = "d";
        var dResults = Descendants(liveResults).OfType<Button>().Select(button => button.Content as string).ToArray();
        Check(dResults.Length > 0 && !cResults.SequenceEqual(dResults), "changing a query left stale results");
        Check(!clips.IsDropDownOpen, "typing opened focus-stealing ComboBox");
        search.Clear();
        Check(clips.Items.Count == set.Names.Count + 1 && liveResults.Visibility == Visibility.Collapsed,
            "clearing search did not restore clips and hide results");

        // The owner's video searches for a Human Hero clip while an orc model is selected.
        var orc = records.Single(record => record.Table == "units.prototypes" && record.Name == "GZOrcFemaleF4");
        var orcScene = await UnitModelPreview.LoadAsync(database, orc, records);
        Check(!orcScene.Model.Equals(scene.Model, StringComparison.OrdinalIgnoreCase), "video scenario did not change model");
        view.ShowScene(orcScene);
        search.Text = "twer";
        await view.GlobalSearchTask;
        var hint = Descendants(liveResults).OfType<TextBlock>().Single(control => control.Text.Contains("twerk1", StringComparison.OrdinalIgnoreCase));
        Check(hint.Text.Contains(scene.Model, StringComparison.OrdinalIgnoreCase), "cross-model result did not identify owning model");
        Check(hint.Text.Contains("ADB №", StringComparison.Ordinal), "cross-model result did not show ADB index");
        Check(clips.SelectedItem as string == "Исходная поза", "cross-model search selected an incompatible clip");
        view.Measure(new Size(1600, 900));
        view.Arrange(new Rect(0, 0, 1600, 900));
        view.UpdateLayout();
        Check(hint.Visibility == Visibility.Visible && hint.ActualHeight > 0, "cross-model result is not visible in the laid-out view");
        var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string preview = Path.Combine(Path.GetTempPath(), "dbe-animation-search-preview168.png");
        using (var file = File.Create(preview)) png.Save(file);
        Console.WriteLine("ANIMATION SEARCH IMAGE: " + preview);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("ANIMATION SEARCH: " + message);
    }
}
