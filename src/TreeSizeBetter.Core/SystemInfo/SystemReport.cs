using System.Globalization;
using System.Text;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Cleanup;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Offload;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.SystemInfo;

/// <summary>Ein gescanntes Laufwerk fuer den Bericht.</summary>
public sealed record DriveReportInput(string Root, ScanTree Tree, AgeAnalysis Age);

/// <summary>
/// Kompletter Systembericht als Markdown - gedacht zum Einfuegen in einen KI-Chat:
/// Hardware und Windows, jedes Laufwerk mit seinen groessten Ordnern, Aufraeum-Funde,
/// Umlager-Vorschlaege, bereits umgelagerte Ordner und der Autostart mit Einschaetzung.
/// Bewusst kompakt (Ordner ab 1 GB, je Laufwerk begrenzt), damit er in ein Chatfenster passt.
/// </summary>
public static class SystemReport
{
    private const long MinFolderBytes = 1024L * 1024 * 1024;
    private const int MaxFoldersPerDrive = 80;
    private const int MaxDepth = 5;

    /// <summary>
    /// Sammelt alles fuer den Bericht: Hardware, Autostart, jedes feste Laufwerk scannen,
    /// Aufraeum-Funde, Umlager-Kandidaten des Systemlaufwerks und den Umlager-Verlauf.
    /// Liefert das fertige Markdown. Gleicher Ablauf fuer App und Kommandozeile.
    /// </summary>
    public static async Task<string> CreateAsync(SystemSnapshot? snapshot, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report(Loc.T("system.loading"));
        snapshot ??= await Task.Run(SystemInfoReader.Read, ct);
        IReadOnlyList<AutostartEntry> autostart = await Task.Run(() => AutostartManager.ForThisPc().ReadAll(), ct);

        var drives = new List<DriveReportInput>();
        var findings = new List<CleanupFinding>();
        var suggestions = new List<RelocationSuggestion>();
        IReadOnlyList<CleanupRule> rules = CleanupCatalog.Build(KnownFolders.Current());
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(IsFixedAndReady))
        {
            string letter = drive.Name.TrimEnd('\\');
            status?.Report(Loc.F("report.scanning", letter, "0", SizeFormat.Bytes(0)));
            var progress = new Progress<Scanning.ScanProgress>(p =>
                status?.Report(Loc.F("report.scanning", letter, SizeFormat.Count(p.Files), SizeFormat.Bytes(p.Bytes))));
            Scanning.ScanResult result = await new Scanning.ApiScanner()
                .ScanAsync(new Scanning.ScanOptions { RootPath = drive.Name }, progress, ct);
            if (result.WasCancelled) throw new OperationCanceledException();

            status?.Report(Loc.F("report.analyzing", letter));
            AgeAnalysis age = await Task.Run(() => AgeAnalyzer.Analyze(result.Tree), ct);
            drives.Add(new DriveReportInput(drive.Name, result.Tree, age));
            findings.AddRange(await Task.Run(() => CleanupScanner.Find(result.Tree, rules, age.NowFileTime, ct), ct));
            if (string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                suggestions = await Task.Run(() => RelocationAdvisor.Suggest(result.Tree, age), ct);
        }

        IReadOnlyList<JournalEntry> relocations;
        using (var journal = new OffloadJournal()) relocations = journal.All();

        status?.Report(Loc.T("report.writing"));
        return Build(snapshot, autostart, drives, findings, suggestions, relocations, Scanning.Elevation.IsElevated);
    }

    private static bool IsFixedAndReady(DriveInfo drive)
    {
        try { return drive.IsReady && drive.DriveType == DriveType.Fixed; }
        catch (IOException) { return false; }
    }

