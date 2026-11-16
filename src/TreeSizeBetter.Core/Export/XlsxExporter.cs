using System.Diagnostics;
using ClosedXML.Excel;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Export;

public sealed class XlsxOptions
{
    /// <summary>
    /// Zeilen pro Blatt. Excel kann zwar gut eine Million Zeilen, aber eine
    /// Arbeitsmappe mit so vielen Zeilen ist weder schnell zu erzeugen noch
    /// angenehm zu benutzen. Wer wirklich alles braucht, nimmt den CSV-Export.
    /// </summary>
    public int MaxFileRows { get; init; } = 50_000;

    public int MaxFolderRows { get; init; } = 50_000;

    /// <summary>Laenge der Bestenlisten auf dem Uebersichtsblatt.</summary>
    public int TopCount { get; init; } = 25;
}

/// <summary>
/// Excel-Arbeitsmappe mit mehreren Blaettern: Uebersicht, Ordner, Dateien,
/// Dateitypen, Altersverteilung, Umlager-Vorschlaege und die Meldungen des Scans.
/// </summary>
public static class XlsxExporter
{
    private const string ByteFormat = "#,##0";
    private const string DaysFormat = "#,##0.#";
    private static string DateFormat => Loc.T("xlsx.dateFormat");
    private const string PercentFormat = "0.0 %";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3B57");
    private static readonly XLColor HeaderText = XLColor.White;
    private static readonly XLColor AccentText = XLColor.FromHtml("#1F3B57");
    private static readonly XLColor MutedText = XLColor.FromHtml("#666666");

    public static ExportSummary Write(
        string path,
        ExportContext context,
        XlsxOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new XlsxOptions();
        var sw = Stopwatch.StartNew();

        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "SizeShift - " + context.Tree.RootPath;
        workbook.Properties.Author = "SizeShift";

        progress?.Report(Loc.T("xlsx.sheet.overview") + " ...");
        WriteOverviewSheet(workbook, context, options);

        progress?.Report(Loc.T("xlsx.sheet.folders") + " ...");
        long folderRows = WriteEntrySheet(workbook, Loc.T("xlsx.sheet.folders"), context, EntryKind.DirectoriesOnly,
            options.MaxFolderRows, ExportColumns.Folders, ct);

        progress?.Report(Loc.T("xlsx.sheet.files") + " ...");
        long fileRows = WriteEntrySheet(workbook, Loc.T("xlsx.sheet.files"), context, EntryKind.FilesOnly,
            options.MaxFileRows, ExportColumns.All, ct);

        progress?.Report(Loc.T("xlsx.sheet.types") + " ...");
        WriteTypeSheet(workbook, context);

        progress?.Report(Loc.T("xlsx.sheet.age") + " ...");
        WriteAgeSheet(workbook, context);

        progress?.Report(Loc.T("xlsx.progress.relocate"));
        WriteRelocationSheet(workbook, context, ct);

        progress?.Report(Loc.T("xlsx.sheet.messages") + " ...");
        WriteMessagesSheet(workbook, context);

        progress?.Report(Loc.T("xlsx.progress.write"));
        workbook.SaveAs(path);
        sw.Stop();

        long size = new FileInfo(path).Length;
        return new ExportSummary(path, folderRows + fileRows,
            context.Overview.FileCount + context.Overview.DirectoryCount, size, sw.Elapsed);
    }

    // ------------------------------------------------------------- Uebersicht

    private static void WriteOverviewSheet(XLWorkbook workbook, ExportContext context, XlsxOptions options)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(Loc.T("xlsx.sheet.overview"));
        ScanOverview overview = context.Overview;
        ScanResult result = context.Result;
        int row = 1;

        sheet.Cell(row, 1).Value = "SizeShift";
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(18).Font.SetFontColor(AccentText);
        row++;

        sheet.Cell(row, 1).Value = context.Describe();
        sheet.Cell(row, 1).Style.Font.SetFontColor(MutedText);
        row += 2;

