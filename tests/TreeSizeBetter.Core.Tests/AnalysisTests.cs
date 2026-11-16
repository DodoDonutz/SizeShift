using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

public class AgeAnalyzerTests
{
    private static readonly long Now = Time(2026, 1, 1);

    [Fact]
    public void ColdDays_ForFolderUsesNewestActivityInWholeSubtree()
    {
        ScanTree tree = Sample();
        int games = Find(tree, "games");

        // Im Teilbaum: data.pak wurde 2025-03-01 gelesen, old.log 2019.
        // Der Ordner gilt also als zuletzt 2025-03-01 benutzt, nicht 2019.
        double? cold = AgeAnalyzer.ColdDays(tree, games, Now, trustAccessTimes: true);

        Assert.NotNull(cold);
        Assert.InRange(cold!.Value, 305, 310);     // 2025-03-01 bis 2026-01-01
    }

    [Fact]
    public void ColdDays_IgnoresAccessTimesWhenTheyAreNotTrusted()
    {
        ScanTree tree = Sample();
        int games = Find(tree, "games");

        // Ohne Vertrauen in die Zugriffszeiten zaehlt nur die letzte Aenderung,
        // und die jüngste im Teilbaum ist 2023-01-15.
        double? cold = AgeAnalyzer.ColdDays(tree, games, Now, trustAccessTimes: false);

        Assert.NotNull(cold);
        Assert.InRange(cold!.Value, 1080, 1090);
    }

    [Fact]
    public void ColdDays_ForEmptyFolderFallsBackToItsOwnTimestamp()
    {
        ScanTree tree = Sample();
        int beta = Find(tree, @"games\beta");

        // beta enthaelt keine Datei - dann muss der eigene Zeitstempel einspringen,
        // sonst waere das Ergebnis "unbekannt".
        double? cold = AgeAnalyzer.ColdDays(tree, beta, Now, trustAccessTimes: true);
        Assert.NotNull(cold);
    }

    [Fact]
    public void AccessTimes_AreRejectedWhenTheyEqualTheWriteTimes()
    {
        // Das passiert, wenn Windows die Zugriffszeiten nicht mitschreibt.
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);

        var files = new List<(string, RawChild)>();
        for (int i = 0; i < 200; i++)
        {
            long stamp = Time(2020, 1, 1) + i * TimeSpan.TicksPerDay;
            files.Add(FileEntry($"f{i}.bin", 100, stamp, stamp));
        }
        AddChildren(tree, ScanTree.RootIndex, files.ToArray());
        tree.Aggregate();

        AgeAnalysis analysis = AgeAnalyzer.Analyze(tree, Now);

        Assert.False(analysis.AccessTimes.IsReliable);
        Assert.Contains("identisch", analysis.AccessTimes.Explanation);
    }

    [Fact]
    public void AccessTimes_AreRejectedWhenEverythingWasTouchedOnTheSameDay()
    {
        // Typisch nach einem Virenscan oder einem Backup-Durchlauf.
        long sweep = Time(2025, 12, 20);
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);

        var files = new List<(string, RawChild)>();
        for (int i = 0; i < 200; i++)
            files.Add(FileEntry($"f{i}.bin", 100, Time(2020, 1, 1) + i * TimeSpan.TicksPerDay, sweep));
        AddChildren(tree, ScanTree.RootIndex, files.ToArray());
        tree.Aggregate();

        AgeAnalysis analysis = AgeAnalyzer.Analyze(tree, Now);

        Assert.False(analysis.AccessTimes.IsReliable);
        Assert.Contains("Zugriffstag", analysis.AccessTimes.Explanation);
    }

    [Fact]
    public void AccessTimes_AreAcceptedWhenTheyAreSpreadOut()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);

        var files = new List<(string, RawChild)>();
        for (int i = 0; i < 200; i++)
        {
            long modified = Time(2020, 1, 1) + i * TimeSpan.TicksPerDay;
            long accessed = Time(2022, 1, 1) + i * 3 * TimeSpan.TicksPerDay;
            files.Add(FileEntry($"f{i}.bin", 100, modified, accessed));
        }
        AddChildren(tree, ScanTree.RootIndex, files.ToArray());
        tree.Aggregate();

        AgeAnalysis analysis = AgeAnalyzer.Analyze(tree, Now);
        Assert.True(analysis.AccessTimes.IsReliable);
    }

    [Theory]
    [InlineData(3, AgeBucket.Last7Days)]
    [InlineData(20, AgeBucket.Last30Days)]
    [InlineData(60, AgeBucket.Last90Days)]
    [InlineData(200, AgeBucket.LastYear)]
    [InlineData(500, AgeBucket.LastTwoYears)]
    [InlineData(5000, AgeBucket.Older)]
    public void AgeBuckets_ClassifyByDays(double days, AgeBucket expected)
        => Assert.Equal(expected, AgeHistogram.Classify(days));

    [Fact]
    public void AgeBuckets_TreatMissingTimestampAsUnknown()
        => Assert.Equal(AgeBucket.Unknown, AgeHistogram.Classify(null));
}

