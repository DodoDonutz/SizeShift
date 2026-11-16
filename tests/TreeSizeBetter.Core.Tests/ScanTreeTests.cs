using TreeSizeBetter.Core.Model;
using static TreeSizeBetter.Core.Tests.TreeBuilder;

namespace TreeSizeBetter.Core.Tests;

public class ScanTreeTests
{
    [Fact]
    public void Aggregation_SumsSizesUpTheTree()
    {
        ScanTree tree = Sample();

        ref DirAggregate root = ref tree.AggregateOf(ScanTree.RootIndex);
        Assert.Equal(1015, root.TotalSize);     // 1000 + 10 + 5
        Assert.Equal(3, root.FileCount);
        Assert.Equal(3, root.DirCount);         // games, alpha, beta
    }

    [Fact]
    public void Aggregation_LeafFolderHoldsOnlyItsOwnFiles()
    {
        ScanTree tree = Sample();
        int alpha = Find(tree, @"games\alpha");

        ref DirAggregate agg = ref tree.AggregateOf(alpha);
        Assert.Equal(1005, agg.TotalSize);
        Assert.Equal(2, agg.FileCount);
        Assert.Equal(0, agg.DirCount);
    }

    [Fact]
    public void Aggregation_EmptyFolderStaysNeutral()
    {
        ScanTree tree = Sample();
        int beta = Find(tree, @"games\beta");

        ref DirAggregate agg = ref tree.AggregateOf(beta);
        Assert.Equal(0, agg.TotalSize);
        Assert.Equal(0, agg.FileCount);

        // Ein leerer Ordner darf die Zeitstempel seines Elternteils nicht verfaelschen:
        // beim Hochrollen wirken diese Werte als neutrales Element von Max bzw. Min.
        Assert.Equal(0, agg.NewestWriteUtc);
        Assert.Equal(long.MaxValue, agg.OldestWriteUtc);
    }

    [Fact]
    public void Aggregation_TakesNewestAndOldestTimestampsFromSubtree()
    {
        ScanTree tree = Sample();
        ref DirAggregate root = ref tree.AggregateOf(ScanTree.RootIndex);

        Assert.Equal(Time(2024, 6, 1), root.NewestWriteUtc);    // readme.txt
        Assert.Equal(Time(2019, 5, 5), root.OldestWriteUtc);    // old.log
        Assert.Equal(Time(2025, 3, 1), root.NewestAccessUtc);   // data.pak
    }

    [Fact]
    public void Aggregation_IgnoresFolderOwnTimestamps()
    {
        // Ordner-Zeitstempel aendern sich schon beim Anlegen einer Datei darin.
        // Wuerden sie mitzaehlen, waere jeder Ordner "frisch benutzt".
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2026, 1, 1), Time(2026, 1, 1), Time(2026, 1, 1));
        int dir = AddChildren(tree, ScanTree.RootIndex, Dir("neu"));
        AddChildren(tree, dir, FileEntry("alt.bin", 100, Time(2015, 1, 1)));
        tree.Aggregate();

        ref DirAggregate agg = ref tree.AggregateOf(dir);
        Assert.Equal(Time(2015, 1, 1), agg.NewestWriteUtc);
    }

    [Fact]
    public void ParentIndexIsAlwaysSmallerThanChildIndex()
    {
        // Genau darauf beruht die Aggregation in einem einzigen Rueckwaertsdurchlauf.
        ScanTree tree = Sample();
        for (int i = 1; i < tree.Count; i++)
            Assert.True(tree.Entries[i].Parent < i,
                $"Eintrag {i} hat Elternteil {tree.Entries[i].Parent} - nicht kleiner als der eigene Index.");
    }

    [Fact]
    public void FullPath_IsBuiltFromParentChain()
    {
        ScanTree tree = Sample();

        Assert.Equal(@"T:\", tree.GetFullPath(ScanTree.RootIndex));
        Assert.Equal(@"T:\games", tree.GetFullPath(Find(tree, "games")));
        Assert.Equal(@"T:\games\alpha\data.pak", tree.GetFullPath(Find(tree, @"games\alpha\data.pak")));
    }

    [Fact]
    public void FullPath_DoesNotDoubleTheSeparatorAtTheDriveRoot()
    {
        ScanTree tree = Sample();
        Assert.Equal(@"T:\readme.txt", tree.GetFullPath(Find(tree, "readme.txt")));
    }

    [Fact]
    public void SizeOf_UsesAggregateForFoldersAndOwnLengthForFiles()
    {
        ScanTree tree = Sample();

        Assert.Equal(1005, tree.SizeOf(Find(tree, "games")));
        Assert.Equal(10, tree.SizeOf(Find(tree, "readme.txt")));
    }

    [Fact]
    public void GetNameBytes_MatchesGetName()
    {
        ScanTree tree = Sample();
        int index = Find(tree, "readme.txt");

        string fromBytes = System.Text.Encoding.UTF8.GetString(tree.GetNameBytes(index));
        Assert.Equal(tree.GetName(index), fromBytes);
    }

    [Fact]
    public void NamePool_RoundTripsUnicodeNames()
    {
        var pool = new NamePool();
        string[] names = { "Gruesse", "Ümläüte.txt", "日本語のファイル", "a", new('x', 255) };
        var handles = new List<(long Handle, ushort Length)>();

        foreach (string name in names)
        {
            long handle = pool.Add(name.AsSpan(), out ushort length);
            handles.Add((handle, length));
        }

        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], pool.GetString(handles[i].Handle, handles[i].Length));
    }

    [Fact]
    public void NamePool_KeepsNamesWholeAcrossChunkBoundaries()
    {
        // Ein Name darf nie ueber eine Blockgrenze laufen, sonst muesste das Lesen
        // zwei Bloecke zusammensetzen. Genug Namen ablegen, um mehrere Bloecke zu fuellen.
        var pool = new NamePool();
        var handles = new List<(long Handle, ushort Length, string Name)>();

        for (int i = 0; i < 20_000; i++)
        {
            string name = $"datei-{i}-{new string('y', 200)}";
            long handle = pool.Add(name.AsSpan(), out ushort length);
            handles.Add((handle, length, name));
        }

        foreach (var (handle, length, name) in handles)
            Assert.Equal(name, pool.GetString(handle, length));
    }

    [Fact]
    public void ChunkedList_GrowsBeyondOneChunkAndKeepsValues()
    {
        var list = new ChunkedList<FsEntry>();
        const int chunk = ChunkedList<FsEntry>.ChunkSize;
        int count = chunk * 2 + 7;

        for (int i = 0; i < count; i++) list.Add(new FsEntry { Length = i });

        Assert.Equal(count, list.Count);
        Assert.Equal(0, list[0].Length);
        Assert.Equal(chunk, list[chunk].Length);
        Assert.Equal(count - 1, list[count - 1].Length);
    }

    [Fact]
    public void ChunkedList_ReserveReturnsContiguousBlock()
    {
        var list = new ChunkedList<int>();
        list.Add(1);
        int start = list.Reserve(5);

        Assert.Equal(1, start);
        Assert.Equal(6, list.Count);

        for (int i = 0; i < 5; i++) list[start + i] = i * 10;
        Assert.Equal(40, list[5]);
    }

    [Fact]
    public void ChunkedList_RejectsIndexBeyondCount()
    {
        var list = new ChunkedList<int>();
        list.Add(7);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[1]);
    }
}
