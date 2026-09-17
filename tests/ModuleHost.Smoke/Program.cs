using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.Host;
using DatabaseEditor.Modules.V1;

internal sealed class FakeHost : IModuleHost, IWorkspaceReader, IModuleFiles
{
    public IWorkspaceReader Workspace => this;
    public IModuleFiles Files => this;
    public ImmutableArray<string> Capabilities => ["workspace.read.v1", "navigation.record.v1", "files.export-copy.v1"];
    public event EventHandler<WorkspaceEvent>? Changed;
    public WorkspaceSnapshot Snapshot() => new(new(Guid.Empty, 1, 1), "fake", false, []);
    public Result Navigate(RecordRef reference) => new(ResultStatus.Unavailable);
    public void Report(string message) => Console.WriteLine(message);
    public OpenedFile? Open(string filter) => null;
    public Result Export(string suggestedName, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
    public void Notify() => Changed?.Invoke(this, new(new(Guid.Empty, 1, 2), "ContentChanged"));
}
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var app = new Application(); var host = new FakeHost();
            var result = ModuleLoader.Load(args[0], host);
            if (result.Errors.Count != 0 || result.Modules.Count != 1) throw new Exception(string.Join("\n", result.Errors));
            var module = result.Modules[0].Instance;
            var view = module.CreateView();
            view.Measure(new Size(1400, 850)); view.Arrange(new Rect(0, 0, 1400, 850)); view.UpdateLayout();
            module.Activate(true); host.Notify();
            var before = module.CaptureRecovery();
            if (!module.RestoreRecovery(before).Succeeded) throw new Exception("Recovery roundtrip failed");
            if (module.RestoreRecovery("{}"u8.ToArray()).Status == ResultStatus.Success) throw new Exception("Unknown recovery accepted");
            if (Application.Current != app || app.Windows.Count != 0) throw new Exception("Module created a window/application");
            module.Activate(false); module.Dispose();
            Console.WriteLine("PASS independent DLL load, WPF/BAML view, shared contracts, lifecycle, recovery format, no Window/Application");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
