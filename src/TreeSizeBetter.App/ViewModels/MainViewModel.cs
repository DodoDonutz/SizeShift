using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Export;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ApiScanner _scanner = new();
    private readonly DispatcherTimer _filterDebounce;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _queryCts;
    private ScanContext? _context;

    /// <summary>Der zuletzt abgeschlossene Scan - fuer Ansichten wie das Kacheldiagramm.</summary>
    public ScanContext? Context => _context;

    public MainViewModel()
    {
        Roots = new ObservableCollection<string>(EnumerateRoots());
        _selectedRoot = Roots.FirstOrDefault() ?? @"C:\";
        _rows = null!;

        // Filtereingaben wirken erst, wenn ein Moment nicht getippt wurde -
        // sonst laeuft bei jedem Tastendruck eine Abfrage ueber 1,4 Mio. Eintraege.
        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            _ = RefreshRowsAsync();
        };

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        CancelCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        ExportCommand = new AsyncRelayCommand<string>(ExportAsync, _ => CanExport);
        OpenExportFolderCommand = new RelayCommand(OpenExportFolder, () => _lastExportPath is not null);
        SetModeCommand = new RelayCommand<string>(SetMode);
        ScanPathCommand = new RelayCommand<string>(path => { if (path is not null) _ = ScanPathAsync(path); });
        DismissWarningCommand = new RelayCommand(() => WarningText = string.Empty);
        RevealCommand = new RelayCommand<int>(Reveal);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsElevated);
        RefreshDrives();

        Offload = new OffloadViewModel();
        Offload.FilesystemChanged += WarnScanIsStale;
        Cleanup.FilesystemChanged += WarnScanIsStale;
        // Scan des Systemlaufwerks beim Aufraeumen: Groessen im Umlagern-Bereich gleich daraus.
        Cleanup.DriveScanned += tree =>
        {
            string system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            if (string.Equals(tree.RootPath.TrimEnd('\\') + "\\", system, StringComparison.OrdinalIgnoreCase))
                Offload.Sizes.UseScan(tree);
        };
    }

    /// <summary>Der Aufräumen-Tab.</summary>
    public CleanupViewModel Cleanup { get; } = new();

    /// <summary>Der Umlagern-Tab.</summary>
    public OffloadViewModel Offload { get; }

    /// <summary>Der Bereich System: Uebersicht und Autostart.</summary>
    public SystemViewModel SystemArea { get; } = new();

    // ---------------------------------------------------------------- Bereiche

    private AppArea _area = AppArea.Analyse;
    /// <summary>Welcher Hauptbereich oben gewaehlt ist.</summary>
    public AppArea Area
    {
        get => _area;
        set
        {
            if (!SetProperty(ref _area, value)) return;
            OnPropertyChanged(nameof(IsAnalyse));
            OnPropertyChanged(nameof(IsCleanup));
            OnPropertyChanged(nameof(IsRelocate));
            OnPropertyChanged(nameof(IsSystem));
            OnPropertyChanged(nameof(ShowWelcome));
            OnPropertyChanged(nameof(ShowCleanupEmpty));
            OnPropertyChanged(nameof(ShowCleanupEmpty));
            if (value == AppArea.Umlagern) Offload.EnsureLoaded();
            if (value == AppArea.Aufraeumen) Cleanup.LoadDrives();
            if (value == AppArea.System) SystemArea.EnsureLoaded();
        }
    }

    public bool IsAnalyse { get => Area == AppArea.Analyse; set { if (value) Area = AppArea.Analyse; } }
    public bool IsCleanup { get => Area == AppArea.Aufraeumen; set { if (value) Area = AppArea.Aufraeumen; } }
    public bool IsRelocate { get => Area == AppArea.Umlagern; set { if (value) Area = AppArea.Umlagern; } }
    public bool IsSystem { get => Area == AppArea.System; set { if (value) Area = AppArea.System; } }

    /// <summary>Aufraeumen braucht einen Scan - ohne zeigt der Bereich einen Startknopf.</summary>
    public bool ShowCleanupEmpty => IsCleanup && RootNode is null && !IsScanning;

    /// <summary>Wechselt in den Umlagern-Bereich und hakt dort den Ordner an.</summary>
    public async Task RelocateFromAnalysisAsync(string path)
    {
        Area = AppArea.Umlagern;
        await Offload.SelectSourceAsync(path);
    }

    // ---------------------------------------------------------------- Anzeigemodus

    /// <summary>
    /// Standard wie bei TreeSize: belegter Platz. Die logische Groesse zaehlt
    /// Online-Platzhalter von iCloud und OneDrive mit, die lokal nichts belegen -
    /// fuer die Frage "wo hole ich Platz" fuehrt sie in die Irre.
    /// </summary>
    public DisplaySettings Display { get; } = new() { Mode = DisplayMode.SizeOnDisk };

    public IRelayCommand<string> SetModeCommand { get; }

    // Beschreibbar fuer die zweiseitige Bindung der Segmentknoepfe. Das Abwaehlen
    // (false) ignorieren wir: das passiert automatisch, wenn ein anderer Knopf der
    // Gruppe gewaehlt wird.
    public bool IsModeSize
    {
        get => Display.Mode == DisplayMode.Size;
        set { if (value) SetMode(nameof(DisplayMode.Size)); }
    }

    public bool IsModeSizeOnDisk
    {
        get => Display.Mode == DisplayMode.SizeOnDisk;
        set { if (value) SetMode(nameof(DisplayMode.SizeOnDisk)); }
    }

    public bool IsModeFileCount
    {
        get => Display.Mode == DisplayMode.FileCount;
        set { if (value) SetMode(nameof(DisplayMode.FileCount)); }
    }

    public bool IsModePercent
    {
        get => Display.Mode == DisplayMode.Percent;
        set { if (value) SetMode(nameof(DisplayMode.Percent)); }
    }

    private void SetMode(string? mode)
    {
        if (!Enum.TryParse(mode, out DisplayMode parsed) || parsed == Display.Mode) return;
        Display.Mode = parsed;

        OnPropertyChanged(nameof(IsModeSize));
        OnPropertyChanged(nameof(IsModeSizeOnDisk));
        OnPropertyChanged(nameof(IsModeFileCount));
        OnPropertyChanged(nameof(IsModePercent));

        RootNode?.RefreshDisplay();
    }

    // ---------------------------------------------------------------- Auswahl

    public ObservableCollection<string> Roots { get; }

    private string _selectedRoot;
    public string SelectedRoot
    {
        get => _selectedRoot;
        set => SetProperty(ref _selectedRoot, value);
    }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    public IRelayCommand ResetFiltersCommand { get; }
    public IAsyncRelayCommand<string> ExportCommand { get; }
    public IRelayCommand OpenExportFolderCommand { get; }

    // ---------------------------------------------------------------- Zustand

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            ScanCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            BrowseCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ShowWelcome));
            OnPropertyChanged(nameof(ShowCleanupEmpty));
            OnPropertyChanged(nameof(CanExport));
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsScanning;

    private bool _isQuerying;
    public bool IsQuerying
    {
        get => _isQuerying;
        private set => SetProperty(ref _isQuerying, value);
    }

    private string _progressText = Loc.T("main.nothingScanned");
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    private string _statusText = Loc.T("main.chooseAndScan");
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _warningText = string.Empty;
    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (!SetProperty(ref _warningText, value)) return;
            WarningOffersRescan = false;
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => WarningText.Length > 0;

    private bool _warningOffersRescan;
    /// <summary>Der Hinweis betrifft einen veralteten Scan - dann steht "Neu scannen" daneben.</summary>
    public bool WarningOffersRescan
    {
        get => _warningOffersRescan;
        private set => SetProperty(ref _warningOffersRescan, value);
    }

    private void WarnScanIsStale(string message)
    {
        WarningText = Loc.F("main.stale", message);
        WarningOffersRescan = !string.IsNullOrEmpty(SelectedRoot);
    }

    public IRelayCommand DismissWarningCommand { get; }

    /// <summary>Springt im Baum zu einem Eintrag - fuer anklickbare Listen.</summary>
    public IRelayCommand<int> RevealCommand { get; }

    // ---------------------------------------------------------------- Administrator

    /// <summary>Laeuft die App mit Administratorrechten?</summary>
    public bool IsElevated => Core.Scanning.Elevation.IsElevated;

    public bool IsNotElevated => !IsElevated;

    public string WindowTitle => IsElevated ? Loc.T("main.titleAdmin") : "SizeShift";

    public IRelayCommand RestartAsAdminCommand { get; }

    /// <summary>
    /// Startet die App mit Administratorrechten neu (Windows fragt per UAC nach)
    /// und uebergibt den aktuellen Pfad, damit dort direkt weitergescannt wird.
    /// Lehnt man ab, laeuft diese Instanz einfach weiter.
    /// </summary>
    private void RestartAsAdmin()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;

        // Ohne abschliessenden Backslash: "C:\" in Anfuehrungszeichen wuerde
        // das schliessende Anfuehrungszeichen maskieren.
        string path = (SelectedRoot ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = path.Length > 0 ? $"--scan \"{path}\"" : string.Empty
        };

        try
        {
            Process.Start(start);
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText = Loc.T("main.adminDeclined");
        }
    }

    // ---------------------------------------------------------------- Startbildschirm und Scan-Anzeige

    /// <summary>Startbildschirm mit Laufwerkskarten, solange nichts gescannt ist.</summary>
    public bool ShowWelcome => IsAnalyse && !IsScanning && RootNode is null;

    public ObservableCollection<DriveCard> Drives { get; } = new();

    public IRelayCommand<string> ScanPathCommand { get; }

    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (DriveCard card in DriveCard.ReadAll()) Drives.Add(card);
    }

    private string _scanEntriesText = "0";
    public string ScanEntriesText { get => _scanEntriesText; private set => SetProperty(ref _scanEntriesText, value); }

    private string _scanBytesText = "0 B";
    public string ScanBytesText { get => _scanBytesText; private set => SetProperty(ref _scanBytesText, value); }

    private string _scanRateText = string.Empty;
    public string ScanRateText { get => _scanRateText; private set => SetProperty(ref _scanRateText, value); }

    private string _scanCurrentPath = string.Empty;
    public string ScanCurrentPath { get => _scanCurrentPath; private set => SetProperty(ref _scanCurrentPath, value); }

    // ---------------------------------------------------------------- Ergebnis

    private FolderNode? _rootNode;
    public FolderNode? RootNode
    {
        get => _rootNode;
        private set
        {
            if (!SetProperty(ref _rootNode, value)) return;
            OnPropertyChanged(nameof(RootNodes));
            OnPropertyChanged(nameof(ShowWelcome));
            OnPropertyChanged(nameof(ShowCleanupEmpty));
        }
    }

    /// <summary>Einelementige Liste, weil TreeView eine Auflistung als Quelle braucht.</summary>
    public IEnumerable<FolderNode> RootNodes => RootNode is null ? Array.Empty<FolderNode>() : new[] { RootNode };

    private FolderNode? _selectedNode;
    public FolderNode? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(SelectedPathText));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanExport));
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedNode is not null;
    public string SelectedPathText => SelectedNode?.FullPath ?? string.Empty;

    private RowCollection _rows;
    public RowCollection Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    private string _rowSummary = string.Empty;
    public string RowSummary
    {
        get => _rowSummary;
        private set => SetProperty(ref _rowSummary, value);
    }

    private ScanOverview? _overview;
    public ScanOverview? Overview
    {
        get => _overview;
        private set
        {
            if (!SetProperty(ref _overview, value)) return;
            OnPropertyChanged(nameof(HasOverview));
        }
    }

    public bool HasOverview => Overview is not null;

    // ---------------------------------------------------------------- Filter

    private bool _recursive;
    public bool Recursive
    {
        get => _recursive;
        set
        {
            if (SetProperty(ref _recursive, value)) ScheduleRefresh();
        }
    }

    private string _minSizeText = string.Empty;
    public string MinSizeText
    {
        get => _minSizeText;
        set
        {
            if (SetProperty(ref _minSizeText, value)) ScheduleRefresh();
        }
    }

    private string _minAgeText = string.Empty;
    public string MinAgeText
    {
        get => _minAgeText;
        set
        {
            if (SetProperty(ref _minAgeText, value)) ScheduleRefresh();
        }
    }

    private string _nameFilterText = string.Empty;
    public string NameFilterText
    {
        get => _nameFilterText;
        set
        {
            if (SetProperty(ref _nameFilterText, value)) ScheduleRefresh();
        }
    }

    private string _extensionFilterText = string.Empty;
    public string ExtensionFilterText
    {
        get => _extensionFilterText;
        set
        {
            if (SetProperty(ref _extensionFilterText, value)) ScheduleRefresh();
        }
    }

    public IReadOnlyList<string> KindOptions { get; } = new[] { Loc.T("main.kind.both"), Loc.T("filter.foldersOnly"), Loc.T("filter.filesOnly") };

    private int _kindIndex;
    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (SetProperty(ref _kindIndex, value)) ScheduleRefresh();
        }
    }

    private bool _excludeLinks;
    public bool ExcludeLinks
    {
        get => _excludeLinks;
        set
        {
            if (SetProperty(ref _excludeLinks, value)) ScheduleRefresh();
        }
    }

    private EntrySortKey _sortKey = EntrySortKey.SizeOnDisk;
    public EntrySortKey ActiveSortKey => _sortKey;

    private bool _sortDescending = true;
    public bool SortDescending => _sortDescending;

    /// <summary>Obergrenze fuer die Tabelle. Ohne sie wuerde ein rekursiver Blick auf C:\ die Oberflaeche lahmlegen.</summary>
    public int RowLimit { get; set; } = 200_000;

    // ---------------------------------------------------------------- Scannen

    private async Task ScanAsync()
    {
        string path = SelectedRoot?.Trim() ?? string.Empty;
        if (!_scanner.CanScan(path, out string? reason))
        {
            StatusText = reason ?? Loc.T("main.pathUnreadable");
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        WarningText = string.Empty;
        RootNode = null;
        SelectedNode = null;
        Overview = null;
        Rows = null!;
        RowSummary = string.Empty;

        var sw = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText = Loc.F("main.progress", p.Entries, SizeFormat.Bytes(p.Bytes), p.EntriesPerSecond, p.DirectoriesPending);
            ScanEntriesText = p.Entries.ToString("N0");
            ScanBytesText = SizeFormat.Bytes(p.Bytes);
            ScanRateText = $"{p.EntriesPerSecond:N0}";
            ScanCurrentPath = p.CurrentPath;
        });
        ScanEntriesText = "0";
        ScanBytesText = "0 B";
        ScanRateText = string.Empty;
        ScanCurrentPath = path;

        try
        {
            var options = new ScanOptions { RootPath = path };
            ScanResult result = await _scanner.ScanAsync(options, progress, _scanCts.Token);

            AgeAnalysis age = await Task.Run(() => AgeAnalyzer.Analyze(result.Tree), CancellationToken.None);
            ScanOverview overview = await Task.Run(() => ScanOverview.Build(result, age), CancellationToken.None);

            _context = new ScanContext { Result = result, Age = age, Overview = overview, Display = Display };
            Overview = overview;
            Offload.UpdateFromScan(_context);
            await Cleanup.UpdateAsync(_context);

            ScanTree tree = result.Tree;
            var root = new FolderNode(_context, ScanTree.RootIndex, null);
            RootNode = root;
            root.IsExpanded = true;
            root.IsSelected = true;
            SelectNode(root);

            StatusText = BuildStatus(result, sw.Elapsed);
            if (!result.WasCancelled) RememberRecent(path);
            Core.Util.ActivityLog.Write(Loc.T("area.analysis"), $"{path}  " + Loc.F("main.log.scan", SizeFormat.Bytes(result.TotalBytesOnDisk),
                result.FileCount, result.Duration.TotalSeconds) + (result.WasCancelled ? "  " + Loc.T("main.log.cancelled") : string.Empty));
            WarningText = BuildWarning(result, age);
            ProgressText = result.WasCancelled
                ? Loc.T("main.scanCancelled")
                : Loc.F("main.scanDone", result.Duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("main.scanFailed", ex.Message);
            ProgressText = string.Empty;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static string BuildStatus(ScanResult result, TimeSpan wall)
    {
        var parts = new List<string>(6)
        {
            Loc.F("main.status.entries", result.Tree.Count),
            Loc.F("main.status.files", result.FileCount),
            Loc.F("main.status.folders", result.DirectoryCount),
            SizeFormat.Bytes(result.TotalBytes),
            Loc.F("xlsx.seconds", wall.TotalSeconds)
        };
        if (result.RescuedCount > 0) parts.Add(Loc.F("main.status.rescued", result.RescuedCount));
        if (result.DeniedCount > 0) parts.Add(Loc.F("main.status.denied", result.DeniedCount));
        return string.Join("  ·  ", parts);
    }

    private static string BuildWarning(ScanResult result, AgeAnalysis age)
    {
        var lines = new List<string>(3);

        if (!age.AccessTimes.IsReliable)
            lines.Add(age.AccessTimes.Explanation);

        if (result.DeniedCount > 0 && !result.ScannedElevated)
            lines.Add(Loc.F("main.warn.denied", result.DeniedCount, Loc.T("admin.restart")));
        else if (result.DeniedCount > 0)
            lines.Add(Loc.F("main.warn.deniedAdmin", result.DeniedCount));

        if (result.SizeOnDiskIsEstimate)
            lines.Add(Loc.T("xlsx.estimate"));

        return string.Join("\n", lines);
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("main.browseTitle"),
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        _ = ScanPathAsync(dialog.FolderName);
    }

    /// <summary>Wie bei TreeSize: ein Ziel waehlen heisst, es auch gleich zu scannen.</summary>
    public async Task ScanPathAsync(string path)
    {
        if (IsScanning) return;
        SelectedRoot = path;
        if (ScanCommand.CanExecute(null)) await ScanCommand.ExecuteAsync(null);
    }

    // ---------------------------------------------------------------- Zuletzt untersucht

    private const int MaxRecent = 8;

    private static readonly string RecentPath = Core.Util.AppPaths.File("zuletzt.json");

    /// <summary>Zuletzt gescannte Pfade, neuester zuerst - ueberleben einen Neustart.</summary>
    public ObservableCollection<string> RecentRoots { get; } = new(LoadRecent());

    private void RememberRecent(string path)
    {
        if (App.ScreenshotDir is not null) return;   // Demo-Daten nicht in "Zuletzt untersucht" schreiben
        string normalized = path.Length > 3 ? path.TrimEnd('\\') : path;
        string? existing = RecentRoots.FirstOrDefault(r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentRoots.Remove(existing);
        RecentRoots.Insert(0, normalized);
        while (RecentRoots.Count > MaxRecent) RecentRoots.RemoveAt(RecentRoots.Count - 1);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentPath)!);
            File.WriteAllText(RecentPath, System.Text.Json.JsonSerializer.Serialize(RecentRoots.ToArray()));
        }
        catch (Exception)
        {
            // Nicht speichern zu koennen kostet nur die Liste beim naechsten Start.
        }
    }

    private static IEnumerable<string> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentPath)) return Array.Empty<string>();
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(RecentPath))
                   ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- Tabelle

    public void SelectNode(FolderNode? node)
    {
        SelectedNode = node;
        ScheduleRefresh();
    }

    public void SetSort(EntrySortKey key)
    {
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            // Bei Groessen und Altersangaben ist absteigend das, was man sehen will.
            _sortDescending = key is not (EntrySortKey.Name or EntrySortKey.Extension);
        }
        OnPropertyChanged(nameof(ActiveSortKey));
        OnPropertyChanged(nameof(SortDescending));
        _ = RefreshRowsAsync();
    }

    private void ScheduleRefresh()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void ResetFilters()
    {
        _minSizeText = string.Empty;
        _minAgeText = string.Empty;
        _nameFilterText = string.Empty;
        _extensionFilterText = string.Empty;
        _kindIndex = 0;
        _excludeLinks = false;
        _recursive = false;
        OnPropertyChanged(nameof(MinSizeText));
        OnPropertyChanged(nameof(MinAgeText));
        OnPropertyChanged(nameof(NameFilterText));
        OnPropertyChanged(nameof(ExtensionFilterText));
        OnPropertyChanged(nameof(KindIndex));
        OnPropertyChanged(nameof(ExcludeLinks));
        OnPropertyChanged(nameof(Recursive));
        _ = RefreshRowsAsync();
    }

    private async Task RefreshRowsAsync()
    {
        ScanContext? context = _context;
        FolderNode? node = SelectedNode;
        if (context is null || node is null)
        {
            Rows = null!;
            RowSummary = string.Empty;
            return;
        }

        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        CancellationToken ct = _queryCts.Token;

        EntryFilter filter = BuildFilter();
        bool recursive = Recursive;

        // Sammelknoten "[N Dateien]": genau die losen Dateien dieses Ordners zeigen.
        if (node.IsFileBucket)
        {
            recursive = false;
            filter = new EntryFilter(
                minSize: filter.MinSize, minColdDays: filter.MinColdDays, maxColdDays: filter.MaxColdDays,
                kind: EntryKind.FilesOnly, nameContains: filter.NameContains,
                extensions: filter.ExtensionText, excludeLinks: filter.ExcludeLinks);
        }
        EntrySortKey key = _sortKey;
        bool descending = _sortDescending;
        int index = node.Index;
        int limit = RowLimit;

        IsQuerying = true;
        try
        {
            QueryResult query = await Task.Run(() => EntryQuery.Run(
                context.Tree, index, recursive, filter, key, descending,
                context.Now, context.TrustAccessTimes, limit, ct), ct);

            if (ct.IsCancellationRequested) return;

            long basis = context.Tree.SizeOf(index);
            Rows = new RowCollection(context, query.Indices, basis > 0 ? basis : 1);
            RowSummary = BuildRowSummary(query, filter, recursive);
        }
        catch (OperationCanceledException)
        {
            // Eine neuere Abfrage hat diese ersetzt.
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsQuerying = false;
        }
    }

    private string BuildRowSummary(QueryResult query, EntryFilter filter, bool recursive)
    {
        var parts = new List<string>(4)
        {
            Loc.F("main.rows.matches", query.MatchCount),
            SizeFormat.Bytes(query.MatchedBytes)
        };
        parts.Add(recursive ? Loc.T("main.rows.recursive") : Loc.T("main.rows.level"));
        if (!filter.IsEmpty) parts.Add(filter.Describe());
        if (query.WasCapped) parts.Add(Loc.F("main.rows.capped", query.Indices.Length));
        return string.Join("  ·  ", parts);
    }

    private EntryFilter BuildFilter() => new(
        minSize: ParseSizeMb(MinSizeText),
        minColdDays: ParseDouble(MinAgeText),
        kind: KindIndex switch
        {
            1 => EntryKind.DirectoriesOnly,
            2 => EntryKind.FilesOnly,
            _ => EntryKind.Everything
        },
        nameContains: NameFilterText,
        extensions: ExtensionFilterText,
        excludeLinks: ExcludeLinks);

    /// <summary>Blendet einen Eintrag im Baum ein und waehlt ihn aus (Doppelklick in der Tabelle).</summary>
    public void Reveal(int targetIndex)
    {
        if (_context is null || RootNode is null) return;
        ScanTree tree = _context.Tree;

        // Bei einer Datei den enthaltenden Ordner zeigen.
        if (!tree.Entries[targetIndex].IsDirectory)
        {
            targetIndex = tree.Entries[targetIndex].Parent;
            if (targetIndex < 0) return;
        }

        var chain = new List<int>();
        int current = targetIndex;
        while (current > ScanTree.RootIndex)
        {
            chain.Add(current);
            current = tree.Entries[current].Parent;
        }
        chain.Reverse();

        FolderNode node = RootNode;
        node.IsExpanded = true;
        foreach (int index in chain)
        {
            FolderNode? child = node.FindChild(index);
            if (child is null) break;
            node = child;
            node.IsExpanded = true;
        }

        node.IsSelected = true;
        SelectNode(node);
    }

    // ---------------------------------------------------------------- Export

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (!SetProperty(ref _isExporting, value)) return;
            ExportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public bool CanExport => !IsScanning && !IsExporting && _context is not null && SelectedNode is not null;

    private string _exportStatus = string.Empty;
    public string ExportStatus
    {
        get => _exportStatus;
        private set
        {
            if (!SetProperty(ref _exportStatus, value)) return;
            OnPropertyChanged(nameof(HasExportStatus));
        }
    }

    public bool HasExportStatus => ExportStatus.Length > 0;

    private string? _lastExportPath;

    /// <summary>
    /// Der Export nimmt immer den ausgewaehlten Ordner samt allem darunter, mit
    /// den eingestellten Filtern. Der Schalter "Alles darunter" gilt nur fuer die
    /// Tabellenansicht - ein Export einer einzelnen Ebene waere kaum zu gebrauchen.
    /// </summary>
    private async Task ExportAsync(string? format)
    {
        ScanContext? context = _context;
        FolderNode? node = SelectedNode;
        if (context is null || node is null || format is null) return;

        (string extension, string filterText, string label) = format switch
        {
            "xlsx" => (".xlsx", Loc.T("export.filter.xlsx") + " (*.xlsx)|*.xlsx", Loc.T("export.label.xlsx")),
            "csv" => (".csv", Loc.T("export.filter.csv") + " (*.csv)|*.csv", "CSV"),
            "json" => (".json", "JSON (*.json)|*.json", "JSON"),
            "ki" => (".md", "Markdown (*.md)|*.md", Loc.T("export.label.ai")),
            _ => (".csv", "CSV (*.csv)|*.csv", "CSV")
        };

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.F("export.saveTitle", label),
            Filter = filterText,
            FileName = SuggestFileName(node, format) + extension,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            // Standardort: Dokumente\SizeShift\Exporte bzw. \KI-Berichte - dort findet man sie wieder.
            InitialDirectory = format == "ki" ? Core.Util.AppPaths.AiReportDirectory : Core.Util.AppPaths.ExportDirectory
        };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FileName;
        ExportContext exportContext = CreateExportContext(context, node);

        // Passt die Datei ueberhaupt auf das Ziellaufwerk? Lieber vorher fragen als mittendrin scheitern.
        long expected = format switch
        {
            "csv" => _exportEstimate?.CsvBytes ?? 0,
            "json" => _exportEstimate?.JsonBytes ?? 0,
            "xlsx" => _exportEstimate?.XlsxBytes ?? 0,
            _ => 0
        };
        if (expected > 0 && FreeBytesAt(path) is long free && free < expected * 11 / 10)
        {
            var answer = System.Windows.MessageBox.Show(
                Loc.F("export.noSpace", SizeFormat.Bytes(expected), SizeFormat.Bytes(free)),
                Loc.F("export.saveTitle", label), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        IsExporting = true;
        ExportStatus = Loc.F("export.writing", label);

        try
        {
            ExportSummary summary = await Task.Run(() => format switch
            {
                "xlsx" => XlsxExporter.Write(path, exportContext,
                    progress: new Progress<string>(step => ExportStatus = $"{label}: {step}")),
                "csv" => CsvExporter.Write(path, exportContext),
                "json" => JsonExporter.Write(path, exportContext),
                "ki" => AiReportExporter.Write(path, exportContext),
                _ => throw new NotSupportedException(format)
            });

            _lastExportPath = path;
            OpenExportFolderCommand.NotifyCanExecuteChanged();

            string capped = summary.WasCapped
                ? " " + Loc.F("export.capped", summary.TotalMatches)
                : string.Empty;
            ExportStatus = Loc.F("export.done", label, summary.RowsWritten, SizeFormat.Bytes(summary.FileSizeBytes),
                                 summary.Duration.TotalSeconds) + capped;
            Core.Util.ActivityLog.Write(Loc.T("export.button"), $"{label}: {path}  " +
                Loc.F("export.log", summary.RowsWritten, SizeFormat.Bytes(summary.FileSizeBytes)));
        }
        catch (Exception ex)
        {
            ExportStatus = Loc.F("export.failed", label, ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private ExportContext CreateExportContext(ScanContext context, FolderNode node) => new()
    {
        Result = context.Result,
        Age = context.Age,
        Overview = context.Overview,
        RootIndex = node.Index,
        Filter = BuildFilter(),
        Recursive = true
    };

    private static long? FreeBytesAt(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------- Groessenschaetzung im Export-Menue

    private ExportEstimate? _exportEstimate;
    private string? _exportEstimateKey;

    private string _exportHintXlsx = string.Empty, _exportHintCsv = string.Empty, _exportHintJson = string.Empty;
    public string ExportHintXlsx { get => _exportHintXlsx; private set => SetProperty(ref _exportHintXlsx, value); }
    public string ExportHintCsv { get => _exportHintCsv; private set => SetProperty(ref _exportHintCsv, value); }
    public string ExportHintJson { get => _exportHintJson; private set => SetProperty(ref _exportHintJson, value); }
    public string ExportHintAi => Loc.F("export.estimate.size", SizeFormat.Bytes(ExportEstimate.AiReportBytes));

    /// <summary>
    /// Rechnet beim Oeffnen des Export-Menues aus, wie gross jede Datei ungefaehr wird. Das
    /// Menue geht sofort auf; die Zahlen erscheinen, sobald sie da sind (bei Millionen
    /// Eintraegen nach einem Augenblick). Ohne Aenderung an Ordner oder Filter gilt die alte Rechnung.
    /// </summary>
    public async Task RefreshExportEstimateAsync()
    {
        ScanContext? context = _context;
        FolderNode? node = SelectedNode;
        if (context is null || node is null) return;

        ExportContext exportContext = CreateExportContext(context, node);
        string key = $"{RuntimeHelpers.GetHashCode(context)}|{node.Index}|{exportContext.Filter.Describe()}";
        if (key == _exportEstimateKey && _exportEstimate is not null) return;

        _exportEstimateKey = key;
        _exportEstimate = null;
        ExportHintXlsx = ExportHintCsv = ExportHintJson = Loc.T("export.estimate.pending");

        ExportEstimate estimate;
        try
        {
            estimate = await Task.Run(() => ExportEstimator.Estimate(exportContext));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportHintXlsx = ExportHintCsv = ExportHintJson = string.Empty;
            _exportEstimateKey = null;
            return;
        }
        if (key != _exportEstimateKey) return;   // inzwischen anderer Ordner oder Filter

        _exportEstimate = estimate;
        ExportHintCsv = Loc.F("export.estimate.rows", SizeFormat.Bytes(estimate.CsvBytes), estimate.Rows);
        ExportHintJson = Loc.F("export.estimate.rows", SizeFormat.Bytes(estimate.JsonBytes), estimate.Rows);
        ExportHintXlsx = estimate.XlsxRows < estimate.Rows
            ? Loc.F("export.estimate.rowsOf", SizeFormat.Bytes(estimate.XlsxBytes), estimate.XlsxRows, estimate.Rows)
            : Loc.F("export.estimate.rows", SizeFormat.Bytes(estimate.XlsxBytes), estimate.XlsxRows);
    }

    private string SuggestFileName(FolderNode node, string format)
    {
        string cleaned = node.FullPath.Replace(":", string.Empty).Replace('\\', '-').Replace('/', '-').Trim('-', ' ');
        foreach (char invalid in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(invalid, '_');
        if (cleaned.Length > 60) cleaned = cleaned[..60];
        string suffix = format == "ki" ? "-ki" : string.Empty;
        return $"sizeshift-{cleaned}-{DateTime.Now:yyyyMMdd-HHmm}{suffix}";
    }

    private void OpenExportFolder()
    {
        if (_lastExportPath is not null) OpenInExplorer(_lastExportPath);
    }

    public string? GetSelectedPath(EntryRow? row) => row?.FullPath ?? SelectedNode?.FullPath;

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer nicht verfuegbar oder Pfad verschwunden - kein Grund, die App zu stoeren.
        }
    }

    // ---------------------------------------------------------------- Hilfen

    private static IEnumerable<string> EnumerateRoots()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (Exception)
            {
                continue;
            }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }

    private static long ParseSizeMb(string text)
    {
        double? value = ParseDouble(text);
        return value is null or <= 0 ? 0 : (long)(value.Value * 1024 * 1024);
    }

    private static double? ParseDouble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = text.Trim().Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}
