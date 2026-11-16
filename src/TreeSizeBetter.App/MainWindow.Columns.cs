using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TreeSizeBetter.App.Shell;

namespace TreeSizeBetter.App;

/// <summary>
/// Spaltenauswahl der Detailtabelle: Spalten per Menue ein- und ausblenden,
/// mit "Kompakt" und "Alle Spalten" als schnelle Umschalter. Die Auswahl wird
/// gespeichert und beim naechsten Start wiederhergestellt.
/// </summary>
public partial class MainWindow
{
    private static readonly string ColumnLayoutPath = Core.Util.AppPaths.File("spalten.json");

    /// <summary>Spalten, die die kompakte Ansicht zeigt. Alles andere ist ausgeblendet.</summary>
    private static readonly HashSet<string> CompactColumns = new(StringComparer.Ordinal)
    {
        "Name", "Typ", "Größe", "Anteil", "Belegt", "Dateien", "Zuletzt genutzt", "Geändert am"
    };

    /// <summary>Die Namensspalte bleibt immer sichtbar - ohne sie ist die Tabelle nutzlos.</summary>
    private const string FixedColumn = "Name";

    private static Dictionary<string, string>? _columnIds;

    /// <summary>
    /// Sprachunabhaengige Kennung einer Spalte: ihr deutscher Name. So bleiben die
    /// gespeicherte Auswahl (spalten.json) und die kompakte Ansicht gueltig, egal in
    /// welcher Sprache die Kopfzeile gerade beschriftet ist.
    /// </summary>
    private static string HeaderOf(DataGridColumn column)
    {
        string header = column.Header as string ?? string.Empty;
        _columnIds ??= BuildColumnIds();
        return _columnIds.TryGetValue(header, out string? id) ? id : header;
    }

