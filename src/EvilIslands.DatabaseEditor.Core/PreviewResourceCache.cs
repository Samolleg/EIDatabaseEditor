namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class TextureResourceCatalog
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, Task<TextureResourceCatalog>> CachedCatalogs = new(StringComparer.Ordinal);

    // A caller may cancel its wait without cancelling another preview's shared
    // scan. Completed catalogs contain managed metadata, not open file handles.
    public static Task<TextureResourceCatalog> OpenCachedAsync(TextureSources sources, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        string key = sources.Complete + "|" + sources.ModProfile + "|" + sources.ConfigurationPath + "|" + sources.ConfigurationHash + "|" +
            string.Join("|", sources.Archives.Select(path => path + ":" + (File.Exists(path) ? new FileInfo(path).Length + ":" + ResourceFileStamp.Read(path) : "missing"))) + "|" +
            string.Join("|", sources.OverrideFolders) + "|" + string.Join("|", sources.LooseFolders);
        Task<TextureResourceCatalog> pending;
        lock (CacheLock)
        {
            if (CachedCatalogs.TryGetValue(key, out var found) && !found.IsFaulted && !found.IsCanceled &&
                (!found.IsCompletedSuccessfully || found.Result.IsCurrent())) pending = found;
            else
            {
                if (CachedCatalogs.Count >= 2) CachedCatalogs.Remove(CachedCatalogs.Keys.First());
                // Lookups need names/provider priority, not decompression of every
                // unrelated texture. Preview reads and F5 keep full validation.
                pending = Task.Run(() => Open(sources, CancellationToken.None, validateHeaders: false));
                CachedCatalogs[key] = pending;
            }
        }
        return await pending.WaitAsync(token).ConfigureAwait(false);
    }, token);
}

internal static class PreviewModelArchiveCache
{
    private const long MaximumBytes = 256L * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly LinkedList<(string Key, ResArchiveDocument Archive)> Cache = new();
    private static long _bytes;

    internal static ResArchiveDocument Parse(string path, string name, Func<byte[]> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long length = new FileInfo(path).Length, stamp = ResourceFileStamp.Read(path);
        string key = path.ToUpperInvariant() + "|" + name.ToUpperInvariant() + "|" + length + "|" + stamp;
        lock (Sync)
        {
            token.ThrowIfCancellationRequested();
            for (var node = Cache.First; node is not null; node = node.Next)
                if (node.Value.Key == key) { var value = node.Value; Cache.Remove(node); Cache.AddLast(value); return value.Archive; }
            var document = ResArchiveDocument.Parse(name, read(), token);
            if (new FileInfo(path).Length != length || ResourceFileStamp.Read(path) != stamp)
                throw new IOException("Модель изменилась при чтении. Обновите предпросмотр.");
            if (document.Length <= MaximumBytes)
            {
                while (_bytes + document.Length > MaximumBytes && Cache.First is { } first) { _bytes -= first.Value.Archive.Length; Cache.RemoveFirst(); }
                Cache.AddLast((key, document)); _bytes += document.Length;
            }
            return document;
        }
    }
}
