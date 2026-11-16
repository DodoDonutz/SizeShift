using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TreeSizeBetter.Core.Offload;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Ein Ordner in den Explorer-Baeumen des Umlagern-Bereichs. Kinder werden erst
/// beim Aufklappen gelesen, Groessen kommen im Hintergrund nach.
/// </summary>
public sealed class ExplorerNode : ObservableObject
{
    private static readonly ExplorerNode Placeholder = new();

    private readonly ExplorerPane? _pane;
    private bool _loaded;

    private ExplorerNode() => FullPath = Name = string.Empty;

    public ExplorerNode(ExplorerPane pane, ExplorerNode? parent, string fullPath, string name, bool isHidden)
    {
        _pane = pane;
        Parent = parent;
        FullPath = fullPath;
        Name = name;
        IsHidden = isHidden;
        JunctionTarget = SafeReadJunction(fullPath);
        if (!IsJunction) Children.Add(Placeholder);   // Pfeil zum Aufklappen, bevor die Kinder gelesen sind
    }

    public ExplorerNode? Parent { get; }
    public string FullPath { get; }
    public string Name { get; }
    public bool IsHidden { get; }

    /// <summary>Ziel, falls der Ordner eine Junction ist - also schon umgelagert.</summary>
    public string? JunctionTarget { get; private set; }
    public bool IsJunction => JunctionTarget is not null;
    public string JunctionText => IsJunction ? "→ " + JunctionTarget : string.Empty;
    public bool IsPlaceholder => _pane is null;

    /// <summary>Die Laufwerkswurzel - zeigt statt einer Groesse den Laufwerksfuellstand.</summary>
    public bool IsRoot => Parent is null && !IsPlaceholder;

    public bool ShowSize => !IsRoot;

    public ObservableCollection<ExplorerNode> Children { get; } = new();

    public bool ShowCheckBox => _pane?.HasCheckBoxes == true && Parent is not null;

    // -------------------------------------------------------------- Groesse

    private FolderSize? _size;
    public FolderSize? Size
    {
        get => _size;
        private set
        {
            if (!SetProperty(ref _size, value)) return;
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(Bytes));
            OnPropertyChanged(nameof(Share));
        }
    }

    public long Bytes => _size?.BytesOnDisk ?? -1;

    public string SizeText => _size switch
    {
        null => Loc.T("explorer.measuring"),
        { Complete: false } s => "≥ " + SizeFormat.Bytes(s.BytesOnDisk),
        { } s => SizeFormat.Bytes(s.BytesOnDisk)
    };

    /// <summary>Anteil am groessten Geschwisterordner, fuer den Balken.</summary>
    public double Share
    {
        get
        {
            if (_size is not { } size || Parent is null) return 0;
            long max = Parent.Children.Where(c => !c.IsPlaceholder).Max(c => Math.Max(0, c.Bytes));
            return max <= 0 ? 0 : (double)size.BytesOnDisk / max;
        }
    }

    public async Task MeasureAsync(FolderSizeService sizes)
    {
        // Bei Junctions zaehlt, was am Ziel liegt - dort liegen die Daten.
        Size = await sizes.MeasureAsync(JunctionTarget ?? FullPath);
        if (Parent is not null)
        {
            foreach (ExplorerNode sibling in Parent.Children) sibling.OnPropertyChanged(nameof(Share));
            Parent.OnChildMeasured();
        }
    }

    private void OnChildMeasured()
    {
        if (_pane is { SortBySize: true }) ScheduleSort();
    }

    private bool _sortPending;

    /// <summary>
    /// Sortiert nach Groesse, sobald neue Messungen da sind - gebuendelt, damit nicht
    /// jede einzelne Zahl die Liste bewegt, und nie waehrend man gerade klickt:
    /// sonst rutscht der Ordner unter dem Mauszeiger weg und der Haken landet beim Nachbarn.
    /// </summary>
    private async void ScheduleSort()
    {
        if (_sortPending || _pane is null) return;
        _sortPending = true;
        try
        {
            do
            {
                await Task.Delay(400);
            }
            while (DateTime.UtcNow - _pane.LastInteractionUtc < TimeSpan.FromSeconds(2));
        }
        finally
        {
            _sortPending = false;
        }
        if (_pane.SortBySize) ApplySort();
    }

    /// <summary>Ordnet die Kinder nach der gewaehlten Sortierung; noch nicht vermessene stehen unten.</summary>
    internal void ApplySort()
    {
        if (_pane is null || Children.Count < 2 || Children.Any(c => c.IsPlaceholder)) return;
        List<ExplorerNode> sorted = _pane.SortBySize
            ? Children.OrderByDescending(c => c.Bytes).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = Children.IndexOf(sorted[i]);
            if (current != i) Children.Move(current, i);
        }
    }

    /// <summary>Sortiert diesen Ordner und alles bereits Geladene darunter neu.</summary>
    internal void ResortTree()
    {
        ApplySort();
        foreach (ExplorerNode child in Children)
            if (!child.IsPlaceholder) child.ResortTree();
    }

    // -------------------------------------------------------------- Zustand

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) _ = LoadChildrenAsync();
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value) _pane?.OnSelected(this);
        }
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value)) _pane?.OnCheckedChanged(this);
        }
    }

    private string? _status;
    /// <summary>Kurzer Zustand waehrend einer Aktion, z. B. "wird umgelagert".</summary>
    public string? Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public async Task LoadChildrenAsync(bool force = false)
    {
        if (_pane is null || (_loaded && !force)) return;
        _loaded = true;

        string path = FullPath;
        List<(string Path, string Name, bool Hidden)> folders = await Task.Run(() => ReadFolders(path));

        Children.Clear();
        foreach (var (childPath, name, hidden) in folders)
            Children.Add(new ExplorerNode(_pane, this, childPath, name, hidden));

        foreach (ExplorerNode child in Children.ToList()) _ = child.MeasureAsync(_pane.Sizes);
    }

    /// <summary>Liest den Ordner neu ein, z. B. nachdem etwas hinein- oder herausgewandert ist.</summary>
    public async Task RefreshAsync()
    {
        if (_pane is null) return;
        JunctionTarget = SafeReadJunction(FullPath);
        OnPropertyChanged(nameof(JunctionTarget));
        OnPropertyChanged(nameof(IsJunction));
        OnPropertyChanged(nameof(JunctionText));
        Size = null;
        _ = MeasureAsync(_pane.Sizes);
        if (_loaded) await LoadChildrenAsync(force: true);
    }

    private static List<(string, string, bool)> ReadFolders(string path)
    {
        var result = new List<(string, string, bool)>();
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateDirectories())
            {
                FileAttributes attributes;
                try { attributes = info.Attributes; }
                catch (IOException) { continue; }

                // Versteckte Systemordner ($Recycle.Bin, System Volume Information ...)
                // lassen sich ohnehin nicht umlagern und verwirren nur.
                bool hidden = (attributes & FileAttributes.Hidden) != 0;
                bool system = (attributes & FileAttributes.System) != 0;
                if (hidden && system) continue;

                result.Add((info.FullName, info.Name, hidden));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static string? SafeReadJunction(string path)
    {
        if (path.Length == 0) return null;
        try
        {
            return path.Length > 3 ? JunctionManager.ReadTarget(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