    public static string Build(
        SystemSnapshot system,
        IReadOnlyList<AutostartEntry> autostart,
        IReadOnlyList<DriveReportInput> drives,
        IReadOnlyList<CleanupFinding> findings,
        IReadOnlyList<RelocationSuggestion> suggestions,
        IReadOnlyList<JournalEntry> relocations,
        bool elevated)
    {
        CultureInfo c = Loc.Culture;
        var md = new StringBuilder();

        md.AppendLine("# " + Loc.F("report.title", system.ComputerName));
        md.AppendLine();
        md.AppendLine(Loc.F("report.created", DateTime.Now.ToString("g", c), "SizeShift"));
        md.AppendLine();
        md.AppendLine("> " + Loc.T("report.forAi"));
        if (!elevated) md.AppendLine(">\n> " + Loc.T("report.notElevated"));
        md.AppendLine();

        // ------------------------------------------------------------ Hardware
        md.AppendLine("## " + Loc.T("report.section.system"));
        md.AppendLine();
        md.AppendLine("```");
        md.Append(SystemInfoReader.ToText(system));
        md.AppendLine("```");
        md.AppendLine();

        // ------------------------------------------------------------ Laufwerke
        md.AppendLine("## " + Loc.T("report.section.drives"));
        md.AppendLine();
        md.AppendLine($"| {Loc.T("report.col.drive")} | {Loc.T("report.col.size")} | {Loc.T("report.col.used")} | {Loc.T("report.col.free")} | {Loc.T("report.col.type")} |");
        md.AppendLine("|---|---:|---:|---:|---|");
        foreach (DiskInfo disk in system.Disks)
            foreach (VolumeInfo v in disk.Volumes)
                md.AppendLine($"| {($"{v.Letter} {Escape(v.Label)}").Trim()} | {SizeFormat.Bytes(v.SizeBytes)} | {SizeFormat.Bytes(v.UsedBytes)} ({Percent(v.UsedBytes, v.SizeBytes)}) | {SizeFormat.Bytes(v.FreeBytes)} | {SystemInfoReader.KindText(disk.Kind)}, {SystemInfoReader.HealthText(disk.Health)} |");
        md.AppendLine();

        foreach (DriveReportInput drive in drives)
        {
            md.AppendLine("### " + Loc.F("report.drive.heading", drive.Root.TrimEnd('\\')));
            md.AppendLine();
            md.AppendLine(Loc.F("report.drive.totals",
                SizeFormat.Bytes(drive.Tree.SizeOnDiskOf(ScanTree.RootIndex)),
                SizeFormat.Count(drive.Tree.AggregateOf(ScanTree.RootIndex).FileCount)));
            md.AppendLine();
            md.AppendLine($"| {Loc.T("report.col.folder")} | {Loc.T("report.col.used")} | {Loc.T("report.col.files")} | {Loc.T("report.col.lastUsed")} |");
            md.AppendLine("|---|---:|---:|---|");
            foreach (int index in LargestFolders(drive.Tree))
            {
                double? cold = AgeAnalyzer.ColdDays(drive.Tree, index, drive.Age.NowFileTime, drive.Age.AccessTimes.IsReliable);
                md.AppendLine($"| {Escape(drive.Tree.GetFullPath(index))} | {SizeFormat.Bytes(drive.Tree.SizeOnDiskOf(index))} | " +
                              $"{SizeFormat.Count(drive.Tree.AggregateOf(index).FileCount)} | {SizeFormat.Ago(cold)} |");
            }
            md.AppendLine();
        }

        // ------------------------------------------------------------ Aufraeumen
        md.AppendLine("## " + Loc.T("report.section.cleanup"));
        md.AppendLine();
        List<CleanupFinding> space = findings.Where(f => !f.Rule.IsPrivacy && f.Rule.Action != CleanupAction.NurHinweis)
            .OrderByDescending(f => f.Bytes).ToList();
        if (space.Count == 0)
        {
            md.AppendLine(Loc.T("report.none"));
        }
        else
        {
            md.AppendLine($"| {Loc.T("report.col.what")} | {Loc.T("report.col.rating")} | {Loc.T("report.col.used")} | {Loc.T("report.col.where")} |");
            md.AppendLine("|---|---|---:|---|");
            foreach (CleanupFinding f in space)
                md.AppendLine($"| {Escape(f.Rule.Title)} | {Loc.T("safety." + f.Rule.Safety)} | {SizeFormat.Bytes(f.Bytes)} | {Escape(f.Location)} |");
            md.AppendLine();
            md.AppendLine(Loc.F("report.cleanup.total",
                SizeFormat.Bytes(space.Where(f => f.Rule.Safety == FindingSafety.Sicher).Sum(f => f.Bytes)),
                SizeFormat.Bytes(space.Where(f => f.Rule.Safety != FindingSafety.Sicher).Sum(f => f.Bytes))));
        }
        md.AppendLine();

        List<CleanupFinding> hints = findings.Where(f => f.Rule.Action == CleanupAction.NurHinweis).ToList();
        if (hints.Count > 0)
        {
            md.AppendLine("**" + Loc.T("report.cleanup.hints") + "**");
            md.AppendLine();
            foreach (CleanupFinding f in hints)
                md.AppendLine($"- {f.Rule.Title}: {SizeFormat.Bytes(f.Bytes)} – {f.Rule.Advice}");
            md.AppendLine();
        }

        List<CleanupFinding> privacy = findings.Where(f => f.Rule.IsPrivacy).ToList();
        if (privacy.Count > 0)
        {
            md.AppendLine("**" + Loc.T("report.cleanup.privacy") + "**");
            md.AppendLine();
            foreach (CleanupFinding f in privacy)
                md.AppendLine($"- {f.Rule.Title}" + (f.Rule.IsVirtual ? string.Empty : $": {SizeFormat.Bytes(f.Bytes)}"));
            md.AppendLine();
        }

        // ------------------------------------------------------------ Umlagern
        md.AppendLine("## " + Loc.T("report.section.relocate"));
        md.AppendLine();
        if (suggestions.Count == 0)
        {
            md.AppendLine(Loc.T("report.none"));
        }
        else
        {
            md.AppendLine($"| {Loc.T("report.col.folder")} | {Loc.T("report.col.used")} | {Loc.T("report.col.kind")} | {Loc.T("report.col.lastUsed")} |");
            md.AppendLine("|---|---:|---|---|");
            foreach (RelocationSuggestion s in suggestions.Take(25))
                md.AppendLine($"| {Escape(s.Path)} | {s.BytesText} | {s.CategoryText} | {s.ColdText} |");
        }
        md.AppendLine();

        List<JournalEntry> moved = relocations.Where(r => r.CanUndo).ToList();
        if (moved.Count > 0)
        {
            md.AppendLine("**" + Loc.T("report.relocate.done") + "**");
            md.AppendLine();
            foreach (JournalEntry r in moved)
                md.AppendLine($"- {r.Source} → {r.Target} ({SizeFormat.Bytes(r.Bytes)})");
            md.AppendLine();
        }

        // ------------------------------------------------------------ Autostart
        md.AppendLine("## " + Loc.T("report.section.startup"));
        md.AppendLine();
        md.AppendLine(Loc.F("startup.summary", autostart.Count, autostart.Count(e => e.Enabled && !e.FileMissing)));
        md.AppendLine();
        md.AppendLine($"| {Loc.T("startup.col.name")} | {Loc.T("startup.col.state")} | {Loc.T("startup.col.location")} | {Loc.T("startup.col.publisher")} | {Loc.T("startup.col.command")} |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (AutostartEntry e in autostart
                     .OrderByDescending(e => e.Enabled && !e.FileMissing)
                     .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string state = e.FileMissing ? Loc.T("startup.state.missing") : e.Enabled ? Loc.T("startup.state.on") : Loc.T("startup.state.off");
            md.AppendLine($"| {Escape(e.Name)} | {state} | {LocationText(e.Location)} | {Escape(e.Publisher)} | `{Escape(e.Command)}` |");
        }
        md.AppendLine();

        md.AppendLine("---");
        md.AppendLine(Loc.T("report.footer"));
        return md.ToString();
    }

