using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record BriefingPhrase(int Number, string Speaker, string Text);

public enum BriefingEventKind
{
    Phrase,
    Camera,
    Animation,
    Thought,
    Show,
    Hide,
    Description,
    Command,
}

public sealed record BriefingTimelineEvent(
    int Ordinal,
    BriefingEventKind Kind,
    string Directive,
    string Arguments,
    int PhraseNumber,
    string Speaker,
    string Text)
{
    public bool IsPhrase => Kind == BriefingEventKind.Phrase;
}

public static partial class BriefingTranscriptParser
{
    public static IReadOnlyList<BriefingPhrase> Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        string[] lines = description
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new List<BriefingPhrase>();
        PhraseBuilder? current = null;
        foreach (string line in lines)
        {
            Match phrase = PhraseDirective().Match(line);
            if (phrase.Success && int.TryParse(phrase.Groups["number"].Value, out int number))
            {
                AddPhrase(result, current);
                current = new PhraseBuilder(number, phrase.Groups["speaker"].Value.Trim());
                continue;
            }

            string trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith('#'))
            {
                AddPhrase(result, current);
                current = null;
                continue;
            }

            current?.Append(line);
        }

        AddPhrase(result, current);
        return result;
    }

    public static IReadOnlyList<BriefingTimelineEvent> ParseTimeline(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        string[] lines = SplitLines(description);
        var result = new List<BriefingTimelineEvent>();
        var ordinal = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryParseDirective(lines[index], out string directive, out string arguments))
            {
                continue;
            }

            BriefingEventKind kind = GetEventKind(directive);
            if (kind == BriefingEventKind.Phrase)
            {
                Match phrase = PhraseDirective().Match(lines[index]);
                if (!phrase.Success ||
                    !int.TryParse(phrase.Groups["number"].Value, out int phraseNumber))
                {
                    result.Add(new(
                        ordinal++,
                        BriefingEventKind.Command,
                        directive,
                        arguments,
                        0,
                        string.Empty,
                        string.Empty));
                    continue;
                }

                int end = FindDirectiveEnd(lines, index);
                result.Add(new(
                    ordinal++,
                    kind,
                    directive,
                    arguments,
                    phraseNumber,
                    phrase.Groups["speaker"].Value.Trim(),
                    BuildPhraseText(lines[(index + 1)..end])));
                index = end - 1;
                continue;
            }

            result.Add(new(
                ordinal++,
                kind,
                directive,
                arguments,
                0,
                string.Empty,
                string.Empty));
        }

        return result;
    }

    public static bool TryGetClipIdentity(
        string? resourceName,
        out int phraseNumber,
        out string clipId)
    {
        phraseNumber = 0;
        clipId = string.Empty;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(resourceName);
        Match match = ClipFileName().Match(fileName);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out phraseNumber))
        {
            phraseNumber = 0;
            return false;
        }

        clipId = match.Value;
        return true;
    }

    public static string UpdatePhrase(
        string? description,
        int phraseNumber,
        string speaker,
        string text)
    {
        if (phraseNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(phraseNumber));
        }

        string normalizedSpeaker = speaker.Trim();
        string normalizedText = text.Trim();
        if (normalizedSpeaker.Length == 0)
        {
            throw new ArgumentException("Укажите персонажа реплики.", nameof(speaker));
        }

        if (normalizedText.Length == 0)
        {
            throw new ArgumentException("Введите текст реплики.", nameof(text));
        }

        string source = description ?? string.Empty;
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            Match phrase = PhraseDirective().Match(lines[index]);
            if (!phrase.Success ||
                !int.TryParse(phrase.Groups["number"].Value, out int number) ||
                number != phraseNumber)
            {
                continue;
            }

            int end = index + 1;
            while (end < lines.Length && !lines[end].TrimStart().StartsWith('#'))
            {
                end++;
            }

            var result = new List<string>(lines.Length - (end - index) + 2);
            result.AddRange(lines[..index]);
            result.Add($"#phrase {normalizedSpeaker} {phraseNumber}");
            result.AddRange(NormalizeTextLines(normalizedText));
            result.AddRange(lines[end..]);
            return string.Join(newline, result);
        }

        string separator = source.Length == 0 || source.EndsWith('\n') || source.EndsWith('\r')
            ? string.Empty
            : newline;
        return source + separator +
               $"#phrase {normalizedSpeaker} {phraseNumber}" + newline +
               string.Join(newline, NormalizeTextLines(normalizedText));
    }

    public static string UpdateTimelineEvent(
        string? description,
        int eventOrdinal,
        string arguments,
        int phraseNumber,
        string speaker,
        string text)
    {
        if (eventOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventOrdinal));
        }

        string source = description ?? string.Empty;
        string newline = DetectNewline(source);
        string[] lines = SplitLines(source);
        var currentOrdinal = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryParseDirective(lines[index], out string directive, out _))
            {
                continue;
            }

            if (currentOrdinal++ != eventOrdinal)
            {
                continue;
            }

            BriefingEventKind kind = GetEventKind(directive);
            if (kind == BriefingEventKind.Phrase)
            {
                ValidatePhrase(phraseNumber, speaker, text);
                int end = FindDirectiveEnd(lines, index);
                var result = new List<string>(lines.Length - (end - index) + 2);
                result.AddRange(lines[..index]);
                result.Add($"#phrase {speaker.Trim()} {phraseNumber}");
                result.AddRange(NormalizeTextLines(text.Trim()));
                result.AddRange(lines[end..]);
                return string.Join(newline, result);
            }

            string normalizedArguments = NormalizeDirectiveArguments(kind, arguments);
            int marker = lines[index].IndexOf('#');
            string indentation = marker > 0 ? lines[index][..marker] : string.Empty;
            lines[index] = indentation + "#" + directive +
                           (normalizedArguments.Length == 0 ? string.Empty : " " + normalizedArguments);
            return string.Join(newline, lines);
        }

        throw new ArgumentOutOfRangeException(nameof(eventOrdinal), "Событие брифинга больше не найдено.");
    }

    public static int GetNextPhraseNumber(string? description)
    {
        int maximum = Parse(description).Select(phrase => phrase.Number).DefaultIfEmpty(0).Max();
        return checked(maximum + 1);
    }

    private static string[] SplitLines(string source) => source
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n');

    private static string DetectNewline(string source) =>
        source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int FindDirectiveEnd(IReadOnlyList<string> lines, int directiveIndex)
    {
        int end = directiveIndex + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('#'))
        {
            end++;
        }

        return end;
    }

    private static bool TryParseDirective(
        string line,
        out string directive,
        out string arguments)
    {
        directive = string.Empty;
        arguments = string.Empty;
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
        {
            return false;
        }

        string content = trimmed[1..].Trim();
        if (content.Length == 0)
        {
            return false;
        }

        int separator = content.IndexOfAny([' ', '\t']);
        directive = separator < 0 ? content : content[..separator];
        arguments = separator < 0 ? string.Empty : content[(separator + 1)..].Trim();
        return true;
    }

    private static BriefingEventKind GetEventKind(string directive) => directive.ToUpperInvariant() switch
    {
        "PHRASE" => BriefingEventKind.Phrase,
        "CAMERA" => BriefingEventKind.Camera,
        "ANIMATION" => BriefingEventKind.Animation,
        "NOLIPS" => BriefingEventKind.Thought,
        "SHOW" => BriefingEventKind.Show,
        "HIDE" => BriefingEventKind.Hide,
        "DESC" => BriefingEventKind.Description,
        _ => BriefingEventKind.Command,
    };

    private static string NormalizeDirectiveArguments(BriefingEventKind kind, string arguments)
    {
        string normalized = arguments
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
        switch (kind)
        {
            case BriefingEventKind.Thought:
                return string.Empty;
            case BriefingEventKind.Camera:
                ValidateCameraArguments(normalized, nameof(arguments));
                return normalized;
            case BriefingEventKind.Animation when
                !int.TryParse(normalized, out _):
                throw new ArgumentException("Номер анимации должен быть целым числом.", nameof(arguments));
            case BriefingEventKind.Show or BriefingEventKind.Hide when normalized.Length == 0:
                throw new ArgumentException("Укажите участника сцены.", nameof(arguments));
            default:
                return normalized;
        }
    }

    private static void ValidateCameraArguments(string arguments, string parameterName)
    {
        string[] parts = arguments.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (1 or 4) ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            parts.Skip(1).Any(part =>
                !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException(
                "Камера задаётся одним целым номером либо четырьмя значениями: номер, угол, дистанция и высота.",
                parameterName);
        }
    }

    private static void ValidatePhrase(int phraseNumber, string speaker, string text)
    {
        if (phraseNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(phraseNumber));
        }

        if (string.IsNullOrWhiteSpace(speaker))
        {
            throw new ArgumentException("Укажите персонажа реплики.", nameof(speaker));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Введите текст реплики.", nameof(text));
        }
    }

    private static string BuildPhraseText(IEnumerable<string> lines)
    {
        var phrase = new PhraseBuilder(0, string.Empty);
        foreach (string line in lines)
        {
            phrase.Append(line);
        }

        return phrase.Text.ToString().Trim();
    }

    private static IReadOnlyList<string> NormalizeTextLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();
        for (var index = 0; index + 1 < lines.Length; index++)
        {
            if (lines[index].Length > 0)
            {
                lines[index] += " ";
            }
        }

        return lines;
    }

    private static void AddPhrase(ICollection<BriefingPhrase> destination, PhraseBuilder? phrase)
    {
        if (phrase is null)
        {
            return;
        }

        string text = phrase.Text.ToString().Trim();
        if (text.Length > 0)
        {
            destination.Add(new(phrase.Number, phrase.Speaker, text));
        }
    }

    [GeneratedRegex(
        @"^\s*#phrase\s+(?<speaker>.+?)\s+(?<number>\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhraseDirective();

    [GeneratedRegex(@"^(?<number>\d+)[A-Za-z]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClipFileName();

    private sealed class PhraseBuilder(int number, string speaker)
    {
        private bool _hasText;
        private bool _previousLineEndedWithWhitespace;

        public int Number { get; } = number;

        public string Speaker { get; } = speaker;

        public StringBuilder Text { get; } = new();

        public void Append(string line)
        {
            string content = line.TrimStart().TrimEnd();
            if (content.Length == 0)
            {
                return;
            }

            if (_hasText && (_previousLineEndedWithWhitespace || char.IsWhiteSpace(line[0])))
            {
                Text.Append(' ');
            }

            Text.Append(content);
            _hasText = true;
            _previousLineEndedWithWhitespace = line.Length > 0 && char.IsWhiteSpace(line[^1]);
        }
    }
}
