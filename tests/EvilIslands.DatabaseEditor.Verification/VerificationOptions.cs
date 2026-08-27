namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record VerificationOptions(
    IReadOnlyList<string> SourcePaths,
    bool RequireSources,
    bool ShowHelp)
{
    private static readonly string[] DefaultSourcePaths =
    [
        @"C:\GOG Games\Evil Islands\Starter\MODS\JamaisVu135\res\database.res",
        @"C:\GOG Games\database.res",
    ];

    public static VerificationOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> sources = [];
        var requireSources = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--source":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("Параметр --source требует путь к файлу.");
                    }

                    sources.Add(Path.GetFullPath(args[index]));
                    break;

                case "--require-sources":
                    requireSources = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Неизвестный параметр: {args[index]}");
            }
        }

        if (sources.Count == 0)
        {
            sources.AddRange(DefaultSourcePaths.Select(Path.GetFullPath));
        }

        string[] distinctSources = sources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VerificationOptions(distinctSources, requireSources, showHelp);
    }
}