    /// <summary>
    /// Groesste Ordner bis zur fuenften Ebene ab 1 GB, nach Pfad sortiert - so liest sich
    /// die Liste wie ein Baum (C:\Users, C:\Users\ich, C:\Users\ich\AppData ...).
    /// Kleinere Zweige werden gar nicht erst betreten.
    /// </summary>
    private static IEnumerable<int> LargestFolders(ScanTree tree)
    {
        var candidates = new List<int>();
        var stack = new Stack<(int Index, int Depth)>();
        stack.Push((ScanTree.RootIndex, 0));
        while (stack.Count > 0)
        {
            (int parent, int depth) = stack.Pop();
            if (depth >= MaxDepth) continue;
            foreach (int child in tree.Children(parent))
            {
                ref FsEntry e = ref tree.Entries[child];
                if (!e.IsDirectory || e.IsReparsePoint || tree.SizeOnDiskOf(child) < MinFolderBytes) continue;
                candidates.Add(child);
                stack.Push((child, depth + 1));
            }
        }
        return candidates
            .Where(i => tree.SizeOnDiskOf(i) >= MinFolderBytes)
            .OrderByDescending(tree.SizeOnDiskOf)
            .Take(MaxFoldersPerDrive)
            .OrderBy(tree.GetFullPath, StringComparer.OrdinalIgnoreCase);
    }

    public static string LocationText(AutostartLocation location) => location switch
    {
        AutostartLocation.BenutzerRegistry => Loc.T("startup.loc.userRegistry"),
        AutostartLocation.AlleRegistry => Loc.T("startup.loc.allRegistry"),
        AutostartLocation.AlleRegistry32 => Loc.T("startup.loc.allRegistry32"),
        AutostartLocation.BenutzerOrdner => Loc.T("startup.loc.userFolder"),
        AutostartLocation.StoreApp => Loc.T("startup.loc.store"),
        _ => Loc.T("startup.loc.allFolder")
    };

    private static string Percent(long part, long whole) =>
        whole <= 0 ? "-" : ((double)part / whole).ToString("P0", Loc.Culture);

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", string.Empty);
}
