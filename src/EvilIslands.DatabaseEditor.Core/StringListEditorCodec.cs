using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record StringListElementAnalysis(
    string Kind,
    string PrimaryValue,
    string? SpellCode,
    IReadOnlyList<string> Modifiers,
    string Summary,
    string? Error)
{
    public bool IsValid => string.IsNullOrWhiteSpace(Error);
}

public static class StringListEditorCodec
{
    public static string FormatEditorText(IReadOnlyList<string> values) =>
        string.Join("; ", values);

    public static bool TryParseEditorText(
        string text,
        out IReadOnlyList<string> values,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            values = [];
            error = null;
            return true;
        }

        var result = new List<string>();
        var token = new StringBuilder();
        var delimiters = new Stack<(char Character, int Position)>();
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (!TryUpdateDelimiterStack(delimiters, character, index, out error))
            {
                values = [];
                return false;
            }

            bool isTopLevelSeparator = delimiters.Count == 0 &&
                character is ',' or ';' or '\r' or '\n';
            if (isTopLevelSeparator)
            {
                AddToken(result, token);
                continue;
            }

            token.Append(character);
        }

        if (delimiters.Count > 0)
        {
            (char opening, int position) = delimiters.Peek();
            char closing = opening == '[' ? ']' : '}';
            values = [];
            error = $"Не закрыта скобка «{opening}» в позиции {position + 1}. Добавьте «{closing}» перед применением.";
            return false;
        }

        AddToken(result, token);
        values = result;
        error = null;
        return true;
    }

    public static bool TryValidateElement(string? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Элемент списка не может быть пустым.";
            return false;
        }

        if (!TryParseEditorText(value, out IReadOnlyList<string> parsed, out error))
        {
            return false;
        }

        if (parsed.Count != 1)
        {
            error = "Элемент содержит разделитель верхнего уровня. Разнесите значения по отдельным строкам списка.";
            return false;
        }

        error = null;
        return true;
    }

    public static StringListElementAnalysis AnalyzeElement(string? value, string? fieldName = null)
    {
        string text = value?.Trim() ?? string.Empty;
        if (!TryValidateElement(text, out string? error))
        {
            return new("Ошибка", text, null, [], "Требует исправления", error);
        }

        int openBracket = text.IndexOf('[');
        if (openBracket > 0 && text.EndsWith(']'))
        {
            string item = text[..openBracket].Trim();
            string magic = text[(openBracket + 1)..^1].Trim();
            if (magic.Length == 0)
            {
                return new("Предмет с магией", item, null, [], $"Предмет: {item}", "В квадратных скобках не указано заклинание.");
            }

            ParseSpellSpecification(magic, out string spell, out string[] modifiers);
            string summary = modifiers.Length == 0
                ? $"Предмет: {item} · магия: {spell}"
                : $"Предмет: {item} · магия: {spell} · модификаторы: {string.Join(", ", modifiers)}";
            return new("Предмет с магией", item, spell, modifiers, summary, null);
        }

        if (AllowsStandaloneSpell(fieldName) &&
            TryParseStandaloneSpell(text, out string spellCode, out string[] runeCodes))
        {
            string summary = runeCodes.Length == 0
                ? $"Заклинание: {spellCode}"
                : $"Заклинание: {spellCode} · модификаторы: {string.Join(", ", runeCodes)}";
            return new("Заклинание", spellCode, spellCode, runeCodes, summary, null);
        }

        if (TryAnalyzeResourcePath(text, fieldName, out string pathKind, out string pathSummary))
        {
            return new(pathKind, text, null, [], pathSummary, null);
        }

        return new("Ссылка / значение", text, null, [], text, null);
    }

    private static bool AllowsStandaloneSpell(string? fieldName) =>
        string.IsNullOrWhiteSpace(fieldName) ||
        fieldName.Contains("заклин", StringComparison.CurrentCultureIgnoreCase) ||
        fieldName.Contains("маг", StringComparison.CurrentCultureIgnoreCase) ||
        fieldName.Contains("spell", StringComparison.OrdinalIgnoreCase);

    private static bool TryAnalyzeResourcePath(
        string text,
        string? fieldName,
        out string kind,
        out string summary)
    {
        string[] parts = text
            .Split('\\', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool looksLikePath = parts.Length > 1 ||
            (!string.IsNullOrWhiteSpace(fieldName) &&
             (fieldName.Contains("путь", StringComparison.CurrentCultureIgnoreCase) ||
              fieldName.Contains("звук", StringComparison.CurrentCultureIgnoreCase) ||
              fieldName.Contains("текстур", StringComparison.CurrentCultureIgnoreCase)));
        if (!looksLikePath)
        {
            kind = string.Empty;
            summary = string.Empty;
            return false;
        }

        if (parts.Length >= 3 && string.Equals(parts[0], "steps", StringComparison.OrdinalIgnoreCase))
        {
            kind = "Звук шага";
            summary = $"Блок: {parts[0]} · модель: {parts[1]} · поверхность: {string.Join(" / ", parts.Skip(2))}";
            return true;
        }

        kind = "Путь к ресурсу";
        summary = parts.Length > 1
            ? $"Блоки пути: {string.Join(" → ", parts)}"
            : text;
        return true;
    }

    private static bool TryParseStandaloneSpell(string text, out string spellCode, out string[] modifiers)
    {
        int openBrace = text.IndexOf('{');
        if (openBrace <= 0 || !text.EndsWith('}'))
        {
            spellCode = string.Empty;
            modifiers = [];
            return false;
        }

        ParseSpellSpecification(text, out spellCode, out modifiers);
        return spellCode.Length > 0;
    }

    private static void ParseSpellSpecification(string specification, out string spellCode, out string[] modifiers)
    {
        int openBrace = specification.IndexOf('{');
        if (openBrace < 0 || !specification.EndsWith('}'))
        {
            spellCode = specification.Trim();
            modifiers = [];
            return;
        }

        spellCode = specification[..openBrace].Trim();
        modifiers = specification[(openBrace + 1)..^1]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryUpdateDelimiterStack(
        Stack<(char Character, int Position)> delimiters,
        char character,
        int position,
        out string? error)
    {
        error = null;
        if (character is '[' or '{')
        {
            delimiters.Push((character, position));
            return true;
        }

        if (character is not (']' or '}'))
        {
            return true;
        }

        char expected = character == ']' ? '[' : '{';
        if (delimiters.Count == 0)
        {
            error = $"Лишняя закрывающая скобка «{character}» в позиции {position + 1}.";
            return false;
        }

        (char actual, int openingPosition) = delimiters.Peek();
        if (actual != expected)
        {
            error = $"Скобка «{actual}» в позиции {openingPosition + 1} закрыта символом «{character}» в позиции {position + 1}.";
            return false;
        }

        delimiters.Pop();
        return true;
    }

    private static void AddToken(ICollection<string> values, StringBuilder token)
    {
        string value = token.ToString().Trim();
        token.Clear();
        if (value.Length > 0)
        {
            values.Add(value);
        }
    }
}
