using System.Globalization;
using System.Net;
using System.Text;

namespace EvilIslands.DatabaseEditor.App.Models;

internal static class SpreadsheetClipboardText
{
    public static SpreadsheetClipboardPayload CreatePayload(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string text = Serialize(rows);
        return new SpreadsheetClipboardPayload(
            text,
            SerializeCsv(rows),
            SerializeHtmlClipboard(rows));
    }

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

    private static string SerializeCsv(IReadOnlyList<IReadOnlyList<string>> rows) =>
        string.Join(
            "\r\n",
            rows.Select(row => string.Join(',', row.Select(EscapeCsvCell))));

    private static string EscapeCsvCell(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string SerializeHtmlClipboard(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var fragment = new StringBuilder("<table>");
        foreach (IReadOnlyList<string> row in rows)
        {
            fragment.Append("<tr>");
            foreach (string value in row)
            {
                string encoded = WebUtility.HtmlEncode(value ?? string.Empty)
                    .Replace("\r\n", "<br>", StringComparison.Ordinal)
                    .Replace("\r", "<br>", StringComparison.Ordinal)
                    .Replace("\n", "<br>", StringComparison.Ordinal);
                fragment.Append("<td>").Append(encoded).Append("</td>");
            }

            fragment.Append("</tr>");
        }

        fragment.Append("</table>");
        const string beforeFragment = "<html><body><!--StartFragment-->";
        const string afterFragment = "<!--EndFragment--></body></html>";
        const string headerTemplate =
            "Version:1.0\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";
        string emptyHeader = string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0);
        int startHtml = Encoding.UTF8.GetByteCount(emptyHeader);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(beforeFragment);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment.ToString());
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(afterFragment);
        string header = string.Format(
            CultureInfo.InvariantCulture,
            headerTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);
        return header + beforeFragment + fragment.ToString() + afterFragment;
    }
}

internal sealed record SpreadsheetClipboardPayload(string Text, string Csv, string Html);

internal sealed record SpreadsheetPasteCell(
    RecordRowViewModel Record,
    int FieldId,
    string Value,
    EvilIslands.DatabaseEditor.Core.DatabaseFieldProjection? Projection = null,
    SpreadsheetPasteTarget Target = SpreadsheetPasteTarget.DatabaseField);

internal enum SpreadsheetPasteTarget
{
    DatabaseField,
    LocalizationTitle,
    LocalizationDescription,
}
