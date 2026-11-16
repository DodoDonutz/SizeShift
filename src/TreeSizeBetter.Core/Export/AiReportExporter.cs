using System.Diagnostics;
using System.Globalization;
using System.Text;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Export;

public sealed class AiReportOptions
{
    /// <summary>Wie viele Zeilen der Ordnerbaum hoechstens bekommt.</summary>
    public int MaxTreeLines { get; init; } = 1200;

    /// <summary>Hoechste Tiefe, die ueberhaupt noch aufgefuehrt wird.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Untergeordnete Ordner pro Elternordner, bevor zusammengefasst wird.</summary>
    public int MaxChildrenPerFolder { get; init; } = 20;

    public int TopCount { get; init; } = 40;
}

/// <summary>
/// Kompakter Bericht zum Mitgeben in ein Gespraech mit einem Sprachmodell.
///
/// Der ganze Sinn ist Dichte: ein Dateisystem mit Millionen Eintraegen muss auf
/// wenige hundert Kilobyte schrumpfen, ohne dass die Entscheidungsgrundlage
/// verloren geht. Deshalb wird der Baum ab einer Groessenschwelle abgeschnitten,
/// die sich am Gesamtvolumen orientiert, und alles Kleine zu einer Sammelzeile
/// zusammengefasst.
/// </summary>
public static class AiReportExporter
{
    public static ExportSummary Write(
        string path,
        ExportContext context,
        AiReportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new AiReportOptions();
        var sw = Stopwatch.StartNew();
        var text = Build(context, options, ct);

        File.WriteAllText(path, text, new UTF8Encoding(false));
        sw.Stop();

        long size = new FileInfo(path).Length;
        long lines = CountLines(text);
        // Der Bericht ist keine Zeilenliste, sondern eine Zusammenfassung - er kann
        // also nicht "gekuerzt" sein. Treffer gleich Zeilen, damit die Ausgabe das
        // nicht faelschlich meldet.
        return new ExportSummary(path, lines, lines, size, sw.Elapsed);
    }

