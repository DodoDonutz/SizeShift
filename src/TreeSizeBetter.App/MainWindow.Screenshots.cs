using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TreeSizeBetter.App.ViewModels;
using TreeSizeBetter.Core.Cleanup;
using TreeSizeBetter.Core.SystemInfo;

namespace TreeSizeBetter.App;

/// <summary>
/// Screenshot-Modus fuer die README ("--screenshots ordner"): ausschliesslich erfundene
/// Daten. Erwartet zwei Hilfslaufwerke S: (Demo-Systemplatte) und T: (Demo-Datenplatte),
/// die installer\screenshots.ps1 per subst auf Platzhalter-Ordner legt. Hardware und
/// Autostart kommen aus <see cref="DemoData"/>. Nichts davon wird gespeichert.
/// </summary>
public partial class MainWindow
{
    private const string DemoSystem = @"S:\";
    private const string DemoData2 = @"T:\";

    private async Task RunScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        WindowState = WindowState.Normal;
        Width = 1600;
        Height = 960;

        // Erfundene Ordner statt der echten - auch fuer die Aufraeum-Regeln.
        KnownFolders.Override = new KnownFolders(
            SystemDrive: "S:", Windows: @"S:\Windows", ProgramData: @"S:\ProgramData",
            UserProfile: @"S:\Users\Alex", LocalAppData: @"S:\Users\Alex\AppData\Local",
            RoamingAppData: @"S:\Users\Alex\AppData\Roaming");
        CleanupViewModel.DriveFilter = new[] { DemoSystem, DemoData2 };
        _viewModel.SystemArea.ShowDemo(DemoData.Snapshot(), DemoData.Autostart());
        _viewModel.SystemArea.ShowDemoProcesses(DemoData.Processes(), TimeSpan.FromMinutes(10), 7.4);

        // ------------------------------------------------ Analyse
        await _viewModel.ScanPathAsync(DemoSystem);
        await Settle(2500);
        // Der Hinweis zu den Zugriffszeiten kommt nur von den Platzhalter-Dateien.
        _viewModel.DismissWarningCommand.Execute(null);
        MainTabs.SelectedIndex = 0;
        await Settle(1500);
        Capture(folder, "analysis.png");

        MainTabs.SelectedIndex = 1;
        await Settle(2000);
        Capture(folder, "overview.png");
        MainTabs.SelectedIndex = 0;

        // ------------------------------------------------ Aufraeumen
        _viewModel.Area = AppArea.Aufraeumen;
        await Settle(1500);
        Capture(folder, "cleanup.png");

        // ------------------------------------------------ Umlagern
        _viewModel.Area = AppArea.Umlagern;
        OffloadViewModel offload = _viewModel.Offload;
        offload.Left.LoadDrives(DemoSystem);
        offload.Right.LoadDrives(DemoData2);
        await Settle(1500);
        // Demo-Ordnernamen gibt es auf Deutsch und Englisch (installer\screenshots.ps1 -Lang).
        bool german = Loc.Language == "de";
        string video = german ? @"S:\Users\Alex\Videos\Urlaub 2025" : @"S:\Users\Alex\Videos\Vacation 2025";
        if (await offload.Left.RevealAsync(video) is { } node) node.IsChecked = true;
        await offload.Right.RevealAsync(german ? @"T:\Archiv" : @"T:\Archive");
        await Settle(7000);
        Capture(folder, "relocate.png");

        // ------------------------------------------------ System
        _viewModel.Area = AppArea.System;
        SystemTabs.SelectedIndex = 0;
        await Settle(2000);
        Capture(folder, "system.png");
        SystemTabs.SelectedIndex = 1;
        await Settle(1000);
        Capture(folder, "startup.png");
        SystemTabs.SelectedIndex = 2;
        await Settle(1000);
        Capture(folder, "processes.png");

        Application.Current.Shutdown(0);
    }

    private async Task Settle(int milliseconds)
    {
        await Task.Delay(milliseconds);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Zeichnet den Fensterinhalt selbst - unabhaengig davon, was gerade auf dem Bildschirm liegt.</summary>
    private void Capture(string folder, string file)
    {
        UpdateLayout();
        if (Content is not FrameworkElement content || content.ActualWidth < 1) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        double scale = dpi / 96.0;
        int width = (int)Math.Round(content.ActualWidth * scale);
        int height = (int)Math.Round(content.ActualHeight * scale);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(Background, null, rect);
            // Viewbox fest auf den Fensterinhalt - sonst richtet sich der Pinsel nach den
            // Inhaltsgrenzen und verschiebt bzw. verkleinert das Bild.
            var brush = new VisualBrush(content)
            {
                Viewbox = rect,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
            dc.DrawRectangle(brush, null, rect);
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(folder, file));
        encoder.Save(stream);
    }
}

