using DatabaseEditor.Modules.V1;
using DatabaseEditor.Modules.Host;
using EvilIslands.DatabaseEditor.Core;
using System.Collections.Immutable;

namespace EvilIslands.DatabaseEditor.App.ViewModels;
internal sealed partial class MainWindowViewModel
{
    internal DocumentStore ModuleDocuments { get; } = new();
    private readonly HashSet<Guid> _moduleTransactionIds = new();
    private bool _moduleTransactionActive;
    internal bool HasModuleHistory => _undo.OfType<ModuleTransactionFrame>().Any() || _redo.OfType<ModuleTransactionFrame>().Any();
    private sealed record ModuleDocumentFrame(DocumentSnapshot Before, DocumentSnapshot After) : WorkspaceEditFrame;
    private sealed record ModuleTransactionFrame(ImmutableArray<WorkspaceEditFrame> Frames, string Description) : WorkspaceEditFrame;

    internal Result ApplyModuleTransaction(ChangeSet change)
    {
        if (IsBusy || _moduleTransactionActive) return new(ResultStatus.Conflict, "Другая операция ещё не завершена.");
        var reader = (ModuleWorkspaceReader)ModuleWorkspace;
        if (change.ExpectedWorkspace != reader.Stamp) return new(ResultStatus.Conflict, "Снимок рабочей области устарел.");
        if (_moduleTransactionIds.Contains(change.TransactionId)) return new(ResultStatus.Conflict, "Транзакция уже применена.");
        var frames = ImmutableArray.CreateBuilder<WorkspaceEditFrame>();
        var targets = new HashSet<string>();
        try
        {
            foreach (var op in change.Operations)
            {
                switch (op)
                {
                    case SetField field:
                    {
                        if (field.Address.Record.SessionId != reader.Stamp.SessionId || field.Address.Record.Generation != reader.Stamp.Generation)
                            return new(ResultStatus.Conflict, "Поколение записи изменилось.");
                        var row = reader.Resolve(field.Address.Record);
                        if (row is null || !field.Address.NestedPath.IsEmpty) return new(ResultStatus.Unsupported, "Запись или вложенное поле недоступны.");
                        var core = row.CoreRecord.Fields.Where(f => f.Id == field.Address.FieldId).ElementAtOrDefault(field.Address.Occurrence);
                        var vmField = row.Fields.Where(f => f.Id == field.Address.FieldId).ElementAtOrDefault(field.Address.Occurrence);
                        if (core is null || vmField is null || !vmField.IsEditable) return new(ResultStatus.Unsupported, "Поле недоступно для редактирования.");
                        if (field.Expected.Kind != core.Kind || field.Value.Kind != core.Kind || field.Expected.ExactValue != core.EditorText)
                            return new(ResultStatus.Conflict, "Тип или исходное значение поля изменились.");
                        if (!targets.Add(core.Path)) return new(ResultStatus.Unsupported, "Повторная операция над полем в одном наборе.");
                        if (!core.TryPrepareValue(field.Value.ExactValue, out var after, out var error)) return new(ResultStatus.Failed, error ?? "Значение не принято.");
                        frames.Add(new FieldEditFrame(row, vmField, core.CaptureState(), after!)); break;
                    }
                    case SetText text:
                    {
                        var matches = TextResources.Where(t => t.ResourceName == text.Address.ResourceKey).ToArray();
                        if (matches.Length != 1) return new(ResultStatus.Conflict, "Текст отсутствует или неоднозначен.");
                        var entry = matches[0]; var resource = entry.CoreResource;
                        if (text.Address.Component is not ("title" or "description")) return new(ResultStatus.Unsupported);
                        if (!targets.Add("text:" + text.Address.ResourceKey)) return new(ResultStatus.Unsupported, "Повторная операция над текстом.");
                        string before = text.Address.Component == "title" ? resource.Title : resource.Description;
                        if (before != text.Expected) return new(ResultStatus.Conflict, "Исходный текст изменился.");
                        if (!resource.TryPrepareValue(text.Address.Component == "title" ? text.Value : resource.Title,
                            text.Address.Component == "description" ? text.Value : resource.Description, out var after, out var error))
                            return new(ResultStatus.Failed, error ?? "Текст не принят.");
                        frames.Add(new TextEditFrame(entry, resource.CaptureState(), after!)); break;
                    }
                    case UpdateDocument update:
                    {
                        var d = ModuleDocuments.Get(update.DocumentId);
                        if (d is null || d.Revision != update.ExpectedRevision) return new(ResultStatus.Conflict, "Документ изменился.");
                        if (!targets.Add("document:" + d.DocumentId)) return new(ResultStatus.Unsupported, "Повторная операция над документом.");
                        var candidate = d with { Text = update.Text, State = update.State };
                        var valid = ModuleDocuments.Validate(candidate); if (!valid.Succeeded) return valid;
                        frames.Add(new ModuleDocumentFrame(d, candidate)); break;
                    }
                    case ReplaceScriptRange range:
                    {
                        var d = ModuleDocuments.Get(range.DocumentId);
                        if (d is null || d.Revision != range.ExpectedRevision) return new(ResultStatus.Conflict, "Документ изменился.");
                        if (range.Start < 0 || range.Length < 0 || range.Start > d.Text.Length - range.Length || d.Text.Substring(range.Start, range.Length) != range.Expected)
                            return new(ResultStatus.Conflict, "Диапазон текста устарел.");
                        if (!targets.Add("document:" + d.DocumentId)) return new(ResultStatus.Unsupported);
                        var candidate = d with { Text = d.Text.Remove(range.Start, range.Length).Insert(range.Start, range.Value) };
                        var valid = ModuleDocuments.Validate(candidate); if (!valid.Succeeded) return valid;
                        frames.Add(new ModuleDocumentFrame(d, candidate)); break;
                    }
                    default: return new(ResultStatus.Unsupported, "Операция не поддерживается.");
                }
            }
            if (frames.Count == 0) return Result.Ok;
            var frame = new ModuleTransactionFrame(frames.ToImmutable(), change.Description);
            if (!ApplyModuleFrame(frame, false, out var applyError)) return new(ResultStatus.Conflict, applyError ?? "Транзакция отклонена.");
            _undo.Push(frame); _redo.Clear(); _moduleTransactionIds.Add(change.TransactionId); RaiseCommandStates();
            return Result.Ok;
        }
        catch (Exception ex) { return new(ResultStatus.Failed, ex.Message); }
    }

