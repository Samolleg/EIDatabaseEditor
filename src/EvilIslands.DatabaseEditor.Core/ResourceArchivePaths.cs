namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Physical archive selection; configuration keys remain legacy RES names.</summary>
public static class ResourceArchivePaths
{
    public static string? FindLoose(string? configurationPath,string name)
    {
        if(configurationPath is null)return null;
        string root=Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        string candidate=Path.GetFullPath(Path.Combine(root,name));
        return candidate.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(candidate)&&!DatabaseSafetyPolicy.ContainsReparsePoint(candidate)?candidate:null;
    }
    public static bool IsArchive(string path) =>
        Path.GetExtension(path).Equals(".res", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase);

    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        var directory=Directory.GetParent(full);
        if(directory?.Name.Equals("Res",StringComparison.OrdinalIgnoreCase)==true&&directory.Parent?.Name.Equals("Base",StringComparison.OrdinalIgnoreCase)==true)return full;
        if (Path.GetExtension(full).Equals(".res", StringComparison.OrdinalIgnoreCase))
        {
            string pak = Path.ChangeExtension(full, ".pak");
            if (File.Exists(pak)) return pak;
        }
        return full;
    }

    public static IReadOnlyList<string> Order(IEnumerable<string> modPaths, IEnumerable<string>? basePaths = null)
    {
        string[] selected = modPaths.Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return selected.Where(p => Path.GetExtension(p).Equals(".pak", StringComparison.OrdinalIgnoreCase))
            .Concat(selected.Where(p => !Path.GetExtension(p).Equals(".pak", StringComparison.OrdinalIgnoreCase)))
            .Concat(basePaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> Enumerate(string directory) => Order(
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsArchive).Order(StringComparer.OrdinalIgnoreCase));
}

internal sealed class PakArchiveSet : IDisposable
{
    private readonly Dictionary<string, PakArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    public PakArchive Get(string path, CancellationToken token)
    {
        if (!_archives.TryGetValue(path, out var archive))
        {
            archive = PakArchive.Open(path, token);
            _archives.Add(path, archive);
        }
        return archive;
    }
    public void Dispose()
    {
        foreach (var archive in _archives.Values) archive.Dispose();
        _archives.Clear();
    }
}
