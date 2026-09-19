using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public enum LuaLiteralKind { Table, String, Number, Boolean, Nil }
public sealed record LuaField(string Key, int Start, LuaLiteral Value, int? Separator);
public sealed record LuaLiteral(LuaLiteralKind Kind, int Start, int End, string Text, IReadOnlyList<LuaField> Fields)
{
    public LuaField? Field(string key) => Fields.LastOrDefault(f => f.Key == key);
}
public sealed record LuaPatch(int Start, int Length, string Text);

/// <summary>Reads literal data only. Lua code is never executed. All offsets refer to the original text.</summary>
public static class LuaLiteralReader
{
    private sealed record Token(string Text, int Start, int End, bool String = false);
    private static readonly Regex Number = new(@"^-?(?:0[xX][0-9a-fA-F]+|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)$", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z_0-9]*$", RegexOptions.CultureInvariant);

    public static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (char c in value)
            b.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ when c < 32 || c == 127 => "\\" + ((int)c).ToString("D3", CultureInfo.InvariantCulture), _ => c.ToString() });
        return b.Append('"').ToString();
    }

    public static string Scalar(LuaLiteralKind kind, string text) => kind switch
    {
        LuaLiteralKind.String => Quote(text),
        LuaLiteralKind.Boolean when text is "true" or "false" => text,
        LuaLiteralKind.Nil when text == "nil" => text,
        LuaLiteralKind.Number when Number.IsMatch(text.Trim()) && IsFinite(text.Trim()) => text.Trim(),
        _ => throw new FormatException("Ожидается конечное число (десятичная точка), true/false либо строка соответствующего типа.")
    };
    public static bool IsFinite(string text) => text.Contains('x', StringComparison.OrdinalIgnoreCase)
        ? long.TryParse(text.TrimStart('+', '-')[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n);
    public static string Key(string key) => key.StartsWith('#') ? "[" + key[1..] + "]" : Identifier.IsMatch(key) ? key : "[" + Quote(key) + "]";

    public static LuaLiteral Read(string text)
    {
        var tokens = Tokenize(text); int i = 0;
        var node = Parse(tokens, ref i, 0);
        if (i != tokens.Count) throw new FormatException("Вместо значения обнаружено выражение Lua. Оно не редактируется как данные.");
        return node;
    }

    public static IReadOnlyDictionary<string, (LuaLiteral? Value, string? Error)> Assignments(string text, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, (LuaLiteral?, string?)>(StringComparer.Ordinal);
        var tokens = Tokenize(text); int blocks = 0, braces = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i]; if (t.String) continue;
            if (blocks == 0 && braces == 0 && wanted.Contains(t.Text) && i + 1 < tokens.Count && tokens[i + 1].Text == "=" &&
                (i == 0 || tokens[i - 1].Text is not ("." or ":" or "local")))
            {
                if (result.ContainsKey(t.Text)) result[t.Text] = (null, "Несколько присваиваний таблицы: требуется ручная проверка Lua.");
                else
                {
                    try
                    {
                        int j = i + 2; var value = Parse(tokens, ref j, 0);
                        if (value.Kind != LuaLiteralKind.Table || j < tokens.Count && ContinuesExpression(tokens[j]))
                            throw new FormatException("Ожидается самостоятельная таблица литералов.");
                        result[t.Text] = (value, null);
                    }
                    catch (FormatException ex) { result[t.Text] = (null, ex.Message); }
                }
            }
            if (t.Text == "{") braces++;
            if (t.Text == "}") braces--;
            if (t.Text is "function" or "if" or "do" or "repeat") blocks++;
            if (t.Text is "end" or "until") blocks--;
        }
        return result;
    }

    private static bool ContinuesExpression(Token token) => token.String || token.Text is
        "." or "[" or ":" or "(" or "{" or "," or "+" or "-" or "*" or "/" or "%" or "^" or "<" or ">" or "=" or "~" or "&" or "|" or "or" or "and";

    private static LuaLiteral Parse(List<Token> ts, ref int i, int depth)
    {
        if (depth > 40 || i >= ts.Count) throw new FormatException("Незавершённая либо слишком глубокая таблица Lua.");
        Token t = ts[i++];
        if (t.String) return new(LuaLiteralKind.String, t.Start, t.End, Unquote(t.Text), []);
        if (t.Text == "{")
        {
            List<LuaField> fields = []; int sequence = 0;
            while (i < ts.Count && ts[i].Text != "}")
            {
                int start = ts[i].Start; string key;
                if (ts[i].Text == "[")
                {
                    i++; var k = Parse(ts, ref i, depth + 1);
                    key = k.Kind == LuaLiteralKind.String ? k.Text : k.Kind == LuaLiteralKind.Number ? "#" + k.Text : throw new FormatException("Вычисляемый ключ Lua не поддерживается.");
                    Expect(ts, ref i, "]"); Expect(ts, ref i, "=");
                }
                else if (i + 1 < ts.Count && ts[i + 1].Text == "=" && Identifier.IsMatch(ts[i].Text))
                { key = ts[i].Text; i += 2; }
                else key = "#" + (++sequence).ToString(CultureInfo.InvariantCulture);
                var value = Parse(ts, ref i, depth + 1); int? separator = null;
                if (i < ts.Count && ts[i].Text is "," or ";") separator = ts[i++].Start;
                else if (i >= ts.Count || ts[i].Text != "}") throw new FormatException("В таблице есть выражение или вызов Lua; автоматическое редактирование отключено.");
                fields.Add(new(key, start, value, separator));
            }
            Expect(ts, ref i, "}");
            return new(LuaLiteralKind.Table, t.Start, ts[i - 1].End, "", fields);
        }
        string raw = t.Text; int end = t.End;
        if (raw is "+" or "-" && i < ts.Count) { raw += ts[i].Text; end = ts[i++].End; }
        if (Number.IsMatch(raw) && IsFinite(raw)) return new(LuaLiteralKind.Number, t.Start, end, raw, []);
        if (raw is "true" or "false") return new(LuaLiteralKind.Boolean, t.Start, end, raw, []);
        if (raw == "nil") return new(LuaLiteralKind.Nil, t.Start, end, raw, []);
        throw new FormatException($"Значение «{raw}» является кодом, а не поддерживаемым литералом Lua.");
    }
    private static void Expect(List<Token> ts, ref int i, string token)
    { if (i >= ts.Count || ts[i++].Text != token) throw new FormatException("Ожидается «" + token + "»."); }

    private static List<Token> Tokenize(string text)
    {
        List<Token> result = []; int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-')
            {
                i += 2;
                if (LongEnd(text, i, out int end)) i = end;
                else while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                continue;
            }
            if (LongEnd(text, i, out int longEnd))
            { i = longEnd; result.Add(new(text[start..i], start, i, true)); continue; }
            char c = text[i++];
            if (c is '\'' or '"')
            {
                bool closed = false;
                while (i < text.Length)
                {
                    char q = text[i++];
                    if (q == '\\') { if (i < text.Length) i++; }
                    else if (q == c) { closed = true; break; }
                }
                if (!closed) throw new FormatException("Незавершённая строка Lua.");
                result.Add(new(text[start..i], start, i, true)); continue;
            }
            if (char.IsDigit(c) || c == '.' && i < text.Length && char.IsDigit(text[i]))
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] is '+' or '-' && text[i - 1] is 'e' or 'E')) i++;
            }
            else if (char.IsLetter(c) || c == '_') while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            result.Add(new(text[start..i], start, i));
        }
        return result;
    }
    private static bool LongEnd(string text, int start, out int end)
    {
        end = start; if (start >= text.Length || text[start] != '[') return false;
        int i = start + 1; while (i < text.Length && text[i] == '=') i++;
        if (i >= text.Length || text[i] != '[') return false;
        string closing = "]" + new string('=', i - start - 1) + "]";
        int at = text.IndexOf(closing, i + 1, StringComparison.Ordinal);
        if (at < 0) throw new FormatException("Незавершённая длинная строка/комментарий Lua.");
        end = at + closing.Length; return true;
    }
    private static string Unquote(string raw)
    {
        if (raw[0] == '[')
        {
            int n = raw.IndexOf('[', 1) + 1; string value = raw[n..^n];
            value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return value.StartsWith('\n') ? value[1..] : value;
        }
        var b = new StringBuilder();
        for (int i = 1; i < raw.Length - 1; i++)
        {
            char c = raw[i]; if (c != '\\') { b.Append(c); continue; }
            c = raw[++i];
            if (char.IsAsciiDigit(c))
            {
                int n = c - '0', count = 1;
                while (count < 3 && i + 1 < raw.Length - 1 && char.IsAsciiDigit(raw[i + 1])) { n = n * 10 + raw[++i] - '0'; count++; }
                if (n > 127) throw new FormatException("Байтовый escape вне ASCII: строка требует ручной проверки кодировки Lua."); b.Append((char)n);
            }
            else if (c == 'z') { while (i + 1 < raw.Length - 1 && char.IsWhiteSpace(raw[i + 1])) i++; }
            else if (c == 'x') { byte n = Convert.ToByte(raw.Substring(i + 1, 2), 16); if (n > 127) throw new FormatException("Байтовый escape вне ASCII: строка требует ручной проверки кодировки Lua."); b.Append((char)n); i += 2; }
            else { b.Append(c switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'n' => '\n', 'r' => '\r', 't' => '\t', '\r' => '\n', _ => c }); if (c == '\r' && raw[i + 1] == '\n') i++; }
        }
        return b.ToString();
    }
}

