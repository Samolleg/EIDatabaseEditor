using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EvilIslands.DatabaseEditor.Core;

public sealed class TextResourceState
{
    private readonly TextResource _owner;

    internal TextResourceState(TextResource owner, string title, string description, bool isPresent)
    {
        _owner = owner;
        Title = title;
        Description = description;
        IsPresent = isPresent;
    }

    internal string Title { get; }

    internal string Description { get; }

    internal bool IsPresent { get; }
    public TextResourceState Rebind(TextResource owner) => new(owner, Title, Description, IsPresent);

    public bool IsEquivalentTo(TextResourceState? other) =>
        other is not null &&
        ReferenceEquals(_owner, other._owner) &&
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        string.Equals(Description, other.Description, StringComparison.Ordinal) &&
        IsPresent == other.IsPresent;

    internal void EnsureOwner(TextResource resource)
    {
        if (!ReferenceEquals(_owner, resource))
        {
            throw new ArgumentException("Снимок принадлежит другому текстовому ресурсу.", nameof(resource));
        }
    }
}

public sealed class TextResource
{
    private readonly Encoding _encoding;
    private readonly string _originalTitle;
    private readonly string _originalDescription;
    private readonly string _lineBreak;
    private readonly bool _hadLineBreak;
    private string _title;
    private string _description;
    private bool _isPresent = true;

    internal TextResource(
        int? archiveIndex,
        string name,
        string text,
        Encoding encoding,
        bool isNew)
    {
        ArchiveIndex = archiveIndex;
        Name = name;
        _encoding = encoding;
        IsNew = isNew;
        (_originalTitle, _originalDescription, _lineBreak, _hadLineBreak) = Split(text);
        _title = _originalTitle;
        _description = _originalDescription;
    }

    public int? ArchiveIndex { get; }

    public string Name { get; }

    public string Prefix
    {
        get
        {
            int separator = Name.IndexOf(' ');
            return separator < 0 ? Name : Name[..separator];
        }
    }

    public string Title => _title;

    public string Description => _description;

    public string OriginalTitle => _originalTitle;

    public string OriginalDescription => _originalDescription;

    public bool IsNew { get; }
    internal bool HistoryRemovalAllowed { get; set; }

    public bool IsPresent => _isPresent;

    public bool IsModified => IsNew
        ? IsPresent
        : !IsPresent || !string.Equals(_title, _originalTitle, StringComparison.Ordinal) ||
          !string.Equals(_description, _originalDescription, StringComparison.Ordinal);

    public bool TrySetTitle(string value, out string? error) => TrySet(value, _description, out error);

    public bool TrySetDescription(string value, out string? error) => TrySet(_title, value, out error);

    public bool TryPrepareValue(string title, string description, out TextResourceState? state, out string? error)
    {
        state = null; error = null;
        if (!IsPresent) { error = "Текст отсутствует."; return false; }
        if (title.IndexOfAny(['\r', '\n']) >= 0) { error = "Название должно занимать одну строку."; return false; }
        try { _encoding.GetByteCount(Compose(title, description, _lineBreak, _hadLineBreak)); }
        catch (EncoderFallbackException) { error = "Текст нельзя сохранить в исходной кодировке."; return false; }
        state = new(this, title, description, _isPresent); return true;
    }

    public TextResourceState CaptureState() => new(this, _title, _description, _isPresent);

    public void RestoreState(TextResourceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.EnsureOwner(this);
        _title = state.Title;
        _description = state.Description;
        _isPresent = state.IsPresent;
    }

    public void ResetToOriginal()
    {
        _title = _originalTitle;
        _description = _originalDescription;
        _isPresent = !IsNew;
    }

    internal byte[] Encode() => _encoding.GetBytes(Compose(_title, _description, _lineBreak, _hadLineBreak));

