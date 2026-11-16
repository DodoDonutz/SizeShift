using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Cleanup;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Offload;
using TreeSizeBetter.Core.SystemInfo;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

public class SystemReportTests
{
    [Fact]
    public void BuildsAMarkdownReportWithAllSections()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2024, 1, 1), Time(2024, 1, 1), Time(2024, 1, 1));
        int games = AddChildren(tree, ScanTree.RootIndex, Dir("Spiele"));
        AddChildren(tree, games, FileEntry("big.pak", 3L * 1024 * 1024 * 1024, Time(2024, 1, 1)));
        tree.Aggregate();
        AgeAnalysis age = AgeAnalyzer.Analyze(tree, Time(2026, 1, 1));

        var snapshot = new SystemSnapshot("TEST-PC",
            new CpuInfo("Test CPU", 8, 16, 4000),
            new MemoryInfo(32L << 30, 16L << 30, Array.Empty<MemoryModule>()),
            Array.Empty<GpuInfo>(),
            new OsInfo("Windows 11 Pro", "25H2", "26100.1", null, null),
            new[] { new DiskInfo("Test SSD", DiskKind.Ssd, DiskHealth.Gesund, 500L << 30,
                new[] { new VolumeInfo("T:", "Daten", "NTFS", 500L << 30, 100L << 30) }) });
        var autostart = new[]
        {
            new AutostartEntry("Steam", @"C:\Steam\steam.exe", AutostartLocation.BenutzerRegistry, true, null, "Valve", false),
            new AutostartEntry("Alt|Tool", @"C:\weg.exe", AutostartLocation.BenutzerRegistry, true, null, "", true)
        };

        string md = SystemReport.Build(snapshot, autostart,
            new[] { new DriveReportInput(@"T:\", tree, age) },
            Array.Empty<CleanupFinding>(), Array.Empty<RelocationSuggestion>(), Array.Empty<JournalEntry>(), elevated: false);

        Assert.StartsWith("# ", md);
        Assert.Contains("TEST-PC", md);
        Assert.Contains("Test CPU", md);
        Assert.Contains(@"T:\Spiele", md);          // Ordner ab 1 GB erscheint
        Assert.Contains("Steam", md);
        Assert.Contains(@"Alt\|Tool", md);          // Tabellenzeichen maskiert
        Assert.Equal(5, md.Split("\n## ").Length - 1);   // System, Laufwerke, Aufraeumen, Umlagern, Autostart
    }
}
