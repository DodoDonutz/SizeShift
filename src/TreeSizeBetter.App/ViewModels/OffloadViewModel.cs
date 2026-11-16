using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Offload;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Eine Zeile im Journal, fuer die Anzeige aufbereitet.</summary>
public sealed class JournalRow
{
    public JournalRow(JournalEntry entry) => Entry = entry;

    public JournalEntry Entry { get; }
    public long Id => Entry.Id;
    public string StatusText => Entry.Status switch
    {
        RelocationStatus.Fertig => Loc.T("journal.state.done"),
        RelocationStatus.Zurueckgenommen => Loc.T("journal.state.undone"),
        RelocationStatus.Fehlgeschlagen => Loc.T("journal.state.failed"),
        RelocationStatus.Abgebrochen => Loc.T("journal.state.cancelled"),
        _ => Loc.F("journal.state.interrupted", Entry.StatusText)
    };
    public string SizeText => SizeFormat.Bytes(Entry.Bytes);
    public string DateText => Entry.StartedUtc.ToLocalTime().ToString("g", Loc.Culture);
    public string Source => Entry.Source;
    public string Target => Entry.Target;
    public string Message => Entry.LeftoverPath is null ? Entry.Message : Entry.Message + "  " + Loc.F("journal.leftover", Entry.LeftoverPath);
    public bool CanUndo => Entry.CanUndo;
}

/// <summary>Ein Ordner im Umlager-Plan, wie er im Bestaetigungsdialog steht.</summary>
public sealed class PlanItem
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public long Bytes { get; init; }
    public long Files { get; init; }
    public List<GuardIssue> Issues { get; } = new();

    public bool IsBlocked => Issues.Any(i => i.Severity == IssueSeverity.Sperre);
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warnung);
    public string BytesText => Bytes > 0 ? SizeFormat.Bytes(Bytes) : "-";
    public string IssueText => string.Join("\n", Issues.Where(i => i.Severity != IssueSeverity.Hinweis).Select(i => i.Message));
    public string NoteText => string.Join("\n", Issues.Where(i => i.Severity == IssueSeverity.Hinweis).Select(i => i.Message));
    public string StateText => IsBlocked ? Loc.T("plan.state.blocked") : HasWarnings ? Loc.T("plan.state.warning") : Loc.T("plan.state.ready");
}

/// <summary>Was der Benutzer im Dialog freigegeben hat.</summary>
public sealed record PlanDecision(IReadOnlyList<PlanItem> Approved, bool AcceptWarnings);

/// <summary>
/// Der Umlagern-Bereich: links die Quelle mit Haekchen, rechts der Zielordner,
/// dazwischen die Pfeile. Jeder Ordner laeuft einzeln durch die bekannte
/// Paranoia-Kette (pruefen, kopieren, vergleichen, Junction, testen).
/// </summary>
public sealed class OffloadViewModel : ObservableObject, IDisposable
{
    private readonly OffloadJournal _journal = new();
    private readonly OffloadEngine _engine;
    private CancellationTokenSource? _cts;

    public OffloadViewModel()
    {
        _engine = new OffloadEngine(_journal);
        Sizes = new FolderSizeService();
        Left = new ExplorerPane(Sizes, hasCheckBoxes: true);
        Right = new ExplorerPane(Sizes, hasCheckBoxes: false) { SortBySize = false };
        Left.Changed += SelectionChanged;
        Right.Changed += SelectionChanged;

        MoveCommand = new AsyncRelayCommand(MoveAsync, CanMove);
        BackCommand = new AsyncRelayCommand(BackAsync, CanBack);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        UndoCommand = new AsyncRelayCommand<JournalRow>(UndoAsync, row => !IsBusy && row is { CanUndo: true });
        NewFolderCommand = new RelayCommand(NewFolder, () => !IsBusy && Right.Selected is { IsJunction: false });
        UncheckAllCommand = new RelayCommand(() => { foreach (ExplorerNode n in Left.Checked.ToList()) n.IsChecked = false; });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);

