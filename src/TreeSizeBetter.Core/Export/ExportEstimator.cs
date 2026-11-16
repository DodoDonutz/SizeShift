using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TreeSizeBetter.Core.Analysis;

namespace TreeSizeBetter.Core.Export;

/// <summary>Wie gross ein Export ungefaehr wird - bevor er geschrieben wird.</summary>
public sealed record ExportEstimate(
    int Rows,
    int Folders,
    int Files,
    long CsvBytes,
    long JsonBytes,
    int XlsxRows,
    long XlsxBytes)
{
    /// <summary>Der KI-Bericht ist bewusst klein und haengt kaum von der Datenmenge ab.</summary>
    public const long AiReportBytes = 100 * 1024;
}

/// <summary>
/// Schaetzt die Dateigroesse eines Exports, ohne ihn zu schreiben: zaehlt die Treffer
/// mit den eingestellten Filtern und formatiert eine gleichmaessig verteilte Stichprobe
/// von Zeilen genau so, wie es die Exporter tun. Bei 1,5 Millionen Eintraegen dauert das
/// Bruchteile einer Sekunde, und die Schaetzung liegt typischerweise wenige Prozent daneben.
/// </summary>
public static class ExportEstimator
{
    private const int SampleSize = 2000;

    /// <summary>
    /// Excel-Dateien sind gezippte XML-Dateien. Gemessen an echten Exporten: rund 40 Prozent
    /// einer CSV-Zeile pro Tabellenzeile, plus ein fester Anteil fuer Uebersicht und Formate.
    /// </summary>
    private const double XlsxBytesPerCsvByte = 0.40;
    private const long XlsxFixedBytes = 40 * 1024;

    public static ExportEstimate Estimate(ExportContext context, CultureInfo? culture = null, CancellationToken ct = default)
    {
        culture ??= CultureInfo.CurrentCulture;
        List<int> matches = EntryQuery.Collect(context.Tree, context.RootIndex, context.Recursive, context.Filter,
            context.Now, context.TrustAccessTimes, ct);

        int folders = 0;
        foreach (int index in matches)
            if (context.Tree.Entries[index].IsDirectory) folders++;
        int files = matches.Count - folders;

        (double csvRow, double jsonRow) = SampleRowBytes(context, matches, culture);

        long csvHeader = Encoding.UTF8.GetByteCount(string.Join(CsvExporter.DefaultDelimiter,
            ExportColumns.All.Select(c => c.Header))) + 2 + 3;
        long csv = csvHeader + (long)Math.Round(csvRow * matches.Count);
        long json = 4 * 1024 + (long)Math.Round(jsonRow * matches.Count);

        var xlsxOptions = new XlsxOptions();
        int xlsxRows = Math.Min(folders, xlsxOptions.MaxFolderRows) + Math.Min(files, xlsxOptions.MaxFileRows);
        long xlsx = XlsxFixedBytes + (long)Math.Round(csvRow * xlsxRows * XlsxBytesPerCsvByte);

        return new ExportEstimate(matches.Count, folders, files, csv, json, xlsxRows, xlsx);
    }

    /// <summary>Durchschnittliche Bytes je Zeile in CSV und JSON, aus einer Stichprobe.</summary>
    private static (double Csv, double Json) SampleRowBytes(ExportContext context, List<int> matches, CultureInfo culture)
    {
        if (matches.Count == 0) return (0, 0);

        EntryView view = context.NewView();
        IReadOnlyList<ExportColumn> columns = ExportColumns.All;
        int samples = Math.Min(SampleSize, matches.Count);
        double step = (double)matches.Count / samples;

        var line = new StringBuilder(512);
        var buffer = new ArrayBufferWriter<byte>(1024);
        long csvBytes = 0, jsonBytes = 0;

        for (int s = 0; s < samples; s++)
        {
            view.MoveTo(matches[(int)(s * step)]);

            line.Clear();
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0) line.Append(CsvExporter.DefaultDelimiter);
                CsvExporter.AppendEscaped(line, CsvExporter.Format(columns[c], view, culture), CsvExporter.DefaultDelimiter);
            }
            csvBytes += Encoding.UTF8.GetByteCount(line.ToString()) + 2;   // + Zeilenumbruch

            buffer.Clear();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (ExportColumn column in columns) JsonExporter.WriteValue(writer, column, view);
                writer.WriteEndObject();
            }
            jsonBytes += buffer.WrittenCount + 1;   // + Komma zwischen den Eintraegen
        }

        return ((double)csvBytes / samples, (double)jsonBytes / samples);
    }
}
