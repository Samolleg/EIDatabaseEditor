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

    public static string? ResolveRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return path;
        if (Path.GetExtension(path).Equals(".res", StringComparison.OrdinalIgnoreCase))
        {
            string pak = Path.ChangeExtension(path, ".pak");
            if (File.Exists(pak)) return pak;
        }
        return path;
    }

    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
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
            .Concat((basePaths ?? []).Select(Resolve)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> Enumerate(string directory) => Order(
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsArchive).Order(StringComparer.OrdinalIgnoreCase));

    internal static bool IsBaseFigure(string? path, string? gameFolder)
    {
        if (path is null || !IsArchive(path) || !Path.GetFileNameWithoutExtension(path).Equals("figures", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Directory.GetParent(Path.GetFullPath(path));
        return directory?.Name.Equals("Res", StringComparison.OrdinalIgnoreCase) == true &&
            (directory.Parent?.Name.Equals("Base", StringComparison.OrdinalIgnoreCase) == true ||
             gameFolder is not null && Path.GetFullPath(path).Equals(Resolve(Path.Combine(gameFolder, "Res", "figures.res")), StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class PakArchiveSet : IDisposable
{
    private readonly Dictionary<string, ReadOnlyPakArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    public ReadOnlyPakArchive Get(string path, CancellationToken token)
    {
        if (!_archives.TryGetValue(path, out var archive))
        {
            archive = new ReadOnlyPakArchive(path, token);
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

// Preview/validation owns a read guard, not an editable disk snapshot. Keep the
// guard until the request ends; compute a save baseline only when ANM needs it.
public sealed class ReadOnlyPakArchive : IDisposable
{
    private readonly FileStream _guard;
    private readonly NativePakReader _reader;
    private string? _sha256;
    public ReadOnlyPakArchive(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { _reader = NativePakReader.Open(path, token); }
        catch { _guard.Dispose(); throw; }
    }
    public IReadOnlyList<ResEntry> Entries => _reader.Entries;
    public IReadOnlyList<string> Deleted => _reader.Deleted;
    public string Sha256 => _sha256 ??= Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_guard));
    public byte[] Read(string name, CancellationToken token = default) => _reader.ReadPrefix(name, int.MaxValue, token);
    public void Dispose() { _reader.Dispose(); _guard.Dispose(); }
}
