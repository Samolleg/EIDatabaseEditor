using System.Collections.Immutable;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Models;
using System.Windows;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private ModuleWorkspaceReader? _moduleWorkspace;
    internal IWorkspaceReader ModuleWorkspace => _moduleWorkspace ??= new(this);
    private sealed class ModuleWorkspaceReader : IWorkspaceReader
    {
        private readonly MainWindowViewModel vm;
        private readonly Guid session = Guid.NewGuid();
        private long generation = 1, revision;
        private readonly Dictionary<RecordRowViewModel, Guid> ids = new();
        public event EventHandler<WorkspaceEvent>? Changed;
        internal WorkspaceStamp Stamp => new(session, generation, revision);
        internal RecordRowViewModel? Resolve(RecordRef reference) => ids.FirstOrDefault(p => p.Value == reference.RecordId && p.Key.TableKey == reference.TableKey).Key;
        internal ModuleWorkspaceReader(MainWindowViewModel vm) => this.vm = vm;
        internal void Replaced()
        {
            generation++; revision++; ids.Clear();
            Notify("WorkspaceReplaced");
        }
        internal void Edited()
        {
            if (vm._moduleTransactionActive) return;
            revision++;
            Notify("ContentChanged");
        }
        private void Notify(string kind)
        {
            foreach (EventHandler<WorkspaceEvent> handler in Changed?.GetInvocationList() ?? [])
                try { handler(this, new(Stamp, kind)); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        }
        public global::DatabaseEditor.Modules.V1.WorkspaceSnapshot Snapshot()
        {
            Application.Current?.Dispatcher.VerifyAccess();
            var rows = ImmutableArray.CreateBuilder<RecordDto>();
            foreach (var table in vm.Databases.SelectMany(d => d.Tables))
            foreach (var row in table.Records)
            {
                if (!ids.TryGetValue(row, out var id)) ids.Add(row, id = Guid.NewGuid());
                // Core owns QDB/text linking; never reconstruct QUEST/BRIEFING keys here.
                vm._service.RefreshRelatedTexts(row);
                rows.Add(new(new(session, generation, table.Key, id), row.DisplayName, row.LocalizationTitle,
                    row.CoreRecord.Fields.Select((f, index) => new FieldDto(f.Id, row.CoreRecord.Fields.Take(index).Count(x => x.Id == f.Id), [], new ValueDto(f.Kind, f.EditorText, f.DisplayName, f.IsEditable && f.Kind is "String" or "Int32" or "Float32"))).ToImmutableArray(),
                    row.RelatedTexts.Select(t => new TextDto(t.ResourceName, t.Title, t.Description, t.IsModified)).ToImmutableArray()));
            }
            return new(new(session, generation, revision), vm.SourcePath ?? "", vm.HasUnsavedWork, rows.ToImmutable());
        }
        public Result Navigate(RecordRef reference)
        {
            Application.Current?.Dispatcher.VerifyAccess();
            if (reference.SessionId != session || reference.Generation != generation) return new(ResultStatus.Conflict, "Снимок БД устарел.");
            var row = ids.FirstOrDefault(p => p.Value == reference.RecordId).Key;
            var table = vm.Databases.SelectMany(d => d.Tables).FirstOrDefault(t => t.Key == reference.TableKey);
            if (row is null || table is null || !table.Records.Contains(row)) return new(ResultStatus.Unavailable, "Запись недоступна.");
            vm.SelectTableCommand.Execute(table); vm.SelectedRecord = row;
            return Result.Ok;
        }
    }
}

