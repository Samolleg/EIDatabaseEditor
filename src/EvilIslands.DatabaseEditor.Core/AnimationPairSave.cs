using System.Security.Cryptography;

namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Publish animation and action registration as one guarded edit.</summary>
public static class AnimationPairSave
{
    private sealed class ArchiveEdit(ResArchiveDocument source)
    {
        public ResArchiveDocument Source { get; } = source;
        public Dictionary<int, byte[]> Replacements { get; } = [];
    }

    public static Task<string?> SaveAsync(AnimationSaveTarget target, byte[] expectedAnimation,
        byte[] animation, AnimationDatabaseSource actions, AnimationDatabase draft, bool backup = true)
        => Task.Run(() => Save(target, expectedAnimation, animation, actions, draft, backup));

    private static string? Save(AnimationSaveTarget target, byte[] expectedAnimation, byte[] animation,
        AnimationDatabaseSource actions, AnimationDatabase draft, bool backup)
    {
        var animationDocument = ResArchiveDocument.Parse("animation.anm", animation);
        if (draft.Model != actions.Database.Model) throw new ArgumentException("ADB принадлежит другой модели.", nameof(draft));
        byte[] actionBytes = draft.ExportBytes();
        _ = AnimationDatabase.Parse(actionBytes);
        var previousNames = AnimationDatabase.Parse(actions.BaselineBytes).Entries
            .Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in draft.Entries.Where(entry => !previousNames.Contains(entry.Name)))
            if (!animationDocument.Entries.Any(entry => entry.Name.Equals(action.Name, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Replace('/', '\\').StartsWith(action.Name + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Новая запись ADB ссылается на отсутствующий клип ANM: " + action.Name);
        var archives = new Dictionary<string, ArchiveEdit>(StringComparer.OrdinalIgnoreCase);
        var images = new List<(string Path, string? ExpectedHash, byte[] Bytes)>();
        var previous = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void Resource(string path, string name, string expectedHash, byte[] expectedBytes, byte[] bytes)
            {
                path = Path.GetFullPath(path);
                if (!archives.TryGetValue(path, out var edit))
                {
                    PakArchive.RecoverFor(path);
                    edit = new ArchiveEdit(ResArchiveDocument.Open(path));
                    archives.Add(path, edit);
                    previous.Add(path, edit.Source.OriginalBytes.ToArray());
                }
                if (!edit.Source.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Архив ANM/ADB изменён после открытия. Обновите модель: " + path);
                if (!edit.Source.TryGetEntry(name, out var entry) || entry is null ||
                    !edit.Source.GetResourceBytes(entry).Span.SequenceEqual(expectedBytes))
                    throw new IOException("Ресурс ANM/ADB изменён или удалён после открытия: " + name);
                if (!edit.Replacements.TryAdd(entry.Index, bytes))
                    throw new IOException("ANM и ADB не могут заменять одну запись архива.");
            }

            if (target.ResourceName is { } member)
                Resource(target.Path, member, target.ArchiveSha256 ?? throw new IOException("Нет снимка исходного архива ANM. Обновите модель."), expectedAnimation, animation);
            else
            {
                string path = Path.GetFullPath(target.Path);
                PakArchive.RecoverFor(path);
                byte[] current = File.ReadAllBytes(path);
                if (!current.AsSpan().SequenceEqual(expectedAnimation))
                    throw new IOException("Исходный ANM изменён после открытия. Обновите модель.");
                previous.Add(path, current);
                images.Add((path, Hash(expectedAnimation), animation));
            }
            if(actions.IsLoose){
                byte[] current=File.ReadAllBytes(actions.ArchivePath);
                if(!Hash(current).Equals(actions.ArchiveSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Loose ADB changed since opening.");
                previous.Add(actions.ArchivePath,current);images.Add((actions.ArchivePath,actions.ArchiveSha256,actionBytes));
            }else Resource(actions.ArchivePath, actions.ResourceName, actions.ArchiveSha256,
                actions.BaselineBytes, actionBytes);
            foreach (var pair in archives)
                images.Add((pair.Key, pair.Value.Source.Sha256,
                    ResArchiveWriter.Rebuild(pair.Value.Source, pair.Value.Replacements)));

            var backups = new List<string>();
            if (backup)
                foreach (var pair in previous)
                {
                    string path = pair.Key + ".bak-" + Guid.NewGuid().ToString("N");
                    images.Add((path, null, pair.Value)); backups.Add(path);
                }
            // Native transaction validates every owner under all locks before any publication.
            PakArchive.PublishMixedImages(images);
            var targetImage = images.Single(image => image.Path.Equals(Path.GetFullPath(target.Path), StringComparison.OrdinalIgnoreCase));
            var actionImage = images.Single(image => image.Path.Equals(Path.GetFullPath(actions.ArchivePath), StringComparison.OrdinalIgnoreCase));
            target.ArchiveSha256 = target.ResourceName is null ? null : Hash(targetImage.Bytes);
            actions.AcceptSaved(actionBytes, Hash(actionImage.Bytes));
            return backups.Count == 0 ? null : string.Join(Environment.NewLine, backups);
        }
        finally
        {
            foreach (var edit in archives.Values) edit.Source.Pak?.Dispose();
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string HashFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