    private static Dictionary<string, string> BuildColumnIds()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, german) in Loc.Table("de"))
            if (key.StartsWith("grid.", StringComparison.Ordinal))
                map.TryAdd(Loc.T(key), german);
        return map;
    }

    private void LoadColumnLayout()
    {
        HashSet<string>? hidden = null;
        try
        {
            if (File.Exists(ColumnLayoutPath))
                hidden = JsonSerializer.Deserialize<ColumnLayout>(File.ReadAllText(ColumnLayoutPath))?.Hidden?.ToHashSet();
        }
        catch
        {
            // Kaputte Datei: einfach mit der kompakten Ansicht starten.
        }

        foreach (DataGridColumn column in DetailGrid.Columns)
        {
            string header = HeaderOf(column);
            bool visible = header == FixedColumn ||
                           (hidden is null ? CompactColumns.Contains(header) : !hidden.Contains(header));
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateColumnsButton();
    }

    private void SaveColumnLayout()
    {
        try
        {
            var layout = new ColumnLayout
            {
                Hidden = DetailGrid.Columns
                    .Where(c => c.Visibility != Visibility.Visible)
                    .Select(HeaderOf)
                    .ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ColumnLayoutPath)!);
            File.WriteAllText(ColumnLayoutPath, JsonSerializer.Serialize(layout));
        }
        catch
        {
            // Nicht speichern zu koennen ist kein Grund, die Bedienung zu stoeren.
        }
    }

    private void UpdateColumnsButton()
    {
        int visible = DetailGrid.Columns.Count(c => c.Visibility == Visibility.Visible);
        ColumnsButtonText.Text = $"{Loc.T("columns.button")}  {visible}/{DetailGrid.Columns.Count}";
    }

    private void OpenDocumentsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"\"{Core.Util.AppPaths.DocumentsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void Columns_Click(object sender, RoutedEventArgs e) =>
        ShowColumnMenu(ColumnsButton, PlacementMode.Bottom);

    /// <summary>Einstellungen oben rechts: Darstellung, Sprache, Analyse und Spalten der Detailtabelle.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = PlacementMode.Bottom };

        AddSection(menu, Loc.T("settings.section.theme"));
        foreach ((AppTheme theme, string label, string tip) in new[]
                 {
                     (AppTheme.Dunkel, Loc.T("theme.dark"), Loc.T("theme.dark.tip")),
                     (AppTheme.Hell, Loc.T("theme.light"), Loc.T("theme.light.tip")),
                     (AppTheme.System, Loc.T("theme.system"), Loc.T("theme.system.tip"))
                 })
        {
            var item = new MenuItem
            {
                Header = label,
                ToolTip = tip,
                IsCheckable = true,
                IsChecked = ThemeManager.Theme == theme
            };
            item.Click += (_, _) => ThemeManager.Apply(theme);
            menu.Items.Add(item);
        }

        AddSection(menu, Loc.T("settings.section.language"));
        // Eigennamen der Sprachen bleiben in ihrer eigenen Sprache - so findet jeder seine.
        foreach ((string? code, string label) in new (string?, string)[]
                 {
                     (null, Loc.T("language.system")),
                     ("de", "Deutsch"),
                     ("en", "English"),
                     ("fr", "Français"),
                     ("es", "Español")
                 })
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = AppSettings.Language == code
            };
            if (code is null) item.ToolTip = Loc.T("language.system.tip");
            item.Click += (_, _) => ChangeLanguage(code);
            menu.Items.Add(item);
        }

        AddSection(menu, Loc.T("settings.section.analysis"));
        var treemap = new MenuItem
        {
            Header = Loc.T("settings.showTreemap"),
            ToolTip = Loc.T("settings.showTreemap.tip"),
            IsCheckable = true,
            IsChecked = AppSettings.ShowTreemap
        };
        treemap.Click += (_, _) => SetTreemapVisible(treemap.IsChecked);
        menu.Items.Add(treemap);

        AddSection(menu, Loc.T("settings.section.table"));
        var choose = new MenuItem { Header = Loc.T("settings.chooseColumns") };
        choose.Click += (_, _) =>
        {
            _viewModel.IsAnalyse = true;
            MainTabs.SelectedIndex = 0;
            Dispatcher.BeginInvoke(() => ShowColumnMenu(ColumnsButton, PlacementMode.Bottom),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
        var compact = new MenuItem { Header = Loc.T("settings.compactColumns") };
        compact.Click += (_, _) => ApplyColumns(h => CompactColumns.Contains(h));
        var all = new MenuItem { Header = Loc.T("columns.all") };
        all.Click += (_, _) => ApplyColumns(_ => true);
        menu.Items.Add(choose);
        menu.Items.Add(compact);
        menu.Items.Add(all);

        menu.IsOpen = true;
    }

    /// <summary>
    /// Sprache wechseln. Die Oberflaeche liest ihre Texte beim Aufbau, deshalb
    /// greift die neue Sprache nach einem Neustart - den bieten wir gleich an.
    /// </summary>
    private void ChangeLanguage(string? code)
    {
        if (AppSettings.Language == code) return;
        AppSettings.Language = code;

        string target = code ?? Loc.SystemLanguage();
        if (target == Loc.Language) return;   // "Wie Windows" ergibt dieselbe Sprache: nichts zu tun

        // Frage schon in der neuen Sprache - die versteht, wer sie gerade gewaehlt hat.
        string previous = Loc.Language;
        Loc.SetLanguage(target);
        string question = Loc.T("language.restart");
        string title = Loc.T("language.restart.title");
        Loc.SetLanguage(previous);

        if (MessageBox.Show(this, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            != MessageBoxResult.Yes)
            return;

        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            string args = _viewModel.SelectedRoot is { Length: > 0 } root ? $"--scan \"{root.TrimEnd('\\')}\"" : string.Empty;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Neustart ging nicht - die Sprache gilt dann beim naechsten Start von selbst.
        }
    }

    private void ShowColumnMenu(UIElement target, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };

        var compact = new MenuItem { Header = Loc.T("columns.compact"), ToolTip = Loc.T("columns.compact.tip") };
        compact.Click += (_, _) => ApplyColumns(h => CompactColumns.Contains(h));
        var all = new MenuItem { Header = Loc.T("columns.all"), ToolTip = Loc.T("columns.all.tip") };
        all.Click += (_, _) => ApplyColumns(_ => true);
        menu.Items.Add(compact);
        menu.Items.Add(all);
        menu.Items.Add(new Separator());

        foreach (DataGridColumn column in DetailGrid.Columns)
        {
            string header = HeaderOf(column);
            var item = new MenuItem
            {
                Header = column.Header as string ?? header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                IsEnabled = header != FixedColumn,
                StaysOpenOnClick = true
            };
            DataGridColumn captured = column;
            item.Click += (_, _) =>
            {
                captured.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                UpdateColumnsButton();
                SaveColumnLayout();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ApplyColumns(Func<string, bool> visible)
    {
        foreach (DataGridColumn column in DetailGrid.Columns)
        {
            string header = HeaderOf(column);
            column.Visibility = header == FixedColumn || visible(header) ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateColumnsButton();
        SaveColumnLayout();
    }

    /// <summary>Rechtsklick auf einen Spaltenkopf oeffnet dieselbe Auswahl.</summary>
    private bool TryShowColumnMenuFromHeader(MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null)
            return false;
        e.Handled = true;
        ShowColumnMenu(DetailGrid, PlacementMode.MousePoint);
        return true;
    }

    private sealed class ColumnLayout
    {
        public List<string>? Hidden { get; set; }
    }
}
