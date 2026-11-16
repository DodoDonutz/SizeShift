namespace TreeSizeBetter.Core.Util;

public static class FileTimeUtil
{
    public const long TicksPerDay = TimeSpan.TicksPerDay;

    /// <summary>Kleinster Wert, der als echter Zeitstempel durchgeht (1. Januar 1980).</summary>
    public static readonly long MinPlausible = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    public static long NowFileTime() => DateTime.UtcNow.ToFileTimeUtc();

    /// <summary>
    /// Zeitstempel in lokale Zeit, oder null wenn unbrauchbar. Laufwerkswurzeln,
    /// virtuelle Dateisysteme und manche Netzfreigaben liefern 0 oder Werte aus
    /// dem Jahr 1601, die man nicht anzeigen will.
    /// </summary>
    public static DateTime? ToLocal(long fileTime)
    {
        if (fileTime < MinPlausible) return null;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Tage zwischen Zeitstempel und jetzt, oder null wenn der Zeitstempel unbrauchbar ist.</summary>
    public static double? DaysSince(long fileTime, long nowFileTime)
    {
        if (fileTime < MinPlausible) return null;
        long delta = nowFileTime - fileTime;
        if (delta < 0) delta = 0;                 // Dateien mit Datum in der Zukunft gelten als frisch
        return (double)delta / TicksPerDay;
    }

    /// <summary>Tagesnummer fuer Histogramme, ohne DateTime-Umwandlung.</summary>
    public static long ToDayNumber(long fileTime) => fileTime / TicksPerDay;
}
