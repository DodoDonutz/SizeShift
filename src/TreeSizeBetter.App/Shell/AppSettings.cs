using System.IO;
using System.Text.Json;

namespace TreeSizeBetter.App.Shell;

/// <summary>
/// Kleine Einstellungen der Oberflaeche, die einen Neustart ueberleben:
/// Sprache und ob das Kacheldiagramm gezeigt wird. Liegt wie die Darstellung
/// als JSON im AppData-Ordner (einstellungen.json).
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsPath = Core.Util.AppPaths.File("einstellungen.json");

    private static Stored _stored = new();

    /// <summary>Gewaehlte Sprache ("de", "en", ...) oder null fuer "wie Windows".</summary>
    public static string? Language
    {
        get => _stored.Language;
        set
        {
            _stored.Language = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    /// <summary>Kacheldiagramm unter der Analyse zeigen.</summary>
    public static bool ShowTreemap
    {
        get => _stored.ShowTreemap;
        set
        {
            _stored.ShowTreemap = value;
            Save();
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<Stored>(File.ReadAllText(SettingsPath)) is { } stored)
                _stored = stored;
        }
        catch
        {
            // Unlesbare Einstellungen: mit den Vorgaben starten.
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_stored));
        }
        catch
        {
            // Nicht speichern zu koennen ist kein Grund, die Bedienung zu stoeren.
        }
    }

    private sealed class Stored
    {
        public string? Language { get; set; }
        public bool ShowTreemap { get; set; } = true;
    }
}