    internal void SetPresent(bool value)
    {
        if (!IsNew && !HistoryRemovalAllowed && !value)
        {
            throw new InvalidOperationException("Удаление исходных ресурсов texts.res не поддерживается.");
        }

        _isPresent = value;
    }

    private bool TrySet(string title, string description, out string? error)
    {
        error = null;
        if (!IsPresent)
        {
            error = "Текстовый ресурс сейчас отсутствует в документе.";
            return false;
        }

        title ??= string.Empty;
        description ??= string.Empty;
        if (title.IndexOfAny(['\r', '\n']) >= 0)
        {
            error = "Название должно занимать одну строку; переносы используйте в описании.";
            return false;
        }

        try
        {
            _encoding.GetByteCount(Compose(title, description, _lineBreak, _hadLineBreak));
        }
        catch (EncoderFallbackException)
        {
            error = "Текст содержит символы, которые нельзя сохранить в Windows-1251.";
            return false;
        }

        _title = title;
        _description = description;
        return true;
    }

    private static (string Title, string Description, string LineBreak, bool HadLineBreak) Split(string text)
    {
        int newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return (text.TrimEnd('\r'), string.Empty, "\r\n", false);
        }

        bool crlf = newline > 0 && text[newline - 1] == '\r';
        int titleLength = crlf ? newline - 1 : newline;
        return (text[..titleLength], text[(newline + 1)..], crlf ? "\r\n" : "\n", true);
    }

    private static string Compose(string title, string description, string lineBreak, bool preserveEmptyLineBreak) =>
        string.IsNullOrEmpty(description)
            ? preserveEmptyLineBreak ? title + lineBreak : title
            : title + lineBreak + description;
}

public sealed class TextsDocument
{
    private static readonly Encoding TextEncoding = CreateTextEncoding();
    private readonly ResArchiveDocument _archive;
    private readonly List<TextResource> _resources;
    private readonly Dictionary<string, TextResource> _byName;

    private TextsDocument(ResArchiveDocument archive)
    {
        _archive = archive;
        _resources = new List<TextResource>(archive.Entries.Count);
        _byName = new Dictionary<string, TextResource>(archive.Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ResEntry entry in archive.Entries)
        {
            string text;
            try
            {
                text = TextEncoding.GetString(archive.GetResourceBytes(entry).Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new DatabaseFormatException(
                    $"Text resource '{entry.Name}' is not valid Windows-1251 text: {exception.Message}",
                    entry.Name,
                    entry.Offset);
            }

            var resource = new TextResource(entry.Index, entry.Name, text, TextEncoding, isNew: false);
            _resources.Add(resource);
            if (!_byName.TryAdd(entry.Name, resource))
            {
                throw new DatabaseFormatException($"Text resource '{entry.Name}' is duplicated.", entry.Name);
            }
        }
    }

    public string SourcePath => _archive.SourcePath;

    public long FileLength => _archive.Length;

    public string Sha256 => _archive.Sha256;

    public IReadOnlyList<TextResource> Resources => _resources.Where(resource => resource.IsPresent).ToArray();

    public bool IsModified => _resources.Any(resource => resource.IsModified);

    public static TextsDocument Open(string path) => new(ResArchiveDocument.Open(path));

    public static async Task<TextsDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ResArchiveDocument archive = await ResArchiveDocument.OpenAsync(path, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new TextsDocument(archive);
    }

    public bool TryGetResource(string name, out TextResource? resource) =>
        _byName.TryGetValue(name, out resource) && resource.IsPresent;

    public TextResource AddResource(string name, string title = "", string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_byName.TryGetValue(name, out TextResource? existing))
        {
            if (!existing.IsPresent && existing.IsNew)
            {
                existing.SetPresent(true);
                return existing;
            }

            throw new InvalidOperationException($"Ресурс texts.res '{name}' уже существует.");
        }

        if (!name.All(character => character <= 0x7F && !char.IsControl(character)))
        {
            throw new InvalidOperationException("Имя ресурса texts.res должно быть допустимой ASCII-строкой.");
        }

        var resource = new TextResource(null, name, string.Empty, TextEncoding, isNew: true);
        if (!resource.TrySetTitle(title, out string? titleError))
        {
            throw new InvalidOperationException(titleError);
        }

        if (!resource.TrySetDescription(description, out string? descriptionError))
        {
            throw new InvalidOperationException(descriptionError);
        }

        _resources.Add(resource);
        _byName.Add(name, resource);
        return resource;
    }

