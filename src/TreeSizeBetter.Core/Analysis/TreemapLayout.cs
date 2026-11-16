using TreeSizeBetter.Core.Model;

namespace TreeSizeBetter.Core.Analysis;

/// <summary>Rechteck ohne WPF-Abhaengigkeit, damit das Layout im Core testbar bleibt.</summary>
public readonly record struct TreeRect(double X, double Y, double W, double H)
{
    public double Area => W * H;
    public double Right => X + W;
    public double Bottom => Y + H;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    public TreeRect Deflate(double left, double top, double right, double bottom)
        => new(X + left, Y + top, Math.Max(0, W - left - right), Math.Max(0, H - top - bottom));
}

public enum TileKind
{
    /// <summary>Ordner, in dem weitere Kacheln liegen - wird als Rahmen mit Kopfzeile gezeichnet.</summary>
    Container,

    /// <summary>Datei, oder Ordner, der zu klein oder zu tief zum Aufteilen ist - wird gefuellt gezeichnet.</summary>
    Leaf
}

public readonly record struct TreemapTile(int Index, TreeRect Rect, int Depth, TileKind Kind, bool IsDirectory);

public sealed class TreemapOptions
{
    /// <summary>Wie viele Ordnerebenen unterhalb der Wurzel aufgeteilt werden.</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>Hoehe der Namenszeile oben in einem Ordnerrahmen.</summary>
    public double HeaderHeight { get; init; } = 16;

    /// <summary>Innenabstand eines Ordnerrahmens, damit Ebenen voneinander unterscheidbar bleiben.</summary>
    public double Padding { get; init; } = 2;

    /// <summary>Ordner kleiner als das werden nicht weiter aufgeteilt, sondern als eine Kachel gezeigt.</summary>
    public double MinContainerSide { get; init; } = 30;

    /// <summary>Kacheln mit einer Seite darunter werden nicht gezeichnet - sie waeren nur Pixelrauschen.</summary>
    public double MinTileSide { get; init; } = 2;

    /// <summary>Obergrenze, damit das Zeichnen auch bei Millionen Dateien fluessig bleibt.</summary>
    public int MaxTiles { get; init; } = 40_000;
}

/// <summary>
/// Berechnet ein Kacheldiagramm (Treemap) nach dem "Squarified"-Verfahren von
/// Bruls, Huizing und van Wijk: Kacheln werden zeilenweise so gruppiert, dass sie
/// moeglichst quadratisch bleiben. Das klassische Streifenverfahren erzeugt bei
/// vielen kleinen Dateien sonst haarduenne Streifen, die man weder anklicken
/// noch vergleichen kann.
///
/// Ordner werden als Rahmen mit Kopfzeile gezeichnet und rekursiv aufgeteilt,
/// solange sie gross genug sind und die Tiefe es erlaubt. Die Flaeche jeder
/// Kachel ist proportional zu ihrem Gewicht (Groesse, belegter Platz oder
/// Dateianzahl - je nach Anzeigemodus).
/// </summary>
public static class TreemapLayout
{
    public static List<TreemapTile> Compute(
        ScanTree tree, int rootIndex, TreeRect bounds, Func<int, long> weight, TreemapOptions? options = null)
    {
        options ??= new TreemapOptions();
        var tiles = new List<TreemapTile>(1024);
        if (bounds.W < 1 || bounds.H < 1) return tiles;

        if (!tree.Entries[rootIndex].IsDirectory)
        {
            tiles.Add(new TreemapTile(rootIndex, bounds, 0, TileKind.Leaf, false));
            return tiles;
        }

        LayoutChildren(tree, rootIndex, bounds, 0, weight, options, tiles);
        return tiles;
    }

