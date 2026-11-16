using TreeSizeBetter.Core.Model;

namespace TreeSizeBetter.Core.Tests;

/// <summary>
/// Baut Testbaeume genau so, wie ein Scanner es tut: ein Aufruf pro Ordner,
/// Eltern immer vor ihren Kindern, Namen im Zeichenpuffer statt als strings.
/// </summary>
internal static class TreeBuilder
{
    internal static long Time(int year, int month, int day)
        => new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    internal static (string Name, RawChild Child) Dir(string name) => (name, new RawChild
    {
        Attributes = FileAttr.Directory,
        CreatedUtc = Time(2024, 1, 1),
        ModifiedUtc = Time(2024, 1, 1),
        AccessedUtc = Time(2024, 1, 1)
    });

    internal static (string Name, RawChild Child) Link(string name, uint reparseTag) => (name, new RawChild
    {
        Attributes = FileAttr.Directory | FileAttr.ReparsePoint,
        ReparseTag = reparseTag,
        CreatedUtc = Time(2024, 1, 1),
        ModifiedUtc = Time(2024, 1, 1),
        AccessedUtc = Time(2024, 1, 1)
    });

    internal static (string Name, RawChild Child) FileEntry(
        string name, long length, long modified, long? accessed = null) => (name, new RawChild
    {
        Length = length,
        SizeOnDisk = length == 0 ? 0 : (length + 4095) / 4096 * 4096,
        CreatedUtc = modified,
        ModifiedUtc = modified,
        AccessedUtc = accessed ?? modified
    });

    internal static int AddChildren(ScanTree tree, int parent, params (string Name, RawChild Child)[] children)
    {
        var names = new List<char>();
        var prepared = new RawChild[children.Length];

        for (int i = 0; i < children.Length; i++)
        {
            prepared[i] = children[i].Child;
            prepared[i].NameOffset = names.Count;
            prepared[i].NameLength = children[i].Name.Length;
            names.AddRange(children[i].Name);
        }

        return tree.AppendChildren(parent, prepared, names.ToArray());
    }

    internal static int FindChild(ScanTree tree, int parent, string name)
    {
        foreach (int child in tree.Children(parent))
            if (tree.GetName(child) == name) return child;
        throw new InvalidOperationException($"Kind \"{name}\" unter Index {parent} nicht gefunden.");
    }

    internal static int Find(ScanTree tree, string relativePath)
    {
        int current = ScanTree.RootIndex;
        foreach (string part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            current = FindChild(tree, current, part);
        return current;
    }

    /// <summary>
    /// T:\
    ///   games\
    ///     alpha\  data.pak (1000 B, geaendert 2023, zugegriffen 2025)
    ///             old.log  (5 B,    2019 / 2019)
    ///     beta\   (leer)
    ///   readme.txt (10 B, 2024 / 2024)
    /// </summary>
    internal static ScanTree Sample()
    {
        var tree = new ScanTree();
        tree.CreateRoot(@"T:\", 0, Time(2020, 1, 1), Time(2020, 1, 1), Time(2020, 1, 1));

        int games = AddChildren(tree, ScanTree.RootIndex,
            Dir("games"),
            FileEntry("readme.txt", 10, Time(2024, 6, 1)));

        int alpha = AddChildren(tree, games,
            Dir("alpha"),
            Dir("beta"));

        AddChildren(tree, alpha,
            FileEntry("data.pak", 1000, Time(2023, 1, 15), Time(2025, 3, 1)),
            FileEntry("old.log", 5, Time(2019, 5, 5)));

        tree.Aggregate();
        return tree;
    }
}
