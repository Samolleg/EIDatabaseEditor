using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EvilIslands.DatabaseEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        var logPath = TryWriteCrashLog(args.Exception);
        var baseMessage = args.Exception.GetBaseException().Message;
        MessageBox.Show(
            $"{baseMessage}\n\nПодробности записаны в:\n{logPath}",
            "EI Database Studio — ошибка запуска",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        args.Handled = true;
        Shutdown(-1);
    }

    private static string TryWriteCrashLog(Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EI Database Studio",
                "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
            return path;
        }
        catch (Exception logException)
        {
            return $"журнал создать не удалось: {logException.Message}";
        }
    }
}
