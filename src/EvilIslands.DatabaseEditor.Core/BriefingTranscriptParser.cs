using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record BriefingPhrase(int Number, string Speaker, string Text);

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

    public static int GetNextPhraseNumber(string? description)
    {
        int maximum = Parse(description).Select(phrase => phrase.Number).DefaultIfEmpty(0).Max();
        return checked(maximum + 1);
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
