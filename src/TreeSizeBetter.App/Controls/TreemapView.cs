using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TreeSizeBetter.App.ViewModels;
using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Model;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.App.Controls;

public enum TreemapColoring
{
    Alter,
    Dateityp
}

/// <summary>
/// Kacheldiagramm. Zeichnet direkt in zwei DrawingVisuals statt mit WPF-Shapes:
/// bei zehntausenden Kacheln waeren eigene Elemente pro Kachel nicht zu
/// gebrauchen. Die untere Ebene enthaelt alle Kacheln und wird nur bei neuen
/// Daten oder neuer Groesse neu gezeichnet, die obere nur die Hervorhebung unter
/// der Maus - Mausbewegungen kosten damit fast nichts.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private readonly DrawingVisual _tilesLayer = new();
    private readonly DrawingVisual _hoverLayer = new();
    private readonly VisualCollection _visuals;
    private readonly Dictionary<Color, Brush> _cushions = new();

    private ScanContext? _context;
    private int _rootIndex = -1;
    private DisplayMode _mode = DisplayMode.SizeOnDisk;
    private int _depth = 4;
    private TreemapColoring _coloring = TreemapColoring.Alter;
    private List<TreemapTile> _tiles = new();
    private Size _laidOutFor;
    private int _hoverIndex = -1;

    private static readonly Typeface HeaderFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush DarkBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)));
    private static readonly Brush LightBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF1)));
    private static readonly Brush DarkHeaderText = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush LightHeaderText = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x27)));
    private static readonly Brush DarkHeaderDim = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE4, 0xE7, 0xEC)));
    private static readonly Brush LightHeaderDim = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x1B, 0x1F, 0x27)));
    private static readonly Brush DimText = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE4, 0xE7, 0xEC)));

    // Hintergrund und Ordnerrahmen folgen der Darstellung; die farbigen Kacheln bleiben gleich.
    private static bool Light => Shell.ThemeManager.IsLight;
    private static Brush Background => Light ? LightBackground : DarkBackground;
    private static Brush HeaderText => Light ? LightHeaderText : DarkHeaderText;
    private static Brush HeaderDim => Light ? LightHeaderDim : DarkHeaderDim;
    private static readonly Brush LeafText = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen TileEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1));
    private static readonly Pen HoverPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public TreemapView()
    {
        _visuals = new VisualCollection(this) { _tilesLayer, _hoverLayer };
        ClipToBounds = true;
        Focusable = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);
    }

    /// <summary>Kachel unter der Maus hat gewechselt (-1 = keine).</summary>
    public event Action<int>? TileHovered;

    /// <summary>Doppelklick auf eine Kachel - in der Regel: hineinzoomen.</summary>
    public event Action<int>? TileActivated;

    /// <summary>Rechtsklick auf eine Kachel.</summary>
    public event Action<int>? TileContextRequested;

    public IReadOnlyList<TreemapTile> Tiles => _tiles;

    public void Show(ScanContext? context, int rootIndex, DisplayMode mode, int depth, TreemapColoring coloring)
    {
        bool rootChanged = context != _context || rootIndex != _rootIndex;
        _context = context;
        _rootIndex = rootIndex;
        _mode = mode;
        _depth = depth;
        _coloring = coloring;
        _laidOutFor = Size.Empty;
        Relayout();
        if (rootChanged) PlayEntrance();
    }

    // ------------------------------------------------------------- Layout und Zeichnen

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Relayout();
    }

    private void Relayout()
    {
        Size size = RenderSize;
        if (_context is null || _rootIndex < 0 || size.Width < 4 || size.Height < 4)
        {
            _tiles = new List<TreemapTile>();
            DrawTiles();
            return;
        }
        if (size == _laidOutFor) return;
        _laidOutFor = size;

        ScanTree tree = _context.Tree;
        Func<int, long> weight = _mode switch
        {
            DisplayMode.FileCount => i => tree.Entries[i].IsDirectory ? tree.Entries[i].AggregateIndex >= 0
                ? tree.Aggregates[tree.Entries[i].AggregateIndex].FileCount : 0 : 1,
            DisplayMode.Size or DisplayMode.Percent => tree.SizeOf,
            _ => tree.SizeOnDiskOf
        };

        _tiles = TreemapLayout.Compute(tree, _rootIndex, new TreeRect(0, 0, size.Width, size.Height), weight,
            new TreemapOptions { MaxDepth = _depth });
        DrawTiles();
        SetHover(-1);
    }

    private void DrawTiles()
    {
        using DrawingContext dc = _tilesLayer.RenderOpen();
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (_context is null) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        ScanTree tree = _context.Tree;

        foreach (TreemapTile tile in _tiles)
        {
            Rect r = ToRect(tile.Rect);
            if (tile.Kind == TileKind.Container)
            {
                // Ordnerrahmen: je tiefer, desto heller (hell: dunkler) - so bleiben die Ebenen unterscheidbar.
                byte shade = Light
                    ? (byte)Math.Max(0xDC - tile.Depth * 8, 0xA8)
                    : (byte)Math.Min(0x2C + tile.Depth * 9, 0x58);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(shade, (byte)Math.Min(shade + 3, 255), (byte)Math.Min(shade + 8, 255))),
                    TileEdge, Inset(r, 0.5), 3, 3);

                if (r.Width > 46 && r.Height >= 40)
                    DrawHeader(dc, tree, tile, r, dpi);
                continue;
            }

            Color color = ColorFor(tree, tile);
            double radius = Math.Min(r.Width, r.Height) > 10 ? 2.5 : 0;
            dc.DrawRoundedRectangle(Cushion(color), r.Width > 3 && r.Height > 3 ? TileEdge : null, Inset(r, 0.5), radius, radius);

            if (r.Width > 54 && r.Height > 18)
                DrawLeafLabel(dc, tree, tile, r, dpi);
        }
    }

    private void DrawHeader(DrawingContext dc, ScanTree tree, TreemapTile tile, Rect r, double dpi)
    {
        string name = tile.Index == ScanTree.RootIndex ? tree.RootPath : tree.GetName(tile.Index);
        string size = SizeFormat.Bytes(WeightBytes(tree, tile.Index));
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, HeaderFace, 11, HeaderText, dpi)
        {
            MaxTextWidth = Math.Max(1, r.Width - 70),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(text, new Point(r.X + 5, r.Y + 2));

        var sizeText = new FormattedText(size, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, HeaderDim, dpi);
        if (r.Width > text.Width + sizeText.Width + 20)
            dc.DrawText(sizeText, new Point(r.Right - sizeText.Width - 6, r.Y + 2.5));
    }

    private void DrawLeafLabel(DrawingContext dc, ScanTree tree, TreemapTile tile, Rect r, double dpi)
    {
        var name = new FormattedText(tree.GetName(tile.Index), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            LabelFace, 11, LeafText, dpi)
        {
            MaxTextWidth = Math.Max(1, r.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(name, new Point(r.X + 4, r.Y + 3));

        if (r.Height > 34)
        {
            var size = new FormattedText(SizeFormat.Bytes(WeightBytes(tree, tile.Index)), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelFace, 10, DimText, dpi);
            dc.DrawText(size, new Point(r.X + 4, r.Y + 17));
        }
    }

    private long WeightBytes(ScanTree tree, int index)
        => _mode == DisplayMode.Size ? tree.SizeOf(index) : tree.SizeOnDiskOf(index);

    // ------------------------------------------------------------- Farben

    private Color ColorFor(ScanTree tree, TreemapTile tile) => _coloring switch
    {
        TreemapColoring.Dateityp => tile.IsDirectory ? Color.FromRgb(0x5B, 0x64, 0x72) : TypeColor(tree.GetName(tile.Index)),
        _ => AgeColor(tile.IsDirectory ? DominantColdness(tree, tile.Index) : AgeAnalyzer.Classify(_context!.ColdDays(tile.Index)))
    };

    /// <summary>
    /// Alter, in das die meisten Bytes eines Ordners fallen. Bewusst nicht die
    /// juengste Aktivitaet wie im Baum: dort ist "irgendwas darin ist frisch" die
    /// richtige Frage, auf der Flaeche aber faerbt sie fast jeden grossen Ordner
    /// rot. "Der Grossteil liegt seit zwei Jahren unberuehrt" ist die Aussage, die
    /// man in einem Kacheldiagramm sehen will. Die Teilbaeume der Blattkacheln
    /// ueberschneiden sich nicht, also kostet das insgesamt hoechstens einen
    /// Durchlauf ueber den Baum.
    /// </summary>
    private Coldness DominantColdness(ScanTree tree, int folder)
    {
        Span<long> bytesPerBucket = stackalloc long[6];
        long now = _context!.Now;
        bool trust = _context.TrustAccessTimes;

        var pending = new Stack<int>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            foreach (int child in tree.Children(current))
            {
                ref FsEntry e = ref tree.Entries[child];
                if (e.IsDirectory)
                {
                    if (!e.IsReparsePoint) pending.Push(child);
                    continue;
                }
                long activity = trust ? Math.Max(e.ModifiedUtc, e.AccessedUtc) : e.ModifiedUtc;
                Coldness c = AgeAnalyzer.Classify(FileTimeUtil.DaysSince(activity, now));
                bytesPerBucket[(int)c] += Math.Max(e.SizeOnDisk, 1);
            }
        }

        int best = 0;
        for (int i = 1; i < bytesPerBucket.Length; i++)
            if (bytesPerBucket[i] > bytesPerBucket[best]) best = i;
        return (Coldness)best;
    }

    /// <summary>Warm = kuerzlich benutzt, blau = lange nicht angefasst - dieselbe Skala wie im Baum.</summary>
    public static Color AgeColor(Coldness coldness) => coldness switch
    {
        Coldness.Hot => Color.FromRgb(0xE8, 0x63, 0x4F),
        Coldness.Warm => Color.FromRgb(0xE8, 0x9A, 0x4F),
        Coldness.Cool => Color.FromRgb(0xC9, 0xB4, 0x52),
        Coldness.Cold => Color.FromRgb(0x4F, 0xA3, 0xE0),
        Coldness.Frozen => Color.FromRgb(0x6A, 0x6E, 0xE8),
        _ => Color.FromRgb(0x5B, 0x64, 0x72)
    };

    /// <summary>Dateitypen in sinnvolle Gruppen statt einer Farbe pro Endung - sonst ist keine Legende moeglich.</summary>
    public static readonly (string Label, Color Color, string[] Extensions)[] TypeGroups =
    {
        (Loc.T("tm.video"), Color.FromRgb(0xE0, 0x5A, 0x8C), new[] { "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv" }),
        (Loc.T("tm.image"), Color.FromRgb(0xE8, 0x9A, 0x4F), new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "raw", "cr2", "nef", "arw", "psd", "webp" }),
        (Loc.T("tm.audio"), Color.FromRgb(0xC9, 0x6A, 0xE8), new[] { "mp3", "flac", "wav", "ogg", "m4a", "aac", "wma" }),
        (Loc.T("tm.gameData"), Color.FromRgb(0x4F, 0xC2, 0x8E), new[] { "pak", "ucas", "utoc", "rda", "starpak", "vpk", "bundle", "assets", "resource", "big", "dat", "bin", "arc", "wad", "pck" }),
        (Loc.T("tm.program"), Color.FromRgb(0x4F, 0xA3, 0xE0), new[] { "exe", "dll", "sys", "msi", "msp", "cab", "so", "a", "lib", "pdb", "node" }),
        (Loc.T("tm.archive"), Color.FromRgb(0xC9, 0xB4, 0x52), new[] { "zip", "7z", "rar", "tar", "gz", "iso", "img", "vhd", "vhdx", "vmdk" }),
        (Loc.T("tm.document"), Color.FromRgb(0x8A, 0x9B, 0xB0), new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "csv", "odt" }),
        (Loc.T("tm.code"), Color.FromRgb(0x6A, 0xD0, 0xD8), new[] { "js", "ts", "cs", "py", "java", "cpp", "h", "json", "xml", "html", "css", "jar", "class" })
    };

    public static readonly Color OtherTypeColor = Color.FromRgb(0x6B, 0x72, 0x80);

    private static Color TypeColor(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return OtherTypeColor;
        string extension = name[(dot + 1)..].ToLowerInvariant();
        foreach (var group in TypeGroups)
            if (Array.IndexOf(group.Extensions, extension) >= 0) return group.Color;
        return OtherTypeColor;
    }

    /// <summary>
    /// "Kissen"-Fuellung: oben links aufgehellt, unten rechts abgedunkelt. Laesst
    /// jede Kachel leicht gewoelbt wirken - so erkennt das Auge die Grenzen
    /// zwischen gleichfarbigen Nachbarn ohne harte Linien.
    /// </summary>
    private Brush Cushion(Color color)
    {
        if (_cushions.TryGetValue(color, out Brush? cached)) return cached;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Mix(color, Colors.White, 0.30), 0),
                new GradientStop(color, 0.45),
                new GradientStop(Mix(color, Colors.Black, 0.38), 1)
            }
        };
        brush.Freeze();
        _cushions[color] = brush;
        return brush;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ------------------------------------------------------------- Maus

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        TreemapTile? hit = TreemapLayout.HitTest(_tiles, p.X, p.Y);
        SetHover(hit?.Index ?? -1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && _hoverIndex >= 0) TileActivated?.Invoke(_hoverIndex);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_hoverIndex < 0) return;
        e.Handled = true;
        TileContextRequested?.Invoke(_hoverIndex);
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;

        using (DrawingContext dc = _hoverLayer.RenderOpen())
        {
            TreemapTile? tile = index < 0 ? null : _tiles.FirstOrDefault(t => t.Index == index);
            if (tile is { } t)
            {
                Rect r = Inset(ToRect(t.Rect), 1);
                dc.DrawRoundedRectangle(HoverFill, HoverPen, r, 3, 3);
            }
        }

        TileHovered?.Invoke(index);
    }

    // ------------------------------------------------------------- Animation

    /// <summary>Neue Wurzel: kurz eingeblendet und leicht herangezoomt - zeigt, dass sich der Ausschnitt geaendert hat.</summary>
    private void PlayEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        if (RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        }
    }

    // ------------------------------------------------------------- Hilfen

    private static Rect ToRect(TreeRect r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));

    private static Rect Inset(Rect r, double by)
        => r.Width > by * 2 && r.Height > by * 2 ? new Rect(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2) : r;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
