using System.IO;
using System.Text;
using System.Text.Json;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed record KimodoDraftPreferences
{
    public string Model { get; init; } = "";
    public string Template { get; init; } = "";
    public string Prompt { get; init; } = "A person dances in place";
    public string Avoid { get; init; } = "";
    public string KinematicConstraintsJson { get; init; } = "";
    public string Frames { get; init; } = "60";
    public string Steps { get; init; } = "20";
    public string Seed { get; init; } = "0";
    public string LastUsedSeed { get; init; } = "";
    public string Title { get; init; } = "Черновик движения";
    public string EndpointReference { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Motion { get; init; } = "";
    public string Text { get; init; } = "";
    public int Playback { get; init; }
    public bool ReplaceCurrent { get; init; }
    public bool HasRun { get; init; }

    // The bundled Kimodo CLI accepts one prompt. This is an avoidance instruction,
    // not an independent negative-conditioning channel.
    internal static string EffectivePrompt(string prompt, string avoid) => string.IsNullOrWhiteSpace(avoid)
        ? prompt.Trim() : prompt.Trim() + "\nAvoid: " + avoid.Trim();
}

internal sealed class KimodoDraftPreferencesStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Редактор БД от Атамана", "kimodo-draft.json");
    internal KimodoDraftPreferences Load()
    {
        if (!File.Exists(_path)) return new();
        try { return JsonSerializer.Deserialize<KimodoDraftPreferences>(File.ReadAllBytes(_path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal void Save(KimodoDraftPreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value), new UTF8Encoding(false));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
