namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record VerificationOptions(
    IReadOnlyList<string> SourcePaths,
    bool RequireSources,
    bool ShowHelp,
    string? RepairSourcePath,
    string? RepairReferencePath)
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
        string? repairSourcePath = null;
        string? repairReferencePath = null;

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

                case "--repair-source":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("Параметр --repair-source требует путь к повреждённому database.res.");
                    }

                    repairSourcePath = Path.GetFullPath(args[index]);
                    break;

                case "--repair-reference":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("Параметр --repair-reference требует путь к чистой опорной базе.");
                    }

                    repairReferencePath = Path.GetFullPath(args[index]);
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

        bool repairRequested = repairSourcePath is not null || repairReferencePath is not null;
        if (repairRequested && (repairSourcePath is null || repairReferencePath is null))
        {
            throw new ArgumentException(
                "Режим восстановления требует одновременно --repair-source и --repair-reference.");
        }

        if (repairRequested && sources.Count > 0)
        {
            throw new ArgumentException(
                "Режим восстановления нельзя объединять с обычными параметрами --source.");
        }

        if (sources.Count == 0 && !repairRequested)
        {
            sources.AddRange(DefaultSourcePaths.Select(Path.GetFullPath));
        }

        string[] distinctSources = sources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VerificationOptions(
            distinctSources,
            requireSources,
            showHelp,
            repairSourcePath,
            repairReferencePath);
    }
}
