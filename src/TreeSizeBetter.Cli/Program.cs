using System.Diagnostics;
using System.Globalization;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Export;
using TreeSizeBetter.Core.Offload;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // Die Kommandozeile bleibt deutsch, auch die Texte, die sie aus Core holt.
        TreeSizeBetter.Core.Localization.Loc.SetLanguage("de");
        TreeSizeBetter.Core.Util.ActivityLog.Enabled = true;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "scan" => Scan(args.Skip(1).ToArray()),
            "export" => Export(args.Skip(1).ToArray()),
            "attribute" => Attributes(args.Skip(1).ToArray()),
            "pruefen" => CheckRelocation(args.Skip(1).ToArray()),
            "umlagern" => Relocate(args.Skip(1).ToArray()),
            "zuruecknehmen" => Undo(args.Skip(1).ToArray()),
            "journal" => ShowJournal(args.Skip(1).ToArray()),
            "aufraeumen" => ShowCleanup(args.Skip(1).ToArray()),
            "system" => ShowSystem(),
            "bericht" => WriteReport(args.Skip(1).ToArray()),
            "prozesse" => RecordProcesses(args.Skip(1).ToArray()),
            _ => Unknown(args[0])
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("sizeshift - SizeShift auf der Kommandozeile");
        Console.WriteLine();
        Console.WriteLine("  sizeshift scan   <Pfad> [--threads N] [--top N]");
        Console.WriteLine("  sizeshift export <Pfad> --out <Ordner> [--formate xlsx,csv,json,ki]");
        Console.WriteLine("                    [--min-mb N] [--unbenutzt N] [--endung iso,zip]");
        Console.WriteLine("                    [--max-zeilen N] [--diese-ebene]");
        Console.WriteLine();
        Console.WriteLine("  sizeshift aufraeumen <Pfad>   (zeigt nur an, loescht nichts)");
        Console.WriteLine();
        Console.WriteLine("scan    zeigt die Kennzahlen eines Ordners oder Laufwerks.");
        Console.WriteLine("export  scannt und schreibt Excel-, CSV-, JSON- und KI-Bericht-Dateien.");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unbekannter Befehl: {command}");
        PrintUsage();
        return 2;
    }

    private static int Scan(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Kein Pfad angegeben.");
            return 2;
        }

        string path = args[0];
        int threads = ReadIntOption(args, "--threads", 0);
        int top = ReadIntOption(args, "--top", 15);

        var scanner = new ApiScanner();
        if (!scanner.CanScan(path, out string? reason))
        {
            Console.Error.WriteLine(reason);
            return 1;
        }

        var options = new ScanOptions { RootPath = path, MaxThreads = threads };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("\nAbbruch angefordert ...");
        };

        var lastReport = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (lastReport.ElapsedMilliseconds < 400) return;
            lastReport.Restart();
            Console.Error.Write(
                $"\r{p.Entries:N0} Eintraege  {SizeFormat.Bytes(p.Bytes)}  " +
                $"{p.EntriesPerSecond / 1000:F0}k/s  offen: {p.DirectoriesPending:N0}   ");
        });

        ScanResult result = scanner.ScanAsync(options, progress, cts.Token).GetAwaiter().GetResult();
        Console.Error.Write("\r" + new string(' ', 78) + "\r");

        PrintResult(result, top);
        return result.WasCancelled ? 130 : 0;
    }

    private static void PrintResult(ScanResult result, int top)
    {
        ScanTree tree = result.Tree;
        var age = AgeAnalyzer.Analyze(tree);
        bool trust = age.AccessTimes.IsReliable;

        Console.WriteLine($"Wurzel          {tree.RootPath}");
        Console.WriteLine($"Scanner         {result.ScannerName}");
        Console.WriteLine($"Dauer           {result.Duration.TotalSeconds:F2} s" +
                          $"   ({tree.Count / Math.Max(result.Duration.TotalSeconds, 0.001) / 1000:F0}k Eintraege/s)");
        Console.WriteLine($"Eintraege       {tree.Count:N0}  ({result.FileCount:N0} Dateien, {result.DirectoryCount:N0} Ordner)");
        Console.WriteLine($"Groesse         {SizeFormat.Bytes(result.TotalBytes)}  ({result.TotalBytes:N0} Bytes)");
        Console.WriteLine($"Belegt (gesch.) {SizeFormat.Bytes(result.TotalBytesOnDisk)}   Cluster {result.BytesPerCluster:N0} B");
        if (result.VolumeTotalBytes > 0)
            Console.WriteLine($"Volume          {SizeFormat.Bytes(result.VolumeTotalBytes)} gesamt, " +
                              $"{SizeFormat.Bytes(result.VolumeFreeBytes)} frei");
        Console.WriteLine($"Speicher Baum   {SizeFormat.Bytes(tree.ApproximateBytes)}");
        Console.WriteLine($"Zugriff gesperrt {result.DeniedCount:N0} Ordner, {result.Errors.Count:N0} Meldungen");
        if (result.WasCancelled) Console.WriteLine("ABGEBROCHEN - Zahlen sind unvollstaendig.");
        Console.WriteLine();

        Console.WriteLine("Zugriffszeiten  " + (trust ? "brauchbar" : "NICHT brauchbar"));
        Console.WriteLine("                " + Wrap(age.AccessTimes.Explanation, 62, 16));
        Console.WriteLine();

        Console.WriteLine($"Groesse nach Aenderungsdatum:");
        for (int i = 0; i < AgeHistogram.Labels.Length; i++)
        {
            if (age.ByWriteTime.Count[i] == 0) continue;
            Console.WriteLine($"  {AgeHistogram.Labels[i],-20} {SizeFormat.Bytes(age.ByWriteTime.Bytes[i]),12}" +
                              $"   {age.ByWriteTime.Count[i],12:N0} Dateien");
        }
        Console.WriteLine();

        var children = tree.Children(ScanTree.RootIndex)
            .Where(i => tree.Entries[i].IsDirectory)
            .OrderByDescending(tree.SizeOf)
            .Take(top)
            .ToList();

        if (children.Count > 0)
        {
            Console.WriteLine($"Groesste Ordner direkt unter der Wurzel:");
            Console.WriteLine($"  {"Ordner",-42} {"Groesse",12} {"Dateien",10} {"genutzt",10}");
            foreach (int index in children)
            {
                double? cold = AgeAnalyzer.ColdDays(tree, index, age.NowFileTime, trust);
                ref DirAggregate agg = ref tree.AggregateOf(index);
                string name = tree.GetName(index);
                if (name.Length > 42) name = name[..39] + "...";
                Console.WriteLine($"  {name,-42} {SizeFormat.Bytes(tree.SizeOf(index)),12} " +
                                  $"{agg.FileCount,10:N0} {SizeFormat.Days(cold),10}");
            }
            Console.WriteLine();
        }

        var biggest = EnumerateFiles(tree)
            .OrderByDescending(i => tree.Entries[i].Length)
            .Take(top)
            .ToList();

        if (biggest.Count > 0)
        {
            Console.WriteLine("Groesste Dateien:");
            foreach (int index in biggest)
            {
                ref FsEntry e = ref tree.Entries[index];
                double? cold = AgeAnalyzer.ColdDays(tree, index, age.NowFileTime, trust);
                Console.WriteLine($"  {SizeFormat.Bytes(e.Length),12}  {SizeFormat.Days(cold),10}  {tree.GetFullPath(index)}");
            }
            Console.WriteLine();
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"Erste Meldungen ({Math.Min(5, result.Errors.Count)} von {result.Errors.Count}):");
            foreach (var error in result.Errors.Take(5))
                Console.WriteLine($"  [{error.Kind}] {error.Path} - {error.Message}");
        }
    }

    /// <summary>Listet, was der Aufraeumen-Tab finden wuerde. Loescht nichts.</summary>
    private static int ShowCleanup(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Kein Pfad angegeben.");
            return 2;
        }

        ScanResult result = new ApiScanner()
            .ScanAsync(new ScanOptions { RootPath = args[0] }, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        AgeAnalysis age = AgeAnalyzer.Analyze(result.Tree);

        var sw = Stopwatch.StartNew();
        var findings = TreeSizeBetter.Core.Cleanup.CleanupScanner.Find(
            result.Tree, TreeSizeBetter.Core.Cleanup.CleanupCatalog.Build(TreeSizeBetter.Core.Cleanup.KnownFolders.Current()),
            age.NowFileTime);
        Console.WriteLine($"Scan {result.Duration.TotalSeconds:F1} s, Regeln {sw.ElapsedMilliseconds} ms, {findings.Count} Funde");

        foreach (var f in findings.OrderByDescending(f => f.Bytes))
            Console.WriteLine($"{SizeFormat.Bytes(f.Bytes),10}  {f.Rule.Safety,-10} {f.Rule.Id,-22} {f.Items.Count,6} Elemente  " +
                              $"bleibt {SizeFormat.Bytes(f.KeptBytes),9}  {f.Location}");
        return 0;
    }

    private static int Export(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Kein Pfad angegeben.");
            return 2;
        }

        string path = args[0];
        string outDir = ReadOption(args, "--out") ?? Directory.GetCurrentDirectory();
        string formats = (ReadOption(args, "--formate") ?? "xlsx,csv,json,ki").ToLowerInvariant();
        int maxRows = ReadIntOption(args, "--max-zeilen", 0);
        bool recursive = !args.Contains("--diese-ebene", StringComparer.OrdinalIgnoreCase);

        var filter = new EntryFilter(
            minSize: (long)(ReadIntOption(args, "--min-mb", 0)) * 1024 * 1024,
            minColdDays: ReadIntOption(args, "--unbenutzt", 0) is var d && d > 0 ? d : null,
            extensions: ReadOption(args, "--endung"));

        var scanner = new ApiScanner();
        if (!scanner.CanScan(path, out string? reason))
        {
            Console.Error.WriteLine(reason);
            return 1;
        }

        Directory.CreateDirectory(outDir);

        Console.Error.Write("Scanne ...");
        ScanResult result = scanner
            .ScanAsync(new ScanOptions { RootPath = path }, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        AgeAnalysis age = AgeAnalyzer.Analyze(result.Tree);
        ScanOverview overview = ScanOverview.Build(result, age);
        Console.Error.WriteLine($"\rScan fertig: {result.Tree.Count:N0} Eintraege in {result.Duration.TotalSeconds:F1} s");

        var context = new ExportContext
        {
            Result = result,
            Age = age,
            Overview = overview,
            Filter = filter,
            Recursive = recursive,
            MaxRows = maxRows
        };

        ExportEstimate estimate = ExportEstimator.Estimate(context);
        Console.Error.WriteLine($"Schaetzung: {estimate.Rows:N0} Zeilen - CSV ca. {SizeFormat.Bytes(estimate.CsvBytes)}, " +
                                $"JSON ca. {SizeFormat.Bytes(estimate.JsonBytes)}, Excel ca. {SizeFormat.Bytes(estimate.XlsxBytes)}");

        string stem = MakeStem(result.Tree.RootPath);
        var summaries = new List<(string Format, ExportSummary Summary)>();

        try
        {
            if (formats.Contains("csv"))
                summaries.Add(("CSV", CsvExporter.Write(Path.Combine(outDir, stem + ".csv"), context)));

            if (formats.Contains("json"))
                summaries.Add(("JSON", JsonExporter.Write(Path.Combine(outDir, stem + ".json"), context)));

            if (formats.Contains("xlsx"))
                summaries.Add(("Excel", XlsxExporter.Write(Path.Combine(outDir, stem + ".xlsx"), context,
                    progress: new Progress<string>(step => Console.Error.Write($"\rExcel: {step,-30}")))));

            if (formats.Contains("ki"))
                summaries.Add(("KI-Bericht", AiReportExporter.Write(Path.Combine(outDir, stem + "-ki.md"), context)));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"\nSchreiben fehlgeschlagen: {ex.Message}");
            return 1;
        }

        Console.Error.Write("\r" + new string(' ', 60) + "\r");
        Console.WriteLine();
        Console.WriteLine($"{"Format",-12} {"Zeilen",12} {"Dateigroesse",14} {"Dauer",9}   Datei");
        foreach (var (format, summary) in summaries)
        {
            Console.WriteLine($"{format,-12} {summary.RowsWritten,12:N0} {SizeFormat.Bytes(summary.FileSizeBytes),14} " +
                              $"{summary.Duration.TotalSeconds,8:F1}s   {summary.Path}");
            if (summary.WasCapped)
                Console.WriteLine($"{"",-12} gekuerzt von {summary.TotalMatches:N0} Treffern");
        }

        return 0;
    }

    /// <summary>
    /// Diagnose: wie viel logische Groesse steckt in Dateien, deren belegter Platz
    /// systematisch kleiner ist (komprimiert, sparse, Cloud-Platzhalter), und wie
    /// viele Reparse-Points gibt es nach Art. Erklaert Abweichungen zu anderen Tools.
    /// </summary>
    private static int Attributes(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Kein Pfad angegeben.");
            return 2;
        }

        ScanResult result = new ApiScanner()
            .ScanAsync(new ScanOptions { RootPath = args[0] }, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        ScanTree tree = result.Tree;

        var groups = new SortedDictionary<string, (long Count, long Bytes)>();
        void Add(string key, long bytes)
        {
            groups.TryGetValue(key, out var v);
            groups[key] = (v.Count + 1, v.Bytes + bytes);
        }

        long total = 0;
        for (int i = 0; i < tree.Count; i++)
        {
            ref FsEntry e = ref tree.Entries[i];
            if (e.IsReparsePoint)
                Add($"Reparse ({ReparseTags.Describe(e.ReparseTag)}){(e.IsDirectory ? " Ordner" : " Datei")}", e.Length);
            if (e.IsDirectory) continue;

            total += e.Length;
            if (e.IsCompressed) Add("komprimiert", e.Length);
            if (e.IsSparse) Add("sparse", e.Length);
            if ((e.Attributes & (FileAttr.Offline | FileAttr.RecallOnOpen | FileAttr.RecallOnDataAccess)) != 0)
                Add("Cloud/offline", e.Length);
        }

        Console.WriteLine($"Gesamt logisch: {SizeFormat.Bytes(total)} in {result.FileCount:N0} Dateien");
        foreach (var (key, v) in groups)
            Console.WriteLine($"  {key,-36} {v.Count,12:N0}  {SizeFormat.Bytes(v.Bytes),12}");
        return 0;
    }

    // ------------------------------------------------------------- Umlagern

    private static OffloadJournal OpenJournal(string[] args)
        => new(ReadOption(args, "--journal"));

    private static int CheckRelocation(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("sizeshift pruefen <Ordner> <Zielwurzel>");
            return 2;
        }
        using OffloadJournal journal = OpenJournal(args);
        RelocationCheck check = new OffloadEngine(journal).PrepareAsync(args[0], RelocationGuard.BuildTargetPath(args[0], args[1]), CancellationToken.None)
            .GetAwaiter().GetResult();
        PrintCheck(check);
        return check.IsBlocked ? 1 : 0;
    }

    private static void PrintCheck(RelocationCheck check)
    {
        Console.WriteLine($"Quelle  {check.SourcePath}  ({SizeFormat.Bytes(check.SourceBytes)}, {check.SourceFiles:N0} Dateien)");
        Console.WriteLine($"Ziel    {check.TargetPath}  ({SizeFormat.Bytes(check.TargetFreeBytes)} frei)");
        if (check.Issues.Count == 0) Console.WriteLine("Keine Einwaende.");
        foreach (GuardIssue issue in check.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
        Console.WriteLine(check.IsBlocked ? "=> GESPERRT" : check.HasWarnings ? "=> moeglich, Warnungen bestaetigen mit --trotzdem" : "=> moeglich");
    }

    private static int Relocate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("sizeshift umlagern <Ordner> <Zielwurzel> [--endgueltig] [--voll-pruefen] [--trotzdem]");
            return 2;
        }

        DeletionMode deletion = args.Contains("--endgueltig") ? DeletionMode.Endgueltig : DeletionMode.Papierkorb;
        using OffloadJournal journal = OpenJournal(args);
        var engine = new OffloadEngine(journal);
        var progress = new Progress<OffloadProgress>(p =>
            Console.WriteLine($"  [{p.Step}] {p.Message}" + (p.Fraction is { } f ? $" ({f:P0})" : "")));

        OffloadOutcome outcome = engine.RelocateAsync(args[0], RelocationGuard.BuildTargetPath(args[0], args[1]), deletion,
                fullHash: args.Contains("--voll-pruefen"), acceptWarnings: args.Contains("--trotzdem"),
                progress, CancellationToken.None)
            .GetAwaiter().GetResult();

        Thread.Sleep(100);   // letzte Fortschrittsmeldungen durchlassen
        if (outcome.Check is { } check && !outcome.Success) PrintCheck(check);
        Console.WriteLine((outcome.Success ? "ERFOLG" : "NICHT UMGELAGERT") +
                          (outcome.JournalId is { } id ? $" (Journal #{id})" : "") + ": " + outcome.Message);
        return outcome.Success ? 0 : 1;
    }

    private static int Undo(string[] args)
    {
        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            Console.Error.WriteLine("sizeshift zuruecknehmen <Journal-Nr>");
            return 2;
        }
        using OffloadJournal journal = OpenJournal(args);
        var progress = new Progress<OffloadProgress>(p => Console.WriteLine($"  [{p.Step}] {p.Message}"));
        OffloadOutcome outcome = new OffloadEngine(journal).UndoAsync(id, progress, CancellationToken.None)
            .GetAwaiter().GetResult();
        Thread.Sleep(100);
        Console.WriteLine((outcome.Success ? "ERFOLG: " : "FEHLGESCHLAGEN: ") + outcome.Message);
        return outcome.Success ? 0 : 1;
    }

    private static int ShowJournal(string[] args)
    {
        using OffloadJournal journal = OpenJournal(args);
        IReadOnlyList<JournalEntry> entries = journal.All();
        if (entries.Count == 0) Console.WriteLine("Noch keine Umlagerungen.");
        foreach (JournalEntry e in entries)
        {
            Console.WriteLine($"#{e.Id,-4} {e.Status,-18} {SizeFormat.Bytes(e.Bytes),10}  {e.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
            Console.WriteLine($"      {e.Source}  ->  {e.Target}");
            if (e.Message.Length > 0) Console.WriteLine($"      {e.Message}");
            if (e.LeftoverPath is not null) Console.WriteLine($"      Rest: {e.LeftoverPath}");
        }
        return 0;
    }

    private static string MakeStem(string rootPath)
    {
        string cleaned = rootPath.Replace(":", "").Replace("\\", "-").Replace("/", "-").Trim('-', ' ');
        if (cleaned.Length == 0) cleaned = "scan";
        foreach (char invalid in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(invalid, '_');
        return $"sizeshift-{cleaned}-{DateTime.Now:yyyyMMdd-HHmm}";
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static IEnumerable<int> EnumerateFiles(ScanTree tree)
    {
        for (int i = 0; i < tree.Count; i++)
            if (!tree.Entries[i].IsDirectory) yield return i;
    }

    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int value))
                return value;
        return fallback;
    }

    private static string Wrap(string text, int width, int indent)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (string word in words)
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return string.Join(Environment.NewLine + new string(' ', indent), lines);
    }

    private static int ShowSystem()
    {
        Console.Write(TreeSizeBetter.Core.SystemInfo.SystemInfoReader.ToText(TreeSizeBetter.Core.SystemInfo.SystemInfoReader.Read()));
        return 0;
    }

    /// <summary>Kompletter Systembericht als Markdown (wie "KI-Bericht" im System-Bereich).</summary>
    private static int WriteReport(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : Path.Combine(TreeSizeBetter.Core.Util.AppPaths.AiReportDirectory, $"Systembericht-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.md");
        var status = new Progress<string>(text => Console.Error.WriteLine("  " + text));
        string markdown = TreeSizeBetter.Core.SystemInfo.SystemReport.CreateAsync(null, status, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(path, markdown, new System.Text.UTF8Encoding(false));
        Console.WriteLine(path);
        return 0;
    }

    /// <summary>Prozesse aufzeichnen: "prozesse [sekunden] [datei]" - schreibt einen Prozessbericht als Markdown.</summary>
    private static int RecordProcesses(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 60;
        string path = args.Length > 1 ? args[1]
            : Path.Combine(TreeSizeBetter.Core.Util.AppPaths.AiReportDirectory, $"Prozessbericht-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}.md");
        Console.Error.WriteLine($"  Zeichne {seconds} s auf ...");
        var recording = TreeSizeBetter.Core.SystemInfo.ProcessMonitor
            .RecordAsync(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(2), null, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(path, TreeSizeBetter.Core.SystemInfo.ProcessReport.Build(recording, null), new System.Text.UTF8Encoding(false));
        Console.WriteLine(path);
        return 0;
    }
}