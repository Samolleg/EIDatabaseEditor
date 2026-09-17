using System.Security.Cryptography;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record PreparedFileWrite(Guid Id, string Path, string ExpectedHash, byte[] Bytes);
public sealed record CoordinatedFileResult(Guid Id, string Path, string Status, string Message,
    string BackupPath, string PreparedPath, string OriginalHash, string PreparedHash);
public sealed record CoordinatedSaveResult(bool Complete, string JournalPath, IReadOnlyList<CoordinatedFileResult> Files);

/// <summary>Prepares all files and locks every original before publishing any participant.
/// A durable journal and both byte versions survive partial publication.</summary>
public static class DocumentSaveCoordinator
{
    public static CoordinatedSaveResult Commit(IReadOnlyList<PreparedFileWrite> writes, string journalDirectory,
        bool allowGameInstallationPaths = false, Action<int>? beforePublish = null)
    {
        if (writes.Count == 0) return new(true, "", []);
        string operation = Guid.NewGuid().ToString("N");
        string journal = System.IO.Path.Combine(System.IO.Path.GetFullPath(journalDirectory), operation + ".json");
        var results = writes.Select(w => new CoordinatedFileResult(w.Id, System.IO.Path.GetFullPath(w.Path),
            "NotAttempted", "", System.IO.Path.GetFullPath(w.Path) + ".bak-" + operation,
            System.IO.Path.GetFullPath(w.Path) + ".prepared-" + operation, w.ExpectedHash,
            Convert.ToHexString(SHA256.HashData(w.Bytes)))).ToArray();
        var guards = new List<GuardedFileCommit?>();
        var alreadyCurrent = new HashSet<int>();
        int current = 0;
        void WriteJournal()
        {
            string temp = journal + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Files = results });
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, journal, true);
        }
        try
        {
            if (results.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != results.Length ||
                writes.Select(w => w.Id).Distinct().Count() != writes.Count)
                throw new IOException("Повторяющийся участник сохранения.");
            if (DatabaseSafetyPolicy.ContainsReparsePoint(journalDirectory)) throw new IOException("Папка журнала проходит через символическую ссылку.");
            Directory.CreateDirectory(journalDirectory);
            for (current = 0; current < results.Length; current++)
            {
                var result = results[current];
                if (!allowGameInstallationPaths && DatabaseSafetyPolicy.IsLikelyGameInstallationPath(result.Path))
                    throw new IOException("Запись в каталог игры требует отдельного подтверждения.");
                if (string.IsNullOrEmpty(result.OriginalHash))
                {
                    if (File.Exists(result.Path) || DatabaseSafetyPolicy.ContainsReparsePoint(result.Path)) throw new IOException("Новый файл уже существует или путь содержит ссылку.");
                    guards.Add(null);
                }
                else
                {
                    try { guards.Add(new GuardedFileCommit(result.Path, result.OriginalHash)); }
                    catch (IOException)
                    {
                        // A prior partial save may already have published these exact bytes.
                        // Lock and verify that content; never overwrite a different external edit.
                        guards.Add(new GuardedFileCommit(result.Path, result.PreparedHash));
                        alreadyCurrent.Add(current);
                    }
                }
            }
            for (current = 0; current < results.Length; current++)
            {
                if (alreadyCurrent.Contains(current)) continue;
                using var stream = new FileStream(results[current].PreparedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                stream.Write(writes[current].Bytes); stream.Flush(true); stream.Position = 0;
                if (Convert.ToHexString(SHA256.HashData(stream)) != results[current].PreparedHash)
                    throw new IOException("Проверка подготовленного файла не пройдена.");
            }
            WriteJournal();
            for (current = 0; current < results.Length; current++)
            {
                if (alreadyCurrent.Contains(current))
                {
                    results[current] = results[current] with { Status = "Saved", Message = "Проверено: файл уже содержит подготовленную версию; повторной записи не было." };
                    WriteJournal(); continue;
                }
                beforePublish?.Invoke(current);
                results[current] = results[current] with { Status = "Committing" }; WriteJournal();
                var result = results[current];
                if (guards[current] is { } guard) guard.Commit(result.PreparedPath, result.BackupPath, result.PreparedHash);
                else File.Move(result.PreparedPath, result.Path, false);
                results[current] = result with { Status = "Saved" }; WriteJournal();
            }
            return new(true, journal, results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            int index = Math.Min(current, results.Length - 1);
            // Saved remains Saved if publishing succeeded but updating the journal failed.
            if (results[index].Status != "Saved") results[index] = results[index] with { Status = "Failed", Message = ex.Message };
            else results[index] = results[index] with { Message = "Файл записан, журнал требует проверки: " + ex.Message };
            try { WriteJournal(); } catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException)
            { results[index] = results[index] with { Message = results[index].Message + " Журнал: " + journalError.Message }; }
            return new(false, journal, results);
        }
        finally { foreach (var guard in guards) guard?.Dispose(); }
    }
}
