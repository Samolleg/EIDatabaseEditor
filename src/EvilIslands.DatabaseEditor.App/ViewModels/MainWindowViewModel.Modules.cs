using System.Collections.Immutable;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App.Models;
using System.Windows;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private ModuleWorkspaceReader? _moduleWorkspace;
    internal IWorkspaceReader ModuleWorkspace => _moduleWorkspace ??= new(this);
    internal global::DatabaseEditor.Modules.Host.MapsWorkspaceContext ModuleMapsContext
    {
        get { _moduleWorkspace ??= new(this); return new(_moduleWorkspace.Stamp, SourcePath ?? ""); }
    }
    private sealed class ModuleWorkspaceReader : IWorkspaceReader
    {
        private readonly MainWindowViewModel vm;
        private readonly Guid session = Guid.NewGuid();
        private long generation = 1, revision;
        private readonly Dictionary<RecordRowViewModel, Guid> ids = new();
        private global::DatabaseEditor.Modules.V1.WorkspaceSnapshot? cached;
        public event EventHandler<WorkspaceEvent>? Changed;
        internal WorkspaceStamp Stamp => new(session, generation, revision);
        internal RecordRowViewModel? Resolve(RecordRef reference) => ids.FirstOrDefault(p => p.Value == reference.RecordId && p.Key.TableKey == reference.TableKey).Key;
        internal ModuleWorkspaceReader(MainWindowViewModel vm) => this.vm = vm;
        internal void Replaced()
        {
            cached = null;
            generation++; revision++; ids.Clear();
            Notify("WorkspaceReplaced");
        }
        internal void Edited(bool databaseChanged = true)
        {
            if (vm._moduleTransactionActive) return;
            revision++;
            cached = databaseChanged || cached is null ? null : cached with { Stamp = Stamp, Modified = vm.HasUnsavedWork };
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
            if (cached is not null && cached.Stamp == Stamp)
            {
                if (cached.Modified != vm.HasUnsavedWork) cached = cached with { Modified = vm.HasUnsavedWork };
                return cached;
            }
            var rows = ImmutableArray.CreateBuilder<RecordDto>();
            foreach (var table in vm.Databases.SelectMany(d => d.Tables))
            foreach (var row in table.Records)
            {
                if (!ids.TryGetValue(row, out var id)) ids.Add(row, id = Guid.NewGuid());
                // Core owns QDB/text linking; never reconstruct QUEST/BRIEFING keys here.
                vm._service.RefreshRelatedTexts(row);
                var occurrences = new Dictionary<int, int>();
                var fields = row.CoreRecord.Fields.Select(f =>
                {
                    int occurrence = occurrences.GetValueOrDefault(f.Id);
                    occurrences[f.Id] = occurrence + 1;
                    return new FieldDto(f.Id, occurrence, [], new ValueDto(f.Kind, f.EditorText, f.DisplayName, f.IsEditable && f.Kind is "String" or "Int32" or "Float32"));
                }).ToImmutableArray();
                rows.Add(new(new(session, generation, table.Key, id), row.DisplayName, row.LocalizationTitle,
                    fields,
                    row.RelatedTexts.Select(t => new TextDto(t.ResourceName, t.Title, t.Description, t.IsModified)).ToImmutableArray()));
            }
            return cached = new(new(session, generation, revision), vm.SourcePath ?? "", vm.HasUnsavedWork, rows.ToImmutable());
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

