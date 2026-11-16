using System.Runtime.InteropServices;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Localization;

namespace TreeSizeBetter.Core.Cleanup;

public sealed record CleanupProgress(string Message, double Fraction);

/// <summary>Was beim Aufräumen tatsächlich passiert ist.</summary>
public sealed class CleanupOutcome
{
    /// <summary>Endgültig gelöscht - dieser Platz ist sofort frei.</summary>
    public long DeletedBytes { get; set; }
    public int DeletedFiles { get; set; }

    /// <summary>In den Papierkorb verschoben - frei erst nach dem Leeren.</summary>
    public long RecycledBytes { get; set; }
    public int RecycledItems { get; set; }

    /// <summary>Gesperrt, verweigert oder verschwunden - liegt noch da.</summary>
    public int Skipped { get; set; }

    /// <summary>Zwischen Scan und Aufräumen neu geworden und deshalb verschont.</summary>
    public int KeptYoung { get; set; }

    public bool RecycleBinEmptied { get; set; }

    /// <summary>Geleert ohne Dateien: Zwischenablage, DNS-Cache.</summary>
    public int VirtualCleared { get; set; }

    public List<string> Problems { get; } = new();

    internal void Problem(string message)
    {
        Skipped++;
        if (Problems.Count < 50) Problems.Add(message);
    }
}

/// <summary>
/// Führt das Aufräumen aus. Leitplanken, unabhängig von den Regeln:
/// - Gelöscht wird nur innerhalb der Orte, die die Regel gefunden hat.
/// - Verknüpfungen (Junctions, Symlinks) werden nie durchlaufen, höchstens als
///   Verknüpfung selbst entfernt - ihr Ziel bleibt unberührt.
/// - Die Altersgrenze wird beim Löschen noch einmal an der echten Datei geprüft;
///   was seit dem Scan neu ist, bleibt liegen.
/// - Gesperrte Dateien werden übersprungen, nicht erzwungen.
/// - "Sicher" wird endgültig gelöscht (in den Papierkorb verschoben würde der
///   Platz gar nicht frei), alles andere geht in den Papierkorb.
/// </summary>
public static class CleanupExecutor
{
    public static bool GoesToRecycleBin(CleanupFinding finding, bool recycleEverything)
        => recycleEverything || (finding.Rule.Safety != FindingSafety.Sicher && !finding.Rule.IsPrivacy);

    public static CleanupOutcome Run(
        IReadOnlyList<CleanupFinding> findings, bool recycleEverything,
        IProgress<CleanupProgress>? progress, CancellationToken ct, DateTime? nowUtc = null)
    {
        var outcome = new CleanupOutcome();
        DateTime now = nowUtc ?? DateTime.UtcNow;
        int total = Math.Max(1, findings.Sum(f => Math.Max(1, f.Items.Count)));
        int done = 0;

        foreach (CleanupFinding finding in findings)
        {
            ct.ThrowIfCancellationRequested();
            CleanupRule rule = finding.Rule;
            progress?.Report(new CleanupProgress(rule.Title + " ...", (double)done / total));

            if (rule.Action == CleanupAction.NurHinweis)
            {
                done++;
                continue;
            }

            // Browser offen? Dann nicht halb loeschen, sondern ueberspringen und Bescheid sagen.
            if (RunningBlocker(rule) is { } blocker)
            {
                outcome.Problem(Loc.F("cleanup.closeFirst", rule.Title, blocker));
                done += Math.Max(1, finding.Items.Count);
                continue;
            }

            if (rule.Action == CleanupAction.ZwischenablageLeeren)
            {
                if (ClearClipboard()) outcome.VirtualCleared++;
                else outcome.Problem(Loc.T("cleanup.clipboardFailed"));
                done++;
                continue;
            }

            if (rule.Action == CleanupAction.DnsCacheLeeren)
            {
                if (DnsFlushResolverCache()) outcome.VirtualCleared++;
                else outcome.Problem(Loc.T("cleanup.dnsFailed"));
                done++;
                continue;
            }

            if (rule.Action == CleanupAction.PapierkorbLeeren)
            {
                foreach (string root in finding.Roots)
                {
                    string drive = Path.GetPathRoot(root) ?? root;
                    int hr = SHEmptyRecycleBin(IntPtr.Zero, drive, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
                    // S_OK, oder "war schon leer" (E_UNEXPECTED bei leerem Korb).
                    if (hr == 0 || hr == unchecked((int)0x8000FFFF))
                    {
                        outcome.RecycleBinEmptied = true;
                        outcome.DeletedBytes += finding.Bytes;
                    }
                    else
                    {
                        outcome.Problem(Loc.F("cleanup.binFailed", drive, $"0x{hr:X8}"));
                    }
                }
                done++;
                continue;
            }

            DateTime? cutoff = rule.MinAgeDays > 0 ? now.AddDays(-rule.MinAgeDays) : null;
            bool recycle = GoesToRecycleBin(finding, recycleEverything);
            var toRecycle = new List<CleanupItem>();

            foreach (CleanupItem item in finding.Items)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if ((done & 0x1F) == 0) progress?.Report(new CleanupProgress(rule.Title + " ...", (double)done / total));

                if (!IsInsideRoots(item.Path, finding)) { outcome.Problem(Loc.F("cleanup.outside", item.Path)); continue; }
                if (!Exists(item.Path, out FileAttributes attributes)) continue;    // schon weg
                if ((attributes & FileAttributes.ReparsePoint) != 0) { outcome.Problem(Loc.F("cleanup.link", item.Path)); continue; }

                if (recycle)
                {
                    // Bei Ordnern sorgte schon der Scan für die Altersgrenze; einzelne
                    // Dateien prüfen wir hier noch einmal.
                    if (cutoff is { } c && !item.IsDirectory && NewestUtc(item.Path) > c) { outcome.KeptYoung++; continue; }
                    toRecycle.Add(item);
                }
                else if (item.IsDirectory)
                {
                    DeleteTree(item.Path, cutoff, outcome);
                    TryRemoveEmptyDirectory(item.Path);
                }
                else
                {
                    DeleteFile(item.Path, cutoff, outcome);
                }
            }

            if (toRecycle.Count > 0) Recycle(toRecycle, outcome);
        }

        progress?.Report(new CleanupProgress(Loc.T("common.done"), 1));
        return outcome;
    }

