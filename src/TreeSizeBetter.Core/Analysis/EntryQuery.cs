using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Analysis;

public enum EntrySortKey
{
    Name,
    Size,
    SizeOnDisk,
    FileCount,
    DirCount,
    Created,
    Modified,
    Accessed,
    ModifiedAge,
    AccessedAge,
    Cold,
    Extension,
    Depth
}

public sealed record QueryResult(int[] Indices, int MatchCount, long MatchedBytes, bool WasCapped);

/// <summary>
/// Sammelt, filtert und sortiert Eintraege fuer die Tabelle und den Export.
///
/// Sortiert wird ueber ein Array von Schluesseln statt ueber einen Vergleichs-
/// Delegaten: Array.Sort(keys, items) laeuft bei einer Million Eintraegen
/// ungefaehr fuenfmal schneller, weil pro Vergleich kein Aufruf anfaellt.
/// </summary>
public static class EntryQuery
{
    public static QueryResult Run(
        ScanTree tree,
        int rootIndex,
        bool recursive,
        EntryFilter filter,
        EntrySortKey sortKey,
        bool descending,
        long now,
        bool trustAccessTimes,
        int limit,
        CancellationToken ct = default)
    {
        List<int> matches = Collect(tree, rootIndex, recursive, filter, now, trustAccessTimes, ct);

        long matchedBytes = 0;
        for (int i = 0; i < matches.Count; i++) matchedBytes += tree.SizeOf(matches[i]);

        int[] indices = matches.ToArray();
        Sort(tree, indices, sortKey, descending, now, trustAccessTimes);

        bool capped = limit > 0 && indices.Length > limit;
        if (capped) indices = indices[..limit];

        return new QueryResult(indices, matches.Count, matchedBytes, capped);
    }

    internal static List<int> Collect(
        ScanTree tree, int rootIndex, bool recursive, EntryFilter filter,
        long now, bool trustAccessTimes, CancellationToken ct)
    {
        var matches = new List<int>(recursive ? 4096 : 64);

        if (!recursive)
        {
            foreach (int child in tree.Children(rootIndex))
                if (filter.Accepts(tree, child, now, trustAccessTimes)) matches.Add(child);
            return matches;
        }

        // Tiefensuche ueber den Teilbaum. Die Nachkommen eines Ordners liegen im
        // flachen Array NICHT zusammenhaengend - der Scan arbeitet parallel und
        // haengt die Bloecke in der Reihenfolge an, in der sie fertig werden -
        // deshalb ein echter Durchlauf statt eines Indexbereichs.
        var stack = new Stack<int>(1024);
        stack.Push(rootIndex);

        int sinceCheck = 0;
        while (stack.Count > 0)
        {
            if (++sinceCheck >= 8192)
            {
                sinceCheck = 0;
                ct.ThrowIfCancellationRequested();
            }

            int current = stack.Pop();
            int first = tree.Entries[current].FirstChild;
            int count = tree.Entries[current].ChildCount;

            for (int i = 0; i < count; i++)
            {
                int child = first + i;
                if (filter.Accepts(tree, child, now, trustAccessTimes)) matches.Add(child);
                if (tree.Entries[child].ChildCount > 0) stack.Push(child);
            }
        }

        return matches;
    }

    public static void Sort(
        ScanTree tree, int[] indices, EntrySortKey key, bool descending, long now, bool trustAccessTimes)
    {
        if (indices.Length < 2) return;

        switch (key)
        {
            case EntrySortKey.Name:
            case EntrySortKey.Extension:
            {
                var keys = new string[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    string name = tree.GetName(indices[i]);
                    keys[i] = key == EntrySortKey.Name ? name : ExtensionOf(name);
                }
                Array.Sort(keys, indices, StringComparer.OrdinalIgnoreCase);
                break;
            }

            case EntrySortKey.ModifiedAge:
            case EntrySortKey.AccessedAge:
            case EntrySortKey.Cold:
            {
                var keys = new double[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    int index = indices[i];
                    double? value = key switch
                    {
                        EntrySortKey.ModifiedAge => FileTimeUtil.DaysSince(tree.Entries[index].ModifiedUtc, now),
                        EntrySortKey.AccessedAge => FileTimeUtil.DaysSince(tree.Entries[index].AccessedUtc, now),
                        _ => AgeAnalyzer.ColdDays(tree, index, now, trustAccessTimes)
                    };
                    // Unbrauchbare Zeitstempel gelten als "unendlich alt" und landen
                    // damit am Ende der aufsteigenden Sortierung.
                    keys[i] = value ?? double.MaxValue;
                }
                Array.Sort(keys, indices);
                break;
            }

            default:
            {
                var keys = new long[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    int index = indices[i];
                    keys[i] = key switch
                    {
                        EntrySortKey.Size => tree.SizeOf(index),
                        EntrySortKey.SizeOnDisk => tree.SizeOnDiskOf(index),
                        EntrySortKey.FileCount => AggregateValue(tree, index, aggFiles: true),
                        EntrySortKey.DirCount => AggregateValue(tree, index, aggFiles: false),
                        EntrySortKey.Created => tree.Entries[index].CreatedUtc,
                        EntrySortKey.Modified => tree.Entries[index].ModifiedUtc,
                        EntrySortKey.Accessed => tree.Entries[index].AccessedUtc,
                        EntrySortKey.Depth => tree.Entries[index].Depth,
                        _ => tree.SizeOf(index)
                    };
                }
                Array.Sort(keys, indices);
                break;
            }
        }

        if (descending) Array.Reverse(indices);
    }

    private static long AggregateValue(ScanTree tree, int index, bool aggFiles)
    {
        int aggIndex = tree.Entries[index].AggregateIndex;
        if (aggIndex < 0) return 0;
        ref DirAggregate agg = ref tree.Aggregates[aggIndex];
        return aggFiles ? agg.FileCount : agg.DirCount;
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
    }
}
