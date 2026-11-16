using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Ein Eintrag im Baum links: entweder ein Ordner oder - wie bei TreeSize - ein
/// Sammelknoten "[N Dateien]" fuer die losen Dateien eines Ordners.
///
/// Kinder entstehen erst beim Aufklappen. Bei 290.000 Ordnern auf C: wuerde
/// sonst schon das Anzeigen des Baums minutenlang dauern und hunderte MB kosten.
/// </summary>
public sealed class FolderNode : ObservableObject
{
    private static readonly FolderNode Placeholder = new();

    private readonly ScanContext _context = null!;
    private ObservableCollection<FolderNode>? _children;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    /// <summary>Nur fuer den Platzhalter, der den Aufklapp-Pfeil erzeugt.</summary>
    private FolderNode()
    {
        Name = "...";
        ReparseKind = string.Empty;
    }

    /// <summary>Ordnerknoten.</summary>
    public FolderNode(ScanContext context, int index, FolderNode? parent)
    {
        _context = context;
        Index = index;
        Parent = parent;

        ScanTree tree = context.Tree;
        ref FsEntry e = ref tree.Entries[index];

        Name = index == ScanTree.RootIndex ? tree.RootPath : tree.GetName(index);
        Size = tree.SizeOf(index);
        SizeOnDisk = tree.SizeOnDiskOf(index);
        IsReparsePoint = e.IsReparsePoint && !ReparseTags.IsCloudPlaceholder(e.ReparseTag);
        ReparseKind = e.IsReparsePoint ? ReparseTags.Describe(e.ReparseTag) : string.Empty;

        if (e.AggregateIndex >= 0)
        {
            ref DirAggregate agg = ref tree.Aggregates[e.AggregateIndex];
            FileCount = agg.FileCount;
            DirCount = agg.DirCount;
        }

        ColdDays = context.ColdDays(index);
        Coldness = AgeAnalyzer.Classify(ColdDays);

        // Aufklappbar, sobald es Unterordner oder lose Dateien gibt.
        if (HasExpandableContent(tree, index))
            _children = new ObservableCollection<FolderNode> { Placeholder };
    }

    /// <summary>Sammelknoten fuer die losen Dateien direkt in <paramref name="parent"/>.</summary>
    private FolderNode(ScanContext context, FolderNode parent, int fileCount, long bytes, long bytesOnDisk,
        long newestActivity)
    {
        _context = context;
        Index = parent.Index;
        Parent = parent;
        IsFileBucket = true;
        Name = fileCount == 1 ? Loc.T("tree.oneFile") : Loc.F("tree.files", fileCount);
        Size = bytes;
        SizeOnDisk = bytesOnDisk;
        FileCount = fileCount;
        ReparseKind = string.Empty;
        ColdDays = FileTimeUtil.DaysSince(newestActivity, context.Now);
        Coldness = AgeAnalyzer.Classify(ColdDays);
    }

    public int Index { get; }
    public FolderNode? Parent { get; }

    /// <summary>true beim Sammelknoten "[N Dateien]" - Index zeigt dann auf den Ordner, dem die Dateien gehoeren.</summary>
    public bool IsFileBucket { get; }

    public string Name { get; }
    public long Size { get; }
    public long SizeOnDisk { get; }
    public int FileCount { get; }
    public int DirCount { get; }
    public double? ColdDays { get; }
    public Coldness Coldness { get; }
    public bool IsReparsePoint { get; }
    public string ReparseKind { get; }

    private DisplaySettings Settings => _context.Display;

    // ------------------------------------------------------------- Anzeige

    /// <summary>Der Wert, nach dem sich Balken und Sortierung im aktuellen Modus richten.</summary>
    public long DisplayValue => Settings.Mode switch
    {
        DisplayMode.SizeOnDisk => SizeOnDisk,
        DisplayMode.FileCount => FileCount,
        _ => Size
    };

    /// <summary>Anteil am Elternknoten im aktuellen Modus - Laenge des Balkens hinter dem Namen.</summary>
    public double Share
    {
        get
        {
            if (Parent is null) return 1;
            long parentValue = Parent.DisplayValue;
            return parentValue > 0 ? Math.Clamp((double)DisplayValue / parentValue, 0, 1) : 0;
        }
    }

