using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;

namespace TreeSizeBetter.Core.Export;

/// <summary>Was exportiert werden soll. Nach dem Erzeugen unveraenderlich.</summary>
public sealed class ExportContext
{
    public required ScanResult Result { get; init; }
    public required AgeAnalysis Age { get; init; }
    public required ScanOverview Overview { get; init; }

    /// <summary>Teilbaum, der exportiert wird. 0 = alles ab der Wurzel.</summary>
    public int RootIndex { get; init; } = ScanTree.RootIndex;

    public EntryFilter Filter { get; init; } = new();
    public bool Recursive { get; init; } = true;
    public EntrySortKey Sort { get; init; } = EntrySortKey.Size;
    public bool Descending { get; init; } = true;

    /// <summary>Obergrenze fuer Zeilenlisten. 0 = keine.</summary>
    public int MaxRows { get; init; }

    public ScanTree Tree => Result.Tree;
    public long Now => Age.NowFileTime;
    public bool TrustAccessTimes => Age.AccessTimes.IsReliable;

    public EntryView NewView() => new(Tree, Now, TrustAccessTimes);

    public QueryResult Query(EntryKind kind = EntryKind.Everything, int? limit = null, CancellationToken ct = default)
    {
        EntryFilter filter = kind == Filter.Kind
            ? Filter
            : new EntryFilter(
                minSize: Filter.MinSize,
                minColdDays: Filter.MinColdDays,
                maxColdDays: Filter.MaxColdDays,
                kind: kind,
                nameContains: Filter.NameContains,
                extensions: Filter.ExtensionText,
                excludeLinks: Filter.ExcludeLinks);

        return EntryQuery.Run(
            Tree, RootIndex, Recursive, filter, Sort, Descending,
            Now, TrustAccessTimes, limit ?? MaxRows, ct);
    }

    public string Describe()
    {
        string scope = Recursive ? Loc.T("export.scope.recursive") : Loc.T("export.scope.top");
        return $"{Tree.GetFullPath(RootIndex)} ({scope}, {Filter.Describe()})";
    }
}

public sealed record ExportSummary(
    string Path,
    long RowsWritten,
    long TotalMatches,
    long FileSizeBytes,
    TimeSpan Duration)
{
    public bool WasCapped => TotalMatches > RowsWritten;
}
