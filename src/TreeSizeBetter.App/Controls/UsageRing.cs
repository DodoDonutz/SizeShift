using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TreeSizeBetter.App.Controls;

/// <summary>
/// Ringdiagramm fuer Fuellstaende. Laeuft beim Erscheinen von 0 auf den Zielwert
/// hoch - mit einstellbarer Verzoegerung, damit mehrere Ringe nacheinander
/// einlaufen statt alle gleichzeitig.
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double), typeof(UsageRing),
        new PropertyMetadata(0.0, (d, _) => ((UsageRing)d).AnimateToTarget()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(
        nameof(Delay), typeof(TimeSpan), typeof(UsageRing), new PropertyMetadata(TimeSpan.Zero));

    public UsageRing()
    {
        Loaded += (_, _) => AnimateToTarget();
    }

    /// <summary>Aktuell gezeichneter Anteil 0..1 - wird animiert.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Zielwert 0..1. Aendert er sich, laeuft der Ring dorthin.</summary>
    public double Target
    {
        get => (double)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public TimeSpan Delay
    {
        get => (TimeSpan)GetValue(DelayProperty);
        set => SetValue(DelayProperty, value);
    }

    private void AnimateToTarget()
    {
        if (!IsLoaded) return;
        var animation = new DoubleAnimation(0, Math.Clamp(Target, 0, 1), TimeSpan.FromMilliseconds(1100))
        {
            BeginTime = Delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(ValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = (size - Thickness) / 2;

        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        double value = Math.Clamp(Value, 0, 1);
        if (value <= 0.0005) return;

        var pen = new Pen(FillBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.9995)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        // Bogen ab 12 Uhr im Uhrzeigersinn.
        double angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: value > 0.5,
                SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
