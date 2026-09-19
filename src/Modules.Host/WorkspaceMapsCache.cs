using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DatabaseEditor.Modules.V1;

namespace DatabaseEditor.Modules.Host;

public sealed record MapsWorkspaceContext(WorkspaceStamp Stamp, string DatabasePath);

// serialize runs on the owner dispatcher: workspace identity cannot change during
// final authorization/rename. Slow enumeration and cache IO run outside it.
public sealed class WorkspaceMapsCache(Func<MapsWorkspaceContext> context,
    Func<Func<Result>, Result> serialize, Action<ImmutableArray<string>, WorkspaceStamp> authorize,
    string cacheDirectory) : IWorkspaceMapsCache
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EIMAPC01");
    private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private static bool Same(WorkspaceStamp a, WorkspaceStamp b) => a.SessionId == b.SessionId && a.Generation == b.Generation;
    private static Result Stale() => new(ResultStatus.Conflict, "База данных сменилась; результат индексации устарел.");
    private Result Capture(WorkspaceStamp expected, out MapsWorkspaceContext? captured)
    {
        MapsWorkspaceContext? value = null;
        var result = serialize(() =>
        {
            var now = context();
            if (!Same(expected, now.Stamp)) return Stale();
            if (string.IsNullOrWhiteSpace(now.DatabasePath)) return new(ResultStatus.Unavailable, "База не открыта.");
            value = now with { DatabasePath = Path.GetFullPath(now.DatabasePath) }; return Result.Ok;
        }); captured = value; return result;
    }
    private Result Guard(MapsWorkspaceContext captured, CancellationToken ct, Func<Result> action) => serialize(() =>
    {
        ct.ThrowIfCancellationRequested(); var now = context();
        if (!Same(captured.Stamp, now.Stamp) || string.IsNullOrWhiteSpace(now.DatabasePath) || Canonical(now.DatabasePath) != Canonical(captured.DatabasePath)) return Stale();
        return action();
    });
    private static void PlainPath(string path)
    {
        string? node = Path.GetFullPath(path);
        while (node is not null)
        {
            if ((File.Exists(node) || Directory.Exists(node)) && (File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Ссылки в пути карты или кэша не поддерживаются.");
            node = Path.GetDirectoryName(node);
        }
    }
    private string CachePath(MapsWorkspaceContext c) => Path.Combine(Path.GetFullPath(cacheDirectory), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(c.DatabasePath)))) + ".cache");
    private static Result Error(Exception ex) => new(ex is OperationCanceledException ? ResultStatus.Cancelled : ResultStatus.Failed, ex.Message);

    public async Task<WorkspaceMapsResult> DiscoverAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = Capture(expected, out var c);
            if (!result.Succeeded) return new(result, expected, []);
            var files = await Task.Run(() =>
            {
                PlainPath(c!.DatabasePath);
                string parent = Path.GetDirectoryName(c.DatabasePath)!;
                string directory = Path.Combine(parent, "Maps");
                if (!Directory.Exists(directory)) directory = Path.Combine(Path.GetDirectoryName(parent) ?? parent, "Maps");
                if (!Directory.Exists(directory)) return (Found: false, Paths: ImmutableArray<string>.Empty);
                PlainPath(directory);
                var paths = ImmutableArray.CreateBuilder<string>();
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetExtension(path).Equals(".mob", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) paths.Add(Path.GetFullPath(path));
                }
                return (Found: true, Paths: paths.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
            }, cancellationToken).ConfigureAwait(false);
            result = Guard(c!, cancellationToken, () =>
            {
                if (!files.Found) return new(ResultStatus.Unavailable, "Рядом с БД нет папки Maps.");
                authorize(files.Paths, c!.Stamp); return Result.Ok;
            });
            return new(result, c!.Stamp, result.Succeeded ? files.Paths : []);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return new(Error(ex), expected, []); }
    }
    public async Task<WorkspaceCacheResult> LoadAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = Capture(expected, out var c);
            if (!result.Succeeded) return new(result, []);
            var bytes = await Task.Run(() =>
            {
                PlainPath(c!.DatabasePath); string path = CachePath(c); PlainPath(path);
                if (!File.Exists(path)) return (byte[]?)null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length < 44 || stream.Length > MaxBytes + 44L) throw new InvalidDataException("Недопустимый размер кэша карт.");
                using var reader = new BinaryReader(stream);
                if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Неизвестный формат кэша карт.");
                int length = reader.ReadInt32(); var hash = reader.ReadBytes(32);
                if (length < 0 || length > MaxBytes || stream.Length != length + 44L) throw new InvalidDataException("Повреждён размер кэша карт.");
                var data = reader.ReadBytes(length); cancellationToken.ThrowIfCancellationRequested();
                if (!SHA256.HashData(data).AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Повреждены данные кэша карт.");
                return data;
            }, cancellationToken).ConfigureAwait(false);
            result = Guard(c!, cancellationToken, () => bytes is null ? new(ResultStatus.Unavailable, "Кэш карт ещё не создан.") : Result.Ok);
            return new(result, result.Succeeded ? ImmutableArray.Create(bytes!) : []);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return new(Error(ex), []); }
    }
    public async Task<Result> SaveAsync(WorkspaceStamp expected, ImmutableArray<byte> bytes, CancellationToken cancellationToken = default)
    {
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.IsDefault || bytes.Length > MaxBytes) return new(ResultStatus.Failed, "Кэш должен быть задан и не превышать 64 МиБ.");
            var result = Capture(expected, out var c); if (!result.Succeeded) return result;
            byte[] data = bytes.ToArray(); string target = CachePath(c!);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await Task.Run(() =>
            {
                PlainPath(c!.DatabasePath); PlainPath(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); PlainPath(target);
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream); writer.Write(Magic); writer.Write(data.Length); writer.Write(SHA256.HashData(data)); writer.Write(data); writer.Flush(); stream.Flush(true);
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken).ConfigureAwait(false);
            return Guard(c!, cancellationToken, () => { PlainPath(target); File.Move(temporary, target, true); return Result.Ok; });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return Error(ex); }
        finally { if (temporary is not null && File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
}
