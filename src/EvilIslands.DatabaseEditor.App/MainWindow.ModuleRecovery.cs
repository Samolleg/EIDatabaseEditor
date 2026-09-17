using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Services;

namespace EvilIslands.DatabaseEditor.App;
public partial class MainWindow
{
    private readonly Dictionary<string, byte[]> _unavailableModuleRecovery = new();
    private sealed record ModuleRecoveryData(int Version, byte[] Documents, Dictionary<string, byte[]> Views, byte[]? Files = null);
    private byte[] CaptureModuleRecovery()
    {
        var views = new Dictionary<string, byte[]>(_unavailableModuleRecovery);
        foreach (var module in _modules?.Modules ?? [])
        {
            // A replacement module may reject an older/newer recovery format. Keep those bytes
            // until it can restore them; an empty fresh view must not overwrite the snapshot.
            if (views.ContainsKey(module.Manifest.ModuleId)) continue;
            views[module.Manifest.ModuleId] = module.Instance.CaptureRecovery().ToArray();
        }
        return JsonSerializer.SerializeToUtf8Bytes(new ModuleRecoveryData(1, _viewModel.ModuleDocuments.CaptureRecovery(), views, _moduleHost?.CaptureFiles()));
    }
    private void RestoreModuleRecovery(RecoverySnapshot snapshot)
    {
        if (snapshot.ModulesPath is null) return;
        byte[] bytes = File.ReadAllBytes(snapshot.ModulesPath);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != snapshot.ModulesSha256) throw new InvalidDataException("Контрольная сумма recovery модулей не совпала.");
        var data = JsonSerializer.Deserialize<ModuleRecoveryData>(bytes);
        if (data is null || data.Version != 1) throw new InvalidDataException("Версия recovery модулей не поддерживается. Исходный снимок сохранён.");
        var result = _viewModel.ModuleDocuments.RestoreRecovery(data.Documents);
        if (!result.Succeeded) throw new InvalidDataException(result.Message);
        if (data.Files is not null) _moduleHost?.RestoreFiles(data.Files);
        foreach (var entry in data.Views)
        {
            var module = _modules?.Modules.FirstOrDefault(m => m.Manifest.ModuleId == entry.Key);
            Result restored;
            try { restored = module?.Instance.RestoreRecovery(entry.Value) ?? new(ResultStatus.Unavailable); }
            catch (Exception ex) { restored = new(ResultStatus.Failed, ex.Message); }
            if (!restored.Succeeded)
            {
                _unavailableModuleRecovery[entry.Key] = entry.Value;
                _viewModel.ReportSpreadsheetError("Данные модуля " + entry.Key + " сохранены, но модуль не восстановлен: " + restored.Message);
            }
            else _unavailableModuleRecovery.Remove(entry.Key);
        }
    }
}
