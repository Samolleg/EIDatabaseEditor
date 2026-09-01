namespace EvilIslands.DatabaseEditor.Core;

public enum DatabaseDifferenceKind
{
    OnlyInCurrent,
    OnlyInCompared,
    FieldOnlyInCurrent,
    FieldOnlyInCompared,
    FieldChanged,
}

public sealed record DatabaseDifference(
    DatabaseDifferenceKind Kind,
    string TableKey,
    string TableTitle,
    string ResourceName,
    string RecordIdentity,
    string RecordName,
    int? CurrentRecordIndex,
    int? ComparedRecordIndex,
    int? FieldId,
    string FieldName,
    string CurrentValue,
    string ComparedValue);

public sealed record DatabaseComparisonResult(
    string CurrentPath,
    string CurrentSha256,
    string ComparedPath,
    string ComparedSha256,
    IReadOnlyList<DatabaseDifference> Differences)
{
    public int OnlyInCurrentCount => Differences.Count(item =>
        item.Kind == DatabaseDifferenceKind.OnlyInCurrent);

    public int OnlyInComparedCount => Differences.Count(item =>
        item.Kind == DatabaseDifferenceKind.OnlyInCompared);

    public int ModifiedRecordCount => Differences
        .Where(item => item.Kind is DatabaseDifferenceKind.FieldOnlyInCurrent or
            DatabaseDifferenceKind.FieldOnlyInCompared or DatabaseDifferenceKind.FieldChanged)
        .Select(item => (item.TableKey, item.RecordIdentity))
        .Distinct()
        .Count();

    public int ChangedFieldCount => Differences.Count(item =>
        item.Kind is DatabaseDifferenceKind.FieldOnlyInCurrent or
            DatabaseDifferenceKind.FieldOnlyInCompared or DatabaseDifferenceKind.FieldChanged);

    public bool AreEquivalent => Differences.Count == 0;
}

