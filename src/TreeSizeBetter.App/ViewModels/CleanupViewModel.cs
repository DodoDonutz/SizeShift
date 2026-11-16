using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Cleanup;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Eine Zeile im Aufräumen-Tab.</summary>
public sealed class CleanupRow : ObservableObject
{
    private readonly Action _changed;

    public CleanupRow(CleanupFinding finding, Func<bool> recycleEverything, Action changed)
    {
        Finding = finding;
        _recycleEverything = recycleEverything;
        _changed = changed;
        // Privatsphaere wird nie vorab angehakt - das waehlt man bewusst.
        _isChecked = IsActionable && finding.Rule.Safety == FindingSafety.Sicher && !finding.Rule.IsPrivacy;
    }

    private readonly Func<bool> _recycleEverything;

    public CleanupFinding Finding { get; }
    public CleanupRule Rule => Finding.Rule;

    public string Group => Rule.Group;
    public string Title => Rule.Title;
    public string Explanation => Rule.Explanation;
    public string? Advice => Rule.Advice;
    public string Location => Finding.Location;
    public long Bytes => Finding.Bytes;
    public string BytesText => Rule.IsVirtual ? "–" : SizeFormat.Bytes(Finding.Bytes);
    public bool IsPrivacy => Rule.IsPrivacy;
    public FindingSafety Safety => Rule.Safety;
    public string SafetyText => Loc.T("safety." + Rule.Safety);

    public bool IsActionable => Rule.Action != CleanupAction.NurHinweis && !_done;

    public string ItemsText => Rule.Action switch
    {
        CleanupAction.NurHinweis => "-",
        CleanupAction.PapierkorbLeeren => Loc.F("main.status.files", Finding.Files),
        CleanupAction.ZwischenablageLeeren or CleanupAction.DnsCacheLeeren => "–",
        _ => Finding.Items.Count == 1 ? Loc.F("main.status.files", Finding.Files) : Loc.F("cleanup.items", Finding.Items.Count)
    };

    public string KeptText => Finding.KeptBytes > 0 ? Loc.F("cleanup.kept", SizeFormat.Bytes(Finding.KeptBytes)) : string.Empty;

    public string WhereText
    {
        get
        {
            if (_done) return _doneText;
            return Rule.Action switch
            {
                CleanupAction.NurHinweis => Loc.T("cleanup.where.hint"),
                CleanupAction.PapierkorbLeeren => Loc.T("cleanup.where.emptyBin"),
                CleanupAction.ZwischenablageLeeren or CleanupAction.DnsCacheLeeren => Loc.T("cleanup.where.clear"),
                _ => CleanupExecutor.GoesToRecycleBin(Finding, _recycleEverything()) ? Loc.T("relocate.toBin") : Loc.T("relocate.delete")
            };
        }
    }

    public bool NeedsAdmin => !Elevation.IsElevated && Rule.Action != CleanupAction.NurHinweis && !Rule.IsVirtual &&
        (Location.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
         Location.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase));

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!IsActionable) value = false;
            if (SetProperty(ref _isChecked, value)) _changed();
        }
    }

    private bool _done;
    private string _doneText = string.Empty;

    public bool IsDone => _done;

    public void MarkDone(string text)
    {
        _done = true;
        _doneText = text;
        IsChecked = false;
        OnPropertyChanged(nameof(IsActionable));
        OnPropertyChanged(nameof(WhereText));
        OnPropertyChanged(nameof(IsDone));
    }

    public void RefreshWhere() => OnPropertyChanged(nameof(WhereText));
}

/// <summary>Ein Laufwerk in der Auswahl des Aufräumen-Bereichs.</summary>
public sealed class CleanupDrive : ObservableObject
{
    public CleanupDrive(string root, string letter, string label, string freeText)
    {
        Root = root;
        Letter = letter;
        Label = label;
        FreeText = freeText;
    }

