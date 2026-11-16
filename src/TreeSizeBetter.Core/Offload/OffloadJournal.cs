using System.Globalization;
using Microsoft.Data.Sqlite;
using TreeSizeBetter.Core.Localization;

namespace TreeSizeBetter.Core.Offload;

public enum RelocationStatus
{
    Kopieren,
    Pruefen,
    Verknuepfen,
    OriginalEntfernen,
    Fertig,
    Fehlgeschlagen,
    Abgebrochen,
    ZuruecknahmeLaeuft,
    Zurueckgenommen
}

public enum DeletionMode
{
    /// <summary>Original in den Papierkorb - zweites Netz, Platz erst nach dem Leeren frei.</summary>
    Papierkorb,

    /// <summary>Original endgueltig loeschen - Platz sofort frei, die gepruefte Kopie ist das einzige Exemplar.</summary>
    Endgueltig
}

public sealed record JournalEntry(
    long Id,
    string Source,
    string Target,
    long Bytes,
    long Files,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    RelocationStatus Status,
    DeletionMode Deletion,
    string Message,
    string? LeftoverPath)
{
    /// <summary>Endzustaende, die keine Aufmerksamkeit mehr brauchen.</summary>
    public bool IsSettled => Status is RelocationStatus.Fertig or RelocationStatus.Zurueckgenommen
        or RelocationStatus.Abgebrochen or RelocationStatus.Fehlgeschlagen;

    public bool CanUndo => Status == RelocationStatus.Fertig;

    /// <summary>Status in der Sprache der Oberflaeche.</summary>
    public string StatusText => Loc.T("status." + Status);

    /// <summary>Loeschart in der Sprache der Oberflaeche.</summary>
    public string DeletionText => Loc.T("deletion." + Deletion);
}

/// <summary>
/// Protokoll aller Umlagerungen in einer SQLite-Datei. Jeder Schritt wird
/// geschrieben, bevor er beginnt - bricht der Rechner mittendrin ab, steht
/// im Journal, wo die Daten gerade liegen.
/// </summary>
public sealed class OffloadJournal : IDisposable
{
    /// <summary>Dokumente\SizeShift\Verlauf\umlagerungen.db - sichtbar und mit dem Rest des Verlaufs zusammen.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Util.AppPaths.HistoryDirectory, "umlagerungen.db");

    /// <summary>Frueherer Ort im versteckten AppData-Ordner.</summary>
    private static string LegacyPath => Util.AppPaths.File("journal.db");

    private readonly SqliteConnection _connection;

    public OffloadJournal(string? path = null)
    {
        if (path is null)
        {
            path = DefaultPath;
            MigrateLegacy(path);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        DatabasePath = path;

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();

        Execute("""
            CREATE TABLE IF NOT EXISTS umlagerungen (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                quelle       TEXT    NOT NULL,
                ziel         TEXT    NOT NULL,
                bytes        INTEGER NOT NULL,
                dateien      INTEGER NOT NULL,
                begonnen     TEXT    NOT NULL,
                beendet      TEXT,
                status       TEXT    NOT NULL,
                loeschart    TEXT    NOT NULL,
                meldung      TEXT    NOT NULL DEFAULT '',
                rest_pfad    TEXT
            );
            """);
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Uebernimmt das alte Journal aus AppData beim ersten Start mit dem neuen Ort.
    /// Kopiert statt verschiebt: eine noch laufende alte Version haelt die Datei
    /// offen, und das Original bleibt als Sicherung liegen.
    /// </summary>
    private static void MigrateLegacy(string target)
    {
        try
        {
            string legacy = LegacyPath;
            if (System.IO.File.Exists(target) || !System.IO.File.Exists(legacy)) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacy, Mode = SqliteOpenMode.ReadOnly }.ToString());
            source.Open();
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString());
            destination.Open();
            source.BackupDatabase(destination);   // konsistente Kopie, auch wenn die Datei gerade offen ist
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Ohne Uebernahme startet ein leerer Verlauf; das alte Journal bleibt unangetastet.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Traegt eine bestehende Verknuepfung nachtraeglich ein, z. B. wenn ihr
    /// Verlaufseintrag fehlt. Danach laesst sie sich wie jede andere zuruecknehmen.
    /// </summary>
    public long Adopt(string link, string target, long bytes, long files)
    {
        long id = Begin(link, target, bytes, files, DeletionMode.Papierkorb);
        SetStatus(id, RelocationStatus.Fertig, Loc.T("journal.adopted"), finished: true);
        return id;
    }

    public long Begin(string source, string target, long bytes, long files, DeletionMode deletion)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO umlagerungen (quelle, ziel, bytes, dateien, begonnen, status, loeschart)
            VALUES ($q, $z, $b, $d, $t, $s, $l);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$q", source);
        command.Parameters.AddWithValue("$z", target);
        command.Parameters.AddWithValue("$b", bytes);
        command.Parameters.AddWithValue("$d", files);
        command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$s", RelocationStatus.Kopieren.ToString());
        command.Parameters.AddWithValue("$l", deletion.ToString());
        return (long)command.ExecuteScalar()!;
    }

    public void SetStatus(long id, RelocationStatus status, string message = "", bool finished = false)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = finished
            ? "UPDATE umlagerungen SET status = $s, meldung = $m, beendet = $t WHERE id = $id"
            : "UPDATE umlagerungen SET status = $s, meldung = $m WHERE id = $id";
        command.Parameters.AddWithValue("$s", status.ToString());
        command.Parameters.AddWithValue("$m", message);
        command.Parameters.AddWithValue("$id", id);
        if (finished) command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Merkt sich einen Rest (z. B. ein nicht ganz loeschbares Original), damit er nicht vergessen wird.</summary>
    public void SetLeftover(long id, string? path)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "UPDATE umlagerungen SET rest_pfad = $r WHERE id = $id";
        command.Parameters.AddWithValue("$r", (object?)path ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public JournalEntry? Get(long id) => Query("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<JournalEntry> All() => Query("ORDER BY id DESC");

    /// <summary>Eintraege, die mitten in einem Schritt stehen geblieben sind - Absturz, Stromausfall.</summary>
    public IReadOnlyList<JournalEntry> Unsettled() => All().Where(e => !e.IsSettled).ToList();

    private List<JournalEntry> Query(string where, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT id, quelle, ziel, bytes, dateien, begonnen, beendet, status, loeschart, meldung, rest_pfad " +
                              "FROM umlagerungen " + where;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var result = new List<JournalEntry>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new JournalEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Enum.Parse<RelocationStatus>(reader.GetString(7)),
                Enum.Parse<DeletionMode>(reader.GetString(8)),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return result;
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();   // sonst bleibt die Datei gesperrt
    }
}