    public static string Build(ExportContext context, AiReportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AiReportOptions();
        CultureInfo culture = Loc.Culture;
        var sb = new StringBuilder(256 * 1024);
        ScanOverview overview = context.Overview;
        ScanResult result = context.Result;
        ScanTree tree = context.Tree;

        sb.AppendLine("# " + Loc.F("ai.title", tree.RootPath));
        sb.AppendLine();
        sb.AppendLine(Loc.F("ai.created", DateTime.Now.ToString("g", culture), result.ScannerName));
        sb.AppendLine();

        // --- Eckdaten ---
        sb.AppendLine("## " + Loc.T("ai.keyFacts"));
        sb.AppendLine();
        sb.AppendLine("- " + Loc.F("ai.scope", tree.GetFullPath(context.RootIndex)));
        if (overview.VolumeTotalBytes > 0)
        {
            long used = overview.VolumeTotalBytes - overview.VolumeFreeBytes;
            sb.AppendLine("- " + Loc.F("ai.drive", SizeFormat.Bytes(overview.VolumeTotalBytes),
                          SizeFormat.Bytes(used), overview.VolumeUsedShare, SizeFormat.Bytes(overview.VolumeFreeBytes)));
        }
        sb.AppendLine("- " + Loc.F("ai.captured", SizeFormat.Bytes(overview.TotalBytes), overview.FileCount, overview.DirectoryCount));
        if (result.DeniedCount > 0)
            sb.AppendLine("- " + Loc.F("ai.denied", result.DeniedCount));
        if (result.SizeOnDiskIsEstimate)
            sb.AppendLine("- " + Loc.T("ai.estimate"));
        sb.AppendLine();

        // --- Verlaesslichkeit der Zeitstempel ---
        AccessTimeReliability access = overview.AccessTimes;
        sb.AppendLine("## " + Loc.T("ai.reliability"));
        sb.AppendLine();
        sb.AppendLine(access.IsReliable
            ? Loc.T("ai.accessUsable")
            : Loc.T("ai.accessNotUsable"));
        sb.AppendLine();
        sb.AppendLine($"> {access.Explanation}");
        sb.AppendLine();
        sb.AppendLine(Loc.T("ai.folderAge"));
        sb.AppendLine();

        // --- Altersverteilung ---
        sb.AppendLine("## " + Loc.T("ai.ageDistribution"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ai.ageHeader"));
        sb.AppendLine("|---|---:|---:|---:|");
        for (int i = 0; i < AgeHistogram.Labels.Length; i++)
        {
            long writeBytes = context.Age.ByWriteTime.Bytes[i];
            long writeCount = context.Age.ByWriteTime.Count[i];
            long accessBytes = context.Age.ByAccessTime.Bytes[i];
            if (writeCount == 0 && accessBytes == 0) continue;
            sb.AppendLine($"| {AgeHistogram.Labels[i]} | {SizeFormat.Bytes(writeBytes)} | " +
                          $"{writeCount.ToString("N0", culture)} | {SizeFormat.Bytes(accessBytes)} |");
        }
        sb.AppendLine();

        // --- Ordnerbaum ---
        long threshold = ChooseThreshold(overview.TotalBytes);
        sb.AppendLine("## " + Loc.T("ai.tree"));
        sb.AppendLine();
        sb.AppendLine(Loc.F("ai.treeNote", SizeFormat.Bytes(threshold)));
        sb.AppendLine();
        sb.AppendLine("```");
        AppendTree(sb, context, options, threshold, ct);
        sb.AppendLine("```");
        sb.AppendLine();

        // --- Bestenlisten ---
        AppendTopList(sb, Loc.T("report.largestFiles"), overview.LargestFiles, options.TopCount);
        AppendTopList(sb, Loc.T("report.coldBig"), overview.ColdestBigFolders, options.TopCount);

        // --- Dateitypen ---
        sb.AppendLine("## " + Loc.T("ai.byType"));
        sb.AppendLine();
        sb.AppendLine($"| {Loc.T("col.extension")} | {Loc.T("col.size")} | {Loc.T("col.fileCount")} |");
        sb.AppendLine("|---|---:|---:|");
        foreach (TypeBreakdownRow type in overview.Types)
            sb.AppendLine($"| {type.Label} | {type.BytesText} | {type.CountText} |");
        sb.AppendLine();

        // --- Auffaelligkeiten ---
        List<Finding> findings = QuickFindings.Collect(tree, context.Age, ct: ct);
        sb.AppendLine("## " + Loc.T("ai.findings"));
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine(Loc.T("ai.noFindings"));
        }
        else
        {
            sb.AppendLine(Loc.T("ai.findingsNote"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("ai.findingsHeader"));
            sb.AppendLine("|---|---|---:|---:|---|---|");
            foreach (Finding finding in findings.OrderByDescending(f => f.Bytes))
                sb.AppendLine($"| {finding.Rule} | {Loc.T("safety." + finding.Safety).ToLowerInvariant()} | " +
                              $"{finding.BytesText} | {finding.ColdText} | `{Escape(finding.Path)}` | " +
                              $"{Escape(finding.Reason)} |");
        }
        sb.AppendLine();

        // --- Arbeitsauftrag ---
        sb.AppendLine("## " + Loc.T("ai.task"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ai.taskText"));
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Groessenschwelle fuer den Baum, abgeleitet vom Gesamtvolumen. Eine feste
    /// Schwelle waere entweder auf einer 4-TB-Platte zu fein (zehntausende Zeilen)
    /// oder auf einem kleinen Ordner zu grob (gar nichts zu sehen).
    /// </summary>
    private static long ChooseThreshold(long totalBytes)
    {
        long derived = totalBytes / 2000;
        return Math.Clamp(derived, 20L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
    }

    private static void AppendTree(
        StringBuilder sb, ExportContext context, AiReportOptions options, long threshold, CancellationToken ct)
    {
        ScanTree tree = context.Tree;
        EntryView view = context.NewView();
        int lines = 0;

        void Walk(int index, int depth)
        {
            if (lines >= options.MaxTreeLines || depth > options.MaxDepth) return;
            ct.ThrowIfCancellationRequested();

            view.MoveTo(index);
            AppendTreeLine(sb, view, depth, index == context.RootIndex);
            lines++;

            var children = new List<int>();
            foreach (int child in tree.Children(index))
                if (tree.Entries[child].IsDirectory) children.Add(child);

            if (children.Count == 0) return;
            children.Sort((a, b) => tree.SizeOf(b).CompareTo(tree.SizeOf(a)));

            int shown = 0;
            long restBytes = 0;
            int restCount = 0;

            foreach (int child in children)
            {
                long size = tree.SizeOf(child);
                if (size >= threshold && shown < options.MaxChildrenPerFolder && lines < options.MaxTreeLines)
                {
                    Walk(child, depth + 1);
                    shown++;
                }
                else
                {
                    restBytes += size;
                    restCount++;
                }
            }

            if (restCount > 0 && lines < options.MaxTreeLines)
            {
                sb.Append(new string(' ', (depth + 1) * 2));
                sb.AppendLine("... " + Loc.F("ai.more", restCount, SizeFormat.Bytes(restBytes)));
                lines++;
            }
        }

        Walk(context.RootIndex, 0);

        if (lines >= options.MaxTreeLines)
            sb.AppendLine("... " + Loc.F("ai.cut", options.MaxTreeLines));
    }

    private static void AppendTreeLine(StringBuilder sb, EntryView view, int depth, bool isRoot)
    {
        const int nameColumn = 52;

        string indent = new(' ', depth * 2);
        string name = isRoot ? view.FullPath : view.Name;

        int available = Math.Max(10, nameColumn - indent.Length);
        if (name.Length > available) name = string.Concat(name.AsSpan(0, available - 1), "~");

        sb.Append(indent).Append(name.PadRight(available));
        sb.Append(SizeFormat.Bytes(view.Size).PadLeft(10));
        sb.Append(view.FileCount.ToString("N0", Loc.Culture).PadLeft(11));
        sb.Append("  ").Append(SizeFormat.Days(view.ColdDays).PadRight(11));

        string category = EntryCategorizer.Describe(view.Category);
        if (category.Length > 0) sb.Append(' ').Append(category);
        if (view.IsReparsePoint) sb.Append(" -> " + Loc.T("ai.linkNotCounted"));

        sb.AppendLine();
    }

    private static void AppendTopList(StringBuilder sb, string title, IReadOnlyList<TopEntryRow> rows, int limit)
    {
        if (rows.Count == 0) return;

        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"| {Loc.T("col.size")} | {Loc.T("report.unusedSince")} | {Loc.T("col.path")} |");
        sb.AppendLine("|---:|---:|---|");
        foreach (TopEntryRow row in rows.Take(limit))
            sb.AppendLine($"| {row.BytesText} | {row.ColdText} | `{Escape(row.Path)}` |");
        sb.AppendLine();
    }

    /// <summary>Senkrechte Striche wuerden sonst die Markdown-Tabelle zerlegen.</summary>
    private static string Escape(string value) => value.Replace("|", "\\|");

    private static long CountLines(string text)
    {
        long count = 1;
        foreach (char c in text)
            if (c == '\n') count++;
        return count;
    }
}
