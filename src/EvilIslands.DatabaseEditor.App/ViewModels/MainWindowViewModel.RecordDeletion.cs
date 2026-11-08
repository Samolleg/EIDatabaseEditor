using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    internal async Task DeleteSelectedRecordAsync(Func<string, bool> confirm)
    {
        DatabaseTableViewModel? table = SelectedTable;
        RecordRowViewModel? record = SelectedRecord;
        if (table is null || record is null || IsBusy || !IsEditMode || !table.CanAppendRecords)
            return;
        if (record.IsTextOnlyBriefing)
        {
            ShowError("У выбранного брифинга есть только текст в texts.res, строки БД нет.");
            return;
        }
        if (!ReferenceEquals(table.Records.LastOrDefault(row => !row.IsTextOnlyBriefing), record))
        {
            ShowError("Удалить можно только последнюю строку таблицы. Удаление из середины сдвинет физические номера остальных записей и может повредить ссылки игры.");
            return;
        }

        DatabaseReferenceSearchResult? result = await FindReferencesForSelectedAsync();
        if (result is null || !ReferenceEquals(SelectedTable, table) || !ReferenceEquals(SelectedRecord, record))
            return;
        var usages = result.Usages.Where(usage =>
            (usage.TableKey != record.TableKey || usage.RecordIndex != record.SourceRecordIndex) &&
            (usage.TableKey != record.TableKey || usage.FieldId != record.IdentityFieldId)).ToArray();
        if (usages.Length > 0)
        {
            DatabaseReferenceUsage first = usages[0];
            ShowError($"Удаление «{record.DisplayName}» заблокировано: найдено ссылок или возможных использований в БД: {usages.Length}. " +
                $"Первое: {first.TableTitle} / {first.RecordName} / {first.FieldName}. Подробности доступны через «Где используется».");
            return;
        }
        if (!confirm(record.DisplayName)) return;
        // Recheck after the modal confirmation before changing the draft/history.
        if (IsBusy || !IsEditMode || !ReferenceEquals(SelectedTable, table) ||
            !ReferenceEquals(SelectedRecord, record) ||
            !ReferenceEquals(table.Records.LastOrDefault(row => !row.IsTextOnlyBriefing), record)) return;
        try
        {
            SetAddedRecordPresence(table, record, shouldBePresent: false);
            _undo.Push(new RecordPresenceEditFrame(table, record, BeforePresent: true, AfterPresent: false));
            _redo.Clear();
            SelectedRecord = table.Records.LastOrDefault();
            RefreshValidationDeferred();
            RaiseCommandStates();
            StatusText = $"Удалена строка «{record.DisplayName}». Ctrl+Z — вернуть; для записи в database.res нажмите «Сохранить».";
        }
        catch (Exception exception)
        {
            ShowError(UnwrapMessage(exception));
        }
    }
}
