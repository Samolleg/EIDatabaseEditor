using System.Security.Cryptography;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record AnimationSaveTarget(string Path, string? ResourceName, byte[] OriginalBytes)
{
    public string? ArchiveSha256 { get; set; } = ResourceName is null ? null : AnimationPairSave.HashFile(Path);
    public string Description => ResourceName is null ? Path : Path + " → " + ResourceName;

    public async Task<string?> SaveAsync(byte[] expected, byte[] bytes, bool backup)
    {
        _ = ResArchiveDocument.Parse("animation.anm", bytes);
        string? savedBackup;
        if (ResourceName is not null)
        {
            var archive = await ResFileManagerArchive.OpenAsync(Path);
            if (ArchiveSha256 is not null && !archive.Sha256.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Архив ANM изменён после открытия. Обновите модель.");
            var entry = archive.Entries.SingleOrDefault(e => e.Name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("Исходный ANM удалён из архива. Обновите модель или экспортируйте отдельно.");
            byte[] current = new byte[entry.Size];
            current = ResFileManagerArchive.ReadResource(Path, entry.Name);
            if (!current.AsSpan().SequenceEqual(expected)) throw new IOException("Исходный ANM изменён после открытия. Сохранение отменено; экспортируйте правки отдельно.");
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ei-animation-" + Guid.NewGuid().ToString("N") + ".anm");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes);
                var import = await ResFileManagerArchive.InspectImportAsync(temporary);
                var result = await archive.SaveInPlaceAsync([new(ResFileEditKind.AddOrReplace, ResourceName, import)], true, createBackup: backup);
                savedBackup = result.BackupPath;
                ArchiveSha256 = result.Sha256;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var readback = await ResFileManagerArchive.OpenAsync(Path);
            entry = readback.Entries.Single(e => e.Name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase));
            current = new byte[entry.Size];
            current = ResFileManagerArchive.ReadResource(Path, entry.Name);
            if (!current.AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка сохранённого ANM.");
        }
        else
        {
            string prepared = Path + ".prepared-" + Guid.NewGuid().ToString("N");
            string recovery = Path + (backup ? ".bak-" : ".rollback-") + Guid.NewGuid().ToString("N");
            try
            {
                using (var guard = new GuardedFileCommit(Path, Convert.ToHexString(SHA256.HashData(expected))))
                {
                    using (var file = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
                    guard.Commit(prepared, recovery, Convert.ToHexString(SHA256.HashData(bytes)));
                }
                if (!File.ReadAllBytes(Path).AsSpan().SequenceEqual(bytes)) throw new IOException("Не прошла проверка сохранённого ANM.");
                if (!backup) File.Delete(recovery);
                savedBackup = backup ? recovery : null;
            }
            finally { if (File.Exists(prepared)) File.Delete(prepared); }
        }
        return savedBackup;
    }
}