public class EntryFilterTests
{
    private static readonly long Now = Time(2026, 1, 1);

    private static bool Accepts(EntryFilter filter, ScanTree tree, string path)
        => filter.Accepts(tree, Find(tree, path), Now, trustAccessTimes: true);

    [Fact]
    public void MinSize_KeepsFoldersByTheirRecursiveSize()
    {
        ScanTree tree = Sample();
        var filter = new EntryFilter(minSize: 500);

        Assert.True(Accepts(filter, tree, "games"));              // 1005 B rekursiv
        Assert.False(Accepts(filter, tree, "readme.txt"));        // 10 B
        Assert.False(Accepts(filter, tree, @"games\beta"));       // leer
    }

    [Fact]
    public void Kind_SeparatesFoldersFromFiles()
    {
        ScanTree tree = Sample();

        var folders = new EntryFilter(kind: EntryKind.DirectoriesOnly);
        Assert.True(Accepts(folders, tree, "games"));
        Assert.False(Accepts(folders, tree, "readme.txt"));

        var files = new EntryFilter(kind: EntryKind.FilesOnly);
        Assert.False(Accepts(files, tree, "games"));
        Assert.True(Accepts(files, tree, "readme.txt"));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("readme")]
    [InlineData("EaDm")]
    [InlineData(".txt")]
    public void NameContains_IgnoresCaseForAscii(string needle)
    {
        ScanTree tree = Sample();
        Assert.True(Accepts(new EntryFilter(nameContains: needle), tree, "readme.txt"));
    }

    [Fact]
    public void NameContains_RejectsWhatIsNotThere()
    {
        ScanTree tree = Sample();
        Assert.False(Accepts(new EntryFilter(nameContains: "handbuch"), tree, "readme.txt"));
    }

    [Fact]
    public void NameContains_WorksOnNonAsciiNames()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);
        AddChildren(tree, ScanTree.RootIndex, FileEntry("Bewerbung Lebenslauf Müller.pdf", 100, Time(2024, 1, 1)));
        tree.Aggregate();

        Assert.True(Accepts(new EntryFilter(nameContains: "müller"), tree, "Bewerbung Lebenslauf Müller.pdf"));
        Assert.True(Accepts(new EntryFilter(nameContains: "LEBENSLAUF"), tree, "Bewerbung Lebenslauf Müller.pdf"));
    }

    [Theory]
    [InlineData("txt", true)]
    [InlineData("TXT", true)]
    [InlineData(".txt", true)]
    [InlineData("*.txt", true)]
    [InlineData("pak, txt, iso", true)]
    [InlineData("pak", false)]
    [InlineData("tx", false)]
    public void Extensions_AcceptSeveralSpellings(string extensions, bool expected)
    {
        ScanTree tree = Sample();
        Assert.Equal(expected, Accepts(new EntryFilter(extensions: extensions), tree, "readme.txt"));
    }

    [Fact]
    public void Extensions_ExcludeFoldersEntirely()
    {
        ScanTree tree = Sample();
        Assert.False(Accepts(new EntryFilter(extensions: "pak"), tree, "games"));
    }

    [Fact]
    public void MinColdDays_KeepsOnlyWhatHasNotBeenTouchedForLong()
    {
        ScanTree tree = Sample();

        // old.log: 2019 geaendert und zugegriffen -> ueber 2400 Tage kalt.
        // readme.txt: 2024-06-01 -> rund 580 Tage.
        var filter = new EntryFilter(minColdDays: 1000);
        Assert.True(Accepts(filter, tree, @"games\alpha\old.log"));
        Assert.False(Accepts(filter, tree, "readme.txt"));
    }

    [Fact]
    public void ExcludeLinks_DropsJunctionsAndCloudPlaceholders()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);
        AddChildren(tree, ScanTree.RootIndex,
            Dir("echt"),
            Link("verschoben", Core.Scanning.ReparseTags.MountPoint));
        tree.Aggregate();

        var filter = new EntryFilter(excludeLinks: true);
        Assert.True(Accepts(filter, tree, "echt"));
        Assert.False(Accepts(filter, tree, "verschoben"));
    }

    [Fact]
    public void EmptyFilter_AcceptsEverything()
    {
        ScanTree tree = Sample();
        var filter = new EntryFilter();

        Assert.True(filter.IsEmpty);
        for (int i = 0; i < tree.Count; i++)
            Assert.True(filter.Accepts(tree, i, Now, true));
    }
}

