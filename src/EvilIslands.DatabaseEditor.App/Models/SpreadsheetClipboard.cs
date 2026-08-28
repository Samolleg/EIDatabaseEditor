using System.Text;

namespace EvilIslands.DatabaseEditor.App.Models;

internal static class SpreadsheetClipboardText
{
    public static string Serialize(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join(
            "\r\n",
            rows.Select(row => string.Join('\t', row.Select(EscapeCell))));
    }

    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var cellWasQuoted = false;
        var endedWithRowSeparator = false;

        for (var index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(character);
                }

                endedWithRowSeparator = false;
                continue;
            }

            if (character == '"' && cell.Length == 0 && !cellWasQuoted)
            {
                inQuotes = true;
                cellWasQuoted = true;
                endedWithRowSeparator = false;
                continue;
            }

            if (character == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellWasQuoted = false;
                endedWithRowSeparator = false;
                continue;
            }

            if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(cell.ToString());
                rows.Add(row.ToArray());
                row = [];
                cell.Clear();
                cellWasQuoted = false;
                endedWithRowSeparator = true;
                continue;
            }

            cell.Append(character);
            endedWithRowSeparator = false;
        }

        if (inQuotes)
        {
            throw new FormatException("В буфере обмена не закрыта кавычка табличной ячейки.");
        }

        if (!endedWithRowSeparator || row.Count > 0 || cell.Length > 0 || cellWasQuoted)
        {
            row.Add(cell.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static string EscapeCell(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny(['\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

internal sealed record SpreadsheetPasteCell(
    RecordRowViewModel Record,
    int FieldId,
    string Value);
