using System.Globalization;

namespace EvilIslands.DatabaseEditor.Verification;

internal sealed class SafeStagingArea
{
    private SafeStagingArea(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public static SafeStagingArea Create()
    {
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string verificationRoot = EnsureChildPath(
            tempRoot,
            Path.Combine(tempRoot, "EvilIslands.DatabaseEditor.Verification"));

        Directory.CreateDirectory(verificationRoot);

        string runName = string.Create(
            CultureInfo.InvariantCulture,
            $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string runRoot = EnsureChildPath(verificationRoot, Path.Combine(verificationRoot, runName));
        Directory.CreateDirectory(runRoot);

        return new SafeStagingArea(runRoot);
    }

    public string GetNewOutputPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Ожидалось безопасное имя файла без каталогов.", nameof(fileName));
        }

        string outputPath = EnsureChildPath(RootPath, Path.Combine(RootPath, fileName));
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"Выходной путь уже существует: {outputPath}");
        }

        return outputPath;
    }

    public void AssertOutputPath(string outputPath, IReadOnlyCollection<string> sourcePaths)
    {
        string canonicalOutput = EnsureChildPath(RootPath, outputPath);

        foreach (string sourcePath in sourcePaths)
        {
            if (string.Equals(canonicalOutput, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Выходной путь совпал с исходным файлом.");
            }
        }
    }

    private static string EnsureChildPath(string parentPath, string candidatePath)
    {
        string canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        string canonicalCandidate = Path.GetFullPath(candidatePath);
        string relative = Path.GetRelativePath(canonicalParent, canonicalCandidate);

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Путь выходит за пределы безопасного staging-каталога: {canonicalCandidate}");
        }

        return canonicalCandidate;
    }
}