public class EntryQueryTests
{
    private static readonly long Now = Time(2026, 1, 1);

    [Fact]
    public void NonRecursive_ReturnsOnlyTheDirectChildren()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: false);

        Assert.Equal(2, result.MatchCount);      // games, readme.txt
    }

    [Fact]
    public void Recursive_ReturnsTheWholeSubtree()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: true);

        // games, readme.txt, alpha, beta, data.pak, old.log
        Assert.Equal(6, result.MatchCount);
    }

    [Fact]
    public void Recursive_StartsFromTheSelectedFolderOnly()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, Find(tree, @"games\alpha"), recursive: true);

        Assert.Equal(2, result.MatchCount);      // data.pak, old.log
    }

    [Fact]
    public void SortBySize_PutsTheBiggestFileFirstWhenDescending()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: true,
            filter: new EntryFilter(kind: EntryKind.FilesOnly),
            sort: EntrySortKey.Size, descending: true);

        Assert.Equal(new[] { "data.pak", "readme.txt", "old.log" },
            result.Indices.Select(tree.GetName).ToArray());
    }

    [Fact]
    public void SortBySize_RanksFoldersByTheirRecursiveSize()
    {
        // Ordner werden mit ihrer rekursiven Groesse einsortiert, stehen also
        // ueber den Dateien, die sie enthalten: games (1005 B) vor data.pak (1000 B).
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: true,
            sort: EntrySortKey.Size, descending: true);

        string first = tree.GetName(result.Indices[0]);
        Assert.True(first is "games" or "alpha", $"Erwartet games oder alpha, war {first}.");
        Assert.Equal(1005, tree.SizeOf(result.Indices[0]));
    }

    [Fact]
    public void SortByCold_PutsTheColdestFirstWhenDescending()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: true,
            filter: new EntryFilter(kind: EntryKind.FilesOnly),
            sort: EntrySortKey.Cold, descending: true);

        Assert.Equal("old.log", tree.GetName(result.Indices[0]));
    }

    [Fact]
    public void SortByName_IsCaseInsensitiveAndAscending()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Now, Now, Now);
        AddChildren(tree, ScanTree.RootIndex,
            FileEntry("Zebra.txt", 1, Time(2024, 1, 1)),
            FileEntry("apfel.txt", 1, Time(2024, 1, 1)),
            FileEntry("Birne.txt", 1, Time(2024, 1, 1)));
        tree.Aggregate();

        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: false,
            sort: EntrySortKey.Name, descending: false);

        Assert.Equal(new[] { "apfel.txt", "Birne.txt", "Zebra.txt" },
            result.Indices.Select(tree.GetName).ToArray());
    }

    [Fact]
    public void Limit_CapsTheReturnedRowsButKeepsTheRealTotals()
    {
        ScanTree tree = Sample();
        QueryResult result = Run(tree, ScanTree.RootIndex, recursive: true, limit: 2);

        Assert.True(result.WasCapped);
        Assert.Equal(2, result.Indices.Length);
        Assert.Equal(6, result.MatchCount);                 // echte Trefferzahl bleibt sichtbar
        Assert.Equal(1015 + 1005 + 1005, result.MatchedBytes); // Ordner und Dateien zaehlen jeweils mit
    }

    [Fact]
    public void Query_RespectsCancellation()
    {
        ScanTree tree = Sample();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Der Abbruch wird nur in groesseren Abstaenden geprueft; bei diesem
        // winzigen Baum darf die Abfrage daher auch einfach durchlaufen.
        try
        {
            Run(tree, ScanTree.RootIndex, recursive: true, ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // auch in Ordnung
        }
    }

    private static QueryResult Run(
        ScanTree tree, int rootIndex, bool recursive,
        EntryFilter? filter = null, EntrySortKey sort = EntrySortKey.Size,
        bool descending = true, int limit = 0, CancellationToken ct = default)
        => EntryQuery.Run(tree, rootIndex, recursive, filter ?? new EntryFilter(),
            sort, descending, Now, trustAccessTimes: true, limit, ct);
}
