using System.Collections.Concurrent;
using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed record AnimationGlobalHit(string Model, string Clip, string AdbStatus)
{
    public string Label => $"{Clip} · модель {Model} · {AdbStatus}";
}

internal sealed record AnimationGlobalCatalog(IReadOnlyList<AnimationGlobalHit> Hits, IReadOnlyList<string> Issues)
{
    public IReadOnlyList<AnimationGlobalHit> Search(string query, string currentModel) => Hits
        .Where(hit => !hit.Model.Equals(currentModel, StringComparison.OrdinalIgnoreCase) &&
            (hit.Clip.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             hit.Model.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (query.Any(char.IsDigit) && hit.AdbStatus.Contains(query, StringComparison.OrdinalIgnoreCase)))).Take(20).ToArray();
}

internal static class AnimationGlobalClipIndex
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<AnimationGlobalCatalog>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<AnimationGlobalCatalog> OpenAsync(string directory) => Cache.GetOrAdd(directory,
        path => new Lazy<Task<AnimationGlobalCatalog>>(() => Task.Run(() => Build(path)))).Value;

    public static void Invalidate(string directory) => Cache.TryRemove(directory, out _);

    private static AnimationGlobalCatalog Build(string directory)
    {
        var animations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var registered = new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.res"))
        {
            IReadOnlyList<ResEntry> entries;
            try { entries = ResFileManagerArchive.ReadCatalog(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { issues.Add(Path.GetFileName(path) + ": " + ex.Message); continue; }
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var entry in entries.Where(entry => entry.Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith(".adb", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Size <= 0 || entry.Size > 128 * 1024 * 1024) { issues.Add(entry.Name + ": неверный размер"); continue; }
                try
                {
                    stream.Position = entry.Offset;
                    byte[] bytes = new byte[entry.Size];
                    stream.ReadExactly(bytes);
                    string model = Path.GetFileNameWithoutExtension(entry.Name);
                    if (entry.Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    {
                        var set = new FigureAnimationSet(model, bytes, new Dictionary<string, string>(),
                            new Dictionary<string, System.Numerics.Vector3>(), new Dictionary<string, System.Numerics.Vector3>());
                        if (!animations.TryGetValue(model, out var names)) animations[model] = names = new(StringComparer.OrdinalIgnoreCase);
                        names.UnionWith(set.Names);
                    }
                    else
                    {
                        var database = AnimationDatabase.Parse(bytes);
                        if (!registered.TryGetValue(model, out var names)) registered[model] = names = new(StringComparer.OrdinalIgnoreCase);
                        foreach (var record in database.Entries)
                        {
                            if (!names.TryGetValue(record.Name, out var indices)) names[record.Name] = indices = [];
                            if (!indices.Contains(record.Index)) indices.Add(record.Index);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or DatabaseFormatException or UnauthorizedAccessException)
                { issues.Add(entry.Name + ": " + ex.Message); }
            }
        }
        var hits = animations.SelectMany(model => model.Value.Select(clip =>
        {
            string status = registered.TryGetValue(model.Key, out var names) && names.TryGetValue(clip, out var indices)
                ? "ADB №" + string.Join(", №", indices) : "не прописана в ADB";
            return new AnimationGlobalHit(model.Key, clip, status);
        })).OrderBy(hit => hit.Clip, StringComparer.OrdinalIgnoreCase).ThenBy(hit => hit.Model, StringComparer.OrdinalIgnoreCase).ToArray();
        return new AnimationGlobalCatalog(hits, issues);
    }
}
