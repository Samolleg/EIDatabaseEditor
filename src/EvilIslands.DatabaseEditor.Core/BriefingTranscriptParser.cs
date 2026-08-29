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