public sealed class LuaSourceDocument
{
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private byte[] _baseline;
    private readonly Stack<(string Text, string Description)> _undo = new(), _redo = new();
    private readonly string[] _tableNames;
    public string Path { get; }
    public string Text { get; private set; }
    public string EncodingName => _encoding.WebName + (_preamble.Length > 0 ? " BOM" : "");
    public string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    public IReadOnlyDictionary<string, (LuaLiteral? Value, string? Error)> Tables { get; private set; }
    public bool HasChanges => !Encode(Text).AsSpan().SequenceEqual(_baseline);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<string> Changes => HasChanges ? _undo.Reverse().Select(x => x.Description).ToArray() : [];
    public string BaselineHash => Convert.ToHexString(SHA256.HashData(_baseline));

    private LuaSourceDocument(string path, byte[] bytes, string[] names)
    {
        Path = System.IO.Path.GetFullPath(path); _baseline = bytes; _tableNames = names;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { _encoding = new UTF8Encoding(false, true); _preamble = [0xef, 0xbb, 0xbf]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { _encoding = new UnicodeEncoding(false, false, true); _preamble = [0xff, 0xfe]; }
        else { _preamble = []; try { _ = new UTF8Encoding(false, true).GetString(bytes); _encoding = new UTF8Encoding(false, true); } catch (DecoderFallbackException) { _encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); } }
        Text = _encoding.GetString(bytes, _preamble.Length, bytes.Length - _preamble.Length);
        if (!Encode(Text).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Кодировка не допускает точное повторное сохранение.");
        Tables = LuaLiteralReader.Assignments(Text, names);
    }
    public static LuaSourceDocument Open(string path, params string[] tableNames) => new(path, File.ReadAllBytes(path), tableNames);
    public byte[] Bytes => Encode(Text);
    private byte[] Encode(string text) => [.. _preamble, .. _encoding.GetBytes(text)];
    public int Line(int offset) => 1 + Text[..offset].Count(c => c == '\n');

    public void Apply(IEnumerable<LuaPatch> edits, string description)
    {
        string next = Text; int previous = Text.Length + 1;
        foreach (var p in edits.OrderByDescending(p => p.Start))
        {
            if (p.Start < 0 || p.Length < 0 || p.Start + p.Length > Text.Length || p.Start + p.Length > previous) throw new InvalidOperationException("Пересекающиеся правки Lua.");
            next = next.Remove(p.Start, p.Length).Insert(p.Start, p.Text); previous = p.Start;
        }
        if (next == Text) return;
        _ = Encode(next); var parsed = LuaLiteralReader.Assignments(next, _tableNames);
        foreach (var (name, value) in Tables.Where(t => t.Value.Value is not null))
            if (!parsed.TryGetValue(name, out var after) || after.Value is null) throw new InvalidDataException("Правка нарушила структуру " + name + ": " + after.Error);
        _undo.Push((Text, description)); _redo.Clear(); Text = next; Tables = parsed;
    }
    public void Undo() { if (_undo.TryPop(out var v)) { _redo.Push((Text, v.Description)); Text = v.Text; Tables = LuaLiteralReader.Assignments(Text, _tableNames); } }
    public void Redo() { if (_redo.TryPop(out var v)) { _undo.Push((Text, v.Description)); Text = v.Text; Tables = LuaLiteralReader.Assignments(Text, _tableNames); } }
    public void Discard() { Text = _encoding.GetString(_baseline, _preamble.Length, _baseline.Length - _preamble.Length); _undo.Clear(); _redo.Clear(); Tables = LuaLiteralReader.Assignments(Text, _tableNames); }
    public IReadOnlyList<LuaPatch> Append(LuaLiteral table, string literalField)
        => AppendTo(Text, table, literalField);
    public static IReadOnlyList<LuaPatch> AppendTo(string text, LuaLiteral table, string literalField)
    {
        _ = LuaLiteralReader.Read("{" + literalField + "}");
        var last = table.Fields.LastOrDefault(); List<LuaPatch> patches = [];
        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int close = table.End - 1; int line = text.LastIndexOf('\n', close);
        bool commaAtClose = last is not null && last.Separator is null && last.Value.End == close;
        if (last is not null && last.Separator is null && !commaAtClose) patches.Add(new(last.Value.End, 0, ","));
        string indentation = text[(line + 1)..close]; if (indentation.Any(c => !char.IsWhiteSpace(c))) indentation = "";
        patches.Add(new(close, 0, (commaAtClose ? "," : "") + newLine + indentation + "    " + literalField + "," + newLine + indentation));
        return patches;
    }
    public IReadOnlyList<LuaPatch> Remove(LuaLiteral table, LuaField field)
    {
        List<LuaPatch> patches = [new(field.Start, field.Value.End - field.Start, "")];
        if (field.Separator is int separator) patches.Add(new(separator, 1, ""));
        // A trailing separator on the previous field is legal Lua and is deliberately preserved.
        if (!table.Fields.Contains(field)) throw new InvalidOperationException("Устаревшая строка Lua.");
        return patches;
    }
    public void VerifyUnchanged()
    { if (!File.ReadAllBytes(Path).AsSpan().SequenceEqual(_baseline)) throw new IOException("Файл изменён вне редактора: " + Path + ". Сохранение остановлено. Сохраните свои правки отдельно и переподключите Lua."); }

    public void AcceptSaved()
    {
        byte[] output = Bytes;
        if (!File.ReadAllBytes(Path).AsSpan().SequenceEqual(output)) throw new IOException("Сохранённый Lua изменён извне: " + Path);
        _baseline = output; _undo.Clear(); _redo.Clear();
    }

    public string Save()
    {
        if (!HasChanges) return "";
        var result = DocumentSaveCoordinator.Commit([new(Guid.NewGuid(), Path, BaselineHash, Bytes)],
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EI-Lua-save-journals"), true);
        if (!result.Complete) throw new IOException(string.Join("; ", result.Files.Select(f => f.Status + ": " + f.Message)) + " Журнал: " + result.JournalPath);
        AcceptSaved();
        return result.Files[0].BackupPath;
    }
}
