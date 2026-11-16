using Microsoft.VisualBasic.FileIO;
using TreeSizeBetter.Core.Localization;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Offload;

public enum OffloadStep
{
    Pruefen,
    Kopieren,
    Vergleichen,
    Umschalten,
    Testen,
    OriginalEntfernen,
    Fertig
}

public sealed record OffloadProgress(OffloadStep Step, string Message, double? Fraction = null);

public sealed record OffloadOutcome(bool Success, string Message, long? JournalId = null, RelocationCheck? Check = null);

/// <summary>
/// Lagert einen Ordner auf ein anderes Laufwerk um und laesst an der alten
/// Stelle eine Junction zurueck. Programme merken davon nichts.
///
/// Ablauf - jeder Schritt steht vorher im Journal:
///  1. Pruefen       Sperrlisten, Platz, laufende Programme
///  2. Kopieren      Robocopy auf das Ziel; das Original bleibt unberuehrt
///  3. Vergleichen   Bestand, Groessen, Zeitstempel, Pruefsummen-Stichprobe
///  4. Umschalten    Original umbenennen (atomar - scheitert sauber, wenn darin
///                   etwas geoeffnet ist), sofort die Junction an den alten Pfad
///  5. Testen        durch die Junction lesen
///  6. Original weg  Papierkorb oder endgueltig
///
/// Bis einschliesslich Schritt 3 ist jeder Fehler folgenlos: die Kopie wird
/// verworfen, das Original war nie angefasst. In Schritt 4 liegt das Original
/// nur umbenannt daneben und wird bei jedem Fehler zurueckbenannt. Das
/// gefaehrliche Fenster "Pfad ist weder Original noch Junction" gibt es nicht.
/// </summary>
public sealed class OffloadEngine
{
    private readonly OffloadJournal _journal;

    public OffloadEngine(OffloadJournal journal) => _journal = journal;

    public OffloadJournal Journal => _journal;

    /// <summary>Prueft ohne Nebenwirkung, ob und wohin umgelagert wuerde.</summary>
    public async Task<RelocationCheck> PrepareAsync(string source, string targetPath, CancellationToken ct)
    {
        ScanResult? scan = Directory.Exists(source)
            ? await new ApiScanner().ScanAsync(new ScanOptions { RootPath = source }, null, ct)
            : null;
        RelocationCheck check = RelocationGuard.Check(source, targetPath, scan);

        if (scan is not null && scan.DeniedCount > 0)
            check.Issues.Add(new GuardIssue(IssueSeverity.Sperre,
                Loc.F("offload.deniedSubfolders", scan.DeniedCount)));
        if (scan is not null && ContainsCloudFiles(scan))
            check.Issues.Add(new GuardIssue(IssueSeverity.Sperre,
                Loc.T("offload.cloudFiles")));
        return check;
    }

    public async Task<OffloadOutcome> RelocateAsync(
        string source, string targetPath, DeletionMode deletion, bool fullHash, bool acceptWarnings,
        IProgress<OffloadProgress>? progress, CancellationToken ct)
    {
        OffloadOutcome outcome = await RelocateCoreAsync(source, targetPath, deletion, fullHash, acceptWarnings, progress, ct);
        ActivityLog.Write(outcome.Success ? Loc.T("log.relocated") : Loc.T("log.relocateFailed"),
            $"{source} → {targetPath}" + (outcome.JournalId is { } id ? "  " + Loc.F("log.historyRef", id) : string.Empty) + $"  {outcome.Message}");
        return outcome;
    }

