using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App.Services;
using System.IO;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private LuaDataWorkspace? _luaWorkspace;
    public LuaDataWorkspace? LuaWorkspace => _luaWorkspace;
    public bool HasPendingLuaChanges => _luaWorkspace?.HasChanges == true;

    public void ConnectLua(string folder)
    {
        if (HasPendingLuaChanges) throw new InvalidOperationException("Сначала сохраните или отмените подготовленные изменения Lua.");
        var next = LuaDataWorkspace.Open(folder);
        if (next.Documents.Count == 0) throw new InvalidOperationException(string.Join(Environment.NewLine, next.Problems));
        if (_luaWorkspace is not null) _luaWorkspace.Changed -= LuaWorkspaceChanged;
        _luaWorkspace = next; _luaWorkspace.Changed += LuaWorkspaceChanged;
        LuaWorkspaceChanged(this, EventArgs.Empty);
    }
    private void LuaWorkspaceChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LuaWorkspace));
        OnPropertyChanged(nameof(HasPendingLuaChanges));
    }

    private sealed record LuaGridEditFrame(LuaSourceDocument Document, string Before, string After) : WorkspaceEditFrame;
    public void RecordLuaGridEdit(LuaSourceDocument doc, string before)
    {
        if (before == doc.Text) return;
        _undo.Push(new LuaGridEditFrame(doc, before, doc.Text)); _redo.Clear();
        StatusText = "Изменены параметры Lua. Для записи нажмите «Сохранить» (Ctrl+S).";
        LuaWorkspaceChanged(this, EventArgs.Empty);
        RaiseCommandStates();
    }
    private void DisconnectLua()
    {
        if (_luaWorkspace is not null) _luaWorkspace.Changed -= LuaWorkspaceChanged;
        _luaWorkspace = null;
        LuaWorkspaceChanged(this, EventArgs.Empty);
    }

    internal string? LinkedSaveJournalDirectory { get; set; }

    private void SaveLinkedLua(bool allowGamePaths, bool createDatabaseBackup)
    {
        if (LuaWorkspace is not { } workspace || _service is not CoreDatabaseWorkspaceService service) return;
        IsBusy = true;
        CoordinatedSaveResult? result = null;
        try
        {
            var documents = workspace.Documents.Where(d => d.HasChanges).ToArray();
            foreach (var document in documents) workspace.ValidateForSave(document);
            var dbWrites = service.PrepareModuleSave();
            var writes = dbWrites.Concat(documents.Select(d => new PreparedFileWrite(Guid.NewGuid(), d.Path, d.BaselineHash, d.Bytes))).ToArray();
            string journal = LinkedSaveJournalDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Редактор БД от Атамана", "save-journals");
            result = DocumentSaveCoordinator.Commit(writes, journal, allowGamePaths);
            if (!result.Complete)
            {
                ShowError("Сохранение не завершено. " + string.Join("; ", result.Files.Select(f => Path.GetFileName(f.Path) + ": " + f.Status + " " + f.Message)) + " Журнал: " + result.JournalPath);
                return;
            }
            if (dbWrites.Count > 0)
            {
                var tableKey = SelectedTable?.Key;
                var recordIndex = SelectedRecord?.SourceRecordIndex;
                ApplyWorkspace(service.ReloadModuleSave(), preserveSoundEdits: true);
                var table = Databases.SelectMany(d => d.Tables).FirstOrDefault(t => t.Key == tableKey);
                if (table is not null) { SelectTable(table); SelectedRecord = table.Records.FirstOrDefault(r => r.SourceRecordIndex == recordIndex); }
            }
            foreach (var document in documents) document.AcceptSaved();
            string backupNotice = "";
            if (!createDatabaseBackup)
                foreach (var saved in result.Files.Where(f => dbWrites.Any(w => w.Id == f.Id)))
                    try { File.Delete(saved.BackupPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { backupNotice += " Резервная копия оставлена: " + saved.BackupPath; }
            RetainSoundFrames(_undo); RetainSoundFrames(_redo);
            LuaWorkspaceChanged(this, EventArgs.Empty); RaiseCommandStates();
            NotificationIsError = false;
            NotificationMessage = StatusText = "БД и связанные Lua-файлы сохранены. Журнал: " + result.JournalPath + backupNotice;
        }
        catch (Exception ex)
        {
            ShowError((result?.Complete == true ? "Файлы записаны, но обновление редактора не завершено: " : "Сохранение остановлено: ") + ex.Message);
        }
        finally { IsBusy = false; }
    }

    public void RecordLuaGridBatch(IReadOnlyDictionary<LuaSourceDocument, string> before)
    {
        var frames = before.Where(p => p.Key.Text != p.Value)
            .Select(p => (WorkspaceEditFrame)new LuaGridEditFrame(p.Key, p.Value, p.Key.Text)).ToArray();
        if (frames.Length == 0) return;
        _undo.Push(new CompositeEditFrame(frames, "Вставка Lua отменена.", "Вставка Lua повторена."));
        _redo.Clear(); StatusText = "Значения Lua вставлены. Для записи нажмите «Сохранить».";
        LuaWorkspaceChanged(this, EventArgs.Empty); RaiseCommandStates();
    }
}
