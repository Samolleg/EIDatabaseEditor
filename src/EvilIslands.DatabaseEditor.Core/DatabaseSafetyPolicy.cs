namespace EvilIslands.DatabaseEditor.Core;

public static class DatabaseSafetyPolicy
{
    public static bool ContainsReparsePoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directoryPath = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        var directory = string.IsNullOrWhiteSpace(directoryPath) ? null : new DirectoryInfo(directoryPath);
        while (directory is not null)
        {
            directory.Refresh();
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    public static bool IsLikelyGameInstallationPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}Starter{Path.DirectorySeparatorChar}MODS{Path.DirectorySeparatorChar}";
        if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directoryPath = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        var directory = string.IsNullOrWhiteSpace(directoryPath) ? null : new DirectoryInfo(directoryPath);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "game.exe")) ||
                File.Exists(Path.Combine(directory.FullName, "Game.exe")))
            {
                return true;
            }
        }

        return false;
    }
}
