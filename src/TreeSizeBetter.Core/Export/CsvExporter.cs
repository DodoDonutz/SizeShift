using System.Diagnostics;
using System.Globalization;
using System.Text;
using TreeSizeBetter.Core.Analysis;

namespace TreeSizeBetter.Core.Export;

/// <summary>
/// Streamender CSV-Export ohne Zeilenlimit. Das ist die Ausgabe fuer den
/// vollstaendigen Datenbestand - Excel kann nur gut eine Million Zeilen, eine
/// CSV-Datei kann so gross werden, wie die Platte hergibt.
/// </summary>
public static class CsvExporter
{
    /// <summary>
    /// Standardtrenner ist das Semikolon: deutsches Excel erwartet es, weil das
    /// Komma dort das Dezimaltrennzeichen ist. Mit Komma als Trenner landet jede
    /// Zahl mit Nachkommastelle in der falschen Spalte.
    /// </summary>
    public const char DefaultDelimiter = ';';

    public static ExportSummary Write(
        string path,
        ExportContext context,
        char delimiter = DefaultDelimiter,
        CultureInfo? culture = null,
        IReadOnlyList<ExportColumn>? columns = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        culture ??= CultureInfo.CurrentCulture;
        columns ??= ExportColumns.All;
        var sw = Stopwatch.StartNew();

        QueryResult query = context.Query(ct: ct);
        EntryView view = context.NewView();

        // BOM, damit Excel die Datei als UTF-8 erkennt. Ohne sie werden Umlaute
        // in Dateinamen beim Oeffnen zu Zeichensalat.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var writer = new StreamWriter(stream, encoding, 1 << 20);

        var line = new StringBuilder(512);

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) line.Append(delimiter);
            AppendEscaped(line, columns[i].Header, delimiter);
        }
        writer.WriteLine(line);

        long written = 0;
        foreach (int index in query.Indices)
        {
            if ((written & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            view.MoveTo(index);
            line.Clear();

            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0) line.Append(delimiter);
                AppendEscaped(line, Format(columns[c], view, culture), delimiter);
            }

            writer.WriteLine(line);
            written++;

            if ((written & 0xFFFF) == 0) progress?.Report(written);
        }

        writer.Flush();
        long size = stream.Length;
        sw.Stop();

        return new ExportSummary(path, written, query.MatchCount, size, sw.Elapsed);
    }

    internal static string Format(ExportColumn column, EntryView view, CultureInfo culture)
    {
        object? value = column.Get(view);
        return value switch
        {
            null => string.Empty,
            string s => s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            int n => n.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("0.#", culture),
            DateTime dt => dt.ToString("dd.MM.yyyy HH:mm:ss", culture),
            _ => Convert.ToString(value, culture) ?? string.Empty
        };
    }

    internal static void AppendEscaped(StringBuilder target, string value, char delimiter)
    {
        bool needsQuotes = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != delimiter && c != '"' && c != '\n' && c != '\r') continue;
            needsQuotes = true;
            break;
        }

        if (!needsQuotes)
        {
            target.Append(value);
            return;
        }

        target.Append('"');
        foreach (char c in value)
        {
            target.Append(c);
            if (c == '"') target.Append('"');   // Anfuehrungszeichen werden verdoppelt
        }
        target.Append('"');
    }
}
