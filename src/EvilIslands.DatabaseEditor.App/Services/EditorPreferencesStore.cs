using System.IO;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class EditorPreferencesStore
{
    private const double MinimumStoredColumnWidth = 32;
    private const double MaximumStoredColumnWidth = 2000;
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
        EditorPreferences settings = LoadSettings();
        return Directory.Exists(settings.LastDatabaseDirectory)
            ? Path.GetFullPath(settings.LastDatabaseDirectory)
            : null;
    }

    public void RememberDatabasePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        EditorPreferences settings = LoadSettings();
        if (string.Equals(settings.LastDatabaseDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        settings.LastDatabaseDirectory = directory;
        SaveSettings(settings);
    }

    public double? GetColumnWidth(string tableKey, string columnIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnIdentity);
        EditorPreferences settings = LoadSettings();
        if (!settings.ColumnWidths.TryGetValue(tableKey, out Dictionary<string, double>? tableWidths) ||
            !tableWidths.TryGetValue(columnIdentity, out double width) ||
            !IsValidColumnWidth(width))
        {
            return null;
        }

        return width;
    }

    public void RememberColumnWidths(
        string tableKey,
        IReadOnlyDictionary<string, double> columnWidths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableKey);
        ArgumentNullException.ThrowIfNull(columnWidths);
        EditorPreferences settings = LoadSettings();
        if (!settings.ColumnWidths.TryGetValue(tableKey, out Dictionary<string, double>? tableWidths))
        {
            tableWidths = new Dictionary<string, double>(StringComparer.Ordinal);
            settings.ColumnWidths[tableKey] = tableWidths;
        }

        var changed = false;
        foreach ((string identity, double value) in columnWidths)
        {
            if (string.IsNullOrWhiteSpace(identity) || !IsValidColumnWidth(value))
            {
                continue;
            }

            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (tableWidths.TryGetValue(identity, out double existing) &&
                Math.Abs(existing - rounded) < 0.05)
            {
                continue;
            }

            tableWidths[identity] = rounded;
            changed = true;
        }

        if (changed)
        {
            SaveSettings(settings);
        }
    }

    private EditorPreferences LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new EditorPreferences();
            }

            return JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(_settingsPath)) ??
                   new EditorPreferences();
        }
        catch (IOException)
        {
            return new EditorPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new EditorPreferences();
        }
        catch (JsonException)
        {
            return new EditorPreferences();
        }
    }

    private void SaveSettings(EditorPreferences settings)
    {
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
                    settings,
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
            // Preferences must never prevent opening or closing a database.
        }
        catch (UnauthorizedAccessException)
        {
            // Preferences must never prevent opening or closing a database.
        }
    }

    private static bool IsValidColumnWidth(double width) =>
        double.IsFinite(width) && width is >= MinimumStoredColumnWidth and <= MaximumStoredColumnWidth;

    private sealed class EditorPreferences
    {
        public string? LastDatabaseDirectory { get; set; }

        public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = [];
    }
}
