using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace TreeSizeBetter.App.Shell;

/// <summary>Darstellung: dunkel, hell oder wie in Windows eingestellt.</summary>
public enum AppTheme
{
    Dunkel,
    Hell,
    System
}

/// <summary>
/// Schaltet die Farbpalette zur Laufzeit um. Die Oberflaeche holt alle Farben per
/// DynamicResource, deshalb genuegt es, das Paletten-Woerterbuch auszutauschen.
/// Die Wahl wird gespeichert und beim naechsten Start wieder angewandt.
/// </summary>
public static class ThemeManager
{
    private static readonly string SettingsPath = Core.Util.AppPaths.File("darstellung.json");
    private static bool _listening;

    public static AppTheme Theme { get; private set; } = AppTheme.Dunkel;

    /// <summary>Ob gerade die helle Palette aktiv ist (bei "System" nach Windows).</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Wird nach jedem Wechsel ausgeloest, z. B. fuer selbst gezeichnete Flaechen.</summary>
    public static event Action? Changed;

    public static void LoadAndApply()
    {
        AppTheme theme = AppTheme.Dunkel;
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<Stored>(File.ReadAllText(SettingsPath)) is { } stored &&
                Enum.TryParse(stored.Theme, out AppTheme parsed))
                theme = parsed;
        }
        catch
        {
            // Unlesbare Einstellung: dunkel starten.
        }
        Apply(theme, save: false);
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        Theme = theme;
        IsLight = theme == AppTheme.Hell || (theme == AppTheme.System && WindowsUsesLightTheme());

        if (theme == AppTheme.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri(IsLight ? "Themes/Palette.Light.xaml" : "Themes/Palette.Dark.xaml", UriKind.Relative)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        int index = merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Palette.") == true);
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette);

        foreach (Window window in Application.Current.Windows)
            WindowChromeTheme.ApplyCurrent(window);

        if (save) Save();
        Changed?.Invoke();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Theme != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (WindowsUsesLightTheme() != IsLight) Apply(AppTheme.System, save: false);
        });
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Stored { Theme = Theme.ToString() }));
        }
        catch
        {
            // Nicht speichern zu koennen ist kein Grund, die Bedienung zu stoeren.
        }
    }

    private sealed class Stored
    {
        public string? Theme { get; set; }
    }
}
