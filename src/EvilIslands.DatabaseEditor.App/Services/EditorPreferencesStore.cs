using System.IO;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class EditorPreferencesStore
{
    private readonly string _settingsPath;

    public EditorPreferencesStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EI Database Studio",
            "settings.json");
    }

    public string? GetLastDatabaseDirectory()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<EditorPreferences>(
                File.ReadAllText(_settingsPath));
            return Directory.Exists(settings?.LastDatabaseDirectory)
                ? Path.GetFullPath(settings.LastDatabaseDirectory)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void RememberDatabasePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var settingsDirectory = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrWhiteSpace(settingsDirectory))
            {
                return;
            }

            Directory.CreateDirectory(settingsDirectory);
            var tempPath = $"{_settingsPath}.partial-{Guid.NewGuid():N}";
            try
            {
                var json = JsonSerializer.Serialize(
                    new EditorPreferences(directory),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (IOException)
        {
            // Preferences must never prevent opening a database.
        }
        catch (UnauthorizedAccessException)
        {
            // Preferences must never prevent opening a database.
        }
    }

    private sealed record EditorPreferences(string LastDatabaseDirectory);
}
