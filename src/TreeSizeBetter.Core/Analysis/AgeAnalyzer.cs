using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Analysis;

public enum AgeBucket
{
    Last7Days = 0,
    Last30Days = 1,
    Last90Days = 2,
    LastYear = 3,
    LastTwoYears = 4,
    Older = 5,
    Unknown = 6
}

/// <summary>Grobe Einstufung fuer Farben und Filter.</summary>
public enum Coldness
{
    Unknown = 0,
    Hot = 1,      // bis 30 Tage
    Warm = 2,     // bis 180 Tage
    Cool = 3,     // bis 365 Tage
    Cold = 4,     // bis 730 Tage
    Frozen = 5    // laenger
}

public sealed class AgeHistogram
{
    public long[] Bytes { get; } = new long[7];
    public long[] Count { get; } = new long[7];

    /// <summary>Beschriftungen der sieben Altersklassen in der Sprache der Oberflaeche.</summary>
    public static string[] Labels => new[]
    {
        Loc.T("age.7d"), Loc.T("age.30d"), Loc.T("age.90d"), Loc.T("age.1y"), Loc.T("age.2y"), Loc.T("age.older"), Loc.T("common.unknown")
    };

    public void Add(double? days, long bytes)
    {
        int index = (int)Classify(days);
        Bytes[index] += bytes;
        Count[index]++;
    }

    public static AgeBucket Classify(double? days) => days switch
    {
        null => AgeBucket.Unknown,
        < 7 => AgeBucket.Last7Days,
        < 30 => AgeBucket.Last30Days,
        < 90 => AgeBucket.Last90Days,
        < 365 => AgeBucket.LastYear,
        < 730 => AgeBucket.LastTwoYears,
        _ => AgeBucket.Older
    };
}

/// <summary>
/// Beurteilt, ob man den NTFS-Zugriffszeiten auf diesem Volume glauben kann.
///
/// Zwei typische Faelle machen sie unbrauchbar:
/// 1. Die Aktualisierung ist abgeschaltet (fsutil behavior set disablelastaccess 1).
///    Windows setzt den Zugriffszeitpunkt dann meist gleich dem Aenderungszeitpunkt.
/// 2. Ein Virenscanner, der Suchindex oder ein Backup hat vor kurzem alles
///    angefasst. Dann hat fast jede Datei denselben Zugriffstag und "seit 2 Jahren
///    nicht benutzt" existiert nicht mehr.
/// </summary>
public sealed record AccessTimeReliability(
    bool IsReliable,
    double DominantDayShare,
    DateTime? DominantDay,
    double EqualToWriteShare,
    long SampledFiles,
    string Explanation);

public sealed class AgeAnalysis
{
    public required AccessTimeReliability AccessTimes { get; init; }
    public required AgeHistogram ByWriteTime { get; init; }
    public required AgeHistogram ByAccessTime { get; init; }
    public required long NowFileTime { get; init; }
}

public static class AgeAnalyzer
{
    private const int MaxSample = 400_000;

    public static AgeAnalysis Analyze(ScanTree tree, long? nowFileTime = null)
    {
        long now = nowFileTime ?? FileTimeUtil.NowFileTime();
        var byWrite = new AgeHistogram();
        var byAccess = new AgeHistogram();

        int total = tree.Count;
        int step = total > MaxSample ? total / MaxSample : 1;

        var dayCounts = new Dictionary<long, long>(1024);
        long sampled = 0;
        long equalToWrite = 0;

        for (int i = 0; i < total; i++)
        {
            ref FsEntry e = ref tree.Entries[i];
            if (e.IsDirectory) continue;

            byWrite.Add(FileTimeUtil.DaysSince(e.ModifiedUtc, now), e.Length);
            byAccess.Add(FileTimeUtil.DaysSince(e.AccessedUtc, now), e.Length);

            if (step > 1 && i % step != 0) continue;

            sampled++;
            if (e.AccessedUtc >= FileTimeUtil.MinPlausible)
            {
                long day = FileTimeUtil.ToDayNumber(e.AccessedUtc);
                dayCounts[day] = dayCounts.GetValueOrDefault(day) + 1;
            }
            // Gleichheit auf die Sekunde, nicht auf den Tick: NTFS rundet je nach
            // Schreibpfad unterschiedlich, exakte Tick-Gleichheit waere zu streng.
            if (Math.Abs(e.AccessedUtc - e.ModifiedUtc) < TimeSpan.TicksPerSecond * 2) equalToWrite++;
        }

        var reliability = Judge(dayCounts, sampled, equalToWrite, now);

        return new AgeAnalysis
        {
            AccessTimes = reliability,
            ByWriteTime = byWrite,
            ByAccessTime = byAccess,
            NowFileTime = now
        };
    }

