using System.IO;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed class EditorPreferencesStore
{
    private const double MinimumStoredColumnWidth = 32;
    private const double MaximumStoredColumnWidth = 2000;
    private const double MinimumInterfaceScale = 1.0;
    private const double MaximumInterfaceScale = 1.5;
    private const double MinimumSoundPaneHeight = 180;
    private const double MaximumSoundPaneHeight = 900;
    private readonly string _settingsPath;

    public EditorPreferencesStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? ResolveDefaultSettingsPath();
    }

    private static string ResolveDefaultSettingsPath()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string settingsPath = Path.Combine(localApplicationData, "Редактор БД от Атамана", "settings.json");
        string legacySettingsPath = Path.Combine(localApplicationData, "EI Database Studio", "settings.json");
        if (File.Exists(settingsPath) || !File.Exists(legacySettingsPath))
        {
            return settingsPath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.Copy(legacySettingsPath, settingsPath, overwrite: false);
        }
        catch (IOException)
        {
            // A failed preference migration must not prevent application startup.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed preference migration must not prevent application startup.
        }

        return settingsPath;
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

    public double? GetInterfaceScale()
    {
        double? scale = LoadSettings().InterfaceScale;
        return scale is double value && IsValidInterfaceScale(value) ? value : null;
    }

    public void RememberInterfaceScale(double scale)
    {
        if (!IsValidInterfaceScale(scale))
        {
            return;
        }

        EditorPreferences settings = LoadSettings();
        double rounded = Math.Round(scale, 2, MidpointRounding.AwayFromZero);
        if (settings.InterfaceScale is double existing && Math.Abs(existing - rounded) < 0.005)
        {
            return;
        }

        settings.InterfaceScale = rounded;
        SaveSettings(settings);
    }

    public double? GetSoundPaneHeight()
    {
        double? height = LoadSettings().SoundPaneHeight;
        return height is double value && IsValidSoundPaneHeight(value) ? value : null;
    }

    public void RememberSoundPaneHeight(double height)
    {
        if (!IsValidSoundPaneHeight(height))
        {
            return;
        }

        EditorPreferences settings = LoadSettings();
        double rounded = Math.Round(height, 1, MidpointRounding.AwayFromZero);
        if (settings.SoundPaneHeight is double existing && Math.Abs(existing - rounded) < 0.05)
        {
            return;
        }

        settings.SoundPaneHeight = rounded;
        SaveSettings(settings);
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

    private static bool IsValidInterfaceScale(double scale) =>
        double.IsFinite(scale) && scale is >= MinimumInterfaceScale and <= MaximumInterfaceScale;

    private static bool IsValidSoundPaneHeight(double height) =>
        double.IsFinite(height) && height is >= MinimumSoundPaneHeight and <= MaximumSoundPaneHeight;

    private sealed class EditorPreferences
    {
        public string? LastDatabaseDirectory { get; set; }

        public double? InterfaceScale { get; set; }

        public double? SoundPaneHeight { get; set; }

        public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = [];
    }
}
