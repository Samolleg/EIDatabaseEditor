using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class RecoverySnapshotStore
{
    private const int MaximumSnapshotsPerSource = 2;
    private readonly string _rootPath;

    public RecoverySnapshotStore(string? rootPath = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Редактор БД от Атамана",
            "recovery"));
    }

    public RecoverySnapshotDraft Prepare(string sourcePath)
    {
        string sourceRoot = GetSourceRoot(sourcePath);
        string snapshotDirectory = Path.Combine(
            sourceRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(snapshotDirectory);
        return new RecoverySnapshotDraft(
            snapshotDirectory,
            Path.Combine(snapshotDirectory, "database.res"));
    }

    public RecoverySnapshot Complete(
        string sourcePath,
        RecoverySnapshotDraft draft,
        SavePairCopyResult result,
        PendingSoundEditViewModel? pendingSound)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string? soundAssetPath = null;
        if (pendingSound is not null)
        {
            soundAssetPath = Path.Combine(draft.DirectoryPath, $"pending-audio.{pendingSound.Format.ToLowerInvariant()}");
            WriteAtomic(soundAssetPath, pendingSound.Asset.Data.ToArray());
        }

        var manifest = new RecoverySnapshotManifest
        {
            SourcePath = fullSourcePath,
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
        return MapManifest(manifestPath, manifest);
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
            try
            {
                RecoverySnapshotManifest? manifest = JsonSerializer.Deserialize<RecoverySnapshotManifest>(
                    File.ReadAllBytes(manifestPath));
                if (manifest is null ||
                    !string.Equals(Path.GetFullPath(manifest.SourcePath), fullSourcePath, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(manifest.DatabasePath) ||
                    manifest.SourceLastWriteUtc != File.GetLastWriteTimeUtc(fullSourcePath) ||
                    (manifest.TextsPath is not null && !File.Exists(manifest.TextsPath)))
                {
                    continue;
                }

                return MapManifest(manifestPath, manifest);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    public void Abandon(RecoverySnapshotDraft draft) => TryDeleteSnapshotDirectory(draft.DirectoryPath);

    public void Discard(string sourcePath)
    {
        string sourceRoot = GetSourceRoot(sourcePath);
        if (Directory.Exists(sourceRoot) && IsInsideRoot(sourceRoot))
        {
            foreach (DirectoryInfo directory in new DirectoryInfo(sourceRoot).EnumerateDirectories())
            {
                TryDeleteSnapshotDirectory(directory.FullName);
            }

            if (!Directory.EnumerateFileSystemEntries(sourceRoot).Any())
            {
                Directory.Delete(sourceRoot);
            }
        }
    }

    private void Prune(string sourcePath)
    {
        string sourceRoot = GetSourceRoot(sourcePath);
        DirectoryInfo[] directories = new DirectoryInfo(sourceRoot).EnumerateDirectories()
            .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToArray();
        foreach (DirectoryInfo incomplete in directories.Where(directory =>
                     !File.Exists(Path.Combine(directory.FullName, "recovery.json"))))
        {
            TryDeleteSnapshotDirectory(incomplete.FullName);
        }

        DirectoryInfo[] complete = directories
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "recovery.json")))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        foreach (DirectoryInfo directory in complete.Skip(MaximumSnapshotsPerSource))
        {
            TryDeleteSnapshotDirectory(directory.FullName);
        }
    }

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

    private static RecoverySnapshot MapManifest(string manifestPath, RecoverySnapshotManifest manifest) => new(
        manifest.DatabasePath,
        manifest.TextsPath,
        manifest.CreatedUtc,
        manifest.PendingSoundAssetPath,
        manifest.PendingSoundSceneKey,
        manifest.PendingSoundPhraseNumber,
        manifest.PendingSoundSpeaker,
        manifest.PendingSoundArchivePath,
        manifest.PendingSoundResourceName,
        manifestPath);

    private sealed class RecoverySnapshotManifest
    {
        public string SourcePath { get; set; } = string.Empty;
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
    string DatabasePath,
    string? TextsPath,
    DateTime CreatedUtc,
    string? PendingSoundAssetPath,
    string? PendingSoundSceneKey,
    int? PendingSoundPhraseNumber,
    string? PendingSoundSpeaker,
    string? PendingSoundArchivePath,
    string? PendingSoundResourceName,
    string ManifestPath);
