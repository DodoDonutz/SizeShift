using CommunityToolkit.Mvvm.ComponentModel;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Wonach sich Balken, Zahl und Sortierung im Baum richten.</summary>
public enum DisplayMode
{
    /// <summary>Logische Groesse, also die Summe der Dateilaengen.</summary>
    Size,

    /// <summary>Tatsaechlich belegter Platz auf der Platte.</summary>
    SizeOnDisk,

    /// <summary>Anzahl Dateien - findet Ordner, die nicht gross sind, aber den Scan ausbremsen.</summary>
    FileCount,

    /// <summary>Anteil am Elternordner in Prozent.</summary>
    Percent
}

/// <summary>
/// Anzeigeeinstellungen, die sich alle Baumknoten teilen. Bewusst ein gemeinsames
/// Objekt statt einer Kopie pro Knoten: beim Umschalten muessen sonst
/// zehntausende Knoten einzeln nachgezogen werden.
/// </summary>
public sealed class DisplaySettings : ObservableObject
{
    private DisplayMode _mode = DisplayMode.Size;

    public DisplayMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public static string Describe(DisplayMode mode) => mode switch
    {
        DisplayMode.Size => Loc.T("mode.size"),
        DisplayMode.SizeOnDisk => Loc.T("mode.onDisk"),
        DisplayMode.FileCount => Loc.T("mode.files"),
        DisplayMode.Percent => Loc.T("mode.percent"),
        _ => Loc.T("mode.size")
    };
}

/// <summary>Die Hauptbereiche oben im Fenster.</summary>
public enum AppArea
{
    Analyse,
    Aufraeumen,
    Umlagern,
    System
}