    private static void LayoutChildren(
        ScanTree tree, int parent, TreeRect area, int depth, Func<int, long> weight,
        TreemapOptions options, List<TreemapTile> tiles)
    {
        if (area.W < options.MinTileSide || area.H < options.MinTileSide) return;

        var items = new List<(int Index, double Weight)>(Math.Min(tree.Entries[parent].ChildCount, 4096));
        double total = 0;
        foreach (int child in tree.Children(parent))
        {
            long w = weight(child);
            if (w <= 0) continue;
            items.Add((child, w));
            total += w;
        }
        if (items.Count == 0 || total <= 0) return;

        items.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));

        double scale = area.Area / total;
        var scaled = new List<(int Index, double Area)>(items.Count);
        foreach (var (index, w) in items) scaled.Add((index, w * scale));

        var placed = new List<(int Index, TreeRect Rect)>(scaled.Count);
        Squarify(scaled, area, placed);

        foreach (var (index, rect) in placed)
        {
            if (tiles.Count >= options.MaxTiles) return;
            if (rect.W < options.MinTileSide || rect.H < options.MinTileSide) continue;

            ref FsEntry entry = ref tree.Entries[index];
            bool isDirectory = entry.IsDirectory;
            bool canSplit = isDirectory && !entry.IsReparsePoint && entry.ChildCount > 0
                            && depth + 1 < options.MaxDepth
                            && rect.W >= options.MinContainerSide && rect.H >= options.MinContainerSide;

            if (!canSplit)
            {
                tiles.Add(new TreemapTile(index, rect, depth, TileKind.Leaf, isDirectory));
                continue;
            }

            tiles.Add(new TreemapTile(index, rect, depth, TileKind.Container, true));

            // Kopfzeile nur, wenn danach noch genug Platz fuer den Inhalt bleibt.
            double header = rect.H >= options.HeaderHeight * 2.5 ? options.HeaderHeight : 0;
            TreeRect inner = rect.Deflate(options.Padding, options.Padding + header, options.Padding, options.Padding);
            LayoutChildren(tree, index, inner, depth + 1, weight, options, tiles);
        }
    }

    /// <summary>
    /// Kern des Verfahrens: Kacheln (absteigend sortiert) so lange in die aktuelle
    /// Zeile nehmen, wie das schlechteste Seitenverhaeltnis darin besser wird;
    /// dann die Zeile entlang der kuerzeren Seite ablegen und mit dem Rest weitermachen.
    /// </summary>
    internal static void Squarify(List<(int Index, double Area)> items, TreeRect rect, List<(int Index, TreeRect Rect)> output)
    {
        int start = 0;
        while (start < items.Count)
        {
            double side = Math.Min(rect.W, rect.H);
            if (side <= 0) return;

            int end = start;
            double rowArea = 0;
            double worst = double.MaxValue;
            while (end < items.Count)
            {
                double candidate = rowArea + items[end].Area;
                double ratio = WorstRatio(items[start].Area, items[end].Area, candidate, side);
                if (end > start && ratio > worst) break;
                worst = ratio;
                rowArea = candidate;
                end++;
            }

            if (rect.W >= rect.H)
            {
                // Breiter als hoch: die Zeile wird eine Spalte am linken Rand.
                double columnWidth = rowArea / rect.H;
                double y = rect.Y;
                for (int i = start; i < end; i++)
                {
                    double h = items[i].Area / columnWidth;
                    output.Add((items[i].Index, new TreeRect(rect.X, y, columnWidth, h)));
                    y += h;
                }
                rect = new TreeRect(rect.X + columnWidth, rect.Y, rect.W - columnWidth, rect.H);
            }
            else
            {
                // Hoeher als breit: die Zeile liegt oben.
                double rowHeight = rowArea / rect.W;
                double x = rect.X;
                for (int i = start; i < end; i++)
                {
                    double w = items[i].Area / rowHeight;
                    output.Add((items[i].Index, new TreeRect(x, rect.Y, w, rowHeight)));
                    x += w;
                }
                rect = new TreeRect(rect.X, rect.Y + rowHeight, rect.W, rect.H - rowHeight);
            }

            start = end;
        }
    }

    /// <summary>Schlechtestes Seitenverhaeltnis einer Zeile (1 = perfektes Quadrat).</summary>
    private static double WorstRatio(double largest, double smallest, double sum, double side)
    {
        double sum2 = sum * sum;
        double side2 = side * side;
        return Math.Max(side2 * largest / sum2, sum2 / (side2 * smallest));
    }

    /// <summary>Tiefste Kachel an einem Punkt - die zuletzt gezeichnete liegt oben.</summary>
    public static TreemapTile? HitTest(IReadOnlyList<TreemapTile> tiles, double x, double y)
    {
        for (int i = tiles.Count - 1; i >= 0; i--)
            if (tiles[i].Rect.Contains(x, y)) return tiles[i];
        return null;
    }
}