/// <summary>Erfundene Hardware und ein erfundener Autostart fuer die Screenshots.</summary>
internal static class DemoData
{
    public static SystemSnapshot Snapshot() => new(
        "DEMO-PC",
        new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", 8, 16, 4200),
        new MemoryInfo(32L << 30, 13L << 30, new[]
        {
            new MemoryModule(16L << 30, 6000, "Kingston", "KF560C30-16", "DIMM A2"),
            new MemoryModule(16L << 30, 6000, "Kingston", "KF560C30-16", "DIMM B2")
        }),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4070 SUPER", "32.0.15.6614", new DateTime(2026, 10, 2), 12L << 30) },
        new OsInfo("Windows 11 Pro", "25H2", "26200.6899", new DateTime(2025, 3, 14), DateTime.Now.AddHours(-5).AddMinutes(-12)),
        new[]
        {
            new DiskInfo("Samsung SSD 990 PRO 1TB", DiskKind.Nvme, DiskHealth.Gesund, 1000L * 1000 * 1000 * 1000,
                new[] { new VolumeInfo("S:", "System", "NTFS", 931L << 30, 87L << 30) }),
            new DiskInfo("WDC WD40EZAX", DiskKind.Hdd, DiskHealth.Gesund, 4000L * 1000 * 1000 * 1000,
                new[] { new VolumeInfo("T:", Loc.Language == "de" ? "Daten" : "Data", "NTFS", 3726L << 30, 1405L << 30) })
        });