    private static AccessTimeReliability Judge(
        Dictionary<long, long> dayCounts, long sampled, long equalToWrite, long now)
    {
        if (sampled == 0)
            return new AccessTimeReliability(false, 0, null, 0, 0,
                Loc.T("access.noFiles"));

        long dominantCount = 0;
        long dominantDay = 0;
        foreach (var (day, count) in dayCounts)
        {
            if (count <= dominantCount) continue;
            dominantCount = count;
            dominantDay = day;
        }

        double dominantShare = (double)dominantCount / sampled;
        double equalShare = (double)equalToWrite / sampled;
        DateTime? dominantDate = FileTimeUtil.ToLocal(dominantDay * FileTimeUtil.TicksPerDay);

        if (equalShare > 0.90)
            return new AccessTimeReliability(false, dominantShare, dominantDate, equalShare, sampled,
                Loc.F("access.equal", equalShare));

        if (dominantShare > 0.85)
            return new AccessTimeReliability(false, dominantShare, dominantDate, equalShare, sampled,
                Loc.F("access.dominant", dominantShare,
                    dominantDate is null ? "" : " (" + dominantDate.Value.ToString("d", Loc.Culture) + ")"));

        return new AccessTimeReliability(true, dominantShare, dominantDate, equalShare, sampled,
            Loc.T("access.ok"));
    }

    /// <summary>
    /// Tage seit der letzten Aktivitaet. Bei Ordnern zaehlt die jüngste Aktivitaet
    /// im gesamten Teilbaum: ein Spieleordner ist nur dann kalt, wenn darin keine
    /// einzige Datei kuerzlich angefasst wurde.
    /// </summary>
    public static double? ColdDays(ScanTree tree, int index, long now, bool trustAccessTimes)
    {
        ref FsEntry e = ref tree.Entries[index];

        long write;
        long access;

        if (e.AggregateIndex >= 0)
        {
            ref DirAggregate agg = ref tree.Aggregates[e.AggregateIndex];
            // Ein leerer Ordner hat keine Datei-Zeitstempel - dann zaehlt sein eigener.
            write = agg.NewestWriteUtc > 0 ? agg.NewestWriteUtc : e.ModifiedUtc;
            access = agg.NewestAccessUtc > 0 ? agg.NewestAccessUtc : e.AccessedUtc;
        }
        else
        {
            write = e.ModifiedUtc;
            access = e.AccessedUtc;
        }

        long newest = trustAccessTimes ? Math.Max(write, access) : write;
        return FileTimeUtil.DaysSince(newest, now);
    }

    public static Coldness Classify(double? coldDays) => coldDays switch
    {
        null => Coldness.Unknown,
        < 30 => Coldness.Hot,
        < 180 => Coldness.Warm,
        < 365 => Coldness.Cool,
        < 730 => Coldness.Cold,
        _ => Coldness.Frozen
    };

    public static string Describe(Coldness coldness) => coldness switch
    {
        Coldness.Hot => Loc.T("cold.hot"),
        Coldness.Warm => Loc.T("cold.warm"),
        Coldness.Cool => Loc.T("cold.cool"),
        Coldness.Cold => Loc.T("cold.cold"),
        Coldness.Frozen => Loc.T("cold.frozen"),
        _ => Loc.T("common.unknown")
    };
}
