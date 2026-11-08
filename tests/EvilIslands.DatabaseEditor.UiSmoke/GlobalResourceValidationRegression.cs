using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class GlobalResourceValidationRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Run(string original, string repaired, string output)
    {
        original = Path.GetFullPath(original); repaired = Path.GetFullPath(repaired); output = Path.GetFullPath(output);
        return Execute(() => Check(original, repaired, output), output);
    }
    public static int RunLive(string database, string output)
    {
        database = Path.GetFullPath(database); output = Path.GetFullPath(output);
        return Execute(() => CheckLive(database, output), output);
    }
    static int Execute(Func<Task> check, string output)
    {
        Directory.CreateDirectory(output);
        var app = new App.App(); app.InitializeComponent();
        typeof(Application).GetField("_startupUri", Private)!.SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = check(); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Require(app.Windows.Cast<Window>().All(w => !w.IsVisible), "Visible window"); app.Shutdown(); Console.WriteLine("PASS global resource F5/service/VM/page, normal headless shutdown"); return 0; }
        catch (Exception ex) { app.Shutdown(); Console.Error.WriteLine(ex); return 1; }
    }
    static async Task Wait(Func<bool> predicate, string reason)
    {
        var stop = DateTime.UtcNow.AddMinutes(5);
        while (!predicate()) { if (DateTime.UtcNow > stop) throw new Exception("Timeout: " + reason); await Task.Delay(30); }
    }
    static async Task F5(MainWindow window, MainWindowViewModel vm)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.F5);
        Require(ReferenceEquals(binding.Command, vm.RefreshValidationCommand), "F5 binding disconnected");
        binding.Command.Execute(null);
        await Wait(() => !vm.IsResourceValidationRunning, "F5 resource scan");
        Require(vm.Diagnostics.Any(d => d.Code == "RESOURCE_SCAN_COVERAGE"), "F5 did not publish resource coverage: " + vm.ResourceValidationStatus);
    }
    static async Task Check(string original, string repaired, string output)
    {
        var hashes = new[] { original, repaired }.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "isolated-preferences.json")),
            new RecoverySnapshotStore(Path.Combine(output, "isolated-recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(original);
        Require(vm.ResourceValidationStatus.Contains("ещё не проверены"), "Initial coverage claimed clean");
        var structural = vm.Diagnostics.Where(d => d.Domain != "Resources").Select(d => d.Code + d.Path + d.Message).Order().ToArray();
        await F5(window, vm);
        var faulty = vm.Diagnostics.Where(d => d.Code == "FIG_WEAPON_TEXTURE_ID" && d.Path.Contains("unhuzf.mod") && d.Message.Contains("ZombieFemaleF14")).ToArray();
        Require(faulty.Length == 2, "Global F5 must find original both axes; got " + faulty.Length);
        Require(faulty.All(d => !d.IsBlocking && d.DomainText == "Ресурсы" && d.CategoryText == "Неверный атлас оружия"), "Resource classification/save policy");
        Require(vm.DiagnosticsView.Cast<ValidationMessageViewModel>().Contains(faulty[0]), "Visible page projection missing fault");
        var grid = (DataGrid)window.FindName("ValidationGrid");
        Require(ReferenceEquals(grid.ItemsSource, vm.DiagnosticsView) && grid.Items.Cast<ValidationMessageViewModel>().Contains(faulty[0]), "Actual page grid does not project F5 diagnostics");
        await RenderPage(window, vm, Path.Combine(output, "original-page.png"));
        Require(structural.SequenceEqual(vm.Diagnostics.Where(d => d.Domain != "Resources").Select(d => d.Code + d.Path + d.Message).Order()), "Structural checks changed");
        File.WriteAllText(Path.Combine(output, "original-diagnostics.json"), JsonSerializer.Serialize(vm.Diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        vm.RefreshValidationCommand.Execute(null); vm.CancelResourceValidationCommand.Execute(null);
        Require(vm.ResourceValidationStatus.Contains("отменена"), "Cancel looked like clean");
        await Task.Delay(100); Require(!vm.Diagnostics.Any(d => d.Domain == "Resources"), "Cancelled scan injected stale results");
        vm.RefreshValidationCommand.Execute(null);
        await vm.OpenPathAsync(repaired);
        await Task.Delay(100);
        Require(!vm.Diagnostics.Any(d => d.Code == "FIG_WEAPON_TEXTURE_ID"), "Database switch injected old errors");
        await F5(window, vm);
        Require(!vm.Diagnostics.Any(d => d.Code == "FIG_WEAPON_TEXTURE_ID" && d.Path.Contains("unhuzf.mod") &&
            (d.Path.Contains("rh3.daxe01") || d.Path.Contains("lh3.daxe01"))), "Repaired axes still flagged");
        Require(vm.ResourceValidationStatus.Contains("вариантов/явных ссылок"), "Coverage missing actual variant count");
        Require(vm.Diagnostics.Where(d => d.Code == "RESOURCE_PREVIEW_OPTIONAL").All(d => d.Severity == "Information" && !d.IsBlocking),
            "Unbound default preview example counted as a linked resource error");
        Require(vm.ResourceValidationStatus.Contains("Примеры предпросмотра:"), "Optional examples not separated in coverage");
        await RenderPage(window, vm, Path.Combine(output, "repaired-page.png"));
        File.WriteAllText(Path.Combine(output, "repaired-diagnostics.json"), JsonSerializer.Serialize(vm.Diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        // Service control cases use a tiny linked scan to isolate each independent decoder/resolver risk.
        await Controlled(repaired, output, window, vm);
        var deferred = (Task)typeof(MainWindowViewModel).GetMethod("RefreshValidationAsync", Private)!.Invoke(vm, [true])!;
        await deferred;
        Require(!vm.Diagnostics.Any(d => d.Domain == "Resources") && vm.ResourceValidationStatus.Contains("устарели"), "Cell validation reused stale resource pass");
        foreach (var p in hashes) Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value, "Fixture database modified");
        Require(!window.IsVisible, "Test showed window"); window.Close(); vm.Dispose();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { OriginalFaults = faulty.Length,
            F5 = true, ActualPageGrid = true, StructuralPreserved = true, Cancellation = true, DatabaseSwitch = true,
            DeferredOnlyStructural = true, Headless = true, App = Hash(typeof(MainWindow).Assembly.Location), Core = Hash(typeof(DatabaseDocument).Assembly.Location) }, new JsonSerializerOptions { WriteIndented = true }));
    }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static async Task RenderPage(MainWindow window, MainWindowViewModel vm, string output)
    {
        vm.NavigateCommand.Execute(vm.NavigationItems.Single(n => n.Section == WorkspaceSection.Validation));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        var coverage = (TextBlock)window.FindName("ResourceValidationCoverageText");
        Require(coverage.Text == vm.ResourceValidationStatus, "Coverage not projected on page");
        var content = (FrameworkElement)window.Content;
        content.DataContext = vm;
        window.Content = null; // Render the actual page tree without creating a window handle.
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        content.Measure(new Size(1500, 1000)); content.Arrange(new Rect(0, 0, 1500, 1000)); content.UpdateLayout();
        Require(coverage.ActualHeight > 0 && coverage.Visibility == Visibility.Visible && vm.CurrentSection == WorkspaceSection.Validation,
            $"Coverage collapsed by page layout: height={coverage.ActualHeight}, visibility={coverage.Visibility}, section={vm.CurrentSection}");
        var bitmap = new RenderTargetBitmap(1500, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(output); png.Save(file);
        window.Content = content;
    }
    static async Task CheckLive(string database, string output)
    {
        var started = DateTimeOffset.Now;
        string before = Hash(database);
        var sources = await TextureSources.FromDatabaseFolderAsync(database);
        var catalog = await TextureResourceCatalog.OpenAsync(sources);
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(output, "isolated-preferences.json")),
            new RecoverySnapshotStore(Path.Combine(output, "isolated-recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(database); await F5(window, vm);
        Require(before == Hash(database), "Live database changed during scan");
        Require(!window.IsVisible && !vm.HasUnsavedWork, "Live scan changed editor state");
        Require(catalog.IsCurrent(), "Live sources changed during scan");
        await RenderPage(window, vm, Path.Combine(output, "live-page.png"));
        File.WriteAllText(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(vm.Diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Database = database, DatabaseSHA256 = before,
            Started = started, Completed = DateTimeOffset.Now, Sources = sources, SourceProblems = catalog.Problems,
            ConfiguredChainComplete = catalog.Complete, Coverage = vm.ResourceValidationStatus,
            Errors = vm.ErrorCount, Warnings = vm.WarningCount, App = Hash(typeof(MainWindow).Assembly.Location),
            Core = Hash(typeof(DatabaseDocument).Assembly.Location), ReadOnly = true, Headless = true }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close(); vm.Dispose();
    }
    sealed class CallbackProgress(Action<ResourceValidationProgress> action) : IProgress<ResourceValidationProgress>
    { public void Report(ResourceValidationProgress value) => action(value); }
    static async Task Controlled(string database, string output, MainWindow window, MainWindowViewModel vm)
    {
        var sources = await TextureSources.FromDatabaseFolderAsync(database);
        string folder = Path.Combine(output, "controlled"); Directory.CreateDirectory(folder);
        string fakeDatabase = Path.Combine(folder, "database.res"); File.Copy(database, fakeDatabase);
        // No profile: incomplete chain means an absent texture is unverified, not a confirmed missing game resource.
        var race = new ModelRecord("units.races", "Controlled", new Dictionary<int, string>(), new Dictionary<int, string[]> { [31] = ["test"] });
        (ModelRecord, DiagnosticLocation)[] linked = [(race, new("units.udb", race.Table, 0, race.Name))];
        var missing = await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, CancellationToken.None);
        Require(missing.Any(d => d.Code == "RESOURCE_LAYOUT_UNCHECKED" && d.Severity == DiagnosticSeverity.Warning), "Incomplete missing texture marked clean/damaged");
        await vm.OpenPathAsync(fakeDatabase);
        var raceRow = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.races").Records.First(r => !string.IsNullOrWhiteSpace(r.CoreRecord.IdentityValue));
        Require(raceRow.CoreRecord.Fields.Single(f => f.Id == 31).TrySetStringListValues(["test"], out _), "Controlled race texture field");
        await F5(window, vm);
        Require(vm.DiagnosticsView.Cast<ValidationMessageViewModel>().Any(d => d.Code == "RESOURCE_LAYOUT_UNCHECKED" && d.Path.Contains("test.mmp")), "Actual F5 incomplete missing texture absent");
        string texture = Path.Combine(folder, "test.mmp"); File.WriteAllBytes(texture, new byte[80]);
        await F5(window, vm);
        Require(vm.DiagnosticsView.Cast<ValidationMessageViewModel>().Any(d => d.Code == "RESOURCE_DECODE_FAILED" && d.Path.Contains("test.mmp")), "Actual F5 corrupt texture absent");
        var corrupt = await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, CancellationToken.None);
        Require(corrupt.Any(d => d.Code == "RESOURCE_DECODE_FAILED" && d.Path!.Contains("test.mmp")), "Corrupt linked MMP not decoded");
        var fig = new FigureMesh("weapon", [], [new(.2f, .7f)], [0], 7);
        var unknown = FigureTextureValidation.CheckWeapon(fig, false, "controlled.mod");
        Require(unknown is { Severity: DiagnosticSeverity.Warning, Code: "FIG_TEXTURE_LAYOUT_UNCHECKED" }, "Unknown FIG ID became damage");
        var session = await ResourceValidationSession.OpenAsync(fakeDatabase, await TextureSources.FromDatabaseFolderAsync(fakeDatabase), CancellationToken.None);
        session.Location = linked[0].Item2; session.Add(unknown!);
        Require(session.Skipped.Count > 0 && session.Issues.Count == 1, "Unknown layout omitted from coverage");
        // Unknown scheme also flows through the real Core service and page projection.
        var repairSources = await TextureSources.FromDatabaseFolderAsync(database);
        string repairedRes = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(database))!, "..", "unhuzf-repaired.res"));
        var outer = ResArchiveDocument.Parse(repairedRes, File.ReadAllBytes(repairedRes));
        var modEntry = outer.Entries.Single(e => e.Name == "unhuzf.mod");
        var mod = ResArchiveDocument.Parse("unhuzf.mod", outer.GetResourceBytes(modEntry).ToArray());
        var part = mod.Entries.Single(e => e.Name == "rh3.daxe01"); var partBytes = mod.GetResourceBytes(part).ToArray();
        BitConverter.GetBytes(7).CopyTo(partBytes, 36);
        var modBytes = ResArchiveWriter.Rebuild(mod, new Dictionary<int, byte[]> { [part.Index] = partBytes });
        string unknownRes = Path.Combine(folder, "unknown.res");
        File.WriteAllBytes(unknownRes, ResArchiveWriter.Rebuild(outer, new Dictionary<int, byte[]> { [modEntry.Index] = modBytes }));
        Directory.CreateDirectory(Path.Combine(folder, "Base", "Res")); File.WriteAllText(Path.Combine(folder, "portable-defaults.xml"), "<defaults/>");
        string configuration = File.ReadAllText(repairSources.ConfigurationPath!);
        configuration = configuration.Replace("res\\database.res", "database.res");
        // Preserve the exact winning figure override, independent of slash spelling in the fixture.
        configuration = string.Join(Environment.NewLine, configuration.Split('\n').Select(line =>
            line.Contains("unhuzf-repaired.res", StringComparison.OrdinalIgnoreCase) ? "figures.res=unknown.res" : line.TrimEnd('\r')));
        File.WriteAllText(Path.Combine(folder, "config.ini"), configuration);
        await vm.OpenPathAsync(fakeDatabase); await F5(window, vm);
        Require(vm.DiagnosticsView.Cast<ValidationMessageViewModel>().Any(d => d.Code == "FIG_TEXTURE_LAYOUT_UNCHECKED" && d.Path.Contains("rh3.daxe01")), "Unknown raw FIG scheme not exposed by real F5");
        File.Delete(Path.Combine(folder, "config.ini"));
        // Same resource name changes within a pass must invalidate the conclusion.
        var changed = false;
        try
        {
            await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, CancellationToken.None,
                new CallbackProgress(p => { if (p.RecordsChecked == 1 && !changed) { changed = true; File.WriteAllBytes(texture, new byte[81]); } }));
            throw new Exception("Changed source accepted");
        }
        catch (IOException ex) { Require(ex.Message.Contains("изменились"), "Wrong source-change failure"); }
        var current = await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, CancellationToken.None);
        Require(current.Any(d => d.Code == "RESOURCE_DECODE_FAILED"), "Fresh pass reused stale cache");
        var liveCatalog = await TextureResourceCatalog.OpenAsync(sources);
        var winner = liveCatalog.Check("unhumata_01.st.0.x2.mmp").Matches.First();
        using (var stream = File.OpenRead(winner.Source)) { stream.Position = winner.Offset; var bytes = new byte[winner.Length]; stream.ReadExactly(bytes); File.WriteAllBytes(texture, bytes); }
        var valid = await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, CancellationToken.None);
        Require(!valid.Any(d => d.Code == "RESOURCE_DECODE_FAILED" && d.Path!.Contains("test.mmp")), "Fresh pass kept old corrupt MMP");
        using var cancellation = new CancellationTokenSource();
        try { await LinkedResourceValidation.ScanAsync(fakeDatabase, linked, cancellation.Token,
            new CallbackProgress(_ => cancellation.Cancel())); throw new Exception("Cancelled scan accepted"); }
        catch (OperationCanceledException) { }
        // Distinguish absent model, malformed known FIG and unsupported FIG schema.
        try { session.Read("missing.mod", CancellationToken.None); throw new Exception("Missing model accepted"); }
        catch (FileNotFoundException ex) { session.Failure(ex); }
        Require(session.Issues.Values.Any(p => p.Diagnostic.Code == "RESOURCE_MISSING"), "Missing model conflated with header");
        try { FigurePreviewDecoder.Decode("broken", new byte[40], new(.5f)); throw new Exception("Bad FIG signature accepted"); }
        catch (InvalidDataException ex) { session.Path = "controlled.mod → broken.fig"; session.Failure(ex); }
        byte[] future = new byte[40]; System.Text.Encoding.ASCII.GetBytes("FIG2").CopyTo(future, 0);
        try { FigurePreviewDecoder.Decode("future", future, new(.5f)); throw new Exception("Future FIG accepted"); }
        catch (NotSupportedException ex) { session.Path = "controlled.mod → future.fig"; session.Failure(ex); }
        Require(session.Issues.Values.Any(p => p.Diagnostic.Code == "RESOURCE_DECODE_FAILED") &&
            session.Issues.Values.Any(p => p.Diagnostic.Code == "RESOURCE_LAYOUT_UNCHECKED"), "Malformed vs unsupported FIG conflated");
        File.WriteAllText(Path.Combine(output, "controlled.json"), JsonSerializer.Serialize(new { IncompleteMissing = missing, Corrupt = corrupt,
            Unknown = unknown, Skipped = session.Skipped.Count, ChangeRejected = changed, FreshPass = true, Cancellation = true }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