    /// <summary>Text am Balken, vor dem Namen - wie bei TreeSize.</summary>
    public string DisplayText => Settings.Mode switch
    {
        DisplayMode.FileCount => SizeFormat.Count(FileCount),
        DisplayMode.Percent => SizeFormat.Percent(Share),
        DisplayMode.SizeOnDisk => SizeFormat.Bytes(SizeOnDisk),
        _ => SizeFormat.Bytes(Size)
    };

    /// <summary>Rechte Spalte: immer der belegte Platz, unabhaengig vom Modus.</summary>
    public string SizeOnDiskText => SizeFormat.Bytes(SizeOnDisk);

    public string ColdText => SizeFormat.Ago(ColdDays);
    public string FullPath => _context.Tree.GetFullPath(Index);

    public string Tooltip
    {
        get
        {
            string cloud = Size > SizeOnDisk + 64L * 1024 * 1024
                ? "\n" + Loc.F("tip.cloud", SizeFormat.Bytes(Size - SizeOnDisk))
                : string.Empty;
            string link = IsReparsePoint
                ? "\n\n" + Loc.F("tip.link", ReparseKind)
                : string.Empty;
            string where = IsFileBucket ? Loc.F("tip.looseFiles", FullPath) : FullPath;

            return $"{where}\n\n" +
                   Loc.F("tip.size", SizeFormat.Bytes(Size), SizeFormat.Bytes(SizeOnDisk)) + cloud + "\n" +
                   (IsFileBucket
                       ? Loc.F("tip.files", SizeFormat.Count(FileCount)) + "\n"
                       : Loc.F("tip.content", SizeFormat.Count(FileCount), SizeFormat.Count(DirCount)) + "\n") +
                   Loc.F("tip.share", SizeFormat.Percent(Share)) + "\n" +
                   Loc.F("tip.lastUsed", ColdText, AgeAnalyzer.Describe(Coldness)) +
                   link;
        }
    }

    /// <summary>
    /// Nach einem Moduswechsel: Anzeige aller bereits erzeugten Knoten neu
    /// berechnen und Geschwister neu sortieren. Nicht erzeugte Knoten rechnen
    /// beim ersten Aufklappen ohnehin mit dem aktuellen Modus.
    /// </summary>
    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Share));
        OnPropertyChanged(nameof(Tooltip));

        if (!_childrenLoaded || _children is null) return;

        var sorted = _children.OrderByDescending(c => c.DisplayValue).ToList();
        for (int target = 0; target < sorted.Count; target++)
        {
            int current = _children.IndexOf(sorted[target]);
            if (current != target) _children.Move(current, target);
        }

        foreach (FolderNode child in _children) child.RefreshDisplay();
    }

    // ------------------------------------------------------------- Baum

    public ObservableCollection<FolderNode>? Children => _children;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public FolderNode? FindChild(int index)
    {
        LoadChildren();
        if (_children is null) return null;
        foreach (FolderNode child in _children)
            if (!child.IsFileBucket && child.Index == index) return child;
        return null;
    }

    private void LoadChildren()
    {
        if (_childrenLoaded || _children is null || IsFileBucket) return;
        _childrenLoaded = true;

        ScanTree tree = _context.Tree;
        var nodes = new List<FolderNode>(Math.Max(4, DirCount + 1));

        int looseFiles = 0;
        long looseBytes = 0, looseOnDisk = 0, newestActivity = 0;
        bool trust = _context.TrustAccessTimes;

        foreach (int childIndex in tree.Children(Index))
        {
            ref FsEntry entry = ref tree.Entries[childIndex];
            if (entry.IsDirectory)
            {
                nodes.Add(new FolderNode(_context, childIndex, this));
                continue;
            }

            looseFiles++;
            looseBytes += entry.Length;
            looseOnDisk += entry.SizeOnDisk;
            long activity = trust ? Math.Max(entry.ModifiedUtc, entry.AccessedUtc) : entry.ModifiedUtc;
            if (activity > newestActivity) newestActivity = activity;
        }

        if (looseFiles > 0)
            nodes.Add(new FolderNode(_context, this, looseFiles, looseBytes, looseOnDisk, newestActivity));

        nodes.Sort(static (a, b) => b.DisplayValue.CompareTo(a.DisplayValue));

        _children.Clear();
        foreach (FolderNode node in nodes) _children.Add(node);
    }

    private static bool HasExpandableContent(ScanTree tree, int index) => tree.Entries[index].ChildCount > 0;
}
