using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using EvilIslands.DatabaseEditor.App.Dialogs;

namespace EvilIslands.DatabaseEditor.App;

public partial class MainWindow
{
    private bool _homeRefreshRunning;
    internal Task HomeRefreshTask { get; private set; } = Task.CompletedTask;

    private void HomeRefresh_Click(object sender, RoutedEventArgs e) => StartHomeRefresh();

    private void StartHomeRefresh()
    {
        if (_homeRefreshRunning) return;
        HomeRefreshTask = RefreshHomeAsync();
    }

    private async Task RefreshHomeAsync()
    {
        _homeRefreshRunning = true;
        HomeRefreshButton.IsEnabled = false;
        try
        {
            RefreshLastDatabaseButton();
            string revision = ProgramVersionText.Text.Trim().TrimStart('·').Trim();
            HomeEditorVersion.Text = revision;
            HomeAnimationVersion.Text = "В составе версии " + revision.Replace("Версия ", "");
            var module = _modules?.Modules.FirstOrDefault(m => m.Manifest.ModuleId == "malachite");
            string? moduleDirectory = module is null ? null : Path.GetDirectoryName(module.Instance.GetType().Assembly.Location);
            // Reuse the installed module's public, read-only resolver. Do not start its runtime,
            // infer a model from any running llama process, or duplicate its selection precedence.
            var uiAssembly = module is null ? null : System.Runtime.Loader.AssemblyLoadContext
                .GetLoadContext(module.Instance.GetType().Assembly)?.Assemblies
                .FirstOrDefault(a => a.GetName().Name == "Malachite.UI.Wpf");
            var paths = AnimationEditorDialog.KimodoPaths;
            var info = await Task.Run(() => HomeComponentInfo.Read(moduleDirectory, uiAssembly, paths));
            HomeMalachiteVersion.Text = info.ModuleVersion;
            HomeMalachiteStatus.Text = module is null
                ? (_modules?.Errors.Count > 0 ? "Ошибка загрузки — см. подробности" : "Модуль не установлен")
                : "Модуль загружен · квесты и скрипты MOB";
            HomeModelName.Text = info.ModelName;
            HomeModelStatus.Text = info.ModelStatus;
            HomeKimodoStatus.Text = info.KimodoStatus;
            HomeComponentDetails.Text = "Редактор: " + revision +
                "\nПапка редактора: " + AppContext.BaseDirectory + "\n\n" + info.Details +
                (_modules?.Errors.Count > 0 ? "\n\nОшибки модулей:\n" + string.Join("\n", _modules.Errors) : "");
        }
        finally { _homeRefreshRunning = false; HomeRefreshButton.IsEnabled = true; }
    }
}

internal sealed record HomeComponentInfo(string ModuleVersion, string ModelName, string ModelStatus,
    string KimodoStatus, string Details)
{
    internal static HomeComponentInfo Read(string? moduleDirectory, Assembly? uiAssembly,
        (string Executable, string Motion, string Text) kimodo)
    {
        var details = new StringBuilder();
        string version = "Не загружен";
        if (moduleDirectory is not null)
        {
            details.AppendLine("Малахит: " + moduleDirectory);
            try
            {
                // A fingerprint identifies the whole code set, including uncommitted builds.
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (string name in new[] { "Malachite.Core.dll", "Malachite.Module.dll", "Malachite.UI.Wpf.dll" })
                {
                    using var file = File.OpenRead(Path.Combine(moduleDirectory, name));
                    byte[] hash = SHA256.HashData(file);
                    digest.AppendData(hash);
                    details.AppendLine(name + ": " + Convert.ToHexString(hash));
                }
                string id = Convert.ToHexString(digest.GetHashAndReset());
                version = "Сборка " + id[..10];
                details.AppendLine("Идентификатор комплекта: " + id);
                // Optional future package metadata. The legacy 0.4.0 is never a display revision.
                string manifest = Path.Combine(moduleDirectory, "manifest.json");
                if (File.Exists(manifest) && new FileInfo(manifest).Length <= 65536)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (json.RootElement.TryGetProperty("buildRevision", out var rev) && rev.TryGetInt32(out int n) && n > 0)
                        version = "Сборка №" + n;
                    if (json.RootElement.TryGetProperty("builtAtUtc", out var date) && date.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(date.GetString(), out var built))
                        details.AppendLine("Дата сборки: " + built.ToLocalTime().ToString("dd.MM.yyyy HH:mm zzz"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { details.AppendLine("Метаданные сборки: " + ex.Message); }
        }
        string modelName = "Не настроена";
        string modelStatus = "Выберите локальную модель в Малахите.";
        try
        {
            Type? settings = uiAssembly?.GetType("QuestStudio.LocalModelSettings");
            string? settingsPath = settings?.GetProperty("DefaultPath")?.GetValue(null) as string;
            object? files = settingsPath is null || moduleDirectory is null ? null : settings?
                .GetMethod("Resolve", new[] { typeof(string), typeof(string) })?.Invoke(null, new object[] { settingsPath, moduleDirectory });
            if (files?.GetType().GetProperty("Model")?.GetValue(files) is string model &&
                files.GetType().GetProperty("Server")?.GetValue(files) is string server)
            {
                modelName = Path.GetFileNameWithoutExtension(model);
                modelStatus = "Файлы найдены · запуск при генерации";
                details.AppendLine("\nМодель Малахита: " + model);
                details.AppendLine("Движок: " + server);
                details.AppendLine("Проверены выбранные файлы. Состояние запущенного сервера здесь не опрашивается.");
            }
            else if (settings is null)
            {
                modelName = "Нет сведений";
                modelStatus = "Загруженный Малахит не предоставил сведения о модели.";
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            modelName = "Проверка недоступна";
            modelStatus = "Подробности ошибки ниже.";
            details.AppendLine("Модель: " + ex.GetBaseException().Message);
        }
        bool kimodoFound = File.Exists(kimodo.Executable) && File.Exists(kimodo.Motion) &&
            (File.Exists(kimodo.Text) || Directory.Exists(kimodo.Text));
        details.AppendLine("\nKimodo: " + kimodo.Executable);
        details.AppendLine("Модель движения: " + kimodo.Motion);
        details.AppendLine("Текстовая модель: " + kimodo.Text);
        details.AppendLine("Проверка наличия файлов не запускает генератор.");
        return new(version, modelName, modelStatus,
            kimodoFound ? "Файлы Kimodo найдены · генерация движения по тексту" : "Kimodo: комплект не найден полностью. Покадровое редактирование доступно.", details.ToString());
    }
}
