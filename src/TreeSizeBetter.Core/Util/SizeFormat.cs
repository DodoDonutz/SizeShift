using System.Globalization;
using TreeSizeBetter.Core.Localization;

namespace TreeSizeBetter.Core.Util;

public static class SizeFormat
{
    /// <summary>Einheiten der aktiven Sprache (Franzoesisch: o, Ko, Mo ...).</summary>
    /// Zwischengespeichert, weil Baum und Tabelle das hunderttausendfach abfragen.
    private static string[] Units
    {
        get
        {
            var cached = _units;
            if (cached.Language == Loc.Language) return cached.Units;
            string[] units = Loc.T("unit.sizes").Split(' ');
            _units = (Loc.Language, units);
            return units;
        }
    }

    private static (string? Language, string[] Units) _units = (null, Array.Empty<string>());

    /// <summary>
    /// Groesse in der Einheit, die am besten passt. Binaer (1 KB = 1024 B), wie
    /// der Windows-Explorer, damit die Zahlen nicht von denen im Explorer abweichen.
    /// </summary>
    public static string Bytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) return "-";
        string[] units = Units;
        if (bytes < 1024) return bytes.ToString(culture) + " " + units[0];

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Unter 10 zwei Nachkommastellen, unter 100 eine, darueber keine -
        // so bleiben die Spalten gleich breit und lesbar.
        string format = value < 10 ? "F2" : value < 100 ? "F1" : "F0";
        return value.ToString(format, culture) + " " + units[unit];
    }

    public static string Days(double? days, CultureInfo? culture = null)
    {
        if (days is null) return "-";
        culture ??= CultureInfo.CurrentCulture;
        double d = days.Value;

        if (d < 1) return Loc.T("time.today");
        if (d < 2) return Loc.T("time.yesterday");
        if (d < 60) return Loc.F("time.days", ((int)d).ToString(culture));
        if (d < 730) return Loc.F("time.months", (d / 30.44).ToString("F0", culture));
        return Loc.F("time.years", (d / 365.25).ToString("F1", culture));
    }

    /// <summary>
    /// Zeitabstand als "vor ..." in natuerlichem Deutsch: "heute", "gestern",
    /// "vor 5 Tagen", "vor einem Monat", "vor 2,3 Jahren".
    /// </summary>
    public static string Ago(double? days, CultureInfo? culture = null)
    {
        if (days is null) return "–";
        culture ??= CultureInfo.CurrentCulture;
        double d = days.Value;

        if (d < 1) return Loc.T("time.today");
        if (d < 2) return Loc.T("time.yesterday");
        if (d < 14) return Loc.F("ago.days", (int)d);
        if (d < 60)
        {
            int weeks = (int)Math.Round(d / 7);
            return weeks <= 1 ? Loc.T("ago.week") : Loc.F("ago.weeks", weeks);
        }
        if (d < 365)
        {
            int months = (int)Math.Round(d / 30.44);
            return months <= 1 ? Loc.T("ago.month") : Loc.F("ago.months", months);
        }
        double years = d / 365.25;
        if (years < 1.05) return Loc.T("ago.year");
        return Loc.F("ago.years", years.ToString("0.#", culture));
    }

    public static string Count(long value, CultureInfo? culture = null)
        => value.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    public static string Percent(double fraction, CultureInfo? culture = null)
        => (fraction * 100).ToString(fraction < 0.1 ? "F1" : "F0", culture ?? CultureInfo.CurrentCulture) + Loc.T("unit.percentSuffix");
}
