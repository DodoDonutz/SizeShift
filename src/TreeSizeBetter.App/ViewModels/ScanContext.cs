using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;

namespace TreeSizeBetter.App.ViewModels;

/// <summary>
/// Alles, was die Ansichten ueber einen abgeschlossenen Scan wissen muessen.
/// Wird einmal pro Scan gebaut und danach nur gelesen.
/// </summary>
public sealed class ScanContext
{
    public required ScanResult Result { get; init; }
    public required AgeAnalysis Age { get; init; }
    public required ScanOverview Overview { get; init; }
    public required DisplaySettings Display { get; init; }

    public ScanTree Tree => Result.Tree;
    public long Now => Age.NowFileTime;
    public bool TrustAccessTimes => Age.AccessTimes.IsReliable;

    public double? ColdDays(int index) => AgeAnalyzer.ColdDays(Tree, index, Now, TrustAccessTimes);
}
