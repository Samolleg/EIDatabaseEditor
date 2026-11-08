using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record TextureSources(IReadOnlyList<string> Archives, IReadOnlyList<string> LooseFolders,
    bool Complete, bool ModProfile, IReadOnlyList<string> Notes)
{
    public string? GameFolder { get; init; }
    public string? NearbyDatabasePath { get; init; }

    public static Task<string> ResolveDatabasePathAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        string fullPath = Path.GetFullPath(path);
        var entries = ResFileManagerArchive.ReadCatalog(fullPath, token);
        // Identify content before entering the strict, editable database parser.
        // Read-only texture aliases do not relax the database writing contract.
        if (entries.Any(entry => DatabaseSchemas.Groups.Any(group =>
                group.ResourceName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))) ||
            !entries.Any(entry => entry.Name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase)))
            return fullPath;
        string database = Path.Combine(Path.GetDirectoryName(fullPath)!, "database.res");
        if (database.Equals(fullPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(database))
            throw new InvalidOperationException($"{Path.GetFileName(fullPath)} содержит текстуры, а не таблицы БД. " +
                "Рядом нет database.res. Откройте нужную базу и подключите этот архив через «Текстуры → Добавить RES…» слева.");
        return database;
    }, token);

    public static Task<TextureSources> FromDatabaseFolderAsync(string databasePath, CancellationToken token = default) => Task.Run(() =>
    {
        string fullPath = Path.GetFullPath(databasePath);
        string folder = Path.GetDirectoryName(fullPath)!;
        // Only use a nearby mod profile when it explicitly names this database.
        foreach (string config in new[] { Path.Combine(folder, "config.ini"), Path.Combine(Path.GetDirectoryName(folder) ?? folder, "config.ini") }.Distinct())
        {
            if (!File.Exists(config)) continue;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string section = "";
            bool ownsDatabase = false;
            foreach (string raw in File.ReadAllLines(config, Encoding.GetEncoding(1251)))
            {
                token.ThrowIfCancellationRequested();
                string line = raw.Trim();
                if (line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 1 || !section.Equals("RES", StringComparison.OrdinalIgnoreCase) ||
                    !line[..eq].Trim().Equals("database.res", StringComparison.OrdinalIgnoreCase)) continue;
                string value = line[(eq + 1)..].Trim().Trim('"');
                if (value.Length > 0 && Path.GetFullPath(value, Path.GetDirectoryName(config)!).Equals(fullPath, StringComparison.OrdinalIgnoreCase)) ownsDatabase = true;
            }
            if (!ownsDatabase) continue;
            for (DirectoryInfo? game = new(Path.GetDirectoryName(config)!); game is not null; game = game.Parent)
            {
                if (File.Exists(Path.Combine(game.FullName, "portable-defaults.xml")) &&
                    Directory.Exists(Path.Combine(game.FullName, "Base", "Res")))
                    return FromGame(game.FullName, config) with { NearbyDatabasePath = fullPath };
                if (File.Exists(Path.Combine(game.FullName, "game.exe")) && Directory.Exists(Path.Combine(game.FullName, "Res")))
                    return FromGame(game.FullName, config) with { NearbyDatabasePath = fullPath };
            }
        }
        var archives = new List<string>();
        var notes = new List<string> { "Ресурсы рядом с БД. Приоритеты игры не подтверждены; набор считается неполным." };
        foreach (string path in Directory.EnumerateFiles(folder, "*.res", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (ResFileManagerArchive.ReadCatalog(path, token).Any(entry => entry.Name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase)))
                    archives.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notes.Add($"Не удалось проверить {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return new TextureSources(archives, [folder], false, false, notes) { NearbyDatabasePath = fullPath };
    }, token);
    public string? ConfigurationPath { get; init; }
    public string? ConfigurationHash { get; init; }
    public static TextureSources Manual(IEnumerable<string> paths) =>
        new(paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            [], false, false, ["Подключена часть ресурсов. Порядок выбранных RES не подтверждает конфигурацию игры."]);

    public static TextureSources FromGame(string gameFolder, string? configPath = null, IReadOnlyList<string>? resourceGroups = null)
    {
        gameFolder = Path.GetFullPath(gameFolder);
        // Standalone packages keep shared data in Base, separately from Engine.
        // Keep all resource consumers (including figure fallback) on this data root.
        if (File.Exists(Path.Combine(gameFolder, "portable-defaults.xml")) &&
            Directory.Exists(Path.Combine(gameFolder, "Base", "Res")))
            gameFolder = Path.Combine(gameFolder, "Base");
        var overrides = new List<(string Key, string Path)>();
        var notes = new List<string>();
        string? configurationHash = null;
        if (configPath is not null)
        {
            configPath = Path.GetFullPath(configPath);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string section = "";
            byte[] configuration = File.ReadAllBytes(configPath);
            configurationHash = Convert.ToHexString(SHA256.HashData(configuration));
            foreach (string raw in Encoding.GetEncoding(1251).GetString(configuration).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
                if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim(); continue; }
                if (!section.Equals("RES", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim().Trim('"');
                if (value.Length == 0) { notes.Add($"Пустой путь ресурса: {key}"); continue; }
                overrides.Add((key, Path.GetFullPath(value, Path.GetDirectoryName(configPath)!)));
            }
        }

        var archives = new List<string>();
        foreach (string group in resourceGroups ?? new[] { "textures.res", "redress.res", "menus.res" })
        {
            archives.AddRange(overrides.Where(x => x.Key.Equals(group, StringComparison.OrdinalIgnoreCase)).Select(x => x.Path));
            string baseline = Path.Combine(gameFolder, "Res", group);
            if (!File.Exists(baseline) && File.Exists(Path.Combine(gameFolder, group)))
                baseline = Path.Combine(gameFolder, group);
            archives.Add(baseline);
        }

        // Native CTextureManager consults loose textures after archive lookup.
        var folders = new List<string> { Path.Combine(gameFolder, "textures") };
        if (configPath is not null)
        {
            string modLoose = Path.Combine(Path.GetDirectoryName(configPath)!, "textures");
            if (Directory.Exists(modLoose) && !modLoose.Equals(folders[0], StringComparison.OrdinalIgnoreCase))
            {
                folders.Add(modLoose);
                notes.Add("Есть отдельная папка текстур мода; её runtime-приоритет требует подтверждения.");
            }
            notes.Add("Настройки графических хуков addon не подтверждены: обычные и .x2 имена проверяются как варианты.");
        }
        return new(archives.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), folders,
            notes.All(note => note.StartsWith("Настройки", StringComparison.Ordinal)), configPath is not null, notes)
        { GameFolder = gameFolder, ConfigurationPath = configPath, ConfigurationHash = configurationHash };
    }
}

public enum TextureCheckState { Found, Missing, Incomplete, Context, Corrupt }

public sealed record TextureMatch(string Name, string Source, int Priority, int Offset, int Length,
    string? HeaderProblem);

public sealed record TextureCheck(string ExpectedName, TextureCheckState State, string Source,
    string Explanation, IReadOnlyList<TextureMatch> Matches);

public sealed class TextureResourceCatalog
{
    private readonly Dictionary<string, List<TextureMatch>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, long Length, DateTime Written)> _identity = [];
    private readonly List<(string Path, DateTime Written)> _folders = [];
    private readonly List<string> _problems = [];
    public TextureSources Sources { get; }
    public IReadOnlyList<string> Problems => _problems;
    public int ResourceCount => _byName.Count;
    public IReadOnlyList<string> ResourceNames => _byName.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Complete => Sources.Complete && _problems.Count == 0;

    private TextureResourceCatalog(TextureSources sources) => Sources = sources;

    public static Task<TextureResourceCatalog> OpenAsync(TextureSources sources, CancellationToken token = default) =>
        Task.Run(() => Open(sources, token), token);

    private static TextureResourceCatalog Open(TextureSources sources, CancellationToken token)
    {
        var result = new TextureResourceCatalog(sources);
        int priority = 0;
        foreach (string path in sources.Archives)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var before = new FileInfo(path);
                long length = before.Length;
                DateTime written = before.LastWriteTimeUtc;
                var local = new List<TextureMatch>();
                foreach (ResEntry entry in ResFileManagerArchive.ReadCatalog(path, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (!entry.Name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase)) continue;
                    local.Add(new(entry.Name, path, priority, entry.Offset, entry.Size,
                        ReadHeaderProblem(stream, entry.Offset, entry.Size)));
                }
                before.Refresh();
                if (before.Length != length || before.LastWriteTimeUtc != written)
                    throw new IOException("Архив изменился при индексировании.");
                foreach (TextureMatch match in local) result.Add(match);
                result._identity.Add((path, length, written));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result._problems.Add($"{path}: {ex.Message}");
            }
            priority++;
        }

        foreach (string folder in sources.LooseFolders)
        {
            token.ThrowIfCancellationRequested();
            result._folders.Add((folder, Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue));
            if (!Directory.Exists(folder)) continue;
            try
            {
                // Native fallback is textures\<name>.mmp, not a basename search through subfolders.
                foreach (string path in Directory.EnumerateFiles(folder, "*.mmp", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > int.MaxValue) throw new IOException("MMP больше поддерживаемого размера.");
                    result.Add(new(Path.GetFileName(path), path, priority, 0, (int)stream.Length,
                        ReadHeaderProblem(stream, 0, (int)stream.Length)));
                    result._identity.Add((path, stream.Length, File.GetLastWriteTimeUtc(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result._problems.Add($"{folder}: {ex.Message}");
            }
            priority++;
        }
        return result;
    }

    private void Add(TextureMatch match)
    {
        string key = match.Name.Replace('/', '\\');
        if (!_byName.TryGetValue(key, out var list)) _byName.Add(key, list = []);
        list.Add(match);
    }

    public bool IsCurrent()
    {
        try
        {
            if (Sources.ConfigurationPath is { } configuration &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configuration))) != Sources.ConfigurationHash)
                return false;
            return _identity.All(item => new FileInfo(item.Path) is { Exists: true } file &&
                    file.Length == item.Length && file.LastWriteTimeUtc == item.Written) &&
                _folders.All(item => (Directory.Exists(item.Path) ? Directory.GetLastWriteTimeUtc(item.Path) : DateTime.MinValue) == item.Written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public TextureCheck Check(string name, bool current = true)
    {
        string normalized = name.Replace('/', '\\');
        if (!_byName.TryGetValue(normalized, out var matches)) matches = [];
        string source = matches.FirstOrDefault()?.Source ?? "";
        if (!current)
            return new(name, TextureCheckState.Incomplete, source, "Ресурсы изменились. Обновите каталог.", matches);
        if (!Complete)
            return new(name, TextureCheckState.Incomplete, source,
                matches.Count > 0 ? "Найдена в доступных источниках; полная цепочка и победитель не подтверждены." :
                    "Не найдена в доступной части ресурсов.", matches);
        if (matches.Count == 0) return new(name, TextureCheckState.Missing, "", "Нет в подключённой цепочке ресурсов.", []);
        var winner = matches[0];
        return new(name, winner.HeaderProblem is null ? TextureCheckState.Found : TextureCheckState.Corrupt,
            winner.Source, winner.HeaderProblem ?? (matches.Count > 1 ?
                $"Первое совпадение; ещё {matches.Count - 1} перекрытых версий." : "Каталог и основной заголовок MMP проверены."), matches);
    }

    public Task<MmpImage> ReadPreviewAsync(string name, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent()) throw new IOException("Ресурсы изменились. Обновите источники.");
        var check = Check(name);
        var match = check.Matches.FirstOrDefault() ?? throw new FileNotFoundException("Файл не найден в подключённых источниках.");
        if (match.HeaderProblem is not null) throw new InvalidDataException(match.HeaderProblem);
        if (match.Length > MmpPreviewDecoder.MaximumInputBytes)
            throw new InvalidDataException("Ресурс превышает лимит предпросмотра 128 МиБ.");
        using var stream = new FileStream(match.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsCurrent()) throw new IOException("Ресурсы изменились. Обновите источники.");
        if (match.Offset < 0 || match.Length < 0 || (long)match.Offset + match.Length > stream.Length)
            throw new InvalidDataException("Границы MMP выходят за файл.");
        byte[] data = new byte[match.Length];
        stream.Position = match.Offset;
        stream.ReadExactly(data);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent()) throw new IOException("Ресурсы изменились при чтении. Обновите источники.");
        return MmpPreviewDecoder.Decode(data, token);
    }, token);

    private static string? ReadHeaderProblem(FileStream stream, int offset, int length)
    {
        if (length < 76) return "Заголовок MMP обрезан.";
        Span<byte> header = stackalloc byte[76];
        stream.Position = offset;
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00504D4D)
            return "Неверная сигнатура MMP.";
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
            return "Недопустимые размеры MMP.";
        if (length == 76) return "У MMP отсутствуют данные изображения.";
        return null; // Presence/header validation only; decoding and mip validation are a separate scope.
    }
}
