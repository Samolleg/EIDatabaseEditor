using System.Security.Cryptography;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed class AnimationDatabaseSource
{
    private byte[] _baseline;
    public string ArchivePath { get; }
    public string ResourceName { get; }
    public AnimationDatabase Database { get; private set; }
    public string Description => ArchivePath + " → " + ResourceName;

    private AnimationDatabaseSource(string archivePath, string resourceName, byte[] bytes)
    { ArchivePath = archivePath; ResourceName = resourceName; _baseline = bytes; Database = AnimationDatabase.Parse(bytes); }

    public static Task<AnimationDatabaseSource?> OpenAsync(string databasePath, string? configurationPath, string model,
        CancellationToken token = default) => Task.Run(() =>
    {
        var paths = new List<string>();
        if (configurationPath is not null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string section = "";
            foreach (string raw in File.ReadAllLines(configurationPath, Encoding.GetEncoding(1251)))
            {
                token.ThrowIfCancellationRequested();
                string line = raw.Trim();
                if (line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 1 || !section.Equals("RES", StringComparison.OrdinalIgnoreCase) ||
                    !line[..eq].Trim().Equals("database.res", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = line[(eq + 1)..].Trim().Trim('"');
                if (relative.Length > 0) paths.Add(Path.GetFullPath(relative, Path.GetDirectoryName(configurationPath)!));
            }
        }
        paths.Add(databasePath);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var entry = ResFileManagerArchive.ReadCatalog(path, token).FirstOrDefault(e =>
                e.Name.Equals(model + ".adb", StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            if (entry.Size > 16 * 1024 * 1024) throw new InvalidDataException("ADB превышает лимит 16 МиБ.");
            byte[] bytes = new byte[entry.Size];
            using (var stream = File.OpenRead(path)) { stream.Position = entry.Offset; stream.ReadExactly(bytes); }
            return new AnimationDatabaseSource(path, entry.Name, bytes);
        }
        return null;
    }, token);

    public AnimationDatabase CreateDraft() => AnimationDatabase.Parse(Database.ExportBytes());

    public Task<string?> SaveAsync(bool backup = true) => SaveAsync(Database, backup);

    public async Task<string?> SaveAsync(AnimationDatabase draft, bool backup = true)
    {
        if (draft.Model != Database.Model) throw new ArgumentException("ADB принадлежит другой модели.", nameof(draft));
        byte[] bytes = draft.ExportBytes();
        _ = AnimationDatabase.Parse(bytes);
        var archive = await ResFileManagerArchive.OpenAsync(ArchivePath);
        var entry = archive.Entries.SingleOrDefault(e => e.Name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Ресурс ADB удалён из архива. Обновите модель.");
        byte[] current = new byte[entry.Size];
        using (var stream = File.OpenRead(ArchivePath)) { stream.Position = entry.Offset; stream.ReadExactly(current); }
        if (!current.AsSpan().SequenceEqual(_baseline)) throw new IOException("ADB изменён после открытия. Сохранение отменено; обновите модель.");
        if (bytes.AsSpan().SequenceEqual(_baseline)) { Database = AnimationDatabase.Parse(bytes); return null; }
        string temporary = Path.Combine(Path.GetTempPath(), "ei-adb-" + Guid.NewGuid().ToString("N") + ".adb");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            var import = await ResFileManagerArchive.InspectImportAsync(temporary);
            var result = await archive.SaveInPlaceAsync([new(ResFileEditKind.AddOrReplace, ResourceName, import)], true, createBackup: backup);
            var readback = await ResFileManagerArchive.OpenAsync(ArchivePath);
            entry = readback.Entries.Single(e => e.Name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase));
            current = new byte[entry.Size];
            using (var stream = File.OpenRead(ArchivePath)) { stream.Position = entry.Offset; stream.ReadExactly(current); }
            if (!current.AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка сохранённого ADB.");
            _baseline = bytes;
            Database = AnimationDatabase.Parse(current);
            return result.BackupPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
