using System.Security.Cryptography;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed class AnimationDatabaseSource
{
    private byte[] _baseline;
    internal byte[] BaselineBytes => _baseline;
    public string ArchiveSha256 { get; private set; }
    public string ArchivePath { get; }
    public bool IsLoose { get; }
    public string ResourceName { get; }
    public AnimationDatabase Database { get; private set; }
    public string Description => ArchivePath + " → " + ResourceName;

    private AnimationDatabaseSource(string archivePath, string resourceName, byte[] bytes, string archiveSha256,bool isLoose=false)
    { ArchivePath = archivePath; ResourceName = resourceName; _baseline = bytes; ArchiveSha256 = archiveSha256; Database = AnimationDatabase.Parse(bytes);IsLoose=isLoose; }

    public static Task<AnimationDatabaseSource?> OpenAsync(string databasePath, string? configurationPath, string model,
        CancellationToken token = default) => Task.Run(() =>
    {
        if(ResourceArchivePaths.FindLoose(configurationPath,model+".adb") is { } loose){byte[] bytes=File.ReadAllBytes(loose);return new AnimationDatabaseSource(loose,model+".adb",bytes,Convert.ToHexString(SHA256.HashData(bytes)),true);}
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
                if (relative.Length > 0 && !relative.StartsWith('-')) paths.Add(Path.GetFullPath(relative, Path.GetDirectoryName(configurationPath)!));
            }
        }
        paths.Add(databasePath);
        foreach (string path in ResourceArchivePaths.Order(paths))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            using var pak = PakArchive.IsPak(path) ? new ReadOnlyPakArchive(path, token) : null;
            if (pak is not null && pak.Deleted.Any(n => PakArchive.LookupKey(n) == PakArchive.LookupKey(model + ".adb"))) return null;
            byte[]? legacyBytes = pak is null ? File.ReadAllBytes(path) : null;
            var legacy = legacyBytes is null ? null : ResArchiveDocument.Parse(path, legacyBytes, token);
            var entry = (pak?.Entries ?? legacy!.Entries).FirstOrDefault(e =>
                e.Name.Equals(model + ".adb", StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            if (entry.Size > 16 * 1024 * 1024) throw new InvalidDataException("ADB превышает лимит 16 МиБ.");
            byte[] bytes = new byte[entry.Size];
            bytes = pak is not null ? pak.Read(entry.Name, token) : legacy!.GetResourceBytes(entry).ToArray();
            return new AnimationDatabaseSource(path, entry.Name, bytes, pak?.Sha256 ?? legacy!.Sha256);
        }
        return null;
    }, token);

    internal void AcceptSaved(byte[] bytes, string archiveSha256)
    { _baseline = bytes.ToArray(); ArchiveSha256 = archiveSha256; Database = AnimationDatabase.Parse(bytes); }

    public AnimationDatabase CreateDraft() => AnimationDatabase.Parse(Database.ExportBytes());

    public Task<string?> SaveAsync(bool backup = true) => SaveAsync(Database, backup);

    public async Task<string?> SaveAsync(AnimationDatabase draft, bool backup = true)
    {
        if (draft.Model != Database.Model) throw new ArgumentException("ADB принадлежит другой модели.", nameof(draft));
        byte[] bytes = draft.ExportBytes();
        _ = AnimationDatabase.Parse(bytes);
        if(IsLoose){
            string? savedBackup=backup?ArchivePath+".bak."+Guid.NewGuid().ToString("N"):null;
            var images=new List<(string Path,string? ExpectedHash,byte[] Bytes)>{(ArchivePath,ArchiveSha256,bytes)};
            if(savedBackup is not null)images.Add((savedBackup,null,_baseline));
            PakArchive.PublishMixedImages(images);AcceptSaved(bytes,Convert.ToHexString(SHA256.HashData(bytes)));return savedBackup;
        }
        var archive = await ResFileManagerArchive.OpenAsync(ArchivePath);
        if (!archive.Sha256.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Архив ADB изменён после открытия. Обновите модель.");
        var entry = archive.Entries.SingleOrDefault(e => e.Name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Ресурс ADB удалён из архива. Обновите модель.");
        byte[] current = new byte[entry.Size];
        current = ResFileManagerArchive.ReadResource(ArchivePath, entry.Name);
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
            current = ResFileManagerArchive.ReadResource(ArchivePath, entry.Name);
            if (!current.AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка сохранённого ADB.");
            AcceptSaved(bytes, result.Sha256);
            return result.BackupPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
