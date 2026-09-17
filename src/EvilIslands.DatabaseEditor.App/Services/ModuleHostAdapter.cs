using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.ViewModels;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class ModuleHostAdapter(MainWindow owner, MainWindowViewModel vm, string? journalDirectory = null) : IModuleDocumentHost, IModuleFileBrowser, IModuleTransactions, IModuleSave
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> allowedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, OpenedFile> sources = new();
    private readonly Dictionary<Guid, (string Path, string Hash)> savedTargets = new();
    internal void BindSaveTarget(Guid id, string path, string hash) => savedTargets[id] = (Path.GetFullPath(path), hash);
    private sealed record FileBinding(Guid Id, string Path, string Hash);
    private sealed record FileRecovery(FileBinding[] Sources, FileBinding[] Targets);
    internal byte[] CaptureFiles() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new FileRecovery(
        sources.Select(p => new FileBinding(p.Key, p.Value.DisplayPath, p.Value.Sha256)).ToArray(),
        savedTargets.Select(p => new FileBinding(p.Key, p.Value.Path, p.Value.Hash)).ToArray()));
    internal void RestoreFiles(byte[] bytes)
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<FileRecovery>(bytes) ?? throw new InvalidDataException("Нет данных файлов recovery.");
        foreach (var entry in state.Sources)
        {
            sources[entry.Id] = new(entry.Path, entry.Hash, []) { SourceToken = entry.Id };
            allowedFiles[entry.Path] = entry.Id;
        }
        foreach (var entry in state.Targets) savedTargets[entry.Id] = (entry.Path, entry.Hash);
    }
    public IModuleDocuments Documents => vm.ModuleDocuments;
    public IModuleTransactions Transactions => this;
    public IModuleSave Save => this;
    public Result Apply(ChangeSet changeSet) => vm.ApplyModuleTransaction(changeSet);
    public Result Undo() => vm.ApplyModuleHistory(redo: false);
    public Result Redo() => vm.ApplyModuleHistory(redo: true);
    public SaveReport SaveAll()
    {
        var writes = new List<EvilIslands.DatabaseEditor.Core.PreparedFileWrite>();
        var revisions = new Dictionary<Guid, long>();
        SaveReport Fail(string message) => new([new(Guid.Empty, "", "NotAttempted", message)], false);
        foreach (var d in vm.ModuleDocuments.Active.Where(d => d.IsDirty))
        {
            var registration = vm.ModuleDocuments.Registration(d.DocumentId);
            if (registration?.SaveArtifactKind is not { Length: > 0 } kind) return Fail("Модуль не указал формат сохранения документа.");
            string path; string hash;
            if (savedTargets.TryGetValue(d.DocumentId, out var saved)) { path = saved.Path; hash = saved.Hash; }
            else if (registration.SourceToken is Guid token && sources.TryGetValue(token, out var source)) { path = source.DisplayPath; hash = source.Sha256; }
            else
            {
                var dialog = new SaveFileDialog { FileName = Path.GetFileName(d.DisplayName), Filter = "Все файлы|*.*", OverwritePrompt = false };
                if (dialog.ShowDialog(owner) != true) return Fail("Сохранение отменено до записи файлов.");
                path = dialog.FileName; hash = "";
                if (File.Exists(path)) return Fail("Выберите новое имя: исходный файл для этого документа не подтверждён.");
                BindSaveTarget(d.DocumentId, path, hash);
            }
            var prepared = vm.ModuleDocuments.Prepare(d.DocumentId, kind);
            if (!prepared.Result.Succeeded) return Fail(prepared.Result.Message ?? "Подготовка документа не выполнена.");
            writes.Add(new(d.DocumentId, path, hash, prepared.Bytes.ToArray())); revisions.Add(d.DocumentId, d.Revision);
        }
        bool allowGame = false;
        if (writes.Any(w => EvilIslands.DatabaseEditor.Core.DatabaseSafetyPolicy.IsLikelyGameInstallationPath(w.Path)) ||
            (vm.HasChanges && EvilIslands.DatabaseEditor.Core.DatabaseSafetyPolicy.IsLikelyGameInstallationPath(vm.ModuleWorkspace.Snapshot().DisplayPath)))
        {
            allowGame = MessageBox.Show(owner, "Сохранить изменения в файлы игры? Для заменяемых файлов будут созданы резервные копии.", "Общее сохранение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!allowGame) return Fail("Сохранение отменено.");
        }
        var result = vm.SaveModuleFiles(writes, journalDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Редактор БД от Атамана", "save-journals"), allowGame);
        foreach (var file in result.Files.Where(f => f.Status == "Saved" && revisions.ContainsKey(f.DocumentId)))
        {
            var write = writes.Single(w => w.Id == file.DocumentId); string hash = Convert.ToHexString(SHA256.HashData(write.Bytes));
            savedTargets[file.DocumentId] = (file.Path, hash); vm.ModuleDocuments.Checkpoint(file.DocumentId, revisions[file.DocumentId], hash);
        }
        if (result.Complete) Report("Все документы сохранены.");
        else Report(string.Join("; ", result.Files.Select(f => f.Path + ": " + f.Status + " " + f.Message)));
        return result;
    }
    public Result Export(Guid id, string kind, string name, string filter)
    {
        var document = vm.ModuleDocuments.Get(id);
        if (document is null) return new(ResultStatus.Unavailable);
        var prepared = vm.ModuleDocuments.Prepare(id, kind);
        if (!prepared.Result.Succeeded) return prepared.Result;
        var result = Export(name, filter, prepared.Bytes.AsMemory());
        if (result.Succeeded && kind == "malachite-project") vm.ModuleDocuments.Checkpoint(id, document.Revision, Convert.ToHexString(SHA256.HashData(prepared.Bytes.AsSpan())));
        return result;
    }
    public IWorkspaceReader Workspace => vm.ModuleWorkspace;
    public IModuleFiles Files => this;
    public ImmutableArray<string> Capabilities => ["workspace.read.v1", "navigation.record.v1", "files.export-copy.v1", "documents.session.v1", "transactions.v1", "files.browse-folder.v1", "saves.artifact-kind.v1"];
    public void Report(string message) => vm.ReportSpreadsheetError(message);
    public OpenedFile? Open(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, RestoreDirectory = true };
        if (dialog.ShowDialog(owner) != true) return null;
        allowedFiles.TryAdd(Path.GetFullPath(dialog.FileName), Guid.NewGuid());
        return Read(dialog.FileName);
    }
    public ImmutableArray<string> SelectFolder(string extension)
    {
        if (extension != ".mob") throw new ArgumentException("Поддерживается выбор папки MOB.");
        var dialog = new OpenFolderDialog { Title = "Папка с MOB-картами" };
        if (dialog.ShowDialog(owner) != true) return [];
        var files = Directory.EnumerateFiles(dialog.FolderName, "*.mob", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).Select(Path.GetFullPath).ToImmutableArray();
        foreach (string file in files) allowedFiles.TryAdd(file, Guid.NewGuid());
        return files;
    }
    public OpenedFile Read(string path)
    {
        path = Path.GetFullPath(path);
        if (!allowedFiles.TryGetValue(path, out var token)) throw new UnauthorizedAccessException("Этот файл не выбран через хост.");
        var file = new FileInfo(path);
        if (file.Length > 128 * 1024 * 1024) throw new IOException("Файл больше 128 МиБ.");
        byte[] bytes = File.ReadAllBytes(file.FullName);
        var opened = new OpenedFile(file.FullName, Convert.ToHexString(SHA256.HashData(bytes)), ImmutableArray.Create(bytes)) { SourceToken = token };
        sources.TryAdd(token, opened);
        return opened;
    }
    public Result Export(string suggestedName, string filter, ReadOnlyMemory<byte> bytes)
    {
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(suggestedName), Filter = filter, OverwritePrompt = false, RestoreDirectory = true };
        if (dialog.ShowDialog(owner) != true) return new(ResultStatus.Cancelled, "Экспорт отменён.");
        string path = dialog.FileName;
        if (File.Exists(path)) return new(ResultStatus.Conflict, "Выберите новое имя: экспорт не перезаписывает существующие файлы.");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes.Span); output.Flush(true); }
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes.Span)) throw new IOException("Проверка записанной копии не пройдена.");
            File.Move(temp, path, false);
            return Result.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ResultStatus.Failed, ex.Message); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