    public TextResource RebindHistoryResource(TextResource previous, bool present)
    {
        TextResource resource;
        if (present)
            resource = _byName[previous.Name];
        else
        {
            resource = AddResource(previous.Name, previous.Title, previous.Description);
            resource.SetPresent(false);
        }
        resource.HistoryRemovalAllowed = true;
        return resource;
    }

    public void RemoveAddedResource(TextResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureOwned(resource);
        if (!resource.IsNew && !resource.HistoryRemovalAllowed)
        {
            throw new InvalidOperationException("Удаление исходных ресурсов texts.res не поддерживается.");
        }

        resource.SetPresent(false);
    }

    public void RestoreAddedResource(TextResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureOwned(resource);
        resource.SetPresent(true);
    }

    public SaveResult SaveAs(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string sourcePath = Path.GetFullPath(SourcePath);
        string destinationPath = Path.GetFullPath(targetPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Исходный texts.res нельзя перезаписать через «Сохранить копию».");
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("Целевой texts.res уже существует.");
        }

        EnsureSafeDestination(destinationPath, allowGameInstallationPath: false);
        using var sourceGuard = OpenSourceGuard(sourcePath);
        EnsureSourceUnchanged(sourceGuard);
        byte[] rebuilt = BuildAndVerify(destinationPath);
        if(PakArchive.IsPak(destinationPath))rebuilt=PakArchive.ConvertResImage(rebuilt);
        else if(_archive.IsPak)throw new IOException("PAK output requires .pak extension.");
        string tempPath = CreateTempPath(destinationPath);
        try
        {
            WriteVerified(tempPath, rebuilt);
            EnsureSourceUnchanged(sourceGuard);
            if(PakArchive.IsPak(destinationPath)){PakArchive.PublishImages([(destinationPath,null,rebuilt)]);File.Delete(tempPath);}
            else File.Move(tempPath, destinationPath);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }

        return CreateSaveResult(destinationPath, rebuilt, backupPath: null);
    }

    public SaveResult SaveInPlace(bool allowGameInstallationPath = false, bool createBackup = true, bool keepRollbackForPair = false)
    {
        string sourcePath = Path.GetFullPath(SourcePath);
        EnsureSafeDestination(sourcePath, allowGameInstallationPath);
        if (!IsModified)
        {
            return new SaveResult(sourcePath, FileLength, Sha256, true, [], null);
        }

        using var sourceGuard = new GuardedFileCommit(sourcePath, Sha256);
        byte[] rebuilt = BuildAndVerify(sourcePath);
        string tempPath = CreateTempPath(sourcePath);
        string backupPath;
        try
        {
            WriteVerified(tempPath, rebuilt);
            if (createBackup) RetainNewestManagedBackupBeforeSave(sourcePath);
            backupPath = createBackup ? CreateAvailableBackupPath(sourcePath) : sourcePath + ".rollback-" + Guid.NewGuid().ToString("N");
            sourceGuard.Commit(tempPath, backupPath, Convert.ToHexString(SHA256.HashData(rebuilt)));
            sourceGuard.Dispose();
            EnsureBackupMatches(backupPath);
            EnsureFileMatches(sourcePath, rebuilt);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }

        if (!createBackup && !keepRollbackForPair) File.Delete(backupPath);
        return CreateSaveResult(sourcePath, rebuilt, createBackup || keepRollbackForPair ? backupPath : null);
    }

