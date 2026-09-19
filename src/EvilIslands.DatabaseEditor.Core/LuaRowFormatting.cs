using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

public static class LuaRowFormatting
{
    // Only literal numeric rows can be reordered safely. Never discard comments,
    // duplicate assignments, named keys or nested structures while formatting.
    public static string Compact(string text)
    {
        if (text.Contains("--", StringComparison.Ordinal)) return text;
        var row = LuaLiteralReader.Read(text);
        if (row.Kind != LuaLiteralKind.Table || row.Fields.Count == 0) return text;
        var values = new SortedDictionary<int, string>();
        foreach (var field in row.Fields)
        {
            if (!field.Key.StartsWith('#') ||
                !int.TryParse(field.Key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1 ||
                field.Value.Kind == LuaLiteralKind.Table ||
                !values.TryAdd(index, text[field.Value.Start..field.Value.End])) return text;
        }
        int sequence = 1;
        var fields = new List<string>();
        foreach (var (index, value) in values)
        {
            if (index == sequence) { fields.Add(value); sequence++; }
            else fields.Add("[" + index.ToString(CultureInfo.InvariantCulture) + "] = " + value);
        }
        return "{" + string.Join(", ", fields) + "}";
    }
}
