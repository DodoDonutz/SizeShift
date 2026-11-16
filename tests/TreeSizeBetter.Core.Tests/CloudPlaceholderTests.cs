using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Scanning;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

/// <summary>
/// Online-Dateien von iCloud oder OneDrive haben eine grosse logische Groesse,
/// belegen lokal aber nichts. Bestenlisten und Vorschlaege muessen nach
/// belegtem Platz gehen - sonst schlagen sie Aufraeumen vor, das null Byte bringt.
/// </summary>
public class CloudPlaceholderTests
{
    private static ScanResult Build()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));
        int cloud = AddChildren(tree, ScanTree.RootIndex, Dir("iCloudDrive"), Dir("Lokal"));

        var placeholder = new RawChild
        {
            Attributes = FileAttr.Offline | FileAttr.RecallOnDataAccess,
            Length = 10L * 1024 * 1024 * 1024,   // 10 GB logisch
            SizeOnDisk = 0,                      // aber lokal nichts
            ModifiedUtc = Time(2015, 1, 1),
            AccessedUtc = Time(2015, 1, 1),
            CreatedUtc = Time(2015, 1, 1)
        };
        AddChildren(tree, cloud, ("urlaub.mov", placeholder));
        AddChildren(tree, cloud + 1, FileEntry("echt.bin", 800L * 1024 * 1024, Time(2018, 1, 1)));
        tree.Aggregate();

        return new ScanResult
        {
            Tree = tree, ScannerName = "Test", StartedUtc = DateTime.UtcNow, Duration = TimeSpan.Zero,
            TotalBytes = tree.SizeOf(ScanTree.RootIndex)
        };
    }

    [Fact]
    public void LargestFilesIgnoreOnlineOnlyFiles()
    {
        ScanResult result = Build();
        ScanOverview overview = ScanOverview.Build(result, AgeAnalyzer.Analyze(result.Tree, Time(2026, 1, 1)));

        Assert.EndsWith("echt.bin", overview.LargestFiles[0].Path);
    }

    [Fact]
    public void LargestFoldersRankByWhatIsActuallyOnDisk()
    {
        ScanResult result = Build();
        ScanOverview overview = ScanOverview.Build(result, AgeAnalyzer.Analyze(result.Tree, Time(2026, 1, 1)));

        Assert.EndsWith("Lokal", overview.LargestFolders[0].Path);
    }

    [Fact]
    public void ColdBigFoldersSkipCloudFoldersThatFreeNothing()
    {
        ScanResult result = Build();
        ScanOverview overview = ScanOverview.Build(result, AgeAnalyzer.Analyze(result.Tree, Time(2026, 1, 1)));

        Assert.DoesNotContain(overview.ColdestBigFolders, f => f.Path.EndsWith("iCloudDrive"));
    }
}
