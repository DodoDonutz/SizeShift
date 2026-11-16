using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TreeSizeBetter.Core.Analysis;

namespace TreeSizeBetter.App;

/// <summary>Faerbt Eintraege nach Kaelte: warm = kuerzlich benutzt, blau = lange nicht angefasst.</summary>
public sealed class ColdnessToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is Coldness coldness
            ? coldness switch
            {
                Coldness.Hot => "ColdHot",
                Coldness.Warm => "ColdWarm",
                Coldness.Cool => "ColdCool",
                Coldness.Cold => "ColdCold",
                Coldness.Frozen => "ColdFrozen",
                _ => "ColdUnknown"
            }
            : "ColdUnknown";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Breite des Balkens hinter dem Namen: verfuegbare Breite mal Anteil. Als
/// Mehrfachbindung, damit der Balken beim Verbreitern der Spalte mitwaechst
/// statt auf einer festen Pixelzahl stehen zu bleiben.
/// </summary>
public sealed class ShareToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double width || values[1] is not double share) return 0.0;
        if (double.IsNaN(width) || width <= 0) return 0.0;
        return Math.Max(0, Math.Min(1, share)) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Leerer oder fehlender Text wird ausgeblendet.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            null => Visibility.Collapsed,
            string s when s.Length == 0 => Visibility.Collapsed,
            _ => Visibility.Visible
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Kehrt einen Wahrheitswert um - fuer "ausgegraut, solange gescannt wird".</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}
