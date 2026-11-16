namespace TreeSizeBetter.Core.Util;

/// <summary>
/// Name der App und Ort ihrer Daten (Journal der Umlagerungen, zuletzt
/// untersuchte Pfade, Fehlerprotokoll) - an genau einer Stelle.
///
/// Beim ersten Start unter neuem Namen wird der alte Datenordner uebernommen.
/// Das ist nicht kosmetisch: im Journal steht, welche Ordner umgelagert sind und
/// wohin. Ginge es verloren, liesse sich keine fruehere Umlagerung mehr
/// zuruecknehmen.
/// </summary>
public static class AppPaths
{
    public const string AppName = "SizeShift";

    /// <summary>Fruehere Namen der App, deren Datenordner uebernommen werden.</summary>
    private static readonly string[] FormerNames = { "TreeSizeBetter" };

    private static readonly Lazy<string> DataDirectoryLazy = new(Prepare);

    public static string DataDirectory => DataDirectoryLazy.Value;

    public static string File(string name) => Path.Combine(DataDirectory, name);

    // ----------------------------------------------------------------- Dokumente

    private static readonly Lazy<string> DocumentsDirectoryLazy = new(PrepareDocuments);

    /// <summary>
    /// Sichtbarer Ordner in "Dokumente" fuer alles, was man wiederfinden will:
    /// Verlauf der Umlagerungen, Protokoll, Exporte, KI-Berichte. Einstellungen
    /// bleiben im versteckten AppData-Ordner.
    /// </summary>
    public static string DocumentsDirectory => DocumentsDirectoryLazy.Value;

    /// <summary>Verlauf: Umlagerungs-Datenbank und lesbares Protokoll.</summary>
    public static string HistoryDirectory => Sub("Verlauf");

    /// <summary>Excel-, CSV- und JSON-Exporte.</summary>
    public static string ExportDirectory => Sub("Exporte");

    /// <summary>Kompakte Berichte zum Einfuegen in einen KI-Chat.</summary>
    public static string AiReportDirectory => Sub("KI-Berichte");

    private static string Sub(string name)
    {
        string path = Path.Combine(DocumentsDirectory, name);
        try { Directory.CreateDirectory(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return path;
    }

    private static string PrepareDocuments()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
        {
            string path = Path.Combine(documents, AppName);
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dokumente nicht erreichbar (z. B. Laufwerk fehlt): im AppData-Ordner weiterarbeiten.
            }
        }
        return DataDirectory;
    }

    private static string Prepare()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(root, AppName);

        if (!Directory.Exists(current))
        {
            foreach (string former in FormerNames)
            {
                string old = Path.Combine(root, former);
                if (!Directory.Exists(old)) continue;
                try
                {
                    Directory.Move(old, current);
                }
                catch (IOException)
                {
                    // Alte Version laeuft noch und haelt eine Datei offen: kopieren statt verschieben.
                    CopyMissing(old, current);
                }
                catch (UnauthorizedAccessException)
                {
                    CopyMissing(old, current);
                }
                break;
            }
        }

        Directory.CreateDirectory(current);
        return current;
    }

    private static void CopyMissing(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            if (!System.IO.File.Exists(target))
            {
                try { System.IO.File.Copy(file, target); } catch (IOException) { }
            }
        }
    }
}