    /// <summary>Eine erfundene 10-Minuten-Aufzeichnung: Spiel, Browser, Chat, Dienste.</summary>
    public static IReadOnlyList<ProcessGroupStats> Processes()
    {
        const long Mb = 1024 * 1024;
        ProcessGroupStats P(string name, double cpu, double peak, long ramMb, long readMb, long writeMb, int count, string parent,
            string description, string publisher, string path, string? services = null) => new()
        {
            Name = name, Services = services, CpuAvgPercent = cpu, CpuPeakPercent = peak,
            RamAvgBytes = ramMb * Mb, RamPeakBytes = ramMb * Mb * 11 / 10,
            ReadBytes = readMb * Mb, WriteBytes = writeMb * Mb, Instances = count, Parent = parent,
            Description = description, Publisher = publisher, Path = path, PresentShare = 1
        };
        const string SvcHost = @"C:\Windows\System32\svchost.exe";
        bool de = Loc.Language == "de";
        string hostText = de ? "Hostprozess für Windows-Dienste" : "Host Process for Windows Services";
        return new[]
        {
            P("Cyberstorm.exe", 18.40, 31.2, 6420, 2310, 85, 1, "steam.exe", "Cyberstorm", "Demo Games Studio", @"T:\Games\Cyberstorm\Cyberstorm.exe"),
            P("chrome.exe", 2.15, 6.8, 2280, 410, 620, 34, "explorer.exe", "Google Chrome", "Google LLC", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            P("Discord.exe", 0.92, 3.1, 610, 12, 48, 6, "explorer.exe", "Discord", "Discord Inc.", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9200\Discord.exe"),
            P("dwm.exe", 0.88, 1.9, 190, 1, 0, 1, "winlogon.exe", de ? "Desktopfenster-Manager" : "Desktop Window Manager", "Microsoft Corporation", @"C:\Windows\System32\dwm.exe"),
            P("svchost.exe", 0.41, 4.6, 74, 380, 140, 1, "services.exe", hostText, "Microsoft Corporation", SvcHost, "Windows Update"),
            P("NVIDIA Overlay.exe", 0.35, 1.2, 160, 0, 3, 2, "NVIDIA App.exe", "NVIDIA Overlay", "NVIDIA Corporation", @"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA Overlay.exe"),
            P("MsMpEng.exe", 0.30, 2.4, 390, 260, 2, 1, "services.exe", "Microsoft Defender Antimalware Service", "Microsoft Corporation", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe"),
            P("steamwebhelper.exe", 0.22, 0.9, 540, 6, 4, 6, "steam.exe", "Steam Client WebHelper", "Valve Corporation", @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe"),
            P("Spotify.exe", 0.18, 2.2, 420, 95, 88, 5, "explorer.exe", "Spotify", "Spotify Ltd", @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic\Spotify.exe"),
            P("svchost.exe", 0.12, 0.6, 22, 0, 0, 1, "services.exe", hostText, "Microsoft Corporation", SvcHost, de ? "Windows-Audio" : "Windows Audio"),
            P("node.exe", 0.06, 0.4, 210, 0, 0, 3, "Code.exe", "Node.js JavaScript Runtime", "Node.js", @"C:\Program Files\nodejs\node.exe"),
            P("OneDrive.exe", 0.05, 0.8, 120, 30, 26, 1, "explorer.exe", "Microsoft OneDrive", "Microsoft Corporation", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe"),
            P("explorer.exe", 0.04, 0.5, 310, 2, 1, 1, "userinit.exe", de ? "Windows-Explorer" : "Windows Explorer", "Microsoft Corporation", @"C:\Windows\explorer.exe"),
            P("lghub_agent.exe", 0.03, 0.2, 95, 0, 1, 1, "lghub.exe", "LGHUB Agent", "Logitech, Inc.", @"C:\Program Files\LGHUB\lghub_agent.exe"),
            P("svchost.exe", 0.01, 0.3, 30, 52, 0, 1, "services.exe", hostText, "Microsoft Corporation", SvcHost, de ? "Übermittlungsoptimierung" : "Delivery Optimization"),
            P("RtkAudUService64.exe", 0.01, 0.1, 9, 0, 0, 1, "services.exe", "Realtek HD Audio Universal Service", "Realtek Semiconductor", @"C:\Windows\System32\DriverStore\FileRepository\realtek\RtkAudUService64.exe"),
            P("ctfmon.exe", 0.00, 0.0, 14, 0, 0, 1, "svchost.exe", "CTF Loader", "Microsoft Corporation", @"C:\Windows\System32\ctfmon.exe"),
            P("SecurityHealthSystray.exe", 0.00, 0.0, 8, 0, 0, 1, "explorer.exe", "Windows Security notification icon", "Microsoft Corporation", @"C:\Windows\System32\SecurityHealthSystray.exe")
        };
    }

    public static IReadOnlyList<AutostartEntry> Autostart()
    {
        AutostartEntry E(string name, string command, AutostartLocation where, bool on, string publisher, bool missing = false)
            => new(name, command, where, on, null, publisher, missing);
        return new[]
        {
            E("Discord", @"""C:\Users\Alex\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", AutostartLocation.BenutzerRegistry, true, "Discord Inc."),
            E("Epic Games Launcher", @"""C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe"" -silent", AutostartLocation.BenutzerRegistry, false, "Epic Games, Inc."),
            E("Logitech G HUB", @"""C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe"" --minimized", AutostartLocation.BenutzerRegistry, true, "Logitech, Inc."),
            E("NVIDIA App", @"""C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"" --tray", AutostartLocation.AlleRegistry, true, "NVIDIA Corporation"),
            E("OldUpdater", @"C:\Program Files\OldTool\updater.exe /quiet", AutostartLocation.BenutzerRegistry, true, string.Empty, missing: true),
            E("OneDrive", @"""C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background", AutostartLocation.BenutzerRegistry, true, "Microsoft Corporation"),
            E("RtkAudUService", @"""C:\WINDOWS\System32\DriverStore\FileRepository\realtek\RtkAudUService64.exe"" -background", AutostartLocation.AlleRegistry, true, "Realtek Semiconductor"),
            E("SecurityHealth", @"C:\WINDOWS\system32\SecurityHealthSystray.exe", AutostartLocation.AlleRegistry, true, "Microsoft Corporation"),
            E("Spotify", @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic\SpotifyStartupTask.exe", AutostartLocation.StoreApp, false, "Spotify AB"),
            E("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", AutostartLocation.BenutzerRegistry, true, "Valve Corporation"),
            E("Teams", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", AutostartLocation.StoreApp, false, "Microsoft Corporation"),
            E("WhatsApp", @"C:\Program Files\WindowsApps\WhatsAppDesktop\WhatsApp.Root.exe", AutostartLocation.StoreApp, true, "WhatsApp Inc.")
        };
    }
}
