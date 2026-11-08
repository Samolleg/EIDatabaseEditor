namespace EvilIslands.DatabaseEditor.Core;

public static class AnimationClipDeletion
{
    public static byte[] Remove(byte[] bytes, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Выберите клип ANM.");
        var archive = ResArchiveDocument.Parse("animation", bytes);
        var removed = archive.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            e.Name.Replace('/', '\\').StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)).Select(e => e.Index).ToHashSet();
        if (removed.Count == 0) throw new InvalidOperationException("Клип ANM не найден: " + name);
        byte[] result = ResArchiveWriter.Rebuild(archive, new Dictionary<int, byte[]>(), [], removed);
        var check = ResArchiveDocument.Parse("animation", result);
        if (check.Entries.Count != archive.Entries.Count - removed.Count) throw new InvalidDataException("Не удалось удалить клип ANM.");
        foreach (var entry in archive.Entries.Where(e => !removed.Contains(e.Index)))
            if (!archive.GetResourceBytes(entry).Span.SequenceEqual(check.GetResourceBytes(entry.Name).Span))
                throw new InvalidDataException("Удаление затронуло другой ресурс ANM.");
        return result;
    }

    public static async Task<FigureAnimationSet> SaveAsync(FigureAnimationSet source, AnimationDatabaseSource adb, string name)
    {
        var target = source.SaveTarget ?? throw new InvalidOperationException("Источник ANM не определён.");
        var current = await AnimationDatabaseSource.OpenAsync(adb.ArchivePath, null, Path.GetFileNameWithoutExtension(adb.ResourceName))
            ?? throw new IOException("Не удалось проверить ADB перед удалением клипа.");
        if (!current.Database.ExportBytes().AsSpan().SequenceEqual(adb.Database.ExportBytes()))
            throw new IOException("ADB изменился на диске. Обновите список перед удалением клипа.");
        var references = current.Database.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Index).ToArray();
        if (references.Length > 0) throw new InvalidOperationException("На клип ссылаются записи ADB: " + string.Join(", ", references) + ". Сначала удалите или замените эти записи и сохраните ADB.");
        byte[] result = Remove(source.ExportBytes(), name);
        await target.SaveAsync(target.OriginalBytes, result, true);
        return source.WithSavedBytes(result);
    }
}
