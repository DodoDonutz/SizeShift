using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Eine Zeile in der Detailtabelle. Wird erst erzeugt, wenn die Tabelle sie
/// tatsaechlich anzeigt - bei Hunderttausenden Treffern waere alles andere
/// nicht bezahlbar. Der vollstaendige Pfad wird noch spaeter gebaut, naemlich
/// erst beim ersten Lesen.
/// </summary>
public sealed class EntryRow
{
    private readonly ScanContext _context;
    private string? _fullPath;

    public EntryRow(ScanContext context, int index, int rowPosition, long shareBasis)
    {
        _context = context;
        Index = index;
        RowPosition = rowPosition;

        ScanTree tree = context.Tree;
        ref FsEntry e = ref tree.Entries[index];

        IsDirectory = e.IsDirectory;
        Name = tree.GetName(index);
        Depth = e.Depth;
        Attributes = e.Attributes;
        ReparseTag = e.ReparseTag;

        Size = tree.SizeOf(index);
        SizeOnDisk = tree.SizeOnDiskOf(index);

        if (e.AggregateIndex >= 0)
        {
            ref DirAggregate agg = ref tree.Aggregates[e.AggregateIndex];
            FileCount = agg.FileCount;
            DirCount = agg.DirCount;
        }

        Created = FileTimeUtil.ToLocal(e.CreatedUtc);
        Modified = FileTimeUtil.ToLocal(e.ModifiedUtc);
        Accessed = FileTimeUtil.ToLocal(e.AccessedUtc);

        ModifiedDays = FileTimeUtil.DaysSince(e.ModifiedUtc, context.Now);
        AccessedDays = FileTimeUtil.DaysSince(e.AccessedUtc, context.Now);
        ColdDays = context.ColdDays(index);
        Coldness = AgeAnalyzer.Classify(ColdDays);

        Extension = IsDirectory ? string.Empty : GetExtension(Name);
        Share = shareBasis > 0 ? (double)Size / shareBasis : 0;
        TypeText = DescribeType(e);
    }

    public int Index { get; }
    public int RowPosition { get; }

    public string Name { get; }
    public bool IsDirectory { get; }
    public int Depth { get; }
    public uint Attributes { get; }
    public uint ReparseTag { get; }
    public string Extension { get; }
    public string TypeText { get; }

    public long Size { get; }
    public long SizeOnDisk { get; }
    public int FileCount { get; }
    public int DirCount { get; }

    public DateTime? Created { get; }
    public DateTime? Modified { get; }
    public DateTime? Accessed { get; }

    public double? ModifiedDays { get; }
    public double? AccessedDays { get; }
    public double? ColdDays { get; }
    public Coldness Coldness { get; }

    /// <summary>Anteil an der Groesse der aktuellen Auswahl, fuer den Balken.</summary>
    public double Share { get; }

    public string FullPath => _fullPath ??= _context.Tree.GetFullPath(Index);

    // --- Anzeigeform -----------------------------------------------------

    public string SizeText => SizeFormat.Bytes(Size);
    public string SizeOnDiskText => SizeFormat.Bytes(SizeOnDisk);
    public string FileCountText => IsDirectory ? SizeFormat.Count(FileCount) : string.Empty;
    public string DirCountText => IsDirectory ? SizeFormat.Count(DirCount) : string.Empty;
    public string ModifiedText => Modified?.ToString("g", Loc.Culture) ?? "-";
    public string AccessedText => Accessed?.ToString("g", Loc.Culture) ?? "-";
    public string CreatedText => Created?.ToString("g", Loc.Culture) ?? "-";
    public string ModifiedDaysText => SizeFormat.Ago(ModifiedDays);
    public string AccessedDaysText => SizeFormat.Ago(AccessedDays);
    public string ColdText => SizeFormat.Ago(ColdDays);
    public string ShareText => SizeFormat.Percent(Share);
    public double BarWidth => Math.Max(0, Math.Min(1, Share)) * 70;

    public string AttributeText
    {
        get
        {
            var parts = new List<string>(4);
            if ((Attributes & FileAttr.Hidden) != 0) parts.Add(Loc.T("attr.hidden"));
            if ((Attributes & FileAttr.System) != 0) parts.Add(Loc.T("attr.system"));
            if ((Attributes & FileAttr.ReadOnly) != 0) parts.Add(Loc.T("attr.readOnly"));
            if ((Attributes & FileAttr.Compressed) != 0) parts.Add(Loc.T("attr.compressed"));
            if ((Attributes & FileAttr.SparseFile) != 0) parts.Add(Loc.T("attr.sparse"));
            if ((Attributes & FileAttr.Encrypted) != 0) parts.Add(Loc.T("attr.encrypted"));
            return string.Join(", ", parts);
        }
    }

    public string Tooltip
    {
        get
        {
            string reliability = _context.TrustAccessTimes
                ? string.Empty
                : "\n" + Loc.T("tip.accessUnreliable");
            return $"{FullPath}\n\n" +
                   Loc.F("tip.size", SizeText, SizeOnDiskText) + "\n" +
                   (IsDirectory ? Loc.F("tip.content", FileCountText, DirCountText) + "\n" : string.Empty) +
                   Loc.F("tip.created", CreatedText) + "\n" +
                   Loc.F("tip.modified", ModifiedText, ModifiedDaysText) + "\n" +
                   Loc.F("tip.accessed", AccessedText, AccessedDaysText) + "\n" +
                   Loc.F("tip.lastUsed", ColdText, AgeAnalyzer.Describe(Coldness)) +
                   reliability;
        }
    }

    private static string GetExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1
            ? string.Empty
            : name[(dot + 1)..].ToLowerInvariant();
    }

    private string DescribeType(in FsEntry e)
    {
        if (e.IsReparsePoint)
        {
            string kind = ReparseTags.Describe(e.ReparseTag);
            return e.IsDirectory ? Loc.F("type.linkFolder", kind) : kind;
        }
        if (e.IsDirectory) return Loc.T("type.folder");
        return Extension.Length == 0 ? Loc.T("type.file") : Loc.F("type.extFile", Extension.ToUpperInvariant());
    }
}