        row = KeyValue(sheet, row, Loc.T("xlsx.scannedAt"), result.StartedUtc.ToLocalTime().ToString("g", Loc.Culture));
        row = KeyValue(sheet, row, Loc.T("xlsx.scanner"), result.ScannerName);
        row = KeyValue(sheet, row, Loc.T("xlsx.duration"), Loc.F("xlsx.seconds", result.Duration.TotalSeconds));
        row++;

        row = KeyValueBytes(sheet, row, Loc.T("xlsx.totalSize"), overview.TotalBytes);
        row = KeyValueBytes(sheet, row, Loc.T("xlsx.onDisk"), overview.TotalBytesOnDisk);
        row = KeyValueNumber(sheet, row, Loc.T("xlsx.sheet.files"), overview.FileCount);
        row = KeyValueNumber(sheet, row, Loc.T("xlsx.sheet.folders"), overview.DirectoryCount);
        row++;

        if (overview.VolumeTotalBytes > 0)
        {
            row = KeyValueBytes(sheet, row, Loc.T("xlsx.driveTotal"), overview.VolumeTotalBytes);
            row = KeyValueBytes(sheet, row, Loc.T("xlsx.driveUsed"), overview.VolumeTotalBytes - overview.VolumeFreeBytes);
            row = KeyValueBytes(sheet, row, Loc.T("xlsx.driveFree"), overview.VolumeFreeBytes);
            row++;
        }

        row = Note(sheet, row, overview.AccessTimes.IsReliable ? Loc.T("xlsx.accessUsable") : Loc.T("xlsx.accessNotUsable"));
        row = Note(sheet, row, overview.AccessTimes.Explanation);
        if (result.DeniedCount > 0)
            row = Note(sheet, row, Loc.F("xlsx.denied", result.DeniedCount, Loc.T("xlsx.sheet.messages")));
        if (result.SizeOnDiskIsEstimate)
            row = Note(sheet, row, Loc.T("xlsx.estimate"));
        row = Note(sheet, row, Loc.F("xlsx.capNote", Loc.T("xlsx.sheet.folders"), Loc.T("xlsx.sheet.files"),
                               options.MaxFolderRows, options.MaxFileRows));
        row = Note(sheet, row, Loc.F("xlsx.sortNote", Loc.T("col.sizeBytes")));
        row += 2;

