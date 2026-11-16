using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Analysis;

public sealed record TypeBreakdownRow(string Extension, long Bytes, long Count)
{
    /// <summary>Anteil am belegten Platz des ganzen Scans.</summary>
    public double Share { get; init; }
    public string ShareText => SizeFormat.Percent(Share);
    public double BarWidth => Math.Max(0, Math.Min(1, Share)) * 220;

    public string Label => Extension.Length == 0 ? Loc.T("types.noExtension") : "." + Extension;
    public string BytesText => SizeFormat.Bytes(Bytes);
    public string CountText => SizeFormat.Count(Count);
}

public sealed record AgeBreakdownRow(string Label, long Bytes, long Count, double Share)
{
    public string BytesText => SizeFormat.Bytes(Bytes);
    public string CountText => SizeFormat.Count(Count);
    public string ShareText => SizeFormat.Percent(Share);
    public double BarWidth => Math.Max(0, Math.Min(1, Share)) * 220;
}

public sealed record TopEntryRow(int Index, string Path, long Bytes, double? ColdDays)
{
    public string BytesText => SizeFormat.Bytes(Bytes);
    public string ColdText => SizeFormat.Ago(ColdDays);
}

/// <summary>Zusammenfassung eines Scans fuer die Uebersichtsseite und den Export.</summary>
public sealed class ScanOverview
{
    public required string RootPath { get; init; }
    public required long TotalBytes { get; init; }
    public required long TotalBytesOnDisk { get; init; }
    public required long FileCount { get; init; }
    public required long DirectoryCount { get; init; }
    public required long VolumeTotalBytes { get; init; }
    public required long VolumeFreeBytes { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int DeniedCount { get; init; }
    public required AccessTimeReliability AccessTimes { get; init; }

    public required IReadOnlyList<AgeBreakdownRow> AgeByWrite { get; init; }
    public required IReadOnlyList<AgeBreakdownRow> AgeByAccess { get; init; }
    public required IReadOnlyList<TypeBreakdownRow> Types { get; init; }
    public required IReadOnlyList<TopEntryRow> LargestFolders { get; init; }
    public required IReadOnlyList<TopEntryRow> LargestFiles { get; init; }
    public required IReadOnlyList<TopEntryRow> ColdestBigFolders { get; init; }

    public string TotalText => SizeFormat.Bytes(TotalBytes);
    public string OnDiskText => SizeFormat.Bytes(TotalBytesOnDisk);
    public string FileCountText => SizeFormat.Count(FileCount);
    public string DirectoryCountText => SizeFormat.Count(DirectoryCount);
    public string VolumeText => VolumeTotalBytes > 0
        ? Loc.F("overview.volume", SizeFormat.Bytes(VolumeTotalBytes - VolumeFreeBytes), SizeFormat.Bytes(VolumeTotalBytes), SizeFormat.Bytes(VolumeFreeBytes))
        : Loc.T("common.unknown");
    public double VolumeUsedShare => VolumeTotalBytes > 0
        ? (double)(VolumeTotalBytes - VolumeFreeBytes) / VolumeTotalBytes
        : 0;

    /// <summary>Breite des Belegt-Balkens in der Uebersicht (Gesamtbreite 620 Pixel).</summary>
    public double VolumeUsedBarWidth => Math.Max(0, Math.Min(1, VolumeUsedShare)) * 620;

    public static ScanOverview Build(ScanResult result, AgeAnalysis age, int topCount = 15)
    {
        ScanTree tree = result.Tree;
        bool trust = age.AccessTimes.IsReliable;
        long now = age.NowFileTime;

        var typeTotals = new Dictionary<string, (long Bytes, long Count)>(512, StringComparer.OrdinalIgnoreCase);
        var folders = new List<int>(1024);

        for (int i = 0; i < tree.Count; i++)
        {
            ref FsEntry e = ref tree.Entries[i];
            if (e.IsDirectory)
            {
                if (i != ScanTree.RootIndex) folders.Add(i);
                continue;
            }

            string extension = ExtensionOf(tree.GetName(i));
            typeTotals.TryGetValue(extension, out var current);
            typeTotals[extension] = (current.Bytes + e.SizeOnDisk, current.Count + 1);
        }

        var types = typeTotals
            .Select(kv => new TypeBreakdownRow(kv.Key, kv.Value.Bytes, kv.Value.Count)
            {
                Share = result.TotalBytesOnDisk > 0 ? (double)kv.Value.Bytes / result.TotalBytesOnDisk : 0
            })
            .OrderByDescending(row => row.Bytes)
            .Take(topCount)
            .ToList();

        var largestFolders = folders
            .OrderByDescending(tree.SizeOnDiskOf)
            .Take(topCount)
            .Select(i => new TopEntryRow(i, tree.GetFullPath(i), tree.SizeOnDiskOf(i),
                AgeAnalyzer.ColdDays(tree, i, now, trust)))
            .ToList();

        // Gross und lange nicht angefasst - genau die Kandidaten fuers Umlagern.
        var coldest = folders
            .Where(i => tree.SizeOnDiskOf(i) > 500L * 1024 * 1024)
            .Select(i => (Index: i, Cold: AgeAnalyzer.ColdDays(tree, i, now, trust), Size: tree.SizeOnDiskOf(i)))
            .Where(x => x.Cold is not null)
            .OrderByDescending(x => x.Cold!.Value)
            .ThenByDescending(x => x.Size)
            .Take(topCount)
            .Select(x => new TopEntryRow(x.Index, tree.GetFullPath(x.Index), x.Size, x.Cold))
            .ToList();

        var largestFiles = new List<TopEntryRow>(topCount);
        foreach (int index in TopFileIndices(tree, topCount))
        {
            largestFiles.Add(new TopEntryRow(index, tree.GetFullPath(index), tree.Entries[index].SizeOnDisk,
                AgeAnalyzer.ColdDays(tree, index, now, trust)));
        }

        return new ScanOverview
        {
            RootPath = tree.RootPath,
            TotalBytes = result.TotalBytes,
            TotalBytesOnDisk = result.TotalBytesOnDisk,
            FileCount = result.FileCount,
            DirectoryCount = result.DirectoryCount,
            VolumeTotalBytes = result.VolumeTotalBytes,
            VolumeFreeBytes = result.VolumeFreeBytes,
            Duration = result.Duration,
            DeniedCount = result.DeniedCount,
            AccessTimes = age.AccessTimes,
            AgeByWrite = ToRows(age.ByWriteTime),
            AgeByAccess = ToRows(age.ByAccessTime),
            Types = types,
            LargestFolders = largestFolders,
            LargestFiles = largestFiles,
            ColdestBigFolders = coldest
        };
    }

    /// <summary>
    /// Groesste Dateien ohne die ganze Liste zu sortieren: ein Durchlauf mit
    /// einer kleinen Bestenliste reicht und spart bei einer Million Dateien
    /// das Sortieren von einer Million Einträgen.
    /// </summary>
    private static List<int> TopFileIndices(ScanTree tree, int count)
    {
        var best = new List<int>(count + 1);
        long threshold = 0;

        for (int i = 0; i < tree.Count; i++)
        {
            ref FsEntry e = ref tree.Entries[i];
            if (e.IsDirectory) continue;
            if (best.Count == count && e.SizeOnDisk <= threshold) continue;

            long length = e.SizeOnDisk;
            int position = best.Count;
            while (position > 0 && tree.Entries[best[position - 1]].SizeOnDisk < length) position--;
            best.Insert(position, i);

            if (best.Count > count) best.RemoveAt(best.Count - 1);
            if (best.Count == count) threshold = tree.Entries[best[^1]].SizeOnDisk;
        }

        return best;
    }

    private static List<AgeBreakdownRow> ToRows(AgeHistogram histogram)
    {
        long total = histogram.Bytes.Sum();
        var rows = new List<AgeBreakdownRow>(AgeHistogram.Labels.Length);
        for (int i = 0; i < AgeHistogram.Labels.Length; i++)
        {
            if (histogram.Count[i] == 0) continue;
            double share = total > 0 ? (double)histogram.Bytes[i] / total : 0;
            rows.Add(new AgeBreakdownRow(AgeHistogram.Labels[i], histogram.Bytes[i], histogram.Count[i], share));
        }
        return rows;
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }
}
