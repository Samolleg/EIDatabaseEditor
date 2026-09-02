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

    public EditorLayoutState GetLayout()
    {
        EditorPreferences settings = LoadSettings();
        EditorLayoutState layout = NormalizeLayout(settings.Layout ?? new EditorLayoutState());
        if (settings.SoundPaneHeight is double legacyHeight &&
            IsValidSoundPaneHeight(legacyHeight) &&
            settings.Layout is null)
        {
            layout.DetailsPaneHeight = legacyHeight;
        }

        return layout;
    }

    public void RememberLayout(EditorLayoutState layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        EditorPreferences settings = LoadSettings();
        settings.Layout = NormalizeLayout(layout);
        settings.SoundPaneHeight = settings.Layout.DetailsPaneHeight;
        SaveSettings(settings);
    }

    public void ResetLayout()
    {
        EditorPreferences settings = LoadSettings();
        settings.Layout = new EditorLayoutState();
        settings.SoundPaneHeight = settings.Layout.DetailsPaneHeight;
        SaveSettings(settings);
    }

    private static EditorLayoutState NormalizeLayout(EditorLayoutState source) => new()
    {
        SidebarWidth = Clamp(source.SidebarWidth, 220, 520, 300),
        DetailsPaneHeight = Clamp(source.DetailsPaneHeight, 180, 900, 360),
        LocalizationPaneRatio = Clamp(source.LocalizationPaneRatio, 0.2, 0.8, 0.45),
        FormulaBarHeight = Clamp(source.FormulaBarHeight, 60, 240, 60),
        TextsFormulaBarHeight = Clamp(source.TextsFormulaBarHeight, 70, 300, 86),
        BriefingScenesWidth = Clamp(source.BriefingScenesWidth, 170, 520, 220),
        BriefingTimelineWidth = Clamp(source.BriefingTimelineWidth, 300, 900, 460),
        ValidationCategoriesWidth = Clamp(source.ValidationCategoriesWidth, 210, 420, 260),
        ValidationDetailsWidth = Clamp(source.ValidationDetailsWidth, 320, 760, 430),
        MainWindowWidth = Clamp(source.MainWindowWidth, 1180, 5000, 1440),
        MainWindowHeight = Clamp(source.MainWindowHeight, 680, 3000, 900),
        MainWindowMaximized = source.MainWindowMaximized,
        LongTextEditorWidth = Clamp(source.LongTextEditorWidth, 640, 3000, 940),
        LongTextEditorHeight = Clamp(source.LongTextEditorHeight, 440, 2200, 680),
        LongTextEditorMaximized = source.LongTextEditorMaximized,
        DetailsPaneDetached = source.DetailsPaneDetached,
        DetailsWindowWidth = Clamp(source.DetailsWindowWidth, 720, 3000, 1080),
        DetailsWindowHeight = Clamp(source.DetailsWindowHeight, 420, 2200, 620),
        DetailsWindowMaximized = source.DetailsWindowMaximized,
        ResManagerWindowWidth = Clamp(source.ResManagerWindowWidth, 1040, 4000, 1460),
        ResManagerWindowHeight = Clamp(source.ResManagerWindowHeight, 640, 2600, 860),
        ResManagerWindowMaximized = source.ResManagerWindowMaximized,
        ResManagerFoldersWidth = Clamp(source.ResManagerFoldersWidth, 190, 520, 250),
        ResManagerDetailsWidth = Clamp(source.ResManagerDetailsWidth, 240, 620, 310),
        ResManagerDetailsVisible = source.ResManagerDetailsVisible,
        ResManagerChangesExpanded = source.ResManagerChangesExpanded,
    };

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

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

        public EditorLayoutState? Layout { get; set; }
    }
}

internal sealed class EditorLayoutState
{
    public double SidebarWidth { get; set; } = 300;

    public double DetailsPaneHeight { get; set; } = 360;

    public double LocalizationPaneRatio { get; set; } = 0.45;

    public double FormulaBarHeight { get; set; } = 60;

    public double TextsFormulaBarHeight { get; set; } = 86;

    public double BriefingScenesWidth { get; set; } = 220;

    public double BriefingTimelineWidth { get; set; } = 460;

    public double ValidationCategoriesWidth { get; set; } = 260;

    public double ValidationDetailsWidth { get; set; } = 430;

    public double MainWindowWidth { get; set; } = 1440;

    public double MainWindowHeight { get; set; } = 900;

    public bool MainWindowMaximized { get; set; }

    public double LongTextEditorWidth { get; set; } = 940;

    public double LongTextEditorHeight { get; set; } = 680;

    public bool LongTextEditorMaximized { get; set; }

    public bool DetailsPaneDetached { get; set; }

    public double DetailsWindowWidth { get; set; } = 1080;

    public double DetailsWindowHeight { get; set; } = 620;

    public bool DetailsWindowMaximized { get; set; }

    public double ResManagerWindowWidth { get; set; } = 1460;

    public double ResManagerWindowHeight { get; set; } = 860;

    public bool ResManagerWindowMaximized { get; set; }

    public double ResManagerFoldersWidth { get; set; } = 250;

    public double ResManagerDetailsWidth { get; set; } = 310;

    public bool ResManagerDetailsVisible { get; set; } = true;

    public bool ResManagerChangesExpanded { get; set; }
}
