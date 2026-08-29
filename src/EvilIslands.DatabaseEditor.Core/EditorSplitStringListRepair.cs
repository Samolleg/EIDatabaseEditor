namespace EvilIslands.DatabaseEditor.Core;

public sealed record EditorSplitStringListRepairResult(
    string TableKey,
    int RecordIndex,
    string RecordName,
    int FieldId,
    string FieldName,
    int OriginalElementCount,
    int RepairedElementCount,
    int JoinedFragmentGroups);

public static class EditorSplitStringListRepair
{
    public static IReadOnlyList<EditorSplitStringListRepairResult> FindCandidates(
        DatabaseDocument damaged,
        DatabaseDocument reference)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        ArgumentNullException.ThrowIfNull(reference);
        return Find(damaged, reference, apply: false);
    }

    public static IReadOnlyList<EditorSplitStringListRepairResult> Repair(
        DatabaseDocument damaged,
        DatabaseDocument reference)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        ArgumentNullException.ThrowIfNull(reference);
        return Find(damaged, reference, apply: true);
    }

    private static IReadOnlyList<EditorSplitStringListRepairResult> Find(
        DatabaseDocument damaged,
        DatabaseDocument reference,
        bool apply)
    {
        Dictionary<string, DatabaseTable> referenceTables = reference.Databases
            .SelectMany(group => group.Tables)
            .ToDictionary(table => table.Key, StringComparer.Ordinal);
        HashSet<string> confirmedReferenceValues = reference.Databases
            .SelectMany(group => group.Tables)
            .SelectMany(table => table.Records)
            .SelectMany(record => record.Fields)
            .SelectMany(field => field.TryGetStringListValues(out IReadOnlyList<string> values)
                ? values
                : Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        var results = new List<EditorSplitStringListRepairResult>();

        foreach (DatabaseTable table in damaged.Databases.SelectMany(group => group.Tables))
        {
            if (!referenceTables.TryGetValue(table.Key, out DatabaseTable? referenceTable))
            {
                continue;
            }

            int sharedRecordCount = Math.Min(table.Records.Count, referenceTable.Records.Count);
            for (var recordIndex = 0; recordIndex < sharedRecordCount; recordIndex++)
            {
                DatabaseRecord record = table.Records[recordIndex];
                DatabaseRecord referenceRecord = referenceTable.Records[recordIndex];
                Dictionary<int, DatabaseField> referenceFields = referenceRecord.Fields
                    .ToDictionary(field => field.Id);

                foreach (DatabaseField field in record.Fields)
                {
                    if (!referenceFields.TryGetValue(field.Id, out DatabaseField? referenceField) ||
                        !field.TryGetStringListValues(out IReadOnlyList<string> currentValues) ||
                        !referenceField.TryGetStringListValues(out _) ||
                        !TryRejoinConfirmedFragments(
                            currentValues,
                            confirmedReferenceValues,
                            out string[] repairedValues,
                            out int joinedGroups))
                    {
                        continue;
                    }

                    var result = new EditorSplitStringListRepairResult(
                        table.Key,
                        record.RecordIndex,
                        record.Name,
                        field.Id,
                        field.DisplayName,
                        currentValues.Count,
                        repairedValues.Length,
                        joinedGroups);

                    if (apply && !field.TrySetStringListValues(repairedValues, out string? error))
                    {
                        throw new InvalidOperationException(
                            $"Не удалось восстановить {table.Key}[{record.RecordIndex}]/field-{field.Id}: {error}");
                    }

                    results.Add(result);
                }
            }
        }

        return results;
    }

    private static bool TryRejoinConfirmedFragments(
        IReadOnlyList<string> currentValues,
        IReadOnlySet<string> confirmedReferenceValues,
        out string[] repairedValues,
        out int joinedGroups)
    {
        var repaired = new List<string>(currentValues.Count);
        var fragments = new List<string>();
        var delimiters = new Stack<char>();
        joinedGroups = 0;

        foreach (string value in currentValues)
        {
            if (fragments.Count == 0)
            {
                if (!UpdateDelimiterState(delimiters, value))
                {
                    repairedValues = [];
                    return false;
                }

                if (delimiters.Count == 0)
                {
                    repaired.Add(value);
                }
                else
                {
                    fragments.Add(value);
                }

                continue;
            }

            fragments.Add(value);
            if (!UpdateDelimiterState(delimiters, value))
            {
                repairedValues = [];
                return false;
            }

            if (delimiters.Count != 0)
            {
                continue;
            }

            string joined = string.Join(';', fragments);
            if (!confirmedReferenceValues.Contains(joined))
            {
                repairedValues = [];
                return false;
            }

            repaired.Add(joined);
            fragments.Clear();
            joinedGroups++;
        }

        if (fragments.Count != 0 || delimiters.Count != 0 || joinedGroups == 0)
        {
            repairedValues = [];
            return false;
        }

        repairedValues = repaired.ToArray();
        return true;
    }

    private static bool UpdateDelimiterState(Stack<char> delimiters, string value)
    {
        foreach (char character in value)
        {
            if (character is '[' or '{')
            {
                delimiters.Push(character);
                continue;
            }

            if (character is not (']' or '}'))
            {
                continue;
            }

            char expected = character == ']' ? '[' : '{';
            if (delimiters.Count == 0 || delimiters.Pop() != expected)
            {
                return false;
            }
        }

        return true;
    }
}
