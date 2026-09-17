using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    internal SaveReport SaveModuleFiles(IReadOnlyList<PreparedFileWrite> moduleFiles, string journalDirectory,
        bool allowGamePaths = false)
    {
        SaveReport Fail(string message) => new([new(Guid.Empty, "", "NotAttempted", message)], false);
        if (IsBusy || _moduleTransactionActive) return Fail("Рабочая область занята.");
        if (_service is not CoreDatabaseWorkspaceService service) return Fail("Сервис сохранения недоступен.");
        if (HasPendingLuaChanges || PendingSoundEdit is not null) return Fail("Сначала сохраните изменения Lua и звука.");
        bool Supported(WorkspaceEditFrame f) => f switch
        {
            FieldEditFrame or TextEditFrame or ModuleDocumentFrame or RecordPresenceEditFrame or TextPresenceBatchEditFrame => true,
            FieldBatchEditFrame b => b.Edits.All(Supported),
            CompositeEditFrame c => c.Frames.All(Supported),
            ModuleTransactionFrame m => m.Frames.All(Supported),
            _ => false
        };
        if (_undo.Concat(_redo).Any(f => !Supported(f))) return Fail("История содержит изменения Lua или звука. Сохраните их отдельно. Исходники не изменены.");
        IsBusy = true;
        CoordinatedSaveResult? committed = null;
        try
        {
            var dbFiles = service.PrepareModuleSave();
            var writes = dbFiles.Concat(moduleFiles).ToArray();
            var result = DocumentSaveCoordinator.Commit(writes, journalDirectory, allowGamePaths);
            committed = result;
            if (result.Complete && dbFiles.Count > 0)
            {
                var saved = service.ReloadModuleSave();
                var tables = saved.Databases.SelectMany(d => d.Tables).ToDictionary(t => t.Key);
                var rows = new Dictionary<RecordRowViewModel, RecordRowViewModel>();
                var texts = new Dictionary<LocalizationEntryViewModel, LocalizationEntryViewModel>();
                IEnumerable<WorkspaceEditFrame> Flatten(WorkspaceEditFrame f)
                {
                    yield return f;
                    IEnumerable<WorkspaceEditFrame> children = f switch
                    {
                        CompositeEditFrame c => c.Frames,
                        ModuleTransactionFrame m => m.Frames,
                        _ => []
                    };
                    foreach (var child in children)
                        foreach (var nested in Flatten(child)) yield return nested;
                }
                var history = _undo.Concat(_redo).SelectMany(Flatten).ToArray();
                // Register structural identities first: edits can reference a row currently on the Redo stack.
                foreach (var frame in history.OfType<TextPresenceBatchEditFrame>())
                    foreach (var entry in frame.Entries)
                        if (!texts.ContainsKey(entry))
                            texts.Add(entry, service.RebindHistoryText(entry, TextResources.Contains(entry)));
                foreach (var frame in history.OfType<RecordPresenceEditFrame>())
                    if (!rows.ContainsKey(frame.Record))
                        rows.Add(frame.Record, service.RebindHistoryRecord(tables[frame.Table.Key], frame.Record,
                            frame.Table.Records.Contains(frame.Record)));
                RecordRowViewModel RebindRow(RecordRowViewModel previous) => rows.TryGetValue(previous, out var row)
                    ? row : tables[previous.TableKey].Records.Single(r => r.SourceRecordIndex == previous.SourceRecordIndex);
                LocalizationEntryViewModel RebindEntry(LocalizationEntryViewModel previous) => texts.TryGetValue(previous, out var entry)
                    ? entry : saved.TextResources.Single(t => t.CoreResource.Name == previous.CoreResource.Name);
                FieldEditFrame RebindField(FieldEditFrame f)
                {
                    var row = RebindRow(f.Record);
                    int occurrence = f.Record.Fields.Where(x => x.Id == f.Field.Id).TakeWhile(x => !ReferenceEquals(x, f.Field)).Count();
                    var field = row.Fields.Where(x => x.Id == f.Field.Id).ElementAt(occurrence);
                    return new(row, field, f.BeforeState.Rebind(field.CoreField), f.AfterState.Rebind(field.CoreField));
                }
                TextEditFrame RebindText(TextEditFrame f)
                {
                    var text = RebindEntry(f.Entry);
                    return new(text, f.BeforeState.Rebind(text.CoreResource), f.AfterState.Rebind(text.CoreResource), f.RefreshRelatedSounds);
                }
                WorkspaceEditFrame Rebind(WorkspaceEditFrame f) => f switch
                {
                    FieldEditFrame x => RebindField(x), TextEditFrame x => RebindText(x),
                    RecordPresenceEditFrame x => x with { Table = tables[x.Table.Key], Record = RebindRow(x.Record) },
                    TextPresenceBatchEditFrame x => x with { Entries = x.Entries.Select(RebindEntry).ToArray() },
                    FieldBatchEditFrame x => x with { Edits = x.Edits.Select(RebindField).ToArray() },
                    CompositeEditFrame x => x with { Frames = x.Frames.Select(Rebind).ToArray() },
                    ModuleTransactionFrame x => x with { Frames = System.Collections.Immutable.ImmutableArray.CreateRange(x.Frames.Select(Rebind)) },
                    ModuleDocumentFrame => f, _ => throw new InvalidOperationException("Неподдерживаемая история.")
                };
                var undo = _undo.Reverse().Select(Rebind).ToArray(); var redo = _redo.Reverse().Select(Rebind).ToArray();
                ApplyWorkspace(saved);
                foreach (var frame in undo) _undo.Push(frame);
                foreach (var frame in redo) _redo.Push(frame);
                RaiseCommandStates();
            }
            if (!result.Complete) ShowError("Сохранение не завершено. Журнал восстановления: " + result.JournalPath);
            return new(System.Collections.Immutable.ImmutableArray.CreateRange(result.Files.Select(f => new FileSaveResult(f.Id, f.Path, f.Status, f.Message))), result.Complete);
        }
        catch (Exception ex)
        {
            if (committed is not null)
                return new(System.Collections.Immutable.ImmutableArray.CreateRange(committed.Files.Select(f => new FileSaveResult(f.Id, f.Path, f.Status, f.Message + " Переоткрытие не завершено: " + ex.Message))), false);
            return Fail(ex.Message);
        }
        finally { IsBusy = false; }
    }
}
