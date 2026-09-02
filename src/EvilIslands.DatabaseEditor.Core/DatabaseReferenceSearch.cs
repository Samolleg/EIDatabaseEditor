namespace EvilIslands.DatabaseEditor.Core;

public enum DatabaseReferenceMatchKind
{
    Confirmed,
    StructuredValue,
}

public sealed record DatabaseReferenceTarget(
    string TableKey,
    string TableTitle,
    int RecordIndex,
    string RecordIdentity,
    string RecordName);

public sealed record DatabaseReferenceUsage(
    DatabaseReferenceMatchKind MatchKind,
    string TableKey,
    string TableTitle,
    string ResourceName,
    int RecordIndex,
    string RecordIdentity,
    string RecordName,
    int FieldId,
    string FieldName,
    string Value);

public sealed record DatabaseReferenceSearchResult(
    DatabaseReferenceTarget Target,
    IReadOnlyList<DatabaseReferenceUsage> Usages)
{
    public int ConfirmedCount => Usages.Count(usage => usage.MatchKind == DatabaseReferenceMatchKind.Confirmed);

    public int StructuredCount => Usages.Count(usage => usage.MatchKind == DatabaseReferenceMatchKind.StructuredValue);
}

public static class DatabaseReferenceFinder
{
    private static readonly char[] TokenSeparators =
        [',', ';', '|', '\r', '\n', '\t', '[', ']', '{', '}', '(', ')', '='];

    public static DatabaseReferenceSearchResult Find(
        DatabaseDocument document,
        string targetTableKey,
        int targetRecordIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTableKey);

        DatabaseTable targetTable = document.Databases
            .SelectMany(group => group.Tables)
            .FirstOrDefault(table => table.Key.Equals(targetTableKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Целевая таблица не найдена.", nameof(targetTableKey));
        DatabaseRecord targetRecord = targetTable.Records
            .FirstOrDefault(record => record.RecordIndex == targetRecordIndex)
            ?? throw new ArgumentException("Целевая запись не найдена.", nameof(targetRecordIndex));
        string identity = targetRecord.IdentityValue.Trim();
        if (identity.Length == 0)
        {
            throw new InvalidOperationException("У выбранной записи отсутствует технический ключ для поиска ссылок.");
        }

        var confirmedTargetValues = DatabaseReferenceCatalog.ScalarReferences
            .Where(rule => rule.TargetTableKey.Equals(targetTable.Key, StringComparison.OrdinalIgnoreCase))
            .Select(rule => (
                Rule: rule,
                Value: targetRecord.Fields.FirstOrDefault(field => field.Id == rule.TargetFieldId)?.EditorText.Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var usages = new List<DatabaseReferenceUsage>();
        foreach (DatabaseTable table in document.Databases.SelectMany(group => group.Tables))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (DatabaseRecord record in table.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (DatabaseField field in record.Fields)
                {
                    if (ReferenceEquals(record, targetRecord) && field.Id == targetRecord.IdentityFieldId)
                    {
                        continue;
                    }

                    string value;
                    try
                    {
                        value = field.EditorText;
                    }
                    catch (DatabaseFormatException)
                    {
                        continue;
                    }

                    bool confirmed = confirmedTargetValues.Any(item =>
                        item.Rule.SourceTableKey.Equals(table.Key, StringComparison.OrdinalIgnoreCase) &&
                        item.Rule.SourceFieldId == field.Id &&
                        value.Trim().Equals(item.Value, StringComparison.OrdinalIgnoreCase));
                    bool structured = ContainsReferenceToken(value, identity);
                    if (!confirmed && !structured)
                    {
                        continue;
                    }

                    DatabaseReferenceMatchKind kind = confirmed
                        ? DatabaseReferenceMatchKind.Confirmed
                        : DatabaseReferenceMatchKind.StructuredValue;
                    usages.Add(new DatabaseReferenceUsage(
                        kind,
                        table.Key,
                        table.Title,
                        table.ResourceName,
                        record.RecordIndex,
                        record.IdentityValue,
                        record.DisplayName,
                        field.Id,
                        field.DisplayName,
                        value));
                }
            }
        }

        DatabaseReferenceUsage[] ordered = usages
            .OrderBy(usage => usage.MatchKind)
            .ThenBy(usage => usage.TableTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(usage => usage.RecordIndex)
            .ThenBy(usage => usage.FieldId)
            .ToArray();
        return new DatabaseReferenceSearchResult(
            new DatabaseReferenceTarget(
                targetTable.Key,
                targetTable.Title,
                targetRecord.RecordIndex,
                identity,
                targetRecord.DisplayName),
            ordered);
    }

    internal static bool ContainsReferenceToken(string source, string identity)
    {
        if (source.Trim().Equals(identity, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return source
            .Split(TokenSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals(identity, StringComparison.OrdinalIgnoreCase));
    }
}