    public Task<SaveResult> SaveAsAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => SaveAs(targetPath), CancellationToken.None);
    }

    public static void RollbackSavedPair(SaveResult saved, string originalSha256)
    {
        if (saved.BackupPath is not { Length: > 0 } backupPath)
        {
            throw new InvalidOperationException("Нет резервной копии для отката texts.res.");
        }

        EnsureSafeDestination(saved.TargetPath, allowGameInstallationPath: true);
        using var guard = new GuardedFileCommit(saved.TargetPath, saved.Sha256);
        string tempPath = CreateTempPath(saved.TargetPath);
        try
        {
            byte[] original = File.ReadAllBytes(backupPath);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(original)), originalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Резервная копия texts.res изменилась; откат отменён.");
            }

            WriteVerified(tempPath, original);
            RetainNewestManagedBackupBeforeSave(saved.TargetPath);
            guard.Commit(tempPath, CreateAvailableBackupPath(saved.TargetPath), originalSha256);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    public Task<SaveResult> SaveInPlaceAsync(
        bool allowGameInstallationPath = false,
        CancellationToken cancellationToken = default, bool createBackup = true, bool keepRollbackForPair = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => SaveInPlace(allowGameInstallationPath, createBackup, keepRollbackForPair), CancellationToken.None);
    }

    public byte[] PrepareImage() => BuildAndVerify(SourcePath);

    private byte[] BuildAndVerify(string pathForErrors)
    {
        var replacements = new Dictionary<int, byte[]>();
        var additions = new List<AddedResResource>();
        foreach (TextResource resource in _resources.Where(resource => resource.IsPresent && resource.IsModified))
        {
            byte[] encoded = resource.Encode();
            if (resource.ArchiveIndex is int index)
            {
                replacements.Add(index, encoded);
            }
            else
            {
                additions.Add(new AddedResResource(resource.Name, encoded));
            }
        }

        var removals = _resources.Where(r => !r.IsPresent && r.ArchiveIndex.HasValue)
            .Select(r => r.ArchiveIndex!.Value).ToHashSet();
        byte[] rebuilt = ResArchiveWriter.Rebuild(_archive, replacements, additions, removals);
        ResArchiveDocument verified = ResArchiveDocument.Parse(pathForErrors, rebuilt);
        foreach (TextResource removed in _resources.Where(r => !r.IsPresent))
            if (verified.TryGetEntry(removed.Name, out _))
                throw new DatabaseFormatException("Undone text remains after rebuild.", removed.Name);
        foreach (TextResource expected in _resources.Where(resource => resource.IsPresent))
        {
            if (!verified.TryGetEntry(expected.Name, out ResEntry? entry) || entry is null)
            {
                throw new DatabaseFormatException(
                    $"Text resource '{expected.Name}' is missing after rebuild.",
                    expected.Name);
            }

            ResEntry? originalEntry = expected.ArchiveIndex is int originalIndex
                ? _archive.Entries.FirstOrDefault(candidate => candidate.Index == originalIndex)
                : null;
            ReadOnlySpan<byte> expectedBytes = expected.IsModified
                ? expected.Encode()
                : _archive.GetResourceBytes(originalEntry
                    ?? throw new DatabaseFormatException(
                        $"Original text resource '{expected.Name}' is missing.",
                        expected.Name)).Span;
            if (!verified.GetResourceBytes(entry).Span.SequenceEqual(expectedBytes))
            {
                throw new DatabaseFormatException(
                    $"Text resource '{expected.Name}' differs after rebuild.",
                    expected.Name,
                    entry.Offset);
            }
        }

        return rebuilt;
    }

    private static Encoding CreateTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1251,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private void EnsureOwned(TextResource resource)
    {
        if (!_byName.TryGetValue(resource.Name, out TextResource? owned) || !ReferenceEquals(owned, resource))
        {
            throw new ArgumentException("Текстовый ресурс не принадлежит открытому texts.res.", nameof(resource));
        }
    }

    private static void EnsureSafeDestination(string path, bool allowGameInstallationPath)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Папка для texts.res не существует.");
        }

        if (DatabaseSafetyPolicy.ContainsReparsePoint(path))
        {
            throw new InvalidOperationException("Путь texts.res проходит через символическую ссылку или junction.");
        }

        if (!allowGameInstallationPath && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(path))
        {
            throw new InvalidOperationException("Прямая запись texts.res в каталог игры требует отдельного подтверждения.");
        }
    }

    private FileStream OpenSourceGuard(string sourcePath) => new(
        sourcePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.SequentialScan);

    private void EnsureSourceUnchanged(FileStream sourceGuard)
    {
        sourceGuard.Position = 0;
        string currentHash = Convert.ToHexString(SHA256.HashData(sourceGuard));
        if (!string.Equals(currentHash, Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Исходный texts.res изменился на диске после открытия. Сохранение отменено.");
        }
    }

    private void EnsureBackupMatches(string path)
    {
        using var stream = File.OpenRead(path);
        string backupHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(backupHash, Sha256, StringComparison.Ordinal))
        {
            throw new IOException("Резервная копия исходного texts.res не прошла проверку SHA-256.");
        }
    }

    private static SaveResult CreateSaveResult(string path, byte[] data, string? backupPath) => new(
        path,
        data.LongLength,
        Convert.ToHexString(SHA256.HashData(data)),
        true,
        [],
        backupPath);

    private static string CreateTempPath(string targetPath) => Path.Combine(
        Path.GetDirectoryName(targetPath)!,
        $".{Path.GetFileName(targetPath)}.partial-{Guid.NewGuid():N}");

    private static void WriteVerified(string path, byte[] data)
    {
        using (var stream = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   128 * 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(true);
        }

        EnsureFileMatches(path, data);
    }

    private static void TryDeleteTemp(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string CreateAvailableBackupPath(string sourcePath)
    {
        string basePath = sourcePath + ".bak";
        if (!File.Exists(basePath) && !Directory.Exists(basePath))
        {
            return basePath;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            string candidate = suffix == 0
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
        foreach (FileInfo obsolete in backups.Skip(1))
        {
            FileAttributes attributes = File.GetAttributes(obsolete.FullName);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException($"Старая резервная копия texts.res имеет небезопасный тип: {obsolete.FullName}");
            }

            File.Delete(obsolete.FullName);
        }

        if (EnumerateManagedBackups(sourcePath).Count > 1)
        {
            throw new IOException("Не удалось сократить число резервных копий texts.res.");
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateManagedBackups(string sourcePath)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string directory = Path.GetDirectoryName(fullSourcePath)
            ?? throw new DirectoryNotFoundException("Не найдена папка texts.res.");
        string fileName = Path.GetFileName(fullSourcePath);
        var pattern = new Regex(
            $"^{Regex.Escape(fileName)}\\.bak(?:\\.\\d{{8}}-\\d{{6}}(?:-\\d{{3}})?|\\.[0-9a-f]{{32}})?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return Directory.EnumerateFileSystemEntries(directory, fileName + ".bak*", SearchOption.TopDirectoryOnly)
            .Where(path => pattern.IsMatch(Path.GetFileName(path)))
            .Select(path => new FileInfo(path))
            .Where(file => (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureFileMatches(string path, ReadOnlySpan<byte> expected)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != expected.Length)
        {
            throw new IOException("Записанный texts.res имеет неверный размер.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var offset = 0;
            while (offset < expected.Length)
            {
                int requested = Math.Min(buffer.Length, expected.Length - offset);
                int read = stream.Read(buffer, 0, requested);
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read)))
                {
                    throw new IOException("Записанный texts.res не прошёл побайтовую проверку.");
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