    public string Root { get; }
    public string Letter { get; }
    public string Label { get; }
    public string FreeText { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>Der Aufräumen-Tab: Regeltreffer zeigen, anhaken, aufräumen.</summary>
public sealed class CleanupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public CleanupViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);

        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy && Rows.Any(r => r.IsChecked));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectSafeCommand = new RelayCommand(() => SetChecks(r => r.Safety == FindingSafety.Sicher && !r.IsPrivacy));
        SelectAllCommand = new RelayCommand(() => SetChecks(_ => true));
        SelectNoneCommand = new RelayCommand(() => SetChecks(_ => false));
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy && Drives.Any(d => d.IsChecked));
    }

    /// <summary>Fragt vor dem Aufräumen nach. Vom Fenster gesetzt (MessageBox).</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Nach dem Aufräumen ist der Scan veraltet.</summary>
    public event Action<string>? FilesystemChanged;

    public ObservableCollection<CleanupRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }

    public IAsyncRelayCommand CleanCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand SelectSafeCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand SelectNoneCommand { get; }

    private bool _recycleEverything;
    /// <summary>Paranoid: auch "sichere" Funde nur in den Papierkorb.</summary>
    public bool RecycleEverything
    {
        get => _recycleEverything;
        set
        {
            if (!SetProperty(ref _recycleEverything, value)) return;
            foreach (CleanupRow row in Rows) row.RefreshWhere();
        }
    }

    private CleanupRow? _selectedRow;
    public CleanupRow? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    public long SelectedBytes => Rows.Where(r => r.IsChecked).Sum(r => r.Bytes);
    public string SelectedText => SizeFormat.Bytes(SelectedBytes);
    public string CleanButtonText => SelectedBytes > 0 ? Loc.F("cleanup.button", SizeFormat.Bytes(SelectedBytes)) : Loc.T("area.cleanup");

    public string SummaryText
    {
        get
        {
            if (Rows.Count == 0) return Loc.T("cleanup.noFindings");
            long safe = Rows.Where(r => r.IsActionable && !r.IsPrivacy && r.Safety == FindingSafety.Sicher).Sum(r => r.Bytes);
            long careful = Rows.Where(r => r.IsActionable && !r.IsPrivacy && r.Safety != FindingSafety.Sicher).Sum(r => r.Bytes);
            return Loc.F("cleanup.summary", SizeFormat.Bytes(safe), SizeFormat.Bytes(careful));
        }
    }

    public bool HasRows => Rows.Count > 0;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            CleanCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            SearchCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShowStart));
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

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    // ------------------------------------------------------------- Laufwerke

    /// <summary>Alle festen Laufwerke zum Anhaken - aufgeraeumt wird, was angehakt ist.</summary>
    public ObservableCollection<CleanupDrive> Drives { get; } = new();

    public IAsyncRelayCommand SearchCommand { get; private set; } = null!;

    /// <summary>Ein ganzes Laufwerk wurde frisch gescannt - andere Bereiche koennen die Zahlen mitnutzen.</summary>
    public event Action<ScanTree>? DriveScanned;

    /// <summary>Scans ganzer Laufwerke aus der Analyse, damit sie nicht doppelt laufen.</summary>
    private readonly Dictionary<string, (ScanTree Tree, DateTime Utc)> _driveScans = new(StringComparer.OrdinalIgnoreCase);

    private bool _hasSearched;
    /// <summary>Noch nichts durchsucht - dann zeigt der Bereich die Laufwerksauswahl gross.</summary>
    public bool ShowStart => !_hasSearched && !IsBusy;

    public string DrivesText
    {
        get
        {
            List<string> picked = Drives.Where(d => d.IsChecked).Select(d => d.Letter).ToList();
            return picked.Count == 0 ? Loc.T("cleanup.checkDrive") : Loc.F("cleanup.findingsOn", string.Join(", ", picked));
        }
    }

    /// <summary>Nur diese Laufwerke anbieten (Demo-Modus); null = alle festen.</summary>
    public static IReadOnlyCollection<string>? DriveFilter { get; set; }

    public void LoadDrives()
    {
        if (Drives.Count > 0) return;
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                if (DriveFilter is { } only && !only.Contains(drive.Name, StringComparer.OrdinalIgnoreCase)) continue;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Loc.T("cleanup.drive") : drive.VolumeLabel;
                var choice = new CleanupDrive(drive.Name, drive.Name.TrimEnd('\\'), label,
                    Loc.F("drive.freeOf", SizeFormat.Bytes(drive.AvailableFreeSpace), SizeFormat.Bytes(drive.TotalSize)))
                {
                    IsChecked = string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)
                };
                choice.PropertyChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(DrivesText));
                    SearchCommand.NotifyCanExecuteChanged();
                };
                Drives.Add(choice);
            }
            catch (IOException)
            {
            }
        }
        OnPropertyChanged(nameof(DrivesText));
        SearchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Nach einer Analyse: Scan eines ganzen Laufwerks merken. Solange noch nicht
    /// gezielt gesucht wurde, zeigen wir wie bisher gleich dessen Funde.
    /// </summary>
    public async Task UpdateAsync(ScanContext context)
    {
        string root = context.Tree.RootPath;
        bool wholeDrive = string.Equals(Path.GetPathRoot(root), root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (wholeDrive) _driveScans[root.TrimEnd('\\') + "\\"] = (context.Tree, DateTime.UtcNow);
        if (_hasSearched) return;

        LoadDrives();
        List<CleanupFinding> findings = await Task.Run(() =>
            CleanupScanner.Find(context.Tree, CleanupCatalog.Build(KnownFolders.Current()), context.Age.NowFileTime));
        if (wholeDrive)
            foreach (CleanupDrive d in Drives) d.IsChecked = string.Equals(d.Root, root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        ShowFindings(findings);
    }

    /// <summary>Durchsucht alle angehakten Laufwerke und zeigt die Funde zusammen.</summary>
    private async Task SearchAsync()
    {
        List<CleanupDrive> picked = Drives.Where(d => d.IsChecked).ToList();
        if (picked.Count == 0) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultText = string.Empty;
        var findings = new List<CleanupFinding>();
        IReadOnlyList<CleanupRule> rules = CleanupCatalog.Build(KnownFolders.Current());

        try
        {
            foreach (CleanupDrive drive in picked)
            {
                ScanTree tree;
                if (_driveScans.TryGetValue(drive.Root, out var cached) && DateTime.UtcNow - cached.Utc < TimeSpan.FromMinutes(15))
                {
                    tree = cached.Tree;
                }
                else
                {
                    var progress = new Progress<ScanProgress>(p =>
                        ProgressText = Loc.F("cleanup.searchingProgress", drive.Letter, p.Files, SizeFormat.Bytes(p.Bytes)));
                    ProgressText = Loc.F("cleanup.searching", drive.Letter);
                    ScanResult result = await new ApiScanner().ScanAsync(new ScanOptions { RootPath = drive.Root }, progress, _cts.Token);
                    if (result.WasCancelled) throw new OperationCanceledException();
                    tree = result.Tree;
                    _driveScans[drive.Root] = (tree, DateTime.UtcNow);
                    DriveScanned?.Invoke(tree);
                }

                ProgressText = Loc.F("cleanup.checkingRules", drive.Letter);
                findings.AddRange(await Task.Run(() => CleanupScanner.Find(tree, rules, FileTimeUtil.NowFileTime(), _cts.Token)));
            }

            _hasSearched = true;
            ShowFindings(findings);
            ActivityLog.Write(Loc.T("area.cleanup"), Loc.F("cleanup.log.searched", string.Join(", ", picked.Select(d => d.Letter)), findings.Count,
                                           SizeFormat.Bytes(findings.Where(f => f.Rule.Action != CleanupAction.NurHinweis).Sum(f => f.Bytes))));
        }
        catch (OperationCanceledException)
        {
            ResultText = Loc.T("cleanup.searchCancelled");
        }
        catch (Exception ex)
        {
            ResultText = Loc.F("cleanup.searchFailed", ex.Message);
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            OnPropertyChanged(nameof(ShowStart));
        }
    }

    private void ShowFindings(List<CleanupFinding> findings)
    {
        Rows.Clear();
        ResultText = string.Empty;
        foreach (CleanupFinding finding in findings
                     .OrderBy(f => f.Rule.Action == CleanupAction.NurHinweis)
                     .ThenByDescending(f => f.Bytes))
            Rows.Add(new CleanupRow(finding, () => RecycleEverything, SelectionChanged));

        _hasSearched = true;
        SelectionChanged();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowStart));
        SelectedRow = Rows.FirstOrDefault();
    }

    private void SetChecks(Func<CleanupRow, bool> predicate)
    {
        foreach (CleanupRow row in Rows) row.IsChecked = row.IsActionable && predicate(row);
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CleanButtonText));
        CleanCommand.NotifyCanExecuteChanged();
    }

    private async Task CleanAsync()
    {
        List<CleanupRow> chosen = Rows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) return;

        long permanent = chosen.Where(r => r.WhereText == Loc.T("relocate.delete") || r.WhereText == Loc.T("cleanup.where.emptyBin")).Sum(r => r.Bytes);
        long recycled = chosen.Where(r => r.WhereText == Loc.T("relocate.toBin")).Sum(r => r.Bytes);
        string question =
            Loc.F("cleanup.confirm", chosen.Count, SizeFormat.Bytes(permanent + recycled)) + "\n\n" +
            (permanent > 0 ? Loc.F("cleanup.confirm.permanent", SizeFormat.Bytes(permanent)) + "\n" : string.Empty) +
            (recycled > 0 ? Loc.F("cleanup.confirm.recycled", SizeFormat.Bytes(recycled)) + "\n" : string.Empty) +
            "\n" + Loc.T("cleanup.confirm.openFiles");
        if (Confirm is { } confirm && !confirm(question)) return;

        List<string> drives = chosen.Select(r => Path.GetPathRoot(r.Location))
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!.ToUpperInvariant())
            .Distinct().ToList();
        Dictionary<string, long> freeBefore = drives.ToDictionary(d => d, FreeSpace);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultText = string.Empty;
        ProgressValue = 0;
        var progress = new Progress<CleanupProgress>(p =>
        {
            ProgressText = p.Message;
            ProgressValue = p.Fraction * 100;
        });

        try
        {
            CleanupOutcome outcome = await Task.Run(() => CleanupExecutor.Run(
                chosen.Select(r => r.Finding).ToList(), RecycleEverything, progress, _cts.Token));

            foreach (CleanupRow row in chosen) row.MarkDone(Loc.T("cleanup.cleaned"));


            var parts = new List<string>();
            if (outcome.DeletedFiles > 0 || outcome.RecycleBinEmptied)
                parts.Add(Loc.F("cleanup.result.deleted", SizeFormat.Bytes(outcome.DeletedBytes)));
            if (outcome.RecycledItems > 0)
                parts.Add(Loc.F("cleanup.result.recycled", SizeFormat.Bytes(outcome.RecycledBytes)));
            if (outcome.VirtualCleared > 0)
                parts.Add(Loc.F("cleanup.virtualCleared", outcome.VirtualCleared));
            if (outcome.KeptYoung > 0)
                parts.Add(Loc.F("cleanup.result.kept", outcome.KeptYoung));
            if (outcome.Skipped > 0)
                parts.Add(Loc.F("cleanup.result.skipped", outcome.Skipped));
            foreach (string drive in drives)
            {
                long gained = FreeSpace(drive) - freeBefore[drive];
                if (gained > 0) parts.Add(Loc.F("cleanup.result.gained", drive.TrimEnd('\\'), SizeFormat.Bytes(gained)));
            }

            ResultText = Loc.F("cleanup.result", string.Join("  ·  ", parts));
            if (outcome.Problems.Count > 0)
                ResultText += "\n" + Loc.F("cleanup.result.example", outcome.Problems[0]);
            Core.Util.ActivityLog.Write(Loc.T("cleanup.log.cleaned"),
                string.Join(", ", chosen.Select(r => r.Title)) + "  –  " + string.Join("  ·  ", parts));

            FilesystemChanged?.Invoke(Loc.T("cleanup.log.cleaned"));
        }
        catch (OperationCanceledException)
        {
            ResultText = Loc.T("cleanup.cancelled");
            FilesystemChanged?.Invoke(Loc.T("cleanup.cancelledShort"));
        }
        catch (Exception ex)
        {
            ResultText = Loc.F("common.error", ex.Message);
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            SelectionChanged();
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private static long FreeSpace(string? drive)
    {
        if (string.IsNullOrEmpty(drive)) return 0;
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return 0; }
    }
}
