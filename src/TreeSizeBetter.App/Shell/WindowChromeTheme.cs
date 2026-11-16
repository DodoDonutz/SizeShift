using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TreeSizeBetter.App.Shell;

/// <summary>
/// Faerbt die Windows-Titelleiste passend zur App ein. Ohne das steht ueber dem
/// dunklen Fenster eine hellgraue Systemleiste - der staerkste Bruch im Bild.
/// Ab Windows 11 laesst sich die Farbe frei setzen; Windows 10 kennt nur den
/// Dunkelmodus, die uebrigen Aufrufe schlagen dort still fehl.
/// </summary>
public static class WindowChromeTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    /// <summary>Faerbt die Titelleiste nach der aktuellen Palette (Bg1, Fg, Rahmen).</summary>
    public static void ApplyCurrent(Window window)
    {
        Color Get(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) is Color c ? c : fallback;
        Apply(window,
            caption: Get("Bg1Color", Color.FromRgb(0x14, 0x17, 0x1D)),
            text: Get("FgColor", Color.FromRgb(0xE8, 0xEB, 0xF2)),
            border: Get("BorderColor", Color.FromRgb(0x2A, 0x30, 0x39)),
            dark: !ThemeManager.IsLight);
    }

    public static void Apply(Window window, Color caption, Color text, Color border, bool dark = true)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        int captionRef = ToColorRef(caption);
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref captionRef, sizeof(int));
        int textRef = ToColorRef(text);
        DwmSetWindowAttribute(hwnd, DwmTextColor, ref textRef, sizeof(int));
        int borderRef = ToColorRef(border);
        DwmSetWindowAttribute(hwnd, DwmBorderColor, ref borderRef, sizeof(int));
    }

    /// <summary>Rendert eine Vektorgrafik als Fenster- und Taskleistensymbol.</summary>
    public static BitmapSource RenderIcon(ImageSource image, int size = 64)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>COLORREF ist 0x00BBGGRR.</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