        LoadJournal();
    }

    /// <summary>Wird beim ersten Anzeigen des Bereichs aufgerufen.</summary>
    public void EnsureLoaded()
    {
        if (Left.Drives.Count > 0) return;
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        Left.LoadDrives(system);
        Right.LoadDrives(SuggestTargetDrive(system));
    }

    // ------------------------------------------------------------- Hooks fuer das Fenster

    /// <summary>Zeigt den Plan und liefert die Freigabe - oder null bei Abbruch.</summary>
    public Func<IReadOnlyList<PlanItem>, string, PlanDecision?>? ConfirmPlan { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Func<string, string?>? AskFolderName { get; set; }

    /// <summary>Nach einer Umlagerung oder Zuruecknahme ist der Analyse-Scan veraltet.</summary>
    public event Action<string>? FilesystemChanged;

    // ------------------------------------------------------------- Bestandteile

    public FolderSizeService Sizes { get; }
    public ExplorerPane Left { get; }
    public ExplorerPane Right { get; }

    public IAsyncRelayCommand MoveCommand { get; }
    public IAsyncRelayCommand BackCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand<JournalRow> UndoCommand { get; }
    public IRelayCommand NewFolderCommand { get; }

    /// <summary>Beide Seiten neu einlesen und alle Groessen frisch messen.</summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    private async Task RefreshAsync()
    {
        Sizes.Clear();
        await Left.ReloadAsync();
        await Right.ReloadAsync();
        LoadJournal();
        ResultText = string.Empty;
    }
    public IRelayCommand UncheckAllCommand { get; }

    public ObservableCollection<RelocationSuggestion> Suggestions { get; } = new();
    public ObservableCollection<JournalRow> Journal { get; } = new();

    public void UpdateFromScan(ScanContext context)
    {
        Sizes.UseScan(context.Tree);
        Suggestions.Clear();
        foreach (RelocationSuggestion suggestion in RelocationAdvisor.Suggest(context.Tree, context.Age))
            Suggestions.Add(suggestion);
    }

    // ------------------------------------------------------------- Einstellungen

    private bool _deletePermanently;
    /// <summary>false = Papierkorb (Standard): zweites Netz, Platz erst nach dem Leeren frei.</summary>
    public bool DeletePermanently
    {
        get => _deletePermanently;
        set
        {
            if (SetProperty(ref _deletePermanently, value)) OnPropertyChanged(nameof(DeleteToRecycleBin));
        }
    }

    public bool DeleteToRecycleBin
    {
        get => !_deletePermanently;
        set => DeletePermanently = !value;
    }

    private bool _fullHash;
    public bool FullHash
    {
        get => _fullHash;
        set => SetProperty(ref _fullHash, value);
    }

    // ------------------------------------------------------------- Anzeige

    private List<ExplorerNode> Sources => TopmostOnly(Left.Checked.Where(n => !n.IsRoot)).ToList();

    public string MoveText
    {
        get
        {
            List<ExplorerNode> sources = Sources.Where(n => !n.IsJunction).ToList();
            if (sources.Count == 0) return Loc.T("relocate.checkLeft");
            long bytes = sources.Sum(n => Math.Max(0, n.Bytes));
            string what = sources.Count == 1 ? sources[0].Name : Loc.F("main.status.folders", sources.Count);
            return $"{what} · {SizeFormat.Bytes(bytes)}";
        }
    }

    public string TargetText => Right.Selected is { } target
        ? (target.IsJunction ? Loc.T("relocate.targetIsJunction") : Loc.F("relocate.toPath", target.FullPath))
        : Loc.T("relocate.chooseRight");

    public string BackText
    {
        get
        {
            int count = Sources.Count(n => n.IsJunction);
            return count == 0 ? Loc.T("relocate.checkRelocated") : Loc.F("relocate.backCount", count);
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyCommands();
        }
    }

    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetProperty(ref _progressIndeterminate, value);
    }

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(MoveText));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(BackText));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        MoveCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private bool CanMove() => !IsBusy && Sources.Any(n => !n.IsJunction) && Right.Selected is { IsJunction: false };
    private bool CanBack() => !IsBusy && Sources.Any(n => n.IsJunction);

    // ------------------------------------------------------------- Umlagern

    private async Task MoveAsync()
    {
        if (Right.Selected is not { } targetNode) return;
        List<ExplorerNode> sources = Sources.Where(n => !n.IsJunction).ToList();
        string targetFolder = targetNode.FullPath;

        IsBusy = true;
        ResultText = string.Empty;
        ProgressIndeterminate = true;
        List<PlanItem> plan;
        try
        {
            plan = await BuildPlanAsync(sources, targetFolder);
        }
        catch (Exception ex)
        {
            ResultText = Loc.F("relocate.checkFailed", ex.Message);
            IsBusy = false;
            ProgressIndeterminate = false;
            ProgressText = string.Empty;
            return;
        }
        ProgressIndeterminate = false;
        ProgressText = string.Empty;

        PlanDecision? decision = ConfirmPlan?.Invoke(plan, targetFolder);
        if (decision is null || decision.Approved.Count == 0)
        {
            IsBusy = false;
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var results = new List<string>();
        int done = 0, ok = 0;
        long movedBytes = 0;

        try
        {
            foreach (PlanItem item in decision.Approved)
            {
                done++;
                ExplorerNode? node = Left.FindLoaded(item.Source);
                if (node is not null) node.Status = Loc.T("relocate.nodeMoving");
                string prefix = $"{done}/{decision.Approved.Count} · {item.Name}: ";

                var progress = new Progress<OffloadProgress>(p =>
                {
                    ProgressText = prefix + p.Message;
                    ProgressIndeterminate = p.Fraction is null;
                    if (p.Fraction is { } fraction) ProgressValue = fraction * 100;
                });

                OffloadOutcome outcome = await Task.Run(() => _engine.RelocateAsync(
                    item.Source, item.Target,
                    DeletePermanently ? DeletionMode.Endgueltig : DeletionMode.Papierkorb,
                    FullHash, decision.AcceptWarnings, progress, _cts.Token));

                if (outcome.Success)
                {
                    ok++;
                    movedBytes += item.Bytes;
                    results.Add($"✓ {item.Name}");
                }
                else
                {
                    results.Add($"✗ {item.Name}: {outcome.Message}");
                }

                Sizes.Invalidate(item.Source);
                Sizes.Invalidate(item.Target);
                if (node is not null)
                {
                    node.Status = null;
                    node.IsChecked = false;
                    await node.RefreshAsync();
                }
                if (_cts.IsCancellationRequested) break;
            }
        }
        catch (Exception ex)
        {
            results.Add(Loc.F("common.error", ex.Message));
        }
        finally
        {
            await RefreshTargetAsync(targetFolder);
            ProgressText = string.Empty;
            ProgressIndeterminate = false;
            IsBusy = false;
            LoadJournal();
        }

        ResultText = Loc.F("relocate.result", ok, decision.Approved.Count, SizeFormat.Bytes(movedBytes)) + "\n" + string.Join("\n", results);
        if (ok > 0) FilesystemChanged?.Invoke(Loc.F("relocate.changed", ok));
    }

    /// <summary>
    /// Prueft jeden Ordner einzeln und dann den Plan als Ganzes: zwei gleich
    /// benannte Ordner koennen nicht in denselben Zielordner, und der Platz am
    /// Ziel muss fuer alle zusammen reichen, nicht nur fuer jeden einzeln.
    /// </summary>
    private async Task<List<PlanItem>> BuildPlanAsync(List<ExplorerNode> sources, string targetFolder)
    {
        var plan = new List<PlanItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long freeLeft = FreeSpace(targetFolder);
        int index = 0;

        foreach (ExplorerNode source in sources)
        {
            index++;
            ProgressText = Loc.F("relocate.checking", index, sources.Count, source.Name);
            string target = RelocationGuard.TargetInFolder(source.FullPath, targetFolder);
            RelocationCheck check = await _engine.PrepareAsync(source.FullPath, target, CancellationToken.None);

            var item = new PlanItem
            {
                Name = source.Name,
                Source = check.SourcePath,
                Target = check.TargetPath,
                Bytes = check.SourceBytes,
                Files = check.SourceFiles
            };
            item.Issues.AddRange(check.Issues);

            if (!names.Add(source.Name))
                item.Issues.Add(new GuardIssue(IssueSeverity.Sperre, Loc.T("relocate.sameName")));

            if (!item.IsBlocked)
            {
                long needed = item.Bytes + item.Bytes / 20 + 1024L * 1024 * 1024;
                if (needed > freeLeft)
                    item.Issues.Add(new GuardIssue(IssueSeverity.Sperre,
                        Loc.F("relocate.notEnoughTogether", SizeFormat.Bytes(Math.Max(0, freeLeft)))));
                else
                    freeLeft -= item.Bytes;
            }
            plan.Add(item);
        }
        return plan;
    }

    // ------------------------------------------------------------- Zurueckholen

    private async Task BackAsync()
    {
        List<ExplorerNode> links = Sources.Where(n => n.IsJunction).ToList();
        var entries = new List<(ExplorerNode Node, JournalEntry? Entry)>();
        foreach (ExplorerNode node in links)
        {
            JournalEntry? entry = _journal.All()
                .FirstOrDefault(e => e.CanUndo && string.Equals(e.Source.TrimEnd('\\'), node.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            entries.Add((node, entry));
        }

        List<(ExplorerNode Node, JournalEntry Entry)> doable = entries.Where(e => e.Entry is not null).Select(e => (e.Node, e.Entry!)).ToList();
        List<ExplorerNode> foreign = entries.Where(e => e.Entry is null).Select(e => e.Node).ToList();

        // Verknuepfungen ohne Verlaufseintrag: anbieten, sie aufzunehmen. Dann laufen
        // sie durch dieselbe gepruefte Rueckkehr wie jede eigene Umlagerung.
        if (foreign.Count > 0)
        {
            string adoptQuestion =
                Loc.F("relocate.foreign", foreign.Count) + "\n\n" +
                string.Join("\n", foreign.Select(n => $"{n.FullPath}  →  {n.JunctionTarget}")) +
                "\n\n" + Loc.T("relocate.foreign.text") + "\n\n" + Loc.T("relocate.foreign.ask");
            if (Confirm is null || Confirm(adoptQuestion))
            {
                foreach (ExplorerNode node in foreign)
                {
                    OffloadOutcome adopted = await _engine.AdoptAsync(node.FullPath, CancellationToken.None);
                    if (adopted.Success && adopted.JournalId is { } id && _journal.Get(id) is { } entry)
                        doable.Add((node, entry));
                    else
                        ResultText = $"{node.Name}: {adopted.Message}";
                }
                LoadJournal();
            }
            if (doable.Count == 0) return;
        }

        string question = Loc.F("relocate.backAsk", doable.Count) + "\n\n" +
                          string.Join("\n", doable.Select(d => $"{d.Entry.Target}  →  {d.Entry.Source}  ({SizeFormat.Bytes(d.Entry.Bytes)})")) +
                          "\n\n" + Loc.T("relocate.backAsk.text");
        if (Confirm is { } confirm && !confirm(question)) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultText = string.Empty;
        var results = new List<string>();
        int done = 0, ok = 0;

        try
        {
            foreach (var (node, entry) in doable)
            {
                done++;
                node.Status = Loc.T("relocate.nodeBack");
                string prefix = $"{done}/{doable.Count} · {node.Name}: ";
                var progress = new Progress<OffloadProgress>(p =>
                {
                    ProgressText = prefix + p.Message;
                    ProgressIndeterminate = p.Fraction is null;
                    if (p.Fraction is { } fraction) ProgressValue = fraction * 100;
                });

                OffloadOutcome outcome = await Task.Run(() => _engine.UndoAsync(entry.Id, progress, _cts.Token));
                results.Add(outcome.Success ? $"✓ {node.Name}" : $"✗ {node.Name}: {outcome.Message}");
                if (outcome.Success) ok++;

                Sizes.Invalidate(entry.Source);
                Sizes.Invalidate(entry.Target);
                node.Status = null;
                node.IsChecked = false;
                await node.RefreshAsync();
                await RefreshTargetAsync(Path.GetDirectoryName(entry.Target) ?? entry.Target);
                if (_cts.IsCancellationRequested) break;
            }
        }
        finally
        {
            ProgressText = string.Empty;
            ProgressIndeterminate = false;
            IsBusy = false;
            LoadJournal();
        }

        ResultText = Loc.F("relocate.backResult", ok, doable.Count) + "\n" + string.Join("\n", results);
        if (ok > 0) FilesystemChanged?.Invoke(Loc.F("relocate.backChanged", ok));
    }

    private async Task UndoAsync(JournalRow? row)
    {
        if (row is null) return;
        if (Confirm is { } confirm && !confirm(Loc.F("relocate.undoAsk", row.Target, row.Source))) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultText = string.Empty;

        var progress = new Progress<OffloadProgress>(p =>
        {
            ProgressText = p.Message;
            ProgressIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction) ProgressValue = fraction * 100;
        });

        try
        {
            OffloadOutcome outcome = await Task.Run(() => _engine.UndoAsync(row.Id, progress, _cts.Token));
            ResultText = outcome.Message;
            Sizes.Invalidate(row.Source);
            Sizes.Invalidate(row.Target);
            if (Left.FindLoaded(row.Source) is { } node) await node.RefreshAsync();
            await RefreshTargetAsync(Path.GetDirectoryName(row.Target) ?? row.Target);
            if (outcome.Success) FilesystemChanged?.Invoke(Loc.F("relocate.isBack", row.Source));
        }
        catch (Exception ex)
        {
            ResultText = Loc.F("common.error", ex.Message);
        }
        finally
        {
            ProgressIndeterminate = false;
            ProgressText = string.Empty;
            IsBusy = false;
            LoadJournal();
        }
    }

    // ------------------------------------------------------------- Sonstiges

    private async Task RefreshTargetAsync(string folder)
    {
        foreach (ExplorerPane pane in new[] { Right, Left })
            if (pane.FindLoaded(folder) is { } node) await node.RefreshAsync();
    }

    private void NewFolder()
    {
        if (Right.Selected is not { } parent || AskFolderName?.Invoke(parent.FullPath) is not { } name) return;
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ResultText = Loc.F("relocate.invalidName", name);
            return;
        }

        string path = Path.Combine(parent.FullPath, name);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResultText = Loc.F("relocate.createFailed", ex.Message);
            return;
        }

        _ = RevealNewAsync(parent, path);
    }

    private async Task RevealNewAsync(ExplorerNode parent, string path)
    {
        await parent.RefreshAsync();
        parent.IsExpanded = true;
        await Right.RevealAsync(path);
    }

    /// <summary>Waehlt einen Ordner links aus (aus dem Analyse-Baum oder einem Vorschlag) und hakt ihn an.</summary>
    public async Task SelectSourceAsync(string path)
    {
        EnsureLoaded();
        ExplorerNode? node = await Left.RevealAsync(path);
        if (node is not null && !node.IsRoot) node.IsChecked = true;
    }

    private void LoadJournal()
    {
        Journal.Clear();
        foreach (JournalEntry entry in _journal.All()) Journal.Add(new JournalRow(entry));
        OnPropertyChanged(nameof(JournalTitle));
    }

    public string JournalTitle => Loc.F("journal.title", Journal.Count);

    private static IEnumerable<ExplorerNode> TopmostOnly(IEnumerable<ExplorerNode> nodes)
    {
        List<ExplorerNode> list = nodes.ToList();
        return list.Where(n =>
        {
            for (ExplorerNode? p = n.Parent; p is not null; p = p.Parent)
                if (list.Contains(p)) return false;
            return true;
        });
    }

    private static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return 0; }
    }

    /// <summary>Groesstes festes NTFS-Laufwerk ausser dem Systemlaufwerk.</summary>
    private static string? SuggestTargetDrive(string system)
    {
        DriveInfo? best = null;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || drive.AvailableFreeSpace > best.AvailableFreeSpace) best = drive;
            }
            catch (IOException)
            {
            }
        }
        return best?.Name;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _journal.Dispose();
    }
}