        row = TopTable(sheet, row, Loc.T("report.largestFolders"), overview.LargestFolders, options.TopCount);
        row = TopTable(sheet, row, Loc.T("report.coldBig"), overview.ColdestBigFolders, options.TopCount);
        row = TopTable(sheet, row, Loc.T("report.largestFiles"), overview.LargestFiles, options.TopCount);

        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 22;
        sheet.Column(3).Width = 18;
        sheet.Column(4).Width = 90;
        sheet.SheetView.FreezeRows(2);
    }

    private static int KeyValue(IXLWorksheet sheet, int row, string label, string value)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Cell(row, 1).Style.Font.SetBold();
        sheet.Cell(row, 2).Value = value;
        return row + 1;
    }

    private static int KeyValueBytes(IXLWorksheet sheet, int row, string label, long bytes)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Cell(row, 1).Style.Font.SetBold();
        sheet.Cell(row, 2).Value = (double)bytes;
        sheet.Cell(row, 2).Style.NumberFormat.Format = ByteFormat;
        sheet.Cell(row, 3).Value = SizeFormat.Bytes(bytes);
        sheet.Cell(row, 3).Style.Font.SetFontColor(MutedText);
        return row + 1;
    }

    private static int KeyValueNumber(IXLWorksheet sheet, int row, string label, long value)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Cell(row, 1).Style.Font.SetBold();
        sheet.Cell(row, 2).Value = (double)value;
        sheet.Cell(row, 2).Style.NumberFormat.Format = ByteFormat;
        return row + 1;
    }

    private static int Note(IXLWorksheet sheet, int row, string text)
    {
        sheet.Cell(row, 1).Value = text;
        sheet.Cell(row, 1).Style.Font.SetFontColor(MutedText).Font.SetItalic();
        return row + 1;
    }

    private static int TopTable(IXLWorksheet sheet, int row, string title, IReadOnlyList<TopEntryRow> rows, int limit)
    {
        if (rows.Count == 0) return row;

        sheet.Cell(row, 1).Value = title;
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(13).Font.SetFontColor(AccentText);
        row++;

        string[] headers = { Loc.T("col.sizeBytes"), Loc.T("col.size"), Loc.T("report.unusedSince"), Loc.T("col.path") };
        for (int i = 0; i < headers.Length; i++)
        {
            IXLCell cell = sheet.Cell(row, i + 1);
            cell.Value = headers[i];
            StyleHeaderCell(cell);
        }
        row++;

        foreach (TopEntryRow entry in rows.Take(limit))
        {
            sheet.Cell(row, 1).Value = (double)entry.Bytes;
            sheet.Cell(row, 1).Style.NumberFormat.Format = ByteFormat;
            sheet.Cell(row, 2).Value = entry.BytesText;
            sheet.Cell(row, 3).Value = entry.ColdText;
            sheet.Cell(row, 4).Value = entry.Path;
            row++;
        }

        return row + 1;
    }

    // ------------------------------------------------------------ Datenblaetter

    private static long WriteEntrySheet(
        XLWorkbook workbook, string name, ExportContext context, EntryKind kind,
        int maxRows, IReadOnlyList<ExportColumn> columns, CancellationToken ct)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(name);
        QueryResult query = context.Query(kind, maxRows, ct);
        EntryView view = context.NewView();

        for (int c = 0; c < columns.Count; c++)
        {
            IXLCell cell = sheet.Cell(1, c + 1);
            cell.Value = columns[c].Header;
            StyleHeaderCell(cell);
            sheet.Column(c + 1).Width = columns[c].Width;
        }

        int row = 2;
        foreach (int index in query.Indices)
        {
            if ((row & 0x1FFF) == 0) ct.ThrowIfCancellationRequested();

            view.MoveTo(index);
            for (int c = 0; c < columns.Count; c++)
                SetCell(sheet.Cell(row, c + 1), columns[c], view);
            row++;
        }

        int lastRow = row - 1;
        ApplyColumnFormats(sheet, columns, lastRow);

        if (lastRow >= 1)
        {
            sheet.Range(1, 1, Math.Max(lastRow, 2), columns.Count).SetAutoFilter();
            sheet.SheetView.FreezeRows(1);
        }

        if (query.WasCapped)
        {
            IXLCell note = sheet.Cell(lastRow + 2, 1);
            note.Value = Loc.F("xlsx.capped", query.MatchCount, maxRows);
            note.Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml("#B45309"));
        }

        return lastRow - 1;
    }

    private static void SetCell(IXLCell cell, ExportColumn column, EntryView view)
    {
        object? value = column.Get(view);
        switch (value)
        {
            case null:
                break;
            case string s when s.Length == 0:
                break;
            case string s:
                cell.SetValue(s);
                break;
            case long l:
                cell.SetValue((double)l);
                break;
            case int n:
                cell.SetValue((double)n);
                break;
            case double d:
                cell.SetValue(d);
                break;
            case DateTime dt:
                cell.SetValue(dt);
                break;
            default:
                cell.SetValue(value.ToString());
                break;
        }
    }

    private static void ApplyColumnFormats(IXLWorksheet sheet, IReadOnlyList<ExportColumn> columns, int lastRow)
    {
        if (lastRow < 2) return;

        for (int c = 0; c < columns.Count; c++)
        {
            string? format = columns[c].Type switch
            {
                ExportValueType.Bytes or ExportValueType.Integer => ByteFormat,
                ExportValueType.Decimal => DaysFormat,
                ExportValueType.DateTime => DateFormat,
                _ => null
            };
            if (format is null) continue;
            sheet.Range(2, c + 1, lastRow, c + 1).Style.NumberFormat.Format = format;
        }
    }

    // ------------------------------------------------------------ Auswertungen

    private static void WriteTypeSheet(XLWorkbook workbook, ExportContext context)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(Loc.T("xlsx.sheet.types"));
        string[] headers = { Loc.T("col.extension"), Loc.T("col.sizeBytes"), Loc.T("col.size"), Loc.T("xlsx.count"), Loc.T("xlsx.share") };
        for (int i = 0; i < headers.Length; i++) StyleHeaderCell(sheet.Cell(1, i + 1).SetValue(headers[i]));

        long total = context.Overview.TotalBytes;
        int row = 2;
        foreach (TypeBreakdownRow type in context.Overview.Types)
        {
            sheet.Cell(row, 1).Value = type.Label;
            sheet.Cell(row, 2).Value = (double)type.Bytes;
            sheet.Cell(row, 3).Value = type.BytesText;
            sheet.Cell(row, 4).Value = (double)type.Count;
            sheet.Cell(row, 5).Value = total > 0 ? (double)type.Bytes / total : 0;
            row++;
        }

        if (row > 2)
        {
            sheet.Range(2, 2, row - 1, 2).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(2, 4, row - 1, 4).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(2, 5, row - 1, 5).Style.NumberFormat.Format = PercentFormat;
        }

        sheet.Column(1).Width = 18;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 14;
        sheet.Column(4).Width = 14;
        sheet.Column(5).Width = 12;
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteAgeSheet(XLWorkbook workbook, ExportContext context)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(Loc.T("xlsx.sheet.age"));
        int row = 1;

        row = AgeBlock(sheet, row, Loc.T("xlsx.byModified"), context.Overview.AgeByWrite);
        row = AgeBlock(sheet, row, Loc.T("xlsx.byAccessed"), context.Overview.AgeByAccess);

        if (!context.Age.AccessTimes.IsReliable)
        {
            sheet.Cell(row, 1).Value = Loc.F("xlsx.attention", context.Age.AccessTimes.Explanation);
            sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml("#B45309"));
        }

        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 14;
        sheet.Column(4).Width = 14;
        sheet.Column(5).Width = 12;
    }

    private static int AgeBlock(IXLWorksheet sheet, int row, string title, IReadOnlyList<AgeBreakdownRow> rows)
    {
        sheet.Cell(row, 1).Value = title;
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(13).Font.SetFontColor(AccentText);
        row++;

        string[] headers = { Loc.T("xlsx.range"), Loc.T("col.sizeBytes"), Loc.T("col.size"), Loc.T("col.fileCount"), Loc.T("xlsx.share") };
        for (int i = 0; i < headers.Length; i++) StyleHeaderCell(sheet.Cell(row, i + 1).SetValue(headers[i]));
        row++;

        int first = row;
        foreach (AgeBreakdownRow entry in rows)
        {
            sheet.Cell(row, 1).Value = entry.Label;
            sheet.Cell(row, 2).Value = (double)entry.Bytes;
            sheet.Cell(row, 3).Value = entry.BytesText;
            sheet.Cell(row, 4).Value = (double)entry.Count;
            sheet.Cell(row, 5).Value = entry.Share;
            row++;
        }

        if (row > first)
        {
            sheet.Range(first, 2, row - 1, 2).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(first, 4, row - 1, 4).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(first, 5, row - 1, 5).Style.NumberFormat.Format = PercentFormat;
        }

        return row + 2;
    }

    private static void WriteRelocationSheet(XLWorkbook workbook, ExportContext context, CancellationToken ct)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(Loc.T("xlsx.sheet.relocate"));

        sheet.Cell(1, 1).Value = Loc.T("xlsx.relocate.title");
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(AccentText);
        sheet.Cell(2, 1).Value =
            Loc.T("xlsx.relocate.text");
        sheet.Cell(2, 1).Style.Font.SetFontColor(MutedText).Font.SetItalic();

        string[] headers =
        {
            Loc.T("col.path"), Loc.T("col.category"), Loc.T("col.sizeOnDiskBytes"), Loc.T("xlsx.onDiskShort"), Loc.T("col.fileCount"),
            Loc.T("xlsx.unusedDays"), Loc.T("xlsx.lastModified"), Loc.T("xlsx.lastAccessed")
        };
        for (int i = 0; i < headers.Length; i++) StyleHeaderCell(sheet.Cell(4, i + 1).SetValue(headers[i]));

        ct.ThrowIfCancellationRequested();
        EntryView view = context.NewView();
        var suggestions = Offload.RelocationAdvisor.Suggest(context.Tree, context.Age, minBytes: 500L * 1024 * 1024, max: 500);

        int row = 5;
        foreach (Offload.RelocationSuggestion suggestion in suggestions
                     .OrderByDescending(s => s.ColdDays ?? 0).ThenByDescending(s => s.BytesOnDisk))
        {
            view.MoveTo(suggestion.Index);
            sheet.Cell(row, 1).Value = suggestion.Path;
            sheet.Cell(row, 2).Value = suggestion.CategoryText;
            sheet.Cell(row, 3).Value = (double)suggestion.BytesOnDisk;
            sheet.Cell(row, 4).Value = suggestion.BytesText;
            sheet.Cell(row, 5).Value = (double)suggestion.Files;
            if (suggestion.ColdDays is { } cold) sheet.Cell(row, 6).Value = Math.Round(cold, 1);
            if (view.Modified is { } modified) sheet.Cell(row, 7).Value = modified;
            if (view.Accessed is { } accessed) sheet.Cell(row, 8).Value = accessed;
            row++;
        }

        if (row > 5)
        {
            sheet.Range(5, 3, row - 1, 3).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(5, 5, row - 1, 5).Style.NumberFormat.Format = ByteFormat;
            sheet.Range(5, 6, row - 1, 6).Style.NumberFormat.Format = DaysFormat;
            sheet.Range(5, 7, row - 1, 8).Style.NumberFormat.Format = DateFormat;
            sheet.Range(4, 1, row - 1, headers.Length).SetAutoFilter();
        }

        sheet.Column(1).Width = 80;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 18;
        sheet.Column(4).Width = 12;
        sheet.Column(5).Width = 12;
        sheet.Column(6).Width = 20;
        sheet.Column(7).Width = 18;
        sheet.Column(8).Width = 18;
        sheet.SheetView.FreezeRows(4);
    }

    private static void WriteMessagesSheet(XLWorkbook workbook, ExportContext context)
    {
        IXLWorksheet sheet = workbook.AddWorksheet(Loc.T("xlsx.sheet.messages"));
        string[] headers = { Loc.T("xlsx.kind"), Loc.T("col.path"), Loc.T("xlsx.message"), Loc.T("xlsx.win32") };
        for (int i = 0; i < headers.Length; i++) StyleHeaderCell(sheet.Cell(1, i + 1).SetValue(headers[i]));

        int row = 2;
        foreach (ScanError error in context.Result.Errors.Take(50_000))
        {
            sheet.Cell(row, 1).Value = error.Kind.ToString();
            sheet.Cell(row, 2).Value = error.Path;
            sheet.Cell(row, 3).Value = error.Message;
            sheet.Cell(row, 4).Value = error.Win32Error;
            row++;
        }

        if (row == 2)
        {
            sheet.Cell(2, 1).Value = Loc.T("xlsx.noMessages");
            sheet.Cell(2, 1).Style.Font.SetFontColor(MutedText).Font.SetItalic();
        }
        else
        {
            sheet.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
            sheet.SheetView.FreezeRows(1);
        }

        sheet.Column(1).Width = 16;
        sheet.Column(2).Width = 90;
        sheet.Column(3).Width = 36;
        sheet.Column(4).Width = 12;
    }

    private static void StyleHeaderCell(IXLCell cell)
        => cell.Style.Font.SetBold().Font.SetFontColor(HeaderText).Fill.SetBackgroundColor(HeaderFill);
}
