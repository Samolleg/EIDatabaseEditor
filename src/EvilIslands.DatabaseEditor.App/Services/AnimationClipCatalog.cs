using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed record AnimationClipChoice(string Id, string RussianName, string AdbStatus)
{
    public string Label => $"{Id} · {AdbStatus} — {(string.IsNullOrWhiteSpace(RussianName) ? "название не задано" : RussianName)}";
    public bool Matches(string query) => Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        RussianName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (query.Any(char.IsDigit) && AdbStatus.Contains(query, StringComparison.OrdinalIgnoreCase));
}

internal static class AnimationClipCatalog
{
    public static IReadOnlyList<AnimationClipChoice> Build(IEnumerable<string> clips, AnimationDatabase? database,
        IReadOnlyDictionary<string, string> russianNames)
    {
        var indices = database?.Entries.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Index).ToArray(), StringComparer.OrdinalIgnoreCase);
        return clips.Select(id => new AnimationClipChoice(id, russianNames.GetValueOrDefault(id, ""),
            database is null ? "ADB недоступен" : indices!.TryGetValue(id, out var numbers)
                ? "ADB №" + string.Join(", №", numbers) : "не прописана в ADB")).ToArray();
    }

    public static IReadOnlyList<AnimationClipChoice> Search(IEnumerable<AnimationClipChoice> clips, string text)
    {
        string query = text.Trim();
        return query.Length == 0 ? clips.ToArray() : clips.Where(clip => clip.Matches(query)).ToArray();
    }
}
