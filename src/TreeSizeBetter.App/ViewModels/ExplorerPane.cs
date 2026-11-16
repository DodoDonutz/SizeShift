using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>Ein Laufwerk in der kleinen Leiste ueber einem Explorer-Baum.</summary>
public sealed record DriveChoice(string Root, string Letter, string Label, string FreeText);

/// <summary>
/// Eine Seite des Umlagern-Bereichs: Laufwerksleiste plus Ordnerbaum.
/// Links mit Haekchen (was umziehen soll), rechts mit Auswahl (wohin).
/// </summary>
public sealed class ExplorerPane : ObservableObject
{
    public ExplorerPane(FolderSizeService sizes, bool hasCheckBoxes)
    {
        Sizes = sizes;
        HasCheckBoxes = hasCheckBoxes;
    }

    public FolderSizeService Sizes { get; }
    public bool HasCheckBoxes { get; }

    private bool _sortBySize = true;
    /// <summary>Geschwister nach Groesse (groesster oben) statt nach Name sortieren.</summary>
    public bool SortBySize
    {
        get => _sortBySize;
        set
        {
            if (!SetProperty(ref _sortBySize, value)) return;
            OnPropertyChanged(nameof(SortByName));
            // Umschalten ist eine bewusste Aktion: alles Geladene sofort neu ordnen.
            foreach (ExplorerNode root in Roots) root.ResortTree();
        }
    }

    public bool SortByName
    {
        get => !SortBySize;
        set => SortBySize = !value;
    }

    public ObservableCollection<DriveChoice> Drives { get; } = new();
    public ObservableCollection<ExplorerNode> Roots { get; } = new();

    /// <summary>Feuert, wenn sich Haken oder Auswahl aendern.</summary>
    public event Action? Changed;

    private DriveChoice? _drive;
    public DriveChoice? Drive
    {
        get => _drive;
        set
        {
            if (!SetProperty(ref _drive, value) || value is null) return;
            ShowDrive(value);
        }
    }

    private ExplorerNode? _selected;
    public ExplorerNode? Selected
    {
        get => _selected;
        private set
        {
            if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(SelectedPath));
        }
    }

    public string SelectedPath => Selected?.FullPath ?? string.Empty;

    private readonly List<ExplorerNode> _checked = new();
    public IReadOnlyList<ExplorerNode> Checked => _checked;

    public string DriveSummary
    {
        get
        {
            if (Drive is null) return string.Empty;
            try
            {
                var info = new DriveInfo(Drive.Root);
                return Loc.F("explorer.driveInfo", SizeFormat.Bytes(info.TotalSize - info.TotalFreeSpace), SizeFormat.Bytes(info.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    public void LoadDrives(string? preferredRoot)
    {
        Drives.Clear();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Loc.T("cleanup.drive") : drive.VolumeLabel;
                Drives.Add(new DriveChoice(drive.Name, drive.Name.TrimEnd('\\'), label,
                    Loc.F("drive.free", SizeFormat.Bytes(drive.AvailableFreeSpace))));
            }
            catch (IOException)
            {
            }
        }
        Drive = Drives.FirstOrDefault(d => string.Equals(d.Root, preferredRoot, StringComparison.OrdinalIgnoreCase))
                ?? Drives.FirstOrDefault();
    }

    /// <summary>
    /// Liest Laufwerke und Baum neu ein und klappt wieder auf, was offen war.
    /// Haken werden dabei entfernt - nach einer Aenderung soll man bewusst neu waehlen.
    /// </summary>
    public async Task ReloadAsync()
    {
        string? currentRoot = Drive?.Root;
        string? selected = Selected?.FullPath;
        List<string> expanded = new();
        foreach (ExplorerNode root in Roots) CollectExpanded(root, expanded);

        // Laufwerksliste mit frischem freien Platz, ohne den Baum doppelt aufzubauen.
        _drive = null;
        LoadDrives(currentRoot);

        foreach (string path in expanded.OrderBy(p => p.Length))
        {
            ExplorerNode? node = await RevealAsync(path);
            if (node is not null) node.IsExpanded = true;
        }
        if (selected is not null) await RevealAsync(selected);
    }

    private static void CollectExpanded(ExplorerNode node, List<string> into)
    {
        if (node.IsPlaceholder || !node.IsExpanded) return;
        if (node.Parent is not null) into.Add(node.FullPath);
        foreach (ExplorerNode child in node.Children) CollectExpanded(child, into);
    }

    /// <summary>Zeitpunkt der letzten Benutzeraktion (Haken, Auswahl) - danach wird nicht mehr umsortiert.</summary>
    internal DateTime LastInteractionUtc { get; private set; } = DateTime.MinValue;

    private void ShowDrive(DriveChoice drive)
    {
        foreach (ExplorerNode node in _checked.ToList()) node.IsChecked = false;
        Roots.Clear();
        var root = new ExplorerNode(this, null, drive.Root, drive.Letter, isHidden: false);
        Roots.Add(root);
        root.IsExpanded = true;
        root.IsSelected = true;
        OnPropertyChanged(nameof(DriveSummary));
    }

    internal void OnSelected(ExplorerNode node)
    {
        Selected = node;
        LastInteractionUtc = DateTime.UtcNow;
        Changed?.Invoke();
    }

    internal void OnCheckedChanged(ExplorerNode node)
    {
        LastInteractionUtc = DateTime.UtcNow;
        if (node.IsChecked)
        {
            if (!_checked.Contains(node)) _checked.Add(node);
        }
        else
        {
            _checked.Remove(node);
        }
        Changed?.Invoke();
    }

    /// <summary>Klappt den Baum bis zu einem Pfad auf und gibt den Knoten zurueck.</summary>
    public async Task<ExplorerNode?> RevealAsync(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null) return null;
        if (!string.Equals(Drive?.Root, root, StringComparison.OrdinalIgnoreCase))
        {
            DriveChoice? choice = Drives.FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
            if (choice is null) return null;
            Drive = choice;
        }

        ExplorerNode? current = Roots.FirstOrDefault();
        if (current is null) return null;

        string rest = path.Length > root.Length ? path[root.Length..] : string.Empty;
        foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            await current.LoadChildrenAsync();
            current.IsExpanded = true;
            ExplorerNode? next = current.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null) break;
            current = next;
        }
        current.IsSelected = true;
        return current;
    }

    /// <summary>Den Knoten zu einem Pfad, falls er schon geladen ist.</summary>
    public ExplorerNode? FindLoaded(string path)
    {
        foreach (ExplorerNode root in Roots)
        {
            ExplorerNode? hit = Find(root, path.TrimEnd('\\'));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static ExplorerNode? Find(ExplorerNode node, string path)
    {
        if (string.Equals(node.FullPath.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (ExplorerNode child in node.Children)
        {
            if (child.IsPlaceholder) continue;
            string childPath = child.FullPath.TrimEnd('\\');
            if (string.Equals(childPath, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(childPath + "\\", StringComparison.OrdinalIgnoreCase))
                return Find(child, path);
        }
        return null;
    }
}
