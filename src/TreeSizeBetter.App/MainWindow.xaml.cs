using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TreeSizeBetter.App.Controls;
using TreeSizeBetter.App.Shell;
using TreeSizeBetter.App.ViewModels;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;

namespace TreeSizeBetter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    private int _treemapDepth = 4;
    private TreemapColoring _treemapColoring = TreemapColoring.Alter;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        LoadColumnLayout();
        _viewModel.Cleanup.Confirm = question => MessageBox.Show(this, question, Loc.T("area.cleanup"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        _viewModel.Offload.Confirm = question => MessageBox.Show(this, question, Loc.T("area.relocate"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        _viewModel.Offload.ConfirmPlan = ConfirmPlan;
        _viewModel.Offload.AskFolderName = AskFolderName;

        // Das Kacheldiagramm folgt der Auswahl im Baum und dem Anzeigemodus.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SelectedNode) or nameof(MainViewModel.Overview))
                RefreshTreemap();
            if (e.PropertyName == nameof(MainViewModel.Overview) && _viewModel.Overview is not null)
                PlayContentEntrance();
        };
        _viewModel.Display.PropertyChanged += (_, _) => RefreshTreemap();

        // Hell/dunkel: das Kacheldiagramm zeichnet selbst und muss neu gemalt werden.
        Action onTheme = () =>
        {
            UpdateTreemapLegend();
            Treemap.InvalidateVisual();
            RefreshTreemap();
        };
        Shell.ThemeManager.Changed += onTheme;
        Closed += (_, _) => Shell.ThemeManager.Changed -= onTheme;

        Treemap.TileHovered += ShowTreemapInfo;
        Treemap.TileActivated += ZoomTreemapInto;
        Treemap.TileContextRequested += index =>
        {
            ScanContext? context = _viewModel.Context;
            if (context is null) return;
            bool isFolder = context.Tree.Entries[index].IsDirectory;
            ShowShellMenu(context.Tree.GetFullPath(index), revealIndex: index, relocateIndex: isFolder ? index : null);
        };
        UpdateTreemapLegend();
        ApplyTreemapVisibility(AppSettings.ShowTreemap);

        Loaded += (_, _) =>
        {
            if (App.ScreenshotDir is { } shots) _ = RunScreenshotsAsync(shots);
            else if (App.StartupScanPath is { } path) _ = _viewModel.ScanPathAsync(path);
        };
    }

    // ------------------------------------------------------------- Fenster und Animationen

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChromeTheme.ApplyCurrent(this);
        Icon = WindowChromeTheme.RenderIcon((ImageSource)FindResource("LogoImage"));
    }

    /// <summary>Nach einem Scan gleitet die Ansicht von leicht unten herein.</summary>
    private void PlayContentEntrance()
    {
        SlideIn(ContentArea, 18, 450);
        SlideIn(TreemapPane, 24, 600);
    }

    private static void SlideIn(FrameworkElement element, double distance, int milliseconds)
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        element.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        if (element.RenderTransform is not TranslateTransform)
            element.RenderTransform = new TranslateTransform();
        ((TranslateTransform)element.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(distance, 0, duration) { EasingFunction = ease });
    }

    /// <summary>Tab-Inhalte blenden beim Wechsel kurz ein, statt hart umzuspringen.</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !IsLoaded) return;
        if (MainTabs.Template.FindName("PART_SelectedContentHost", MainTabs) is FrameworkElement host)
            SlideIn(host, 10, 260);
    }

    // ------------------------------------------------------------- Kacheldiagramm

    private GridLength _treemapHeight = new(1, GridUnitType.Star);

    /// <summary>
    /// Kacheldiagramm ein- oder ausblenden (Einstellungen oben rechts). Ausgeblendet
    /// bekommt der Baum mit der Tabelle die ganze Hoehe; die zuletzt gezogene Hoehe
    /// des Diagramms bleibt fuer das Wiedereinblenden gemerkt.
    /// </summary>
    private void ApplyTreemapVisibility(bool show)
    {
        if (show)
        {
            TreemapRow.Height = _treemapHeight;
            TreemapPane.Visibility = Visibility.Visible;
            TreemapSplitter.Visibility = Visibility.Visible;
            if (IsLoaded) RefreshTreemap();
        }
        else
        {
            if (TreemapRow.Height.Value > 0) _treemapHeight = TreemapRow.Height;
            TreemapRow.Height = new GridLength(0);
            TreemapPane.Visibility = Visibility.Collapsed;
            TreemapSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void SetTreemapVisible(bool show)
    {
        AppSettings.ShowTreemap = show;
        ApplyTreemapVisibility(show);
    }

    private void RefreshTreemap()
    {
        if (TreemapPane.Visibility != Visibility.Visible) return;
        ScanContext? context = _viewModel.Context;
        int root = _viewModel.SelectedNode?.Index ?? (context is null ? -1 : ScanTree.RootIndex);
        Treemap.Show(context, root, _viewModel.Display.Mode, _treemapDepth, _treemapColoring);
    }

    private void ShowTreemapInfo(int index)
    {
        ScanContext? context = _viewModel.Context;
        if (context is null || index < 0)
        {
            TreemapInfo.Text = Loc.T("treemap.hint");
            return;
        }

        ScanTree tree = context.Tree;
        double? cold = context.ColdDays(index);
        string kind = tree.Entries[index].IsDirectory
            ? Loc.F("main.status.files", tree.AggregateOf(index).FileCount)
            : Loc.T("type.file");
        TreemapInfo.Text = Loc.F("treemap.info", tree.GetFullPath(index), Core.Util.SizeFormat.Bytes(tree.SizeOnDiskOf(index)),
                                 kind, Core.Util.SizeFormat.Ago(cold));
    }

    /// <summary>Doppelklick: in einen Ordner hineinzoomen - er wird im Baum ausgewaehlt, das Diagramm folgt.</summary>
    private void ZoomTreemapInto(int index)
    {
        ScanContext? context = _viewModel.Context;
        if (context is null) return;
        if (!context.Tree.Entries[index].IsDirectory) index = context.Tree.Entries[index].Parent;
        if (index >= 0) _viewModel.Reveal(index);
    }

    private void TreemapUp_Click(object sender, RoutedEventArgs e)
    {
        ScanContext? context = _viewModel.Context;
        FolderNode? node = _viewModel.SelectedNode;
        if (context is null || node is null) return;
        int parent = context.Tree.Entries[node.Index].Parent;
        if (parent >= 0) _viewModel.Reveal(parent);
    }

    private void TreemapDepth_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int depth))
        {
            _treemapDepth = depth;
            if (IsLoaded) RefreshTreemap();
        }
    }

    private void TreemapColor_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out TreemapColoring coloring))
        {
            _treemapColoring = coloring;
            UpdateTreemapLegend();
            if (IsLoaded) RefreshTreemap();
        }
    }

    private void UpdateTreemapLegend()
    {
        if (TreemapLegend is null) return;   // waehrend InitializeComponent

        var entries = _treemapColoring == TreemapColoring.Alter
            ? new (string, Color)[]
            {
                (Loc.T("legend.lt1m"), TreemapView.AgeColor(Coldness.Hot)),
                (Loc.T("legend.1to6m"), TreemapView.AgeColor(Coldness.Warm)),
                (Loc.T("legend.6to12m"), TreemapView.AgeColor(Coldness.Cool)),
                (Loc.T("legend.1to2y"), TreemapView.AgeColor(Coldness.Cold)),
                (Loc.T("legend.gt2y"), TreemapView.AgeColor(Coldness.Frozen))
            }
            : TreemapView.TypeGroups.Select(g => (g.Label, g.Color))
                .Append((Loc.T("legend.other"), TreemapView.OtherTypeColor)).ToArray();

        TreemapLegend.Items.Clear();
        foreach (var (label, color) in entries)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            item.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)FindResource("FgDim") });
            TreemapLegend.Items.Add(item);
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node) _viewModel.SelectNode(node);
    }

    /// <summary>
    /// Sortiert wird selbst, nicht vom DataGrid. Das Standardverhalten wuerde die
    /// Zeilenliste durchlaufen und damit jedes der bis zu 200.000 Zeilenobjekte
    /// erzeugen - genau das, was die verzoegerte Erzeugung vermeiden soll.
    /// </summary>
    private void DetailGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        EntrySortKey? key = MapSortKey(e.Column.SortMemberPath);
        if (key is null) return;

        _viewModel.SetSort(key.Value);

        foreach (DataGridColumn column in DetailGrid.Columns)
        {
            column.SortDirection = column == e.Column
                ? (_viewModel.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    private static EntrySortKey? MapSortKey(string? sortMemberPath) => sortMemberPath switch
    {
        "Name" => EntrySortKey.Name,
        "Size" => EntrySortKey.Size,
        // Der Anteil ist innerhalb einer Auswahl proportional zur Groesse,
        // deshalb dieselbe Reihenfolge.
        "Share" => EntrySortKey.Size,
        "SizeOnDisk" => EntrySortKey.SizeOnDisk,
        "FileCount" => EntrySortKey.FileCount,
        "DirCount" => EntrySortKey.DirCount,
        "Created" => EntrySortKey.Created,
        "Modified" => EntrySortKey.Modified,
        "Accessed" => EntrySortKey.Accessed,
        "ModifiedDays" => EntrySortKey.ModifiedAge,
        "AccessedDays" => EntrySortKey.AccessedAge,
        "ColdDays" => EntrySortKey.Cold,
        "Extension" => EntrySortKey.Extension,
        "Depth" => EntrySortKey.Depth,
        _ => null
    };

    /// <summary>Oeffnet das Export-Menue unter dem Knopf, statt es per Rechtsklick zu verstecken.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        _ = _viewModel.RefreshExportEstimateAsync();   // Groessen erscheinen im Menue, sobald gerechnet
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DetailGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailGrid.SelectedItem is not EntryRow row) return;
        _viewModel.Reveal(row.Index);
    }

    // ------------------------------------------------------------- Kontextmenue

    private const int ExtraCopyPath = 1;
    private const int ExtraShowInExplorer = 2;
    private const int ExtraRevealInTree = 3;
    private const int ExtraRelocate = 4;

    /// <summary>Rechtsklick im Baum: Eintrag auswaehlen und das Windows-Menue dafuer zeigen.</summary>
    private void FolderTree_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FolderNode node || node.Name == "...") return;

        item.IsSelected = true;
        item.Focus();
        e.Handled = true;

        // Beim Sammelknoten gehoert das Menue zum Ordner, der die Dateien enthaelt.
        ShowShellMenu(node.FullPath, revealIndex: null, relocateIndex: node.IsFileBucket ? null : node.Index);
    }

    /// <summary>Rechtsklick in der Tabelle: Zeile auswaehlen und das Windows-Menue dafuer zeigen.</summary>
    private void DetailGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TryShowColumnMenuFromHeader(e)) return;

        DataGridRow? gridRow = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (gridRow?.Item is not EntryRow row) return;

        if (!gridRow.IsSelected)
        {
            DetailGrid.SelectedItems.Clear();
            gridRow.IsSelected = true;
        }
        e.Handled = true;

        ShowShellMenu(row.FullPath, revealIndex: row.Index, relocateIndex: row.IsDirectory ? row.Index : null);
    }

    private void ShowShellMenu(string path, int? revealIndex, int? relocateIndex)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            // Seit dem Scan geloescht oder verschoben - die Shell haette nichts anzubieten.
            MessageBox.Show(this, Loc.F("shell.gone", path),
                Core.Util.AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extras = new List<ShellMenuExtra>
        {
            new(ExtraCopyPath, Loc.T("shell.copyPath")),
            new(ExtraShowInExplorer, Loc.T("shell.showInExplorer"))
        };
        if (revealIndex is not null) extras.Add(new ShellMenuExtra(ExtraRevealInTree, Loc.T("shell.showInTree")));
        if (relocateIndex is not null) extras.Add(new ShellMenuExtra(ExtraRelocate, Loc.T("shell.relocate")));

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int chosen = ShellContextMenu.Show(hwnd, path, extras);

        switch (chosen)
        {
            case ExtraCopyPath:
                CopyToClipboard(path);
                break;
            case ExtraShowInExplorer:
                MainViewModel.OpenInExplorer(path);
                break;
            case ExtraRevealInTree when revealIndex is not null:
                _viewModel.Reveal(revealIndex.Value);
                break;
            case ExtraRelocate when relocateIndex is not null:
                _ = _viewModel.RelocateFromAnalysisAsync(path);
                break;
        }
    }

    /// <summary>
    /// Rechtsklick in den Explorer-Baeumen des Umlagern-Bereichs: dasselbe
    /// Windows-Kontextmenue wie im Explorer (Oeffnen, Loeschen, Umbenennen,
    /// Eigenschaften ...). Danach wird der Elternordner neu eingelesen.
    /// </summary>
    private async void ExplorerTree_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ExplorerNode node || node.IsPlaceholder) return;
        e.Handled = true;
        node.IsSelected = true;

        ShowShellMenu(node.FullPath, revealIndex: null, relocateIndex: null);

        // Loeschen oder Umbenennen laeuft in der Shell nach - kurz danach nochmal nachsehen.
        ExplorerNode target = node.Parent ?? node;
        await target.RefreshAsync();
        await Task.Delay(1500);
        await target.RefreshAsync();
    }

    /// <summary>Vorschlaege aus dem letzten Scan als Menue; ein Klick hakt den Ordner links an.</summary>
    private void Suggestions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SuggestionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_viewModel.Offload.Suggestions.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = _viewModel.Context is null
                    ? Loc.T("suggest.scanFirst")
                    : Loc.T("suggest.none"),
                IsEnabled = false
            });
        }
        foreach (var suggestion in _viewModel.Offload.Suggestions.Take(20))
        {
            var item = new MenuItem
            {
                Header = $"{suggestion.BytesText,10}   {suggestion.Path}",
                ToolTip = Loc.F("suggest.tip", suggestion.CategoryText, suggestion.ColdText),
                FontFamily = new FontFamily("Consolas")
            };
            string path = suggestion.Path;
            item.Click += (_, _) => _ = _viewModel.Offload.SelectSourceAsync(path);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Zeigt den Umlager-Plan zum Bestaetigen.</summary>
    private PlanDecision? ConfirmPlan(IReadOnlyList<PlanItem> plan, string targetFolder)
    {
        var dialog = new RelocationPlanWindow(plan, targetFolder) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Decision : null;
    }

    private string? AskFolderName(string parent)
    {
        var dialog = new InputWindow(Loc.T("relocate.newFolder"), Loc.F("input.newFolderPrompt", parent), Loc.T("input.newFolderDefault")) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    /// <summary>Oeffnet den Fundort im Explorer. Bei mehreren Orten den ersten.</summary>
    private void CleanupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CleanupGrid.SelectedItem is not ViewModels.CleanupRow row) return;
        string path = row.Finding.Roots[0];
        try
        {
            var start = System.IO.Directory.Exists(path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"");
            System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    // ------------------------------------------------------------- Verzeichnis auswaehlen

    /// <summary>
    /// Menue wie bei TreeSize: Laufwerke, Dokumente und Cloud, zuletzt untersucht,
    /// eigenes Verzeichnis. Wird bei jedem Oeffnen neu gebaut, damit Laufwerke und
    /// freier Platz stimmen. Ein Klick scannt sofort.
    /// </summary>
    private void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ChooseRootButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 340
        };

        AddSection(menu, Loc.T("cleanup.drives"));
        foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
        {
            string glyph, label, detail;
            try
            {
                if (!drive.IsReady) continue;
                glyph = drive.DriveType switch
                {
                    DriveType.Removable => "",
                    DriveType.Network => "",
                    DriveType.CDRom => "",
                    _ => ""
                };
                string kind = drive.DriveType switch
                {
                    DriveType.Removable => Loc.T("drive.removable"),
                    DriveType.Network => Loc.T("drive.network"),
                    _ => Loc.T("drive.local")
                };
                label = $"{(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? kind : drive.VolumeLabel)} ({drive.Name.TrimEnd('\\')})";
                detail = Loc.F("drive.freeOf", Core.Util.SizeFormat.Bytes(drive.AvailableFreeSpace), Core.Util.SizeFormat.Bytes(drive.TotalSize));
            }
            catch (IOException)
            {
                continue;
            }
            AddEntry(menu, glyph, label, detail, drive.RootDirectory.FullName, DriveBlue);
        }

        AddSection(menu, Loc.T("root.docsCloud"));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        foreach (var (glyph, label, path) in new[]
                 {
                     ("", "OneDrive", oneDrive ?? Path.Combine(profile, "OneDrive")),
                     ("", "iCloud Drive", Path.Combine(profile, "iCloudDrive")),
                     ("", Loc.T("root.documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                     ("", Loc.T("group.downloads"), Path.Combine(profile, "Downloads")),
                     ("", Loc.T("root.desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                     ("", Loc.T("root.profile"), profile)
                 })
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                AddEntry(menu, glyph, label, null, path, glyph == "" ? DriveBlue : FolderYellow);
        }

        AddSection(menu, Loc.T("start.recent"));
        if (_viewModel.RecentRoots.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("root.nothingYet"), IsEnabled = false });
        foreach (string recent in _viewModel.RecentRoots)
            AddEntry(menu, "", recent, null, recent, HistoryGrey);

        AddSection(menu, Loc.T("root.folder"));
        var browse = new MenuItem { Header = Loc.T("main.browseTitle") + " ...", Icon = Glyph("") };
        browse.Click += (_, _) => _viewModel.BrowseCommand.Execute(null);
        menu.Items.Add(browse);

        menu.IsOpen = true;
    }

    private void AddSection(ContextMenu menu, string title)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = title, Style = (Style)FindResource("MenuSection") });
    }

    private void AddEntry(ContextMenu menu, string glyph, string label, string? detail, string path,
        string color = FolderYellow)
    {
        var header = new TextBlock();
        header.Inlines.Add(new System.Windows.Documents.Run(label));
        if (detail is not null)
        {
            header.Inlines.Add(new System.Windows.Documents.Run("   " + detail)
            {
                Foreground = (Brush)FindResource("FgDim"),
                FontSize = 11.5
            });
        }

        var item = new MenuItem { Header = header, Icon = Glyph(glyph, color), ToolTip = path };
        item.Click += (_, _) => _ = _viewModel.ScanPathAsync(path);
        menu.Items.Add(item);
    }

    private const string FolderYellow = "#F2C14E";
    private const string DriveBlue = "#4C9EEB";
    private const string HistoryGrey = "#9AA3AF";

    /// <summary>Symbol aus der Windows-Symbolschrift, mit fester Farbe wie bei TreeSize.</summary>
    private static TextBlock Glyph(string glyph, string color = FolderYellow) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)new BrushConverter().ConvertFromString(color)!
    };

    /// <summary>Fuer den Selbsttest: jeden Tab einmal zeigen, damit WPF seinen Inhalt baut und bindet.</summary>
    public IEnumerable<string> VisitAllTabs()
    {
        object? previous = MainTabs.SelectedItem;
        foreach (TabItem tab in MainTabs.Items.OfType<TabItem>().ToList())
        {
            MainTabs.SelectedItem = tab;
            UpdateLayout();
            yield return tab.Header?.ToString() ?? "?";
        }
        MainTabs.SelectedItem = previous;

        foreach (AppArea area in new[] { AppArea.Aufraeumen, AppArea.Umlagern, AppArea.System })
        {
            _viewModel.Area = area;
            UpdateLayout();
            yield return "Bereich " + area;
        }

        // Im System-Bereich beide Reiter einmal aufbauen.
        foreach (TabItem tab in SystemTabs.Items.OfType<TabItem>().ToList())
        {
            SystemTabs.SelectedItem = tab;
            UpdateLayout();
            yield return "System: " + tab.Header;
        }
        SystemTabs.SelectedIndex = 0;

        _viewModel.Area = AppArea.Analyse;
        UpdateLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Offload.Dispose();   // Journal-Datei freigeben
        base.OnClosed(e);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Die Zwischenablage ist gelegentlich von einem anderen Programm belegt.
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