public static class DatabaseComparer
{
    public static DatabaseComparisonResult Compare(
        DatabaseDocument current,
        DatabaseDocument compared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(compared);

        var differences = new List<DatabaseDifference>();
        Dictionary<string, DatabaseTable> currentTables = current.Databases
            .SelectMany(group => group.Tables)
            .ToDictionary(table => table.Key, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DatabaseTable> comparedTables = compared.Databases
            .SelectMany(group => group.Tables)
            .ToDictionary(table => table.Key, StringComparer.OrdinalIgnoreCase);
        string[] tableKeys = currentTables.Keys
            .Concat(comparedTables.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string tableKey in tableKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentTables.TryGetValue(tableKey, out DatabaseTable? currentTable);
            comparedTables.TryGetValue(tableKey, out DatabaseTable? comparedTable);
            CompareTable(currentTable, comparedTable, tableKey, differences, cancellationToken);
        }

        return new DatabaseComparisonResult(
            current.SourcePath,
            current.Sha256,
            compared.SourcePath,
            compared.Sha256,
            differences);
    }

    private static void CompareTable(
        DatabaseTable? currentTable,
        DatabaseTable? comparedTable,
        string tableKey,
        ICollection<DatabaseDifference> differences,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, RecordMatch> currentRecords = BuildRecordMatches(currentTable?.Records ?? []);
        IReadOnlyDictionary<string, RecordMatch> comparedRecords = BuildRecordMatches(comparedTable?.Records ?? []);
        string[] recordKeys = currentRecords.Keys
            .Concat(comparedRecords.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string recordKey in recordKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentRecords.TryGetValue(recordKey, out RecordMatch? currentRecord);
            comparedRecords.TryGetValue(recordKey, out RecordMatch? comparedRecord);
            DatabaseTable table = currentTable ?? comparedTable!;
            if (currentRecord is null)
            {
                differences.Add(CreateRecordDifference(
                    DatabaseDifferenceKind.OnlyInCompared,
                    table,
                    null,
                    comparedRecord!));
                continue;
            }

            if (comparedRecord is null)
            {
                differences.Add(CreateRecordDifference(
                    DatabaseDifferenceKind.OnlyInCurrent,
                    table,
                    currentRecord,
                    null));
                continue;
            }

            CompareRecord(table, currentRecord, comparedRecord, differences, cancellationToken);
        }
    }

    private static void CompareRecord(
        DatabaseTable table,
        RecordMatch current,
        RecordMatch compared,
        ICollection<DatabaseDifference> differences,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<FieldMatchKey, DatabaseField> currentFields = BuildFieldMatches(current.Record.Fields);
        IReadOnlyDictionary<FieldMatchKey, DatabaseField> comparedFields = BuildFieldMatches(compared.Record.Fields);
        FieldMatchKey[] fieldKeys = currentFields.Keys
            .Concat(comparedFields.Keys)
            .Distinct()
            .OrderBy(key => key.FieldId)
            .ThenBy(key => key.Occurrence)
            .ToArray();

        foreach (FieldMatchKey fieldKey in fieldKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentFields.TryGetValue(fieldKey, out DatabaseField? currentField);
            comparedFields.TryGetValue(fieldKey, out DatabaseField? comparedField);
            if (currentField is not null && comparedField is not null &&
                currentField.Payload.SequenceEqual(comparedField.Payload))
            {
                continue;
            }

            DatabaseDifferenceKind kind = currentField is null
                ? DatabaseDifferenceKind.FieldOnlyInCompared
                : comparedField is null
                    ? DatabaseDifferenceKind.FieldOnlyInCurrent
                    : DatabaseDifferenceKind.FieldChanged;
            DatabaseField field = currentField ?? comparedField!;
            differences.Add(new DatabaseDifference(
                kind,
                table.Key,
                table.Title,
                table.ResourceName,
                current.Identity,
                current.Record.DisplayName,
                current.Record.RecordIndex,
                compared.Record.RecordIndex,
                field.Id,
                fieldKey.Occurrence == 0
                    ? field.DisplayName
                    : $"{field.DisplayName} · повтор {fieldKey.Occurrence + 1}",
                currentField?.EditorText ?? string.Empty,
                comparedField?.EditorText ?? string.Empty));
        }
    }

    private static DatabaseDifference CreateRecordDifference(
        DatabaseDifferenceKind kind,
        DatabaseTable table,
        RecordMatch? current,
        RecordMatch? compared)
    {
        RecordMatch record = current ?? compared!;
        return new DatabaseDifference(
            kind,
            table.Key,
            table.Title,
            table.ResourceName,
            record.Identity,
            record.Record.DisplayName,
            current?.Record.RecordIndex,
            compared?.Record.RecordIndex,
            null,
            "Вся запись",
            current is null ? string.Empty : $"{current.Record.Fields.Count} полей",
            compared is null ? string.Empty : $"{compared.Record.Fields.Count} полей");
    }

    private static IReadOnlyDictionary<string, RecordMatch> BuildRecordMatches(
        IReadOnlyList<DatabaseRecord> records)
    {
        var result = new Dictionary<string, RecordMatch>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DatabaseRecord record in records)
        {
            string identity = record.IdentityValue.Trim();
            if (identity.Length == 0)
            {
                identity = record.DisplayName.Trim();
            }

            if (identity.Length == 0)
            {
                identity = $"Запись {record.RecordIndex + 1}";
            }

            occurrences.TryGetValue(identity, out int occurrence);
            occurrences[identity] = occurrence + 1;
            string key = $"{identity}\u001F{occurrence}";
            result.Add(key, new RecordMatch(identity, record));
        }

        return result;
    }

    private static IReadOnlyDictionary<FieldMatchKey, DatabaseField> BuildFieldMatches(
        IReadOnlyList<DatabaseField> fields)
    {
        var result = new Dictionary<FieldMatchKey, DatabaseField>();
        var occurrences = new Dictionary<int, int>();
        foreach (DatabaseField field in fields)
        {
            occurrences.TryGetValue(field.Id, out int occurrence);
            occurrences[field.Id] = occurrence + 1;
            result.Add(new FieldMatchKey(field.Id, occurrence), field);
        }

        return result;
    }

    private sealed record RecordMatch(string Identity, DatabaseRecord Record);

    private readonly record struct FieldMatchKey(int FieldId, int Occurrence);
}
