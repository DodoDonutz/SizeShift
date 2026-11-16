using TreeSizeBetter.Core.Export;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Analysis;

public enum FindingSafety
{
    /// <summary>Baut sich von selbst wieder auf oder ist nachweislich Abfall.</summary>
    Sicher,
    /// <summary>Vermutlich entbehrlich, aber vorher anschauen.</summary>
    Vorsichtig,
    /// <summary>Nur mit gutem Grund und Wissen, was daran haengt.</summary>
    Riskant
}

public sealed record Finding(
    string Rule,
    string Path,
    long Bytes,
    double? ColdDays,
    string Reason,
    FindingSafety Safety)
{
    public string BytesText => SizeFormat.Bytes(Bytes);
    public string ColdText => SizeFormat.Days(ColdDays);
}

/// <summary>
/// Schnelle Mustererkennung fuer offensichtliche Platzfresser. Bewusst eine
/// kleine, feste Liste - die ausbaubare Regel-Engine kommt spaeter. Nichts
/// davon loescht irgendetwas; es sind Hinweise zum Anschauen.
/// </summary>
public static class QuickFindings
{
    private const long MinCacheBytes = 200L * 1024 * 1024;
    private const long MinFileBytes = 100L * 1024 * 1024;

    public static List<Finding> Collect(
        ScanTree tree, AgeAnalysis age, int maxPerRule = 25, CancellationToken ct = default)
    {
        var findings = new List<Finding>(64);
        var view = new EntryView(tree, age.NowFileTime, age.AccessTimes.IsReliable);

        var cacheFolders = new List<(int Index, long Size)>();
        var downloads = new List<(int Index, long Size)>();
        var recycle = new List<(int Index, long Size)>();
        var dumps = new List<(int Index, long Size)>();
        var oldInstallers = new List<(int Index, long Size, double Cold)>();
        var bySizeAndName = new Dictionary<(long, string), List<int>>();
        int emptyFolders = 0;

        for (int i = 0; i < tree.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (i == ScanTree.RootIndex) continue;

            ref FsEntry entry = ref tree.Entries[i];
            if (entry.IsReparsePoint) continue;

            long size = tree.SizeOf(i);

            if (entry.IsDirectory)
            {
                if (entry.ChildCount == 0) emptyFolders++;
                if (size < MinCacheBytes) continue;

                view.MoveTo(i);
                string lower = view.FullPath.ToLowerInvariant();

                if (lower.Contains(@"\steamapps\downloading", StringComparison.Ordinal))
                    downloads.Add((i, size));
                else if (lower.Contains(@"$recycle.bin", StringComparison.Ordinal))
                    recycle.Add((i, size));
                else if (view.Category == EntryCategory.Cache)
                    cacheFolders.Add((i, size));

                continue;
            }

            if (size < MinFileBytes) continue;
            view.MoveTo(i);

            string extension = view.Extension;
            if (extension is "dmp" or "mdmp" or "hdmp")
            {
                dumps.Add((i, size));
                continue;
            }

            if (view.Category == EntryCategory.Installer && view.ColdDays is > 180)
                oldInstallers.Add((i, size, view.ColdDays.Value));

            // Verdacht auf Doppel: gleicher Name und exakt gleiche Groesse.
            // Das ersetzt keinen Pruefsummenvergleich, zeigt aber zuverlaessig,
            // wo sich ein genauerer Blick lohnt.
            var key = (tree.Entries[i].Length, view.Name.ToLowerInvariant());
            if (!bySizeAndName.TryGetValue(key, out List<int>? group))
                bySizeAndName[key] = group = new List<int>(2);
            group.Add(i);
        }

        AddFolderFindings(findings, tree, view, TopLevelOnly(tree, cacheFolders), maxPerRule,
            Loc.T("find.cache"), FindingSafety.Sicher,
            Loc.T("find.cache.text"));

        AddFolderFindings(findings, tree, view, downloads, maxPerRule,
            Loc.T("find.steam"), FindingSafety.Vorsichtig,
            Loc.T("find.steam.text"));

        AddFolderFindings(findings, tree, view, recycle, maxPerRule,
            Loc.T("cat.recycleBin"), FindingSafety.Sicher,
            Loc.T("find.bin.text"));

        foreach (var (index, size) in dumps.OrderByDescending(d => d.Size).Take(maxPerRule))
        {
            view.MoveTo(index);
            findings.Add(new Finding(Loc.T("find.dump"), view.FullPath, size, view.ColdDays,
                Loc.T("find.dump.text"), FindingSafety.Sicher));
        }

        foreach (var (index, size, cold) in oldInstallers.OrderByDescending(d => d.Size).Take(maxPerRule))
        {
            view.MoveTo(index);
            findings.Add(new Finding(Loc.T("find.installer"), view.FullPath, size, cold,
                Loc.T("find.installer.text"),
                FindingSafety.Vorsichtig));
        }

        AddDuplicateFindings(findings, tree, age, bySizeAndName, maxPerRule, ct);

        if (emptyFolders > 0)
            findings.Add(new Finding(Loc.T("find.empty"), Loc.F("find.empty.path", emptyFolders), 0, null,
                Loc.T("find.empty.text"),
                FindingSafety.Sicher));

        return findings;
    }

