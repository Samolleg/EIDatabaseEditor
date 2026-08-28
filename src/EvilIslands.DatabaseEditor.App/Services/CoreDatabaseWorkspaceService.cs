using System.IO;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class CoreDatabaseWorkspaceService : IDatabaseWorkspaceService
{
    private DatabaseDocument? _document;

    public async Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл database.res не найден.", fullPath);
        }

        var document = await DatabaseDocument.OpenAsync(fullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var databases = MapDatabases(document.Databases, cancellationToken);
        var resources = MapResources(document.Resources, document.Databases, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = MapDiagnostics(document.Diagnostics, cancellationToken);
        var snapshot = new WorkspaceSnapshot
        {
            SourcePath = document.SourcePath,
            FileLength = document.FileLength,
            Sha256 = document.Sha256,
            Resources = resources,
            Databases = databases,
            Diagnostics = diagnostics,
        };
        cancellationToken.ThrowIfCancellationRequested();
        _document = document;
        return snapshot;
    }

    public Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(
        CancellationToken cancellationToken)
    {
        DatabaseDocument document = RequireDocument();
        return Task.Run<IReadOnlyList<ValidationMessageViewModel>>(
            () => MapDiagnostics(document.Validate(cancellationToken), cancellationToken),
            cancellationToken);
    }

    public RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(template);
        if (!table.CanAppendRecords || !string.Equals(table.Key, template.TableKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Выбранная строка не может быть образцом для этой таблицы.");
        }

        DatabaseRecord record = RequireDocument().AppendRecordClone(template.CoreRecord);
        return new RecordRowViewModel(record, table.Key, table.Title, table.IsEditable);
    }

    public void RemoveAddedRecord(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireDocument().RemoveAddedRecord(record.CoreRecord);
    }

    public void RestoreAddedRecord(RecordRowViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireDocument().RestoreAddedRecord(record.CoreRecord);
    }

    public async Task<SaveCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken)
    {
        var document = RequireDocument();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var result = await document.SaveAsAsync(Path.GetFullPath(targetPath), cancellationToken).ConfigureAwait(false);
        return new SaveCopyResult(
            result.TargetPath,
            result.FileLength,
            result.Sha256,
            result.ReopenedSuccessfully,
            result.Diagnostics.Count);
    }

    private static IReadOnlyList<DatabaseGroupViewModel> MapDatabases(
        IReadOnlyList<DatabaseGroup> groups,
        CancellationToken cancellationToken)
    {
        var result = new DatabaseGroupViewModel[groups.Count];
        for (var groupIndex = 0; groupIndex < result.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[groupIndex];
            var tables = new DatabaseTableViewModel[group.Tables.Count];
            for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
            {
                var table = group.Tables[tableIndex];
                var records = new RecordRowViewModel[table.Records.Count];
                for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
                {
                    if ((recordIndex & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    records[recordIndex] = new RecordRowViewModel(
                        table.Records[recordIndex],
                        table.Key,
                        table.Title,
                        table.IsEditable);
                }

                tables[tableIndex] = new DatabaseTableViewModel(
                    group.Key,
                    group.Title,
                    table.Key,
                    table.Title,
                    table.ResourceName,
                    table.SectionId,
                    table.SupportState,
                    table.CanAppendRecords,
                    records);
            }

            result[groupIndex] = new DatabaseGroupViewModel(
                group.Key,
                group.Title,
                group.ResourceName,
                group.SupportState,
                tables);
        }

        return result;
    }

    private static IReadOnlyList<ResourceRowViewModel> MapResources(
        IReadOnlyList<DatabaseResourceDescriptor> resources,
        IReadOnlyList<DatabaseGroup> databases,
        CancellationToken cancellationToken)
    {
        var supportByResource = databases.ToDictionary(
            database => database.ResourceName,
            database => database.SupportState,
            StringComparer.OrdinalIgnoreCase);
        var result = new ResourceRowViewModel[resources.Count];
        for (var index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = resources[index];
            var supportState = supportByResource.TryGetValue(resource.Name, out var mappedState)
                ? mappedState
                : DatabaseSupportState.Unavailable;
            result[index] = new ResourceRowViewModel(
                resource.Index,
                resource.Name,
                resource.Kind,
                resource.Offset,
                resource.Length,
                supportState switch
                {
                    DatabaseSupportState.Editable => "Распознан · редактирование",
                    DatabaseSupportState.ReadOnly => "Распознан · просмотр",
                    _ => "Сохранён как есть",
                });
        }

        return result;
    }

    private static IReadOnlyList<ValidationMessageViewModel> MapDiagnostics(
        IEnumerable<ValidationDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ValidationMessageViewModel>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = diagnostic.Path ?? diagnostic.Location?.ResourceName ?? "—";
            result.Add(new ValidationMessageViewModel(
                diagnostic.Severity.ToString(),
                FormatSeverity(diagnostic.Severity),
                diagnostic.Domain.ToString(),
                FormatDomain(diagnostic.Domain),
                diagnostic.Code,
                diagnostic.Message,
                FormatPlace(diagnostic),
                FormatDetail(diagnostic, path),
                path,
                diagnostic.Offset,
                diagnostic.IsBlocking,
                diagnostic.Location?.ResourceName,
                diagnostic.Location?.TableKey,
                diagnostic.Location?.RecordIndex,
                diagnostic.Location?.FieldId,
                diagnostic.Location?.FieldKey));
        }

        return result;
    }

    private static string FormatSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "Ошибка",
        DiagnosticSeverity.Warning => "Предупреждение",
        _ => "Информация",
    };

    private static string FormatDomain(DiagnosticDomain domain) => domain switch
    {
        DiagnosticDomain.Semantic => "Семантика",
        _ => "Структура",
    };

    private static string FormatPlace(ValidationDiagnostic diagnostic)
    {
        var location = diagnostic.Location;
        if (location is null)
        {
            if (string.IsNullOrWhiteSpace(diagnostic.Path))
            {
                return "Весь документ";
            }

            return diagnostic.Offset is long offset
                ? $"{diagnostic.Path} · 0x{offset:X}"
                : diagnostic.Path;
        }

        var parts = new List<string>(3);
        parts.Add(string.IsNullOrWhiteSpace(location.TableKey)
            ? location.ResourceName
            : location.TableKey);
        if (location.RecordIndex >= 0)
        {
            var record = $"#{location.RecordIndex + 1:N0}";
            if (!string.IsNullOrWhiteSpace(location.RecordName))
            {
                record += $" {location.RecordName}";
            }

            parts.Add(record);
        }

        if (location.FieldId is int fieldId)
        {
            parts.Add(string.IsNullOrWhiteSpace(location.FieldKey)
                ? $"поле {fieldId}"
                : $"{location.FieldKey} [{fieldId}]");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatDetail(ValidationDiagnostic diagnostic, string path)
    {
        var parts = new List<string>(7);
        if (diagnostic.Location is { } location)
        {
            parts.Add($"Ресурс: {location.ResourceName}");
            parts.Add($"Таблица: {location.TableKey}");
            if (location.RecordIndex >= 0)
            {
                parts.Add($"Запись: {location.RecordIndex + 1:N0} — {location.RecordName}");
            }

            if (location.FieldId is int fieldId)
            {
                var fieldName = string.IsNullOrWhiteSpace(location.FieldKey)
                    ? "без имени"
                    : location.FieldKey;
                parts.Add($"Поле: {fieldName} [{fieldId}]");
            }
        }

        if (path != "—")
        {
            parts.Add($"Путь: {path}");
        }

        if (diagnostic.Offset is long offset)
        {
            parts.Add($"Смещение: {offset:N0} (0x{offset:X})");
        }

        parts.Add(diagnostic.IsBlocking
            ? "Результат: блокирует сохранение копии."
            : "Результат: не блокирует сохранение копии.");
        return string.Join(Environment.NewLine, parts);
    }

    private DatabaseDocument RequireDocument() =>
        _document ?? throw new InvalidOperationException("Сначала откройте database.res.");
}
