using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Export;

/// <summary>
/// Beweglicher Zeiger auf einen Eintrag im Baum, der alle Felder liefert, die
/// ein Export braucht.
///
/// Bewusst ein wiederverwendetes Objekt statt eines neuen pro Zeile: ein Export
/// ueber C: hat ueber eine Million Zeilen, und ein Objekt pro Zeile waere reiner
/// Muell fuer den GC. MoveTo setzt den Zeiger weiter und verwirft die
/// zwischengespeicherten Werte. Nicht threadsicher - pro Export einer.
/// </summary>
public sealed class EntryView
{
    private readonly ScanTree _tree;
    private readonly long _now;
    private readonly bool _trustAccessTimes;

    private int _index = -1;
    private string? _name;
    private string? _fullPath;
    private EntryCategory? _category;

    public EntryView(ScanTree tree, long now, bool trustAccessTimes)
    {
        _tree = tree;
        _now = now;
        _trustAccessTimes = trustAccessTimes;
    }

    public ScanTree Tree => _tree;
    public int Index => _index;

    public void MoveTo(int index)
    {
        _index = index;
        _name = null;
        _fullPath = null;
        _category = null;
    }

    private ref FsEntry Entry => ref _tree.Entries[_index];

    public string Name => _name ??= _index == ScanTree.RootIndex ? _tree.RootPath : _tree.GetName(_index);
    public string FullPath => _fullPath ??= _tree.GetFullPath(_index);

    public string ParentPath
    {
        get
        {
            int parent = Entry.Parent;
            return parent < 0 ? string.Empty : _tree.GetFullPath(parent);
        }
    }

    public bool IsDirectory => Entry.IsDirectory;
    public bool IsReparsePoint => Entry.IsReparsePoint;
    public int Depth => Entry.Depth;

    public long Size => _tree.SizeOf(_index);
    public long SizeOnDisk => _tree.SizeOnDiskOf(_index);

    public int FileCount
    {
        get
        {
            int agg = Entry.AggregateIndex;
            return agg < 0 ? 0 : _tree.Aggregates[agg].FileCount;
        }
    }

    public int DirCount
    {
        get
        {
            int agg = Entry.AggregateIndex;
            return agg < 0 ? 0 : _tree.Aggregates[agg].DirCount;
        }
    }

    public DateTime? Created => FileTimeUtil.ToLocal(Entry.CreatedUtc);
    public DateTime? Modified => FileTimeUtil.ToLocal(Entry.ModifiedUtc);
    public DateTime? Accessed => FileTimeUtil.ToLocal(Entry.AccessedUtc);

    public double? ModifiedDays => FileTimeUtil.DaysSince(Entry.ModifiedUtc, _now);
    public double? AccessedDays => FileTimeUtil.DaysSince(Entry.AccessedUtc, _now);
    public double? ColdDays => AgeAnalyzer.ColdDays(_tree, _index, _now, _trustAccessTimes);
    public Coldness Coldness => AgeAnalyzer.Classify(ColdDays);

    public string Extension
    {
        get
        {
            if (IsDirectory) return string.Empty;
            string name = Name;
            int dot = name.LastIndexOf('.');
            return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
        }
    }

    public string TypeText
    {
        get
        {
            if (IsReparsePoint)
            {
                string kind = ReparseTags.Describe(Entry.ReparseTag);
                return IsDirectory ? Loc.F("type.linkFolder", kind) : kind;
            }
            return IsDirectory ? Loc.T("type.folder") : Loc.T("type.file");
        }
    }

    public string LinkKind => IsReparsePoint ? ReparseTags.Describe(Entry.ReparseTag) : string.Empty;

    public EntryCategory Category =>
        _category ??= EntryCategorizer.Categorize(FullPath, IsDirectory, IsReparsePoint);

    public bool LooksRelocatable => IsDirectory && EntryCategorizer.LooksRelocatable(Category);

    public string AttributeText
    {
        get
        {
            uint attributes = Entry.Attributes;
            var parts = new List<string>(4);
            if ((attributes & FileAttr.Hidden) != 0) parts.Add(Loc.T("attr.hidden"));
            if ((attributes & FileAttr.System) != 0) parts.Add(Loc.T("attr.system"));
            if ((attributes & FileAttr.ReadOnly) != 0) parts.Add(Loc.T("attr.readOnly"));
            if ((attributes & FileAttr.Compressed) != 0) parts.Add(Loc.T("attr.compressed"));
            if ((attributes & FileAttr.SparseFile) != 0) parts.Add(Loc.T("attr.sparse"));
            if ((attributes & FileAttr.Encrypted) != 0) parts.Add(Loc.T("attr.encrypted"));
            if ((attributes & FileAttr.Offline) != 0) parts.Add(Loc.T("attr.offline"));
            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }
    }
}
