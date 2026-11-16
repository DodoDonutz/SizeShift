using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

public class TreemapTests
{
    private static readonly TreeRect Square = new(0, 0, 400, 400);

    /// <summary>Wurzel mit Dateien der angegebenen Groessen direkt darunter.</summary>
    private static ScanTree Flat(params long[] sizes)
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));
        AddChildren(tree, ScanTree.RootIndex,
            sizes.Select((s, i) => FileEntry($"f{i}.bin", s, Time(2025, 1, 1))).ToArray());
        tree.Aggregate();
        return tree;
    }

    private static List<TreemapTile> Layout(ScanTree tree, TreeRect bounds, TreemapOptions? options = null)
        => TreemapLayout.Compute(tree, ScanTree.RootIndex, bounds, tree.SizeOf,
            options ?? new TreemapOptions { MinTileSide = 0 });

    [Fact]
    public void AreasAreProportionalToSizes()
    {
        ScanTree tree = Flat(600, 300, 100);
        List<TreemapTile> tiles = Layout(tree, Square);

        double total = Square.Area;
        Assert.Equal(3, tiles.Count);
        foreach (TreemapTile tile in tiles)
        {
            double expected = (double)tree.SizeOf(tile.Index) / 1000 * total;
            Assert.InRange(tile.Rect.Area, expected * 0.999, expected * 1.001);
        }
    }

    [Fact]
    public void TilesFillTheAreaWithoutGapsOrOverlap()
    {
        ScanTree tree = Flat(50, 40, 30, 20, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
        List<TreemapTile> tiles = Layout(tree, new TreeRect(0, 0, 640, 360));

        Assert.InRange(tiles.Sum(t => t.Rect.Area), 640 * 360 * 0.999, 640 * 360 * 1.001);

        for (int i = 0; i < tiles.Count; i++)
        for (int j = i + 1; j < tiles.Count; j++)
        {
            TreeRect a = tiles[i].Rect, b = tiles[j].Rect;
            double overlapW = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            double overlapH = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
            Assert.False(overlapW > 0.001 && overlapH > 0.001, $"Kacheln {i} und {j} ueberlappen.");
        }
    }

    [Fact]
    public void TilesStayInsideTheBounds()
    {
        var bounds = new TreeRect(10, 20, 300, 150);
        ScanTree tree = Flat(Enumerable.Range(1, 60).Select(i => (long)i * 13).ToArray());

        foreach (TreemapTile tile in Layout(tree, bounds))
        {
            Assert.True(tile.Rect.X >= bounds.X - 0.001 && tile.Rect.Right <= bounds.Right + 0.001);
            Assert.True(tile.Rect.Y >= bounds.Y - 0.001 && tile.Rect.Bottom <= bounds.Bottom + 0.001);
        }
    }

    [Fact]
    public void EqualItemsBecomeNearlySquare()
    {
        // Genau das unterscheidet "squarified" vom Streifenverfahren: 100 gleich
        // grosse Dateien werden Kacheln, keine 100 haarduennen Streifen.
        ScanTree tree = Flat(Enumerable.Repeat(1000L, 100).ToArray());
        List<TreemapTile> tiles = Layout(tree, Square);

        double worst = tiles.Max(t => Math.Max(t.Rect.W / t.Rect.H, t.Rect.H / t.Rect.W));
        Assert.True(worst < 3, $"Schlechtestes Seitenverhaeltnis {worst:F1}");
    }

    [Fact]
    public void EmptyFilesGetNoTile()
    {
        ScanTree tree = Flat(100, 0, 50);
        Assert.Equal(2, Layout(tree, Square).Count);
    }

    [Fact]
    public void FoldersBecomeContainersWithTheirChildrenInside()
    {
        ScanTree tree = Sample();   // games\alpha\{data.pak, old.log}, readme.txt
        List<TreemapTile> tiles = Layout(tree, Square, new TreemapOptions { MinTileSide = 0, MinContainerSide = 0 });

        TreemapTile games = tiles.Single(t => t.Index == Find(tree, "games"));
        TreemapTile pak = tiles.Single(t => t.Index == Find(tree, @"games\alpha\data.pak"));

        Assert.Equal(TileKind.Container, games.Kind);
        Assert.Equal(TileKind.Leaf, pak.Kind);
        Assert.True(pak.Rect.X >= games.Rect.X && pak.Rect.Right <= games.Rect.Right + 0.001);
        Assert.True(pak.Rect.Y >= games.Rect.Y && pak.Rect.Bottom <= games.Rect.Bottom + 0.001);
        Assert.True(pak.Depth > games.Depth);
    }

    [Fact]
    public void DepthLimitTurnsDeeperFoldersIntoLeaves()
    {
        ScanTree tree = Sample();
        List<TreemapTile> tiles = Layout(tree, Square, new TreemapOptions { MaxDepth = 1, MinTileSide = 0, MinContainerSide = 0 });

        // Nur die oberste Ebene: games ist eine gefuellte Kachel, darin nichts.
        Assert.Equal(TileKind.Leaf, tiles.Single(t => t.Index == Find(tree, "games")).Kind);
        Assert.DoesNotContain(tiles, t => t.Index == Find(tree, @"games\alpha\data.pak"));
    }

    [Fact]
    public void TileLimitIsRespected()
    {
        ScanTree tree = Flat(Enumerable.Range(1, 500).Select(i => (long)i).ToArray());
        List<TreemapTile> tiles = Layout(tree, Square, new TreemapOptions { MaxTiles = 50, MinTileSide = 0 });
        Assert.True(tiles.Count <= 50);
    }

    [Fact]
    public void HitTestFindsTheDeepestTile()
    {
        ScanTree tree = Sample();
        List<TreemapTile> tiles = Layout(tree, Square, new TreemapOptions { MinTileSide = 0, MinContainerSide = 0 });
        TreemapTile pak = tiles.Single(t => t.Index == Find(tree, @"games\alpha\data.pak"));

        TreemapTile? hit = TreemapLayout.HitTest(tiles,
            pak.Rect.X + pak.Rect.W / 2, pak.Rect.Y + pak.Rect.H / 2);

        Assert.Equal(pak.Index, hit?.Index);
    }
}
