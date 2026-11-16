using System.Diagnostics;
using System.Text.Json;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Scanning;

namespace TreeSizeBetter.Core.Export;

/// <summary>
/// Maschinenlesbarer Export. Anders als die CSV-Datei bewusst in invarianter
/// Schreibweise - Punkt als Dezimaltrenner, Datum nach ISO 8601 - damit jedes
/// Werkzeug die Zahlen gleich liest, egal mit welchen Laendereinstellungen.
/// </summary>
public static class JsonExporter
{
    public static ExportSummary Write(
        string path,
        ExportContext context,
        bool indented = false,
        IReadOnlyList<ExportColumn>? columns = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        columns ??= ExportColumns.All;
        var sw = Stopwatch.StartNew();

        QueryResult query = context.Query(ct: ct);
        EntryView view = context.NewView();
        ScanResult result = context.Result;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented });

        writer.WriteStartObject();

        writer.WriteStartObject("meta");
        writer.WriteString("werkzeug", "SizeShift");
        writer.WriteString("erzeugt", DateTime.Now.ToString("o"));
        writer.WriteString("wurzel", context.Tree.RootPath);
        writer.WriteString("bereich", context.Tree.GetFullPath(context.RootIndex));
        writer.WriteBoolean("rekursiv", context.Recursive);
        writer.WriteString("filter", context.Filter.Describe());
        writer.WriteString("scanner", result.ScannerName);
        writer.WriteString("gescanntUtc", result.StartedUtc.ToString("o"));
        writer.WriteNumber("scandauerSekunden", Math.Round(result.Duration.TotalSeconds, 2));
        writer.WriteBoolean("belegtIstSchaetzung", result.SizeOnDiskIsEstimate);
        writer.WriteNumber("clusterGroesseBytes", result.BytesPerCluster);
        writer.WriteNumber("ordnerOhneZugriff", result.DeniedCount);
        writer.WriteBoolean("scanAbgebrochen", result.WasCancelled);
        writer.WriteEndObject();

        writer.WriteStartObject("volume");
        writer.WriteNumber("gesamtBytes", result.VolumeTotalBytes);
        writer.WriteNumber("freiBytes", result.VolumeFreeBytes);
        writer.WriteNumber("belegtBytes", Math.Max(0, result.VolumeTotalBytes - result.VolumeFreeBytes));
        writer.WriteEndObject();

        writer.WriteStartObject("summen");
        writer.WriteNumber("dateien", result.FileCount);
        writer.WriteNumber("ordner", result.DirectoryCount);
        writer.WriteNumber("bytes", result.TotalBytes);
        writer.WriteNumber("belegtBytes", result.TotalBytesOnDisk);
        writer.WriteNumber("treffer", query.MatchCount);
        writer.WriteNumber("trefferBytes", query.MatchedBytes);
        writer.WriteEndObject();

        AccessTimeReliability access = context.Age.AccessTimes;
        writer.WriteStartObject("zugriffszeiten");
        writer.WriteBoolean("brauchbar", access.IsReliable);
        writer.WriteString("begruendung", access.Explanation);
        writer.WriteNumber("anteilGleicherTag", Math.Round(access.DominantDayShare, 4));
        writer.WriteNumber("anteilGleichWieAenderung", Math.Round(access.EqualToWriteShare, 4));
        writer.WriteEndObject();

        WriteHistogram(writer, "alterNachAenderung", context.Overview.AgeByWrite);
        WriteHistogram(writer, "alterNachZugriff", context.Overview.AgeByAccess);

        writer.WriteStartArray("eintraege");

        long written = 0;
        foreach (int index in query.Indices)
        {
            if ((written & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            view.MoveTo(index);
            writer.WriteStartObject();
            foreach (ExportColumn column in columns) WriteValue(writer, column, view);
            writer.WriteEndObject();

            written++;
            if ((written & 0xFFFF) == 0)
            {
                writer.Flush();
                progress?.Report(written);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        long size = stream.Length;
        sw.Stop();
        return new ExportSummary(path, written, query.MatchCount, size, sw.Elapsed);
    }

    private static void WriteHistogram(Utf8JsonWriter writer, string name, IReadOnlyList<AgeBreakdownRow> rows)
    {
        writer.WriteStartArray(name);
        foreach (AgeBreakdownRow row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("bereich", row.Label);
            writer.WriteNumber("bytes", row.Bytes);
            writer.WriteNumber("anzahl", row.Count);
            writer.WriteNumber("anteil", Math.Round(row.Share, 4));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static void WriteValue(Utf8JsonWriter writer, ExportColumn column, EntryView view)
    {
        object? value = column.Get(view);
        switch (value)
        {
            case null:
                writer.WriteNull(column.Key);
                break;
            case string s when s.Length == 0:
                writer.WriteNull(column.Key);
                break;
            case string s:
                writer.WriteString(column.Key, s);
                break;
            case long l:
                writer.WriteNumber(column.Key, l);
                break;
            case int n:
                writer.WriteNumber(column.Key, n);
                break;
            case double d:
                writer.WriteNumber(column.Key, d);
                break;
            case DateTime dt:
                writer.WriteString(column.Key, dt.ToString("yyyy-MM-ddTHH:mm:ss"));
                break;
            default:
                writer.WriteString(column.Key, value.ToString());
                break;
        }
    }
}