    /// <summary>
    /// Nimmt eine Verknuepfung ohne Verlaufseintrag in den Verlauf auf, damit sie
    /// sich zuruecknehmen laesst. Nur fuer Junctions, deren Ziel ein vorhandener
    /// Ordner auf einem anderen Laufwerk ist.
    /// </summary>
    public async Task<OffloadOutcome> AdoptAsync(string link, CancellationToken ct)
    {
        string? target = JunctionManager.ReadTarget(link);
        if (target is null) return new OffloadOutcome(false, Loc.F("offload.notALink", link));
        if (!Directory.Exists(target)) return new OffloadOutcome(false, Loc.F("offload.linkTargetMissing", target));

        JournalEntry? existing = _journal.All().FirstOrDefault(e => e.CanUndo &&
            string.Equals(e.Source.TrimEnd('\\'), link.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return new OffloadOutcome(true, Loc.T("offload.alreadyInHistory"), existing.Id);

        ScanResult scan = await new ApiScanner().ScanAsync(new ScanOptions { RootPath = target }, null, ct);
        long id = _journal.Adopt(link.TrimEnd('\\'), target.TrimEnd('\\'), scan.TotalBytesOnDisk, scan.FileCount);
        ActivityLog.Write(Loc.T("log.adopted"), $"{link} → {target}  " + Loc.F("log.adoptedRef", id));
        return new OffloadOutcome(true, Loc.T("offload.adopted"), id);
    }

    private async Task<OffloadOutcome> RelocateCoreAsync(
        string source, string targetPath, DeletionMode deletion, bool fullHash, bool acceptWarnings,
        IProgress<OffloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new OffloadProgress(OffloadStep.Pruefen, Loc.T("offload.step.precheck")));
        RelocationCheck check = await PrepareAsync(source, targetPath, ct);

        if (check.IsBlocked)
            return new OffloadOutcome(false, Loc.T("offload.blockedPrefix") + string.Join(" ", check.Issues
                .Where(i => i.Severity == IssueSeverity.Sperre).Select(i => i.Message)), Check: check);
        if (check.HasWarnings && !acceptWarnings)
            return new OffloadOutcome(false, Loc.T("offload.confirmWarnings"), Check: check);

        string from = check.SourcePath;
        string to = check.TargetPath;
        long id = _journal.Begin(from, to, check.SourceBytes, check.SourceFiles, deletion);

        // ---------------------------------------------------- 2. Kopieren
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            progress?.Report(new OffloadProgress(OffloadStep.Kopieren,
                Loc.F("offload.step.copy", SizeFormat.Bytes(check.SourceBytes), to), 0));

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task poll = PollCopyProgressAsync(to, check.SourceBytes, progress, pollCts.Token);
            CopyResult copy;
            try
            {
                copy = await RobocopyRunner.CopyAsync(from, to, ct);
            }
            finally
            {
                pollCts.Cancel();
                try { await poll; } catch (OperationCanceledException) { }
            }

            if (!copy.Success)
                return Fail(id, to, Loc.F("offload.robocopyError", copy.ExitCode, LastLines(copy.Output)));

            // ------------------------------------------------ 3. Vergleichen
            _journal.SetStatus(id, RelocationStatus.Pruefen);
            progress?.Report(new OffloadProgress(OffloadStep.Vergleichen, Loc.T("offload.step.compare")));
            var verifyProgress = new Progress<string>(m => progress?.Report(new OffloadProgress(OffloadStep.Vergleichen, m)));
            VerifyResult verify = await CopyVerifier.VerifyAsync(from, to, fullHash, verifyProgress, ct);
            if (!verify.Ok)
                return Fail(id, to, Loc.T("offload.copyDiffers") + " " + verify.Summary);

            // Zwischen Kopie und Umschalten koennte jemand das Spiel gestartet haben.
            IReadOnlyList<string> running = RelocationGuard.ProcessesRunningFrom(from);
            if (running.Count > 0)
                return Fail(id, to, Loc.F("offload.startedMeanwhile", running[0]));

            ct.ThrowIfCancellationRequested();
            return SwitchOver(id, from, to, deletion, verify, progress);
        }
        catch (OperationCanceledException)
        {
            DiscardCopy(to);
            _journal.SetStatus(id, RelocationStatus.Abgebrochen, Loc.T("offload.cancelledJournal"), finished: true);
            return new OffloadOutcome(false, Loc.T("offload.cancelled"), id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(id, to, ex.Message);
        }
    }

    /// <summary>
    /// Schritte 4 bis 6. Laeuft bewusst ohne Abbruchmoeglichkeit: zwischen
    /// Umbenennen und Junction soll nichts dazwischenkommen.
    /// </summary>
    private OffloadOutcome SwitchOver(long id, string from, string to, DeletionMode deletion,
        VerifyResult verify, IProgress<OffloadProgress>? progress)
    {
        _journal.SetStatus(id, RelocationStatus.Verknuepfen, verify.Summary);
        progress?.Report(new OffloadProgress(OffloadStep.Umschalten, Loc.T("offload.step.switch")));

        string parked = $"{from}.sizeshift-original-{id}";
        try
        {
            Directory.Move(from, parked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(id, to, Loc.F("offload.parkFailed", ex.Message));
        }

        try
        {
            JunctionManager.Create(from, to);

            progress?.Report(new OffloadProgress(OffloadStep.Testen, Loc.T("offload.step.test")));
            int throughLink = Directory.EnumerateFileSystemEntries(from).Count();
            int atTarget = Directory.EnumerateFileSystemEntries(to).Count();
            if (throughLink != atTarget || !JunctionManager.IsJunctionTo(from, to))
                throw new IOException(Loc.T("offload.junctionWrongContent"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Alles zurueck: Junction weg (falls angelegt), Original zurueckbenennen, Kopie verwerfen.
            try { if (JunctionManager.ReadTarget(from) is not null) JunctionManager.Remove(from); } catch (IOException) { }
            Directory.Move(parked, from);
            return Fail(id, to, Loc.F("offload.junctionFailed", ex.Message));
        }

        // Ab hier funktioniert alles; was jetzt noch schiefgeht, ist nur liegengebliebener Platz.
        _journal.SetStatus(id, RelocationStatus.OriginalEntfernen, verify.Summary);
        _journal.SetLeftover(id, parked);
        progress?.Report(new OffloadProgress(OffloadStep.OriginalEntfernen,
            deletion == DeletionMode.Papierkorb ? Loc.T("offload.step.recycleOriginal") : Loc.T("offload.step.deleteOriginal")));

        string? leftover = RemoveFolder(parked, deletion);
        _journal.SetLeftover(id, leftover);

        string message = verify.Summary + (leftover is null
            ? string.Empty
            : " " + Loc.F("offload.leftover", leftover));
        _journal.SetStatus(id, RelocationStatus.Fertig, message, finished: true);

        progress?.Report(new OffloadProgress(OffloadStep.Fertig, Loc.T("common.done"), 1));
        return new OffloadOutcome(true, message, id);
    }

    /// <summary>Macht eine Umlagerung rueckgaengig: Daten zurueck an den Originalort, Junction weg.</summary>
    public async Task<OffloadOutcome> UndoAsync(long id, IProgress<OffloadProgress>? progress, CancellationToken ct)
    {
        OffloadOutcome outcome = await UndoCoreAsync(id, progress, ct);
        JournalEntry? entry = _journal.Get(id);
        ActivityLog.Write(outcome.Success ? Loc.T("log.restored") : Loc.T("log.restoreFailed"),
            (entry is null ? Loc.F("log.historyNo", id) : $"{entry.Target} → {entry.Source}  " + Loc.F("log.historyRef", id)) + $"  {outcome.Message}");
        return outcome;
    }

    private async Task<OffloadOutcome> UndoCoreAsync(long id, IProgress<OffloadProgress>? progress, CancellationToken ct)
    {
        JournalEntry? entry = _journal.Get(id);
        if (entry is null) return new OffloadOutcome(false, Loc.F("offload.entryNotFound", id));
        if (!entry.CanUndo) return new OffloadOutcome(false, Loc.F("offload.entryNotUndoable", id, entry.StatusText));

        string link = entry.Source;
        string data = entry.Target;

        if (!JunctionManager.IsJunctionTo(link, data))
            return new OffloadOutcome(false, Loc.F("offload.noLongerJunction", link, data));
        if (!Directory.Exists(data))
            return new OffloadOutcome(false, Loc.F("offload.dataMissing", data));

        IReadOnlyList<string> running = RelocationGuard.ProcessesRunningFrom(data);
        if (running.Count > 0)
            return new OffloadOutcome(false, Loc.F("guard.running", running[0]));

        long needed = (await new ApiScanner().ScanAsync(new ScanOptions { RootPath = data }, null, ct)).TotalBytesOnDisk;
        var sourceDrive = new DriveInfo(Path.GetPathRoot(link)!);
        if (sourceDrive.AvailableFreeSpace < needed + needed / 20 + 512L * 1024 * 1024)
            return new OffloadOutcome(false, Loc.F("offload.noSpaceBack", sourceDrive.Name,
                                             SizeFormat.Bytes(sourceDrive.AvailableFreeSpace), SizeFormat.Bytes(needed)));

        _journal.SetStatus(id, RelocationStatus.ZuruecknahmeLaeuft);
        JunctionManager.Remove(link);

        try
        {
            progress?.Report(new OffloadProgress(OffloadStep.Kopieren, Loc.F("offload.step.copyBack", SizeFormat.Bytes(needed)), 0));
            CopyResult copy = await RobocopyRunner.CopyAsync(data, link, ct);
            if (!copy.Success) throw new IOException(Loc.F("offload.robocopyCode", copy.ExitCode, LastLines(copy.Output)));

            progress?.Report(new OffloadProgress(OffloadStep.Vergleichen, Loc.T("offload.step.compareShort")));
            VerifyResult verify = await CopyVerifier.VerifyAsync(data, link, fullHash: false, null, ct);
            if (!verify.Ok) throw new IOException(Loc.F("offload.copyBackDiffers", verify.Summary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Zurueck in den umgelagerten Zustand - der hat ja funktioniert.
            DiscardCopy(link);
            JunctionManager.Create(link, data);
            _journal.SetStatus(id, RelocationStatus.Fertig, Loc.F("offload.undoFailedJournal", ex.Message));
            return new OffloadOutcome(false, Loc.F("offload.undoFailed", ex.Message), id);
        }

        progress?.Report(new OffloadProgress(OffloadStep.OriginalEntfernen, Loc.T("offload.step.recycleCopy")));
        string? leftover = RemoveFolder(data, DeletionMode.Papierkorb);
        _journal.SetLeftover(id, leftover);
        _journal.SetStatus(id, RelocationStatus.Zurueckgenommen,
            leftover is null ? Loc.T("offload.backJournal") : Loc.F("offload.backJournalLeftover", leftover),
            finished: true);

        progress?.Report(new OffloadProgress(OffloadStep.Fertig, Loc.T("offload.undone"), 1));
        return new OffloadOutcome(true, Loc.T("offload.undoneLong"), id);
    }

    // ------------------------------------------------------------- Hilfen

    private OffloadOutcome Fail(long id, string partialCopy, string message)
    {
        DiscardCopy(partialCopy);
        _journal.SetStatus(id, RelocationStatus.Fehlgeschlagen, message, finished: true);
        return new OffloadOutcome(false, message, id);
    }

    /// <summary>Verwirft eine (Teil-)Kopie. Nie auf etwas anwenden, das das einzige Exemplar sein koennte.</summary>
    private static void DiscardCopy(string path)
    {
        try
        {
            if (JunctionManager.ReadTarget(path) is not null) return;   // nie durch eine Junction loeschen
            if (Directory.Exists(path)) DeletePermanently(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bleibt liegen - kostet Platz, gefaehrdet nichts.
        }
    }

    /// <summary>Entfernt einen Ordner und gibt bei Misserfolg den Pfad zurueck, der liegengeblieben ist.</summary>
    private static string? RemoveFolder(string path, DeletionMode mode)
    {
        try
        {
            if (mode == DeletionMode.Papierkorb)
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                    UICancelOption.DoNothing);
            else
                DeletePermanently(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Loescht einen Ordnerbaum endgueltig. Directory.Delete allein scheitert an
    /// schreibgeschuetzten Dateien - typisch fuer Spiel-Configs und Installer-
    /// Reste. Deshalb vorher den Schutz entfernen, und zwar ohne in verschachtelte
    /// Junctions hineinzulaufen: deren Ziel gehoert nicht zu diesem Ordner.
    /// </summary>
    public static void DeletePermanently(string root)
    {
        // Bewusst von Hand statt Directory.Delete(recursive): das scheitert an
        // verschachtelten Junctions mit "Zugriff verweigert", und auf das genaue
        // Verhalten gegenueber Verknuepfungen will ich mich beim Loeschen nicht
        // verlassen. Hier ist garantiert: Verknuepfungen werden nur als
        // Verknuepfung entfernt, nie wird hindurchgegangen.
        foreach (string entry in Directory.EnumerateFileSystemEntries(root).ToList())
        {
            FileAttributes attributes = File.GetAttributes(entry);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isLink = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && isLink)
            {
                Directory.Delete(entry, recursive: false);   // entfernt nur die Verknuepfung
                continue;
            }
            if (isDirectory)
            {
                DeletePermanently(entry);
                continue;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            File.Delete(entry);
        }

        FileAttributes own = File.GetAttributes(root);
        if ((own & FileAttributes.ReadOnly) != 0) File.SetAttributes(root, own & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: false);
    }

    private static bool ContainsCloudFiles(ScanResult scan)
    {
        const uint cloudish = Model.FileAttr.Offline | Model.FileAttr.RecallOnOpen | Model.FileAttr.RecallOnDataAccess;
        for (int i = 0; i < scan.Tree.Count; i++)
        {
            ref Model.FsEntry e = ref scan.Tree.Entries[i];
            if ((e.Attributes & cloudish) != 0 || ReparseTags.IsCloudPlaceholder(e.ReparseTag)) return true;
        }
        return false;
    }

    private static async Task PollCopyProgressAsync(string target, long total, IProgress<OffloadProgress>? progress,
        CancellationToken ct)
    {
        if (progress is null || total <= 0) return;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            if (!Directory.Exists(target)) continue;
            ScanResult scan = await new ApiScanner().ScanAsync(new ScanOptions { RootPath = target }, null, ct);
            double fraction = Math.Min(1, (double)scan.TotalBytesOnDisk / total);
            progress.Report(new OffloadProgress(OffloadStep.Kopieren,
                Loc.F("offload.copied", SizeFormat.Bytes(scan.TotalBytesOnDisk), SizeFormat.Bytes(total)), fraction));
        }
    }

    private static string LastLines(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(3));
    }
}
