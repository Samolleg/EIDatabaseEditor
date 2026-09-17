using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class RecoverySnapshotStore
{
    private const int MaximumSnapshotsPerSessionAndSource = 2;
    private readonly string _rootPath;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _drafts = new(StringComparer.OrdinalIgnoreCase);

    public RecoverySnapshotStore(string? rootPath = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Редактор БД от Атамана",
            "recovery"));
    }

    public RecoverySnapshotDraft Prepare(string sourcePath)
    {
        lock (_sync)
        {
            string sourceRoot = GetSourceRoot(sourcePath);
            string snapshotDirectory = Path.Combine(
                sourceRoot,
                $"{_sessionId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(snapshotDirectory);
            _drafts.Add(snapshotDirectory, Path.GetFullPath(sourcePath));
            return new RecoverySnapshotDraft(
                snapshotDirectory,
                Path.Combine(snapshotDirectory, "database.res"));
        }
    }

    public RecoverySnapshot Complete(
        string sourcePath,
        RecoverySnapshotDraft draft,
        SavePairCopyResult result,
        PendingSoundEditViewModel? pendingSound, byte[]? modules = null)
    {
        lock (_sync)
        {
            string fullSourcePath = Path.GetFullPath(sourcePath);
            if (!_drafts.TryGetValue(draft.DirectoryPath, out string? ownerSource) ||
                !string.Equals(ownerSource, fullSourcePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(result.Database.TargetPath), draft.DatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Аварийный снимок не принадлежит этой сессии или был отменён.");
            }
            string? soundAssetPath = null;
            if (pendingSound is not null)
            {
                soundAssetPath = Path.Combine(draft.DirectoryPath, $"pending-audio.{pendingSound.Format.ToLowerInvariant()}");
                WriteAtomic(soundAssetPath, pendingSound.Asset.Data.ToArray());
            }

            string? modulesPath = null;
            if (modules is not null) { modulesPath = Path.Combine(draft.DirectoryPath, "modules.json"); WriteAtomic(modulesPath, modules); }
            var manifest = new RecoverySnapshotManifest
            {
                ModulesPath = modulesPath,
                ModulesSha256 = modules is null ? null : Convert.ToHexString(SHA256.HashData(modules)),
                SourcePath = fullSourcePath,
                SessionId = _sessionId,
                SourceLastWriteUtc = File.GetLastWriteTimeUtc(fullSourcePath),
                CreatedUtc = DateTime.UtcNow,
                DatabasePath = Path.GetFullPath(result.Database.TargetPath),
                TextsPath = result.Texts?.TargetPath,
                PendingSoundAssetPath = soundAssetPath,
                PendingSoundSceneKey = pendingSound?.SceneKey,
                PendingSoundPhraseNumber = pendingSound?.PhraseNumber,
                PendingSoundSpeaker = pendingSound?.Speaker,
                PendingSoundArchivePath = pendingSound?.ArchivePath,
                PendingSoundResourceName = pendingSound?.ResourceName,
            };
            string manifestPath = Path.Combine(draft.DirectoryPath, "recovery.json");
            WriteAtomic(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Prune(fullSourcePath);
            _drafts.Remove(draft.DirectoryPath);
            return MapManifest(manifestPath, manifest);
        }
    }

    public RecoverySnapshot? FindLatest(string sourcePath)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string sourceRoot = GetSourceRoot(fullSourcePath);
        if (!Directory.Exists(sourceRoot))
        {
            return null;
        }

        foreach (string manifestPath in new DirectoryInfo(sourceRoot).EnumerateDirectories()
                     .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                     .Select(directory => Path.Combine(directory.FullName, "recovery.json"))
                     .Where(File.Exists)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            RecoverySnapshot? snapshot = TryReadSnapshot(manifestPath, fullSourcePath);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }

    public IReadOnlyList<RecoverySnapshot> FindAvailable()
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        var snapshots = new List<RecoverySnapshot>();
        try
        {
            foreach (DirectoryInfo sourceDirectory in new DirectoryInfo(_rootPath).EnumerateDirectories()
                         .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                var complete = sourceDirectory.EnumerateDirectories()
                    .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .Select(directory => Path.Combine(directory.FullName, "recovery.json"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(manifestPath => TryReadSnapshot(manifestPath, expectedSourcePath: null))
                    .OfType<RecoverySnapshot>();
                foreach (var session in complete.GroupBy(snapshot => snapshot.SessionId))
                {
                    snapshots.Add(session.OrderByDescending(snapshot => snapshot.CreatedUtc).First());
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.CreatedUtc)
            .ToArray();
    }

    public void Abandon(RecoverySnapshotDraft draft)
    {
        lock (_sync)
        {
            if (_drafts.Remove(draft.DirectoryPath))
            {
                TryDeleteSnapshotDirectory(draft.DirectoryPath);
            }
        }
    }

    public void Discard(string sourcePath)
    {
        lock (_sync)
        {
            string sourceRoot = GetSourceRoot(sourcePath);
            if (Directory.Exists(sourceRoot) && IsInsideRoot(sourceRoot))
            {
                foreach (DirectoryInfo directory in new DirectoryInfo(sourceRoot).EnumerateDirectories()
                             .Where(IsOwnedDirectory))
                {
                    _drafts.Remove(directory.FullName);
                    TryDeleteSnapshotDirectory(directory.FullName);
                }
            }
        }
    }

    private void Prune(string sourcePath)
    {
        string sourceRoot = GetSourceRoot(sourcePath);
        DirectoryInfo[] directories = new DirectoryInfo(sourceRoot).EnumerateDirectories()
            .Where(IsOwnedDirectory)
            .ToArray();

        DirectoryInfo[] complete = directories
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "recovery.json")))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        foreach (DirectoryInfo directory in complete.Skip(MaximumSnapshotsPerSessionAndSource))
        {
            TryDeleteSnapshotDirectory(directory.FullName);
        }
    }

    private bool IsOwnedDirectory(DirectoryInfo directory) =>
        directory.Name.StartsWith(_sessionId + "-", StringComparison.Ordinal) &&
        !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private string GetSourceRoot(string sourcePath)
    {
        string normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        return Path.Combine(_rootPath, key);
    }

    private void TryDeleteSnapshotDirectory(string directoryPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath) || !IsInsideRoot(fullPath))
            {
                return;
            }

            var directory = new DirectoryInfo(fullPath);
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                directory.EnumerateDirectories().Any())
            {
                return;
            }

            foreach (FileInfo file in directory.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return;
                }
            }

            foreach (FileInfo file in directory.EnumerateFiles())
            {
                file.Delete();
            }

            directory.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool IsInsideRoot(string path) =>
        Path.GetFullPath(path).StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void WriteAtomic(string targetPath, byte[] data)
    {
        string tempPath = $"{targetPath}.partial-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(true);
            }

            File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static RecoverySnapshot? TryReadSnapshot(string manifestPath, string? expectedSourcePath)
    {
        try
        {
            RecoverySnapshotManifest? manifest = JsonSerializer.Deserialize<RecoverySnapshotManifest>(
                File.ReadAllBytes(manifestPath));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.SourcePath))
            {
                return null;
            }

            string sourcePath = Path.GetFullPath(manifest.SourcePath);
            if ((expectedSourcePath is not null &&
                 !string.Equals(sourcePath, expectedSourcePath, StringComparison.OrdinalIgnoreCase)) ||
                (!File.Exists(sourcePath) && manifest.ModulesPath is null) ||
                !File.Exists(manifest.DatabasePath) ||
                (manifest.ModulesPath is null && manifest.SourceLastWriteUtc != File.GetLastWriteTimeUtc(sourcePath)) ||
                (manifest.ModulesPath is not null && !File.Exists(manifest.ModulesPath)) ||
                (manifest.TextsPath is not null && !File.Exists(manifest.TextsPath)))
            {
                return null;
            }

            return MapManifest(manifestPath, manifest);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static RecoverySnapshot MapManifest(string manifestPath, RecoverySnapshotManifest manifest) => new(
        Path.GetFullPath(manifest.SourcePath),
        manifest.DatabasePath,
        manifest.TextsPath,
        manifest.CreatedUtc,
        manifest.PendingSoundAssetPath,
        manifest.PendingSoundSceneKey,
        manifest.PendingSoundPhraseNumber,
        manifest.PendingSoundSpeaker,
        manifest.PendingSoundArchivePath,
        manifest.PendingSoundResourceName,
        manifestPath,
        manifest.SessionId, manifest.ModulesPath, manifest.ModulesSha256);

    private sealed class RecoverySnapshotManifest
    {
        public string? ModulesPath { get; set; }
        public string? ModulesSha256 { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public DateTime SourceLastWriteUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string DatabasePath { get; set; } = string.Empty;
        public string? TextsPath { get; set; }
        public string? PendingSoundAssetPath { get; set; }
        public string? PendingSoundSceneKey { get; set; }
        public int? PendingSoundPhraseNumber { get; set; }
        public string? PendingSoundSpeaker { get; set; }
        public string? PendingSoundArchivePath { get; set; }
        public string? PendingSoundResourceName { get; set; }
    }
}

internal sealed record RecoverySnapshotDraft(string DirectoryPath, string DatabasePath);

internal sealed record RecoverySnapshot(
    string SourcePath,
    string DatabasePath,
    string? TextsPath,
    DateTime CreatedUtc,
    string? PendingSoundAssetPath,
    string? PendingSoundSceneKey,
    int? PendingSoundPhraseNumber,
    string? PendingSoundSpeaker,
    string? PendingSoundArchivePath,
    string? PendingSoundResourceName,
    string ManifestPath,
    string? SessionId = null, string? ModulesPath = null, string? ModulesSha256 = null);