    internal Result ApplyModuleHistory(bool redo)
    {
        if (IsBusy || _moduleTransactionActive) return new(ResultStatus.Conflict, "Рабочая область занята.");
        var source = redo ? _redo : _undo;
        var target = redo ? _undo : _redo;
        if (source.Count == 0) return new(ResultStatus.Unavailable);
        int before = target.Count;
        if (redo) Redo(); else Undo();
        return target.Count == before + 1 ? Result.Ok : new(ResultStatus.Conflict, "История не применена: участник изменился.");
    }

    private bool ApplyModuleFrame(ModuleTransactionFrame frame, bool undo, out string? error)
    {
        error = null;
        // Validate every participant before the first live mutation, including Undo/Redo.
        foreach (var f in frame.Frames)
        {
            bool matches = f switch
            {
                FieldEditFrame x => x.Field.CaptureState().IsEquivalentTo(undo ? x.AfterState : x.BeforeState),
                TextEditFrame x => x.Entry.CaptureState().IsEquivalentTo(undo ? x.AfterState : x.BeforeState),
                ModuleDocumentFrame x => ModuleDocuments.Matches(undo ? x.After : x.Before),
                _ => false
            };
            if (!matches) { error = "Участник транзакции изменился или закрыт."; return false; }
        }
        _moduleTransactionActive = true;
        var applied = new List<WorkspaceEditFrame>();
        try
        {
            foreach (var f in (undo ? frame.Frames.Reverse() : frame.Frames))
            {
                if (f is ModuleDocumentFrame d) ModuleDocuments.Commit(undo ? d.Before : d.After, notify: false);
                else if (!TryApplyEditFrame(f, undo, out _, out error)) throw new InvalidOperationException(error);
                applied.Add(f);
            }
        }
        catch (Exception ex)
        {
            foreach (var f in applied.AsEnumerable().Reverse())
                if (f is ModuleDocumentFrame d) ModuleDocuments.Commit(undo ? d.After : d.Before, notify: false);
                else TryApplyEditFrame(f, !undo, out _, out _);
            error = ex.Message; return false;
        }
        finally { _moduleTransactionActive = false; }
        _moduleWorkspace?.Edited(frame.Frames.Any(f => f is not ModuleDocumentFrame));
        foreach (var d in frame.Frames.OfType<ModuleDocumentFrame>()) ModuleDocuments.Publish(d.Before.DocumentId);
        return true;
    }
}
