using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DatabaseEditor.Modules.Host;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class ModuleIntegrationRegression
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static int Run(string source, string moduleRoot)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "EvilIslands.DatabaseEditor.App", "App.xaml"))) root = root.Parent;
        if (root is null) throw new IOException("Cannot locate application theme source for hidden test");
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, "src", "EvilIslands.DatabaseEditor.App", "App.xaml"));
        var dictionary = xml.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "converters", "clr-namespace:EvilIslands.DatabaseEditor.App.Infrastructure;assembly=EvilIslands.DatabaseEditor.App");
        foreach (var element in dictionary.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:EvilIslands.DatabaseEditor.App.Infrastructure").ToArray())
        {
            string key = element.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value;
            app.Resources[key] = Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("EvilIslands.DatabaseEditor.App.Infrastructure." + element.Name.LocalName, true)!, true)!;
            element.Remove();
        }
        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString()));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(source, moduleRoot); task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default); Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS real DB host + independently built module: view, immediate document edit, shared Undo/Redo, recovery"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Check(string source, string moduleRoot)
    {
        var window = new MainWindow(); var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(source);
        string journals = Path.Combine(Path.GetTempPath(), "module-journal-" + Guid.NewGuid().ToString("N"));
        var adapter = new ModuleHostAdapter(window, vm, journals);
        CheckRejectedModules(adapter);
        var loaded = ModuleLoader.Load(moduleRoot, adapter);
        Require(loaded.Modules.Count == 1 && loaded.Errors.Count == 0, string.Join("; ", loaded.Errors));
        var module = loaded.Modules[0].Instance;
        var view = module.CreateView(); view.Measure(new Size(1400, 850)); view.Arrange(new Rect(0, 0, 1400, 850)); view.UpdateLayout();
        module.Activate(true);
        var editor = Children(view).OfType<TextBox>().Single(t => t.Name == "CodeEditor");
        string old = editor.Text; editor.Text += "\n// host integration";
        Require(vm.ModuleDocuments.List("malachite").Any(d => d.Text == editor.Text && d.IsDirty), "Last input not in host session");
        Require(!editor.IsUndoEnabled, "Competing TextBox history");
        var undo = adapter.Undo(); Require(undo.Succeeded && editor.Text == old, "Real shared Undo did not rebind view");
        var redo = adapter.Redo(); Require(redo.Succeeded && editor.Text.EndsWith("// host integration"), "Real shared Redo failed");
        var document = vm.ModuleDocuments.List("malachite").Single(d => d.Text == editor.Text);
        string savePath = Path.Combine(Path.GetTempPath(), "module-real-" + Guid.NewGuid().ToString("N") + ".malachite");
        adapter.BindSaveTarget(document.DocumentId, savePath, "");
        var saved = adapter.SaveAll();
        Require(saved.Complete && File.Exists(savePath), "Real module SaveAll: " + string.Join("; ", saved.Files.Select(f => f.Message)));
        Require(!vm.ModuleDocuments.Get(document.DocumentId)!.IsDirty, "Saved document remains dirty");
        Require(adapter.Undo().Succeeded && vm.ModuleDocuments.Get(document.DocumentId)!.IsDirty, "Undo did not become dirty after save");
        Require(adapter.Redo().Succeeded && !vm.ModuleDocuments.Get(document.DocumentId)!.IsDirty, "Redo did not return to checkpoint");
        var fileRecovery = adapter.CaptureFiles();
        var restoredAdapter = new ModuleHostAdapter(window, vm, journals); restoredAdapter.RestoreFiles(fileRecovery);
        editor.Text += "\n// after recovery";
        Require(restoredAdapter.SaveAll().Complete, "Restored save target failed");
        byte[] snapshot = vm.ModuleDocuments.CaptureRecovery();
        Require(vm.ModuleDocuments.RestoreRecovery(snapshot).Succeeded, "Host recovery failed");
        Require(module.RestoreRecovery(module.CaptureRecovery()).Succeeded, "Module recovery rebind failed");
        // Simulate a replacement module rejecting a stored view version. Autosave must retain it.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(MainWindow).GetField("_modules", flags)!.SetValue(window, loaded);
        var unavailable = (Dictionary<string, byte[]>)typeof(MainWindow).GetField("_unavailableModuleRecovery", flags)!.GetValue(window)!;
        byte[] rejectedState = "future-state-must-survive"u8.ToArray();
        unavailable.Add("malachite", rejectedState);
        byte[] captured = (byte[])typeof(MainWindow).GetMethod("CaptureModuleRecovery", flags)!.Invoke(window, null)!;
        using var json = System.Text.Json.JsonDocument.Parse(captured);
        Require(json.RootElement.GetProperty("Views").GetProperty("malachite").GetBytesFromBase64().SequenceEqual(rejectedState), "Unavailable view overwritten by replacement module");
        Require(!window.IsVisible && Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "Unexpected visible window");
        module.Dispose(); vm.Dispose();
    }

    private static void CheckRejectedModules(IModuleHost host)
    {
        string root = Path.Combine(Path.GetTempPath(), "module-loader-negative-" + Guid.NewGuid().ToString("N"));
        Require(ModuleLoader.Load(root, host).Modules.Count == 0, "Missing module root");
        void Reject(string name, object manifest)
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "module.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
            var result = ModuleLoader.Load(folder, host);
            Require(result.Modules.Count == 0 && result.Errors.Count == 1, "Invalid module accepted: " + name);
        }
        var baseline = new ModuleManifest("fixture", "1", "entry.dll", "Missing.Type", 1, 0, [], [], "net9.0-windows", "any", 1);
        Reject("major", baseline with { ContractMajor = 2 });
        Reject("capability", baseline with { RequiredCapabilities = ["unavailable.capability"] });
        Reject("traversal", baseline with { EntryAssembly = "../escape.dll" });
        Reject("missing-entry", baseline);
        Reject("invalid-manifest", new { ModuleId = "INVALID ID" });
        string duplicates = Path.Combine(root, "duplicates");
        foreach (string id in new[] { "a", "b" })
        {
            string folder = Path.Combine(duplicates, id); Directory.CreateDirectory(folder);
            File.Copy(typeof(MainWindow).Assembly.Location, Path.Combine(folder, "entry.dll"));
            File.WriteAllText(Path.Combine(folder, "module.json"), System.Text.Json.JsonSerializer.Serialize(baseline));
        }
        var duplicate = ModuleLoader.Load(duplicates, host);
        Require(duplicate.Modules.Count == 0 && duplicate.Errors.Count == 2 && duplicate.Errors.All(e => e.Contains("Повторный ID")), "Duplicate ID was initialized");
    }
}