    /// <summary>
    /// Macht aus Namens- und Groessengleichheit einen belastbaren Befund.
    ///
    /// Gleicher Name und gleiche Groesse heisst noch nicht, dass Platz zu holen
    /// ist: Windows legt WinSxS und den Treiberspeicher als Hardlinks an, also
    /// mehrere Pfade auf dieselben Daten. Deshalb wird fuer jeden Verdachtsfall
    /// nachgesehen, wie viele eigenstaendige Dateien wirklich dahinterstecken.
    /// Das kostet pro Gruppe ein paar Dateihandles und ist nur deshalb
    /// bezahlbar, weil es nur die wenigen Verdachtsfaelle trifft.
    /// </summary>
    private static void AddDuplicateFindings(
        List<Finding> findings, ScanTree tree, AgeAnalysis age,
        Dictionary<(long, string), List<int>> groups, int limit, CancellationToken ct)
    {
        var view = new EntryView(tree, age.NowFileTime, age.AccessTimes.IsReliable);
        var other = new EntryView(tree, age.NowFileTime, age.AccessTimes.IsReliable);

        var ordered = groups
            .Where(g => g.Value.Count > 1)
            .OrderByDescending(g => g.Key.Item1 * (g.Value.Count - 1))
            .Take(limit * 3)                 // Reserve, weil Hardlink-Gruppen wegfallen
            .ToList();

        int added = 0;
        foreach (var group in ordered)
        {
            if (added >= limit) break;
            ct.ThrowIfCancellationRequested();

            long size = group.Key.Item1;
            var paths = new List<string>(group.Value.Count);
            foreach (int index in group.Value)
            {
                other.MoveTo(index);
                paths.Add(other.FullPath);
            }

            int distinct = FileIdentityReader.CountDistinctFiles(paths, out bool verified);
            if (distinct <= 1)
            {
                // Alles derselbe Datenbestand unter mehreren Namen - kein Platzgewinn.
                continue;
            }

            long wasted = size * (distinct - 1);
            view.MoveTo(group.Value[0]);

            string others = string.Join(" | ", paths.Skip(1).Take(3));
            string hardlinkNote = distinct < paths.Count
                ? " " + Loc.F("find.dup.hardlinks", paths.Count - distinct)
                : string.Empty;
            string verifyNote = verified
                ? string.Empty
                : " " + Loc.T("find.dup.unverified");

            findings.Add(new Finding(Loc.T("find.dup"), view.FullPath, wasted, view.ColdDays,
                Loc.F("find.dup.text", distinct) + hardlinkNote + verifyNote + " " + Loc.F("find.dup.also", others),
                FindingSafety.Vorsichtig));
            added++;
        }
    }

    private static void AddFolderFindings(
        List<Finding> findings, ScanTree tree, EntryView view,
        List<(int Index, long Size)> candidates, int limit,
        string rule, FindingSafety safety, string reason)
    {
        foreach (var (index, size) in candidates.OrderByDescending(c => c.Size).Take(limit))
        {
            view.MoveTo(index);
            findings.Add(new Finding(rule, view.FullPath, size, view.ColdDays, reason, safety));
        }
    }

    /// <summary>
    /// Behaelt nur den obersten Ordner je Zweig. Sonst erscheint ein Cache-Ordner
    /// samt all seiner Unterordner, und derselbe Platzgewinn wird mehrfach gezaehlt.
    /// </summary>
    private static List<(int Index, long Size)> TopLevelOnly(ScanTree tree, List<(int Index, long Size)> candidates)
    {
        var taken = new HashSet<int>();
        var result = new List<(int, long)>(candidates.Count);

        foreach (var candidate in candidates.OrderBy(c => tree.Entries[c.Index].Depth))
        {
            int parent = tree.Entries[candidate.Index].Parent;
            bool covered = false;
            while (parent >= 0)
            {
                if (taken.Contains(parent)) { covered = true; break; }
                parent = tree.Entries[parent].Parent;
            }
            if (covered) continue;

            taken.Add(candidate.Index);
            result.Add(candidate);
        }

        return result;
    }
}