    // ------------------------------------------------------------ endgültig

    private static void DeleteTree(string directory, DateTime? cutoff, CleanupOutcome outcome)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.Problem(Loc.F("cleanup.unreadable", directory));
            return;
        }

        foreach (string entry in entries)
        {
            if (!Exists(entry, out FileAttributes attributes)) continue;
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isLink = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && isLink)
            {
                // Nur die Verknüpfung selbst entfernen - nie das, worauf sie zeigt.
                try { Directory.Delete(entry, recursive: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outcome.Problem(Loc.F("cleanup.locked", entry)); }
                continue;
            }

            if (isDirectory)
            {
                DeleteTree(entry, cutoff, outcome);
                TryRemoveEmptyDirectory(entry);
                continue;
            }

            DeleteFile(entry, cutoff, outcome);
        }
    }

    private static void DeleteFile(string path, DateTime? cutoff, CleanupOutcome outcome)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (cutoff is { } c && Max(info.LastWriteTimeUtc, info.CreationTimeUtc) > c)
            {
                outcome.KeptYoung++;
                return;
            }

            long length = info.Length;
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes &= ~FileAttributes.ReadOnly;
            info.Delete();
            outcome.DeletedBytes += length;
            outcome.DeletedFiles++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typisch: Datei ist gerade von einem Programm geöffnet.
            outcome.Problem(Loc.F("cleanup.lockedOrDenied", path));
        }
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leerer Ordner, der stehen bleibt - kostet nichts.
        }
    }

    // ------------------------------------------------------------ Papierkorb

    private static void Recycle(List<CleanupItem> items, CleanupOutcome outcome)
    {
        const int batchSize = 200;
        for (int start = 0; start < items.Count; start += batchSize)
        {
            List<CleanupItem> batch = items.GetRange(start, Math.Min(batchSize, items.Count - start));
            bool ok = ShellDelete(batch.Select(i => i.Path));

            // Ein Fehler bricht den ganzen Stapel ab. Dann einzeln nachfassen,
            // damit eine gesperrte Datei nicht 199 andere mitreißt.
            if (!ok)
                foreach (CleanupItem item in batch.Where(i => Exists(i.Path, out _)))
                    ShellDelete(new[] { item.Path });

            foreach (CleanupItem item in batch)
            {
                if (Exists(item.Path, out _))
                {
                    outcome.Problem(Loc.F("cleanup.notRecycled", item.Path));
                }
                else
                {
                    outcome.RecycledBytes += item.Bytes;
                    outcome.RecycledItems++;
                }
            }
        }
    }

    private static bool ShellDelete(IEnumerable<string> paths)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    // ------------------------------------------------------------ Hilfen

    private static bool IsInsideRoots(string path, CleanupFinding finding)
    {
        if (path.Length <= 3) return false;     // nie eine Laufwerkswurzel
        foreach (string root in finding.Roots)
        {
            if (CleanupScanner.IsUnder(path, root)) return true;
            if (finding.Rule.Action == CleanupAction.GanzEntfernen &&
                string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool Exists(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
    }

    private static DateTime NewestUtc(string path)
    {
        try { return Max(File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>Name des ersten laufenden Programms, das die Regel blockiert - oder null.</summary>
    internal static string? RunningBlocker(CleanupRule rule)
    {
        if (rule.BlockingProcesses is null) return null;
        foreach (string name in rule.BlockingProcesses)
        {
            System.Diagnostics.Process[] running = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                if (running.Length > 0) return name;
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
        }
        return null;
    }

    /// <summary>Leert den aktuellen Inhalt der Zwischenablage. Ein paar Versuche, falls sie gerade belegt ist.</summary>
    private static bool ClearClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { return EmptyClipboard(); }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(60);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    /// <summary>Wie "ipconfig /flushdns" - braucht keine Administratorrechte.</summary>
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);
}
