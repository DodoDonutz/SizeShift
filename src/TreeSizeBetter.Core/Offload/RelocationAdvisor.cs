using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Export;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Offload;

public sealed record RelocationSuggestion(
    int Index,
    string Path,
    long BytesOnDisk,
    long Files,
    double? ColdDays,
    EntryCategory Category)
{
    public string BytesText => SizeFormat.Bytes(BytesOnDisk);
    public string FilesText => SizeFormat.Count(Files);
    public string ColdText => SizeFormat.Ago(ColdDays);
    public string CategoryText => EntryCategorizer.Describe(Category);
}

/// <summary>
/// Schlaegt Ordner vor, deren Umzug viel Platz bringt: gross (nach belegtem
/// Platz - Online-Dateien zaehlen nicht), ihrer Art nach verschiebbar und von
/// den Pfadregeln der Schutzschicht nicht gesperrt. Liegt ein Elternordner schon
/// in der Liste, fallen seine Unterordner weg - sonst stuende derselbe Platz
/// mehrfach darin.
/// </summary>
public static class RelocationAdvisor
{
    public static List<RelocationSuggestion> Suggest(
        ScanTree tree, AgeAnalysis age, long minBytes = 1024L * 1024 * 1024, int max = 40)
    {
        var view = new EntryView(tree, age.NowFileTime, age.AccessTimes.IsReliable);
        var candidates = new List<int>();

        for (int i = 1; i < tree.Count; i++)
        {
            ref FsEntry entry = ref tree.Entries[i];
            if (!entry.IsDirectory || entry.IsReparsePoint) continue;
            if (tree.SizeOnDiskOf(i) < minBytes) continue;

            view.MoveTo(i);
            if (!view.LooksRelocatable) continue;
            if (RelocationGuard.PathRules(view.FullPath).Any(issue => issue.Severity == IssueSeverity.Sperre)) continue;
            candidates.Add(i);
        }

        // Flachste Ebene zuerst, damit Elternordner vor ihren Kindern genommen werden.
        candidates.Sort((a, b) => tree.Entries[a].Depth.CompareTo(tree.Entries[b].Depth));
        var taken = new HashSet<int>();
        var result = new List<RelocationSuggestion>();

        foreach (int index in candidates)
        {
            int parent = tree.Entries[index].Parent;
            bool covered = false;
            while (parent >= 0 && !covered)
            {
                covered = taken.Contains(parent);
                parent = tree.Entries[parent].Parent;
            }
            if (covered) continue;

            taken.Add(index);
            view.MoveTo(index);
            result.Add(new RelocationSuggestion(index, view.FullPath, view.SizeOnDisk, view.FileCount,
                view.ColdDays, view.Category));
        }

        return result.OrderByDescending(s => s.BytesOnDisk).Take(max).ToList();
    }
}
