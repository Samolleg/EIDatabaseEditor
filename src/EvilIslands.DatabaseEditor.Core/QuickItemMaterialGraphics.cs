using System.Globalization;

namespace EvilIslands.DatabaseEditor.Core;

// Optional items.quick_items field 28; the addon uses the same ASCII grammar.
internal static class QuickItemMaterialGraphics
{
    internal static Dictionary<string, int> Parse(string raw, string itemName)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (raw.Length == 0) return result;
        var entries = raw.Split(';');
        for (int i = 0; i < entries.Length; i++)
        {
            if (i == entries.Length - 1 && entries[i].Length == 0) continue;
            var pair = entries[i].Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0 ||
                pair[0].Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_')) || pair[1].Any(c => c < '0' || c > '9') ||
                !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                !result.TryAdd(pair[0], index))
                throw new InvalidDataException($"Неверная привязка модели к материалу в «{itemName}». " +
                    "Формат: sv=853;gd=889. Нужны код материала и целый неотрицательный индекс; без пробелов и повторных кодов.");
        }
        return result;
    }

    internal static Dictionary<string, int> ForItem(ModelRecord item) =>
        Parse(item.Table == "items.quick_items" ? item.Text(28) : "", item.Name);

    internal static int Index(ModelRecord item, ModelRecord? material)
    {
        var graphics = ForItem(item);
        return material is not null && graphics.TryGetValue(material.Text(2), out int index)
            ? index : item.Integer(5);
    }
}
