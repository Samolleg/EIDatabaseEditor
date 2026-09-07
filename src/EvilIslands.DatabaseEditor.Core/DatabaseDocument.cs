using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public sealed class DatabaseDocument
{
    private static readonly HashSet<string> SupportedResourceNames = DatabaseSchemas.Groups
        .Select(group => group.ResourceName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly ResArchiveDocument _archive;
    private readonly Dictionary<string, ParsedDatabaseResource> _parsedResources;
    private readonly List<ValidationDiagnostic> _loadDiagnostics;
    private readonly IReadOnlyList<ValidationDiagnostic> _archiveDiagnostics;
    private readonly IReadOnlyList<ValidationDiagnostic> _staticSemanticDiagnostics;
    private readonly object _editSync;

    private DatabaseDocument(
        ResArchiveDocument archive,
        Dictionary<string, ParsedDatabaseResource> parsedResources,
        UnitsDatabase units,
        SpellsDatabase spells,
        IReadOnlyList<DatabaseGroup> databases,
        List<ValidationDiagnostic> loadDiagnostics,
        object editSync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _archive = archive;
        _parsedResources = parsedResources;
        Units = units;
        Spells = spells;
        Databases = databases;
        _loadDiagnostics = loadDiagnostics;
        _editSync = editSync;
        var resources = new DatabaseResourceDescriptor[archive.Entries.Count];
        for (var index = 0; index < resources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries[index];
            resources[index] = new DatabaseResourceDescriptor(
                entry.Index,
                entry.Name,
                Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant(),
                entry.Offset,
                entry.Size,
                SupportedResourceNames.Contains(entry.Name));
        }

        Resources = resources;
        _archiveDiagnostics = archive.Validate(cancellationToken);
        _staticSemanticDiagnostics = SemanticDatabaseValidator.ValidateStatic(Databases, cancellationToken);
        Diagnostics = Validate(cancellationToken);
    }

    public string SourcePath => _archive.SourcePath;

    public long FileLength => _archive.Length;

    public string Sha256 => _archive.Sha256;

    public IReadOnlyList<DatabaseResourceDescriptor> Resources { get; }

    public UnitsDatabase Units { get; }

    public SpellsDatabase Spells { get; }

    public IReadOnlyList<DatabaseGroup> Databases { get; }

    public IReadOnlyList<ValidationDiagnostic> Diagnostics { get; private set; }

    public bool IsModified
    {
        get
        {
            lock (_editSync)
            {
                return _parsedResources.Values.Any(resource => resource.IsModified);
            }
        }
    }

    public static DatabaseDocument Open(string path) => Create(ResArchiveDocument.Open(path));

    public static async Task<DatabaseDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var archive = await ResArchiveDocument.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Create(archive, cancellationToken);
    }

    public IReadOnlyList<ValidationDiagnostic> Validate() => Validate(CancellationToken.None);

    public IReadOnlyList<ValidationDiagnostic> Validate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<ValidationDiagnostic>(_loadDiagnostics);
        diagnostics.AddRange(_archiveDiagnostics);
        diagnostics.AddRange(_staticSemanticDiagnostics);
        // Each field snapshots its payload under the shared edit lock. Avoid holding
        // that lock for the whole database walk so a background validation pass can
        // never stall a UI commit; a newer debounced pass supersedes a mixed snapshot.
        IReadOnlyList<DatabaseGroup> snapshot;
        lock (_editSync)
        {
            snapshot = SnapshotDatabaseCatalog(Databases);
        }

        SemanticDatabaseValidator.ValidateDynamic(snapshot, diagnostics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidationDiagnostic[] ordered = diagnostics
            .OrderBy(diagnostic => diagnostic.Domain)
            .ThenByDescending(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Location?.TableKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.RecordIndex ?? -1)
            .ThenBy(diagnostic => diagnostic.Location?.FieldId ?? -1)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
        Diagnostics = ordered;
        return ordered;
    }

    public SaveResult SaveAs(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ValidationDiagnostic? blockingDiagnostic = Validate()
            .FirstOrDefault(diagnostic => diagnostic.IsBlocking);
        if (blockingDiagnostic is not null)
        {
            throw new InvalidOperationException(
                $"Сохранение копии заблокировано проверкой {blockingDiagnostic.Code}: {blockingDiagnostic.Message}");
        }

        var sourcePath = Path.GetFullPath(SourcePath);
        var destinationPath = Path.GetFullPath(targetPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Исходный database.res заблокирован от перезаписи. Выберите новое имя.");
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("Целевой файл уже существует. Безопасное сохранение не перезаписывает файлы.");
        }

        if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(destinationPath))
        {
            throw new InvalidOperationException(
                "Preview-версия не записывает файлы в каталог игры или Starter\\MODS. Сохраните копию в staging-папку.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(destinationPath))
        {
            throw new InvalidOperationException(
                "Путь назначения проходит через символическую ссылку или junction. Для безопасности выберите обычную staging-папку.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для сохранения не существует.");
        }

        using var sourceGuard = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        EnsureSourceUnchanged(sourceGuard);
        Dictionary<int, byte[]> replacements;
        lock (_editSync)
        {
            replacements = BuildModifiedResources();
        }

        var rebuilt = ResArchiveWriter.Rebuild(_archive, replacements);
        var verifiedDocument = VerifyRebuiltArchive(destinationPath, rebuilt, replacements);
        var verifiedDiagnostics = verifiedDocument.Diagnostics;
        var rebuiltSha256 = Convert.ToHexString(SHA256.HashData(rebuilt));
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.partial-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(rebuilt);
                stream.Flush(true);
            }

            EnsureFileMatches(tempPath, rebuilt);

            EnsureSourceUnchanged(sourceGuard);
            if (DatabaseSafetyPolicy.IsLikelyGameInstallationPath(destinationPath) ||
                DatabaseSafetyPolicy.ContainsReparsePoint(destinationPath))
            {
                throw new IOException("Путь назначения изменился и больше не является безопасным.");
            }

            File.Move(tempPath, destinationPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        return new SaveResult(
            destinationPath,
            rebuilt.LongLength,
            rebuiltSha256,
            true,
            verifiedDiagnostics);
    }

    public Task<SaveResult> SaveAsAsync(
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Saving becomes a short, atomic transaction once scheduled. It must
        // report success if the final move commits instead of abandoning the
        // task while a valid destination is still being created.
        return Task.Run(() => SaveAs(targetPath), CancellationToken.None);
    }

    public SaveResult SaveInPlace(bool allowGameInstallationPath = false)
    {
        ValidationDiagnostic? blockingDiagnostic = Validate()
            .FirstOrDefault(diagnostic => diagnostic.IsBlocking);
        if (blockingDiagnostic is not null)
        {
            throw new InvalidOperationException(
                $"Сохранение заблокировано проверкой {blockingDiagnostic.Code}: {blockingDiagnostic.Message}");
        }

        var sourcePath = Path.GetFullPath(SourcePath);
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка открытого database.res больше не существует.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(sourcePath))
        {
            throw new InvalidOperationException(
                "Путь открытого файла проходит через символическую ссылку или junction. Сохраните отдельную копию в обычную папку.");
        }

        if (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(sourcePath))
        {
            throw new InvalidOperationException(
                "Прямая запись в каталог игры требует отдельного подтверждения пользователя.");
        }

        using var sourceGuard = new GuardedFileCommit(sourcePath, Sha256);

        Dictionary<int, byte[]> replacements;
        lock (_editSync)
        {
            replacements = BuildModifiedResources();
        }

        if (replacements.Count == 0)
        {
            return new SaveResult(
                sourcePath,
                FileLength,
                Sha256,
                true,
                Diagnostics,
                null);
        }

        var rebuilt = ResArchiveWriter.Rebuild(_archive, replacements);
        var verifiedDocument = VerifyRebuiltArchive(sourcePath, rebuilt, replacements);
        var rebuiltSha256 = Convert.ToHexString(SHA256.HashData(rebuilt));
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(sourcePath)}.partial-{Guid.NewGuid():N}");
        string backupPath;

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(rebuilt);
                stream.Flush(true);
            }

            EnsureFileMatches(tempPath, rebuilt);
            if (DatabaseSafetyPolicy.ContainsReparsePoint(sourcePath) ||
                (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(sourcePath)))
            {
                throw new IOException("Путь открытого файла изменился и больше не является безопасным.");
            }

            RetainNewestManagedBackupBeforeSave(sourcePath);
            backupPath = CreateAvailableBackupPath(sourcePath);
            sourceGuard.Commit(tempPath, backupPath, rebuiltSha256);
            sourceGuard.Dispose();
            EnsureBackupMatches(backupPath);
            EnsureFileMatches(sourcePath, rebuilt);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        return new SaveResult(
            sourcePath,
            rebuilt.LongLength,
            rebuiltSha256,
            true,
            verifiedDocument.Diagnostics,
            backupPath);
    }

    public Task<SaveResult> SaveInPlaceAsync(
        bool allowGameInstallationPath = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => SaveInPlace(allowGameInstallationPath), CancellationToken.None);
    }

    public DatabaseRecord AppendRecordClone(DatabaseRecord template)
    {
        ArgumentNullException.ThrowIfNull(template);
        lock (_editSync)
        {
            if (!_parsedResources.TryGetValue(template.ResourceName, out ParsedDatabaseResource? resource))
            {
                throw new ArgumentException("Строка-образец не принадлежит открытому документу.", nameof(template));
            }

            return resource.AppendClone(template.SectionId, template, _editSync);
        }
    }

    public void RemoveAddedRecord(DatabaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_editSync)
        {
            if (!_parsedResources.TryGetValue(record.ResourceName, out ParsedDatabaseResource? resource))
            {
                throw new ArgumentException("Строка не принадлежит открытому документу.", nameof(record));
            }

            resource.RemoveAddedRecord(record);
        }
    }

    public void RestoreAddedRecord(DatabaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_editSync)
        {
            if (!_parsedResources.TryGetValue(record.ResourceName, out ParsedDatabaseResource? resource))
            {
                throw new ArgumentException("Строка не принадлежит открытому документу.", nameof(record));
            }

            resource.RestoreAddedRecord(record);
        }
    }

    private static DatabaseDocument Create(
        ResArchiveDocument archive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var editSync = new object();
        var parsed = new Dictionary<string, ParsedDatabaseResource>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ValidationDiagnostic>();
        foreach (var group in DatabaseSchemas.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParseResource(
                archive,
                group.ResourceName,
                group.Sections,
                parsed,
                diagnostics,
                editSync,
                cancellationToken);
        }

        parsed.TryGetValue("units.udb", out var units);
        parsed.TryGetValue("spells.sdb", out var spells);
        var databases = CreateDatabaseCatalog(parsed, cancellationToken);

        return new DatabaseDocument(
            archive,
            parsed,
            new UnitsDatabase(
                units?.GetRecords(1) ?? [],
                units?.GetRecords(2) ?? [],
                units?.GetRecords(3) ?? [],
                units?.GetRecords(4) ?? []),
            new SpellsDatabase(
                spells?.GetRecords(1) ?? [],
                spells?.GetRecords(2) ?? [],
                spells?.GetRecords(3) ?? [],
                spells?.GetRecords(4) ?? [],
                spells?.GetRecords(5) ?? []),
            databases,
            diagnostics,
            editSync,
            cancellationToken);
    }

    private static IReadOnlyList<DatabaseGroup> CreateDatabaseCatalog(
        IReadOnlyDictionary<string, ParsedDatabaseResource> parsedResources,
        CancellationToken cancellationToken)
    {
        var groups = new DatabaseGroup[DatabaseSchemas.Groups.Count];
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = DatabaseSchemas.Groups[groupIndex];
            parsedResources.TryGetValue(schema.ResourceName, out var parsedResource);
            var tables = new DatabaseTable[schema.Sections.Count];
            for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
            {
                var section = schema.Sections[tableIndex];
                var tableSupportState = parsedResource is null || !parsedResource.HasSection(section.Id)
                    ? DatabaseSupportState.Unavailable
                    : section.IsEditable
                        ? DatabaseSupportState.Editable
                        : DatabaseSupportState.ReadOnly;
                tables[tableIndex] = new DatabaseTable(
                    section.Key,
                    section.Name,
                    schema.ResourceName,
                    section.Id,
                    parsedResource?.GetRecords(section.Id) ?? [],
                    tableSupportState,
                    tableSupportState != DatabaseSupportState.Unavailable && section.CanAppendRecords);
            }

            var supportState = tables.Any(table => table.SupportState == DatabaseSupportState.Editable)
                ? DatabaseSupportState.Editable
                : tables.Any(table => table.SupportState == DatabaseSupportState.ReadOnly)
                    ? DatabaseSupportState.ReadOnly
                    : DatabaseSupportState.Unavailable;

            groups[groupIndex] = new DatabaseGroup(
                schema.Key,
                schema.Title,
                schema.ResourceName,
                tables,
                supportState);
        }

        return groups;
    }

    private static IReadOnlyList<DatabaseGroup> SnapshotDatabaseCatalog(
        IReadOnlyList<DatabaseGroup> source)
    {
        var groups = new DatabaseGroup[source.Count];
        for (int groupIndex = 0; groupIndex < source.Count; groupIndex++)
        {
            DatabaseGroup group = source[groupIndex];
            var tables = new DatabaseTable[group.Tables.Count];
            for (int tableIndex = 0; tableIndex < group.Tables.Count; tableIndex++)
            {
                DatabaseTable table = group.Tables[tableIndex];
                tables[tableIndex] = new DatabaseTable(
                    table.Key,
                    table.Title,
                    table.ResourceName,
                    table.SectionId,
                    table.Records.ToArray(),
                    table.SupportState,
                    table.CanAppendRecords);
            }

            groups[groupIndex] = new DatabaseGroup(
                group.Key,
                group.Title,
                group.ResourceName,
                tables,
                group.SupportState);
        }

        return groups;
    }

    private static ParsedDatabaseResource? ParseResource(
        ResArchiveDocument archive,
        string name,
        IReadOnlyList<SectionSchema> schemas,
        IDictionary<string, ParsedDatabaseResource> target,
        ICollection<ValidationDiagnostic> diagnostics,
        object editSync,
        CancellationToken cancellationToken = default)
    {
        if (!archive.TryGetEntry(name, out var entry) || entry is null)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Warning,
                "DB_RESOURCE_MISSING",
                $"Ресурс '{name}' не найден.",
                name));
            return null;
        }

        try
        {
            var parsed = ParsedDatabaseResource.Parse(
                name,
                archive.GetResourceBytes(entry),
                entry.Offset,
                schemas,
                editSync,
                cancellationToken);
            target.Add(name, parsed);
            return parsed;
        }
        catch (DatabaseFormatException exception)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "DB_PARSE_FAILED",
                exception.Message,
                exception.Path ?? name,
                exception.Offset));
            return null;
        }
    }

    private Dictionary<int, byte[]> BuildModifiedResources()
    {
        var result = new Dictionary<int, byte[]>();
        foreach (var (name, resource) in _parsedResources)
        {
            if (!resource.IsModified)
            {
                continue;
            }

            if (!_archive.TryGetEntry(name, out var entry) || entry is null)
            {
                throw new InvalidOperationException($"Resource '{name}' disappeared from the source archive.");
            }

            result.Add(entry.Index, resource.Rebuild());
        }

        return result;
    }

    private DatabaseDocument VerifyRebuiltArchive(
        string destinationPath,
        byte[] rebuilt,
        IReadOnlyDictionary<int, byte[]> replacements)
    {
        var verifiedArchive = ResArchiveDocument.Parse(destinationPath, rebuilt);
        foreach (var originalEntry in _archive.Entries)
        {
            var verifiedEntry = verifiedArchive.Entries.Single(entry => entry.Index == originalEntry.Index);
            if (replacements.ContainsKey(originalEntry.Index))
            {
                continue;
            }

            if (!_archive.GetResourceBytes(originalEntry).Span.SequenceEqual(
                    verifiedArchive.GetResourceBytes(verifiedEntry).Span))
            {
                throw new DatabaseFormatException(
                    $"Unchanged resource '{originalEntry.Name}' differs after rebuild.",
                    originalEntry.Name,
                    verifiedEntry.Offset);
            }
        }

        var document = Create(verifiedArchive);
        var errors = document.Diagnostics.Where(diagnostic => diagnostic.IsBlocking).ToArray();
        if (errors.Length > 0)
        {
            throw new DatabaseFormatException(errors[0].Message, errors[0].Path, errors[0].Offset);
        }

        return document;
    }

    private void EnsureSourceUnchanged(FileStream sourceGuard)
    {
        sourceGuard.Position = 0;
        var currentHash = Convert.ToHexString(SHA256.HashData(sourceGuard));
        if (!string.Equals(currentHash, Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Исходный database.res изменился на диске после открытия. Сохранение отменено.");
        }
    }

    private void EnsureBackupMatches(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var backupHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(backupHash, Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Резервная копия исходного database.res не прошла проверку SHA-256.");
        }
    }

    private static string CreateAvailableBackupPath(string sourcePath)
    {
        var basePath = sourcePath + ".bak";
        if (!File.Exists(basePath) && !Directory.Exists(basePath))
        {
            return basePath;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var candidate = suffix == 0
                ? $"{basePath}.{timestamp}"
                : $"{basePath}.{timestamp}-{suffix:D3}";
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{basePath}.{Guid.NewGuid():N}";
    }

    private static void RetainNewestManagedBackupBeforeSave(string sourcePath)
    {
        IReadOnlyList<FileInfo> backups = EnumerateManagedBackups(sourcePath);
        FileInfo[] obsoleteBackups = backups.Skip(1).ToArray();

        foreach (FileInfo backup in obsoleteBackups)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(backup.FullName);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException(
                        $"Старая резервная копия имеет небезопасный тип: {backup.FullName}");
                }

                File.Delete(backup.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Не удалось удалить старую резервную копию '{backup.FullName}'. " +
                    "Сохранение отменено до изменения database.res.",
                    exception);
            }
        }

        if (EnumerateManagedBackups(sourcePath).Count > 1)
        {
            throw new IOException(
                "Не удалось сократить число резервных копий. Сохранение отменено до изменения database.res.");
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateManagedBackups(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var directoryPath = Path.GetDirectoryName(fullSourcePath)
            ?? throw new DirectoryNotFoundException("Не найдена папка открытого database.res.");
        var sourceFileName = Path.GetFileName(fullSourcePath);
        var backupNamePattern = new Regex(
            $"^{Regex.Escape(sourceFileName)}\\.bak(?:\\.\\d{{8}}-\\d{{6}}(?:-\\d{{3}})?|\\.[0-9a-f]{{32}})?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var backups = new List<FileInfo>();
        foreach (string candidatePath in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     sourceFileName + ".bak*",
                     SearchOption.TopDirectoryOnly))
        {
            var candidateName = Path.GetFileName(candidatePath);
            if (!backupNamePattern.IsMatch(candidateName))
            {
                continue;
            }

            var fullCandidatePath = Path.GetFullPath(candidatePath);
            if (!string.Equals(
                    Path.GetDirectoryName(fullCandidatePath),
                    directoryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Резервная копия находится вне папки открытого database.res.");
            }

            FileAttributes attributes = File.GetAttributes(fullCandidatePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException(
                    $"Путь резервной копии имеет небезопасный тип: {fullCandidatePath}");
            }

            backups.Add(new FileInfo(fullCandidatePath));
        }

        return backups
            .OrderByDescending(backup => backup.LastWriteTimeUtc)
            .ThenByDescending(backup => backup.CreationTimeUtc)
            .ThenByDescending(backup => backup.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureFileMatches(string path, ReadOnlySpan<byte> expected)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expected.Length)
        {
            throw new IOException("Записанная копия имеет неверный размер.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var offset = 0;
            while (offset < expected.Length)
            {
                var requested = Math.Min(buffer.Length, expected.Length - offset);
                var read = stream.Read(buffer, 0, requested);
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read)))
                {
                    throw new IOException("Записанная копия не прошла побайтовую проверку.");
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}

public sealed record SaveResult(
    string TargetPath,
    long FileLength,
    string Sha256,
    bool ReopenedSuccessfully,
    IReadOnlyList<ValidationDiagnostic> Diagnostics,
    string? BackupPath = null);
