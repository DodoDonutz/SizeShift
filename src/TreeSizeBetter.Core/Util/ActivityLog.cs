using System.Globalization;
using System.Text;

namespace TreeSizeBetter.Core.Util;

/// <summary>
/// Lesbares Protokoll aller Aktionen (Scans, Umlagerungen, Zuruecknahmen,
/// Aufraeumen, Exporte) in Dokumente\SizeShift\Verlauf\Protokoll.txt.
/// Eine Zeile pro Ereignis, damit man auch ohne SizeShift nachlesen kann,
/// was wann passiert ist. Fehler beim Schreiben werden geschluckt - das
/// Protokoll darf nie eine Aktion verhindern.
/// </summary>
public static class ActivityLog
{
    private static readonly object Gate = new();

    /// <summary>Fuer Tests umstellbar; null = Standardort.</summary>
    public static string? PathOverride { get; set; }

    public static string FilePath => PathOverride ?? System.IO.Path.Combine(AppPaths.HistoryDirectory, "Protokoll.txt");

    /// <summary>App und Kommandozeile schalten es ein; Tests lassen es aus, damit sie nichts in Dokumente schreiben.</summary>
    public static bool Enabled { get; set; }

    public static void Write(string category, string text)
    {
        if (!Enabled) return;
        try
        {
            string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {category,-14}  {text.Replace(Environment.NewLine, " | ").Replace("\n", " | ")}{Environment.NewLine}";
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
