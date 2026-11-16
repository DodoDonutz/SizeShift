using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TreeSizeBetter.App;

public partial class App : Application
{
    private static string LogDirectory => Core.Util.AppPaths.DataDirectory;

    private static string LogPath => Core.Util.AppPaths.File("error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Ohne das verschwindet ein Fehler beim Aufbau der Oberflaeche lautlos:
        // WPF beendet den Prozess, und es gibt nichts zum Nachlesen.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject as Exception, "AppDomain");

        // Lesbares Protokoll in Dokumente\SizeShift\Verlauf (im Selbsttest aus).
        // "--screenshots <ordner>": Demo-Modus mit erfundenen Daten, rendert alle Bereiche als PNG und beendet sich.
        int shotIndex = Array.IndexOf(e.Args, "--screenshots");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length) ScreenshotDir = e.Args[shotIndex + 1].Trim().TrimEnd('"');

        Core.Util.ActivityLog.Enabled = !e.Args.Contains("--selftest") && ScreenshotDir is null;
        // Ordnerstruktur in Dokumente\SizeShift gleich anlegen, damit man sie findet.
        _ = Core.Util.AppPaths.HistoryDirectory;
        _ = Core.Util.AppPaths.ExportDirectory;
        _ = Core.Util.AppPaths.AiReportDirectory;

        // Sprache vor allem anderen: jede Oberflaeche und jede Meldung holt ihre Texte daraus.
        // "--lang xx" erzwingt eine Sprache (fuer Tests und Screenshots), ohne sie zu speichern.
        Shell.AppSettings.Load();
        int langIndex = Array.IndexOf(e.Args, "--lang");
        Loc.SetLanguage(langIndex >= 0 && langIndex + 1 < e.Args.Length ? e.Args[langIndex + 1] : Shell.AppSettings.Language);
        // WPF formatiert Bindungen (Zahlen, Datum) nach FrameworkElement.Language, nicht nach
        // CurrentCulture - ohne das stuende ueberall en-US.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag)));

        // Gespeicherte Darstellung (dunkel / hell / wie Windows) vor dem ersten Fenster setzen.
        Shell.ThemeManager.LoadAndApply();
        // "--theme hell|dunkel" erzwingt die Darstellung, ohne sie zu speichern (fuer Screenshots).
        int themeIndex = Array.IndexOf(e.Args, "--theme");
        if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length &&
            Enum.TryParse(e.Args[themeIndex + 1], ignoreCase: true, out Shell.AppTheme forced))
            Shell.ThemeManager.Apply(forced, save: false);

        // Nach dem Neustart als Administrator: den uebergebenen Pfad gleich scannen.
        int scanIndex = Array.IndexOf(e.Args, "--scan");
        if (scanIndex >= 0 && scanIndex + 1 < e.Args.Length)
        {
            // "C:\" in Anfuehrungszeichen kommt als C:" an - der Backslash maskiert das Zeichen.
            string path = e.Args[scanIndex + 1].Trim().TrimEnd('"');
            StartupScanPath = path.Length == 2 && path[1] == ':' ? path + "\\" : path;
        }

        if (e.Args.Contains("--selftest"))
        {
            _selfTest = true;
            // Baut die Oberflaeche auf, klickt jeden Tab einmal an und beendet sich.
            // Das Anklicken ist noetig: WPF baut den Inhalt eines Tabs erst, wenn er
            // gezeigt wird - Bindungsfehler darin fielen sonst erst dem Benutzer auf.
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    foreach (string tab in window.VisitAllTabs())
                        Console.WriteLine($"Selbsttest: Tab \"{tab}\" aufgebaut.");
                }
                Dispatcher.BeginInvoke(() =>
                {
                    Console.WriteLine("Selbsttest: bestanden.");
                    Shutdown(0);
                }, DispatcherPriority.ApplicationIdle);
            }, DispatcherPriority.ApplicationIdle);
        }

        base.OnStartup(e);
    }

    private bool _selfTest;

    /// <summary>Zielordner im Screenshot-Modus ("--screenshots"), sonst null.</summary>
    public static string? ScreenshotDir { get; private set; }

    /// <summary>Pfad aus "--scan", der direkt nach dem Start gescannt wird.</summary>
    public static string? StartupScanPath { get; private set; }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception, "Dispatcher");
        if (_selfTest)
        {
            // Im Test kein Dialog, der auf einen Klick wartet - Fehlercode und Ende.
            Console.WriteLine("Selbsttest FEHLGESCHLAGEN: " + e.Exception.Message);
            e.Handled = true;
            Shutdown(1);
            return;
        }
        MessageBox.Show(
            Loc.F("app.unexpectedError", e.Exception.Message, LogPath),
            Core.Util.AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Log(Exception? exception, string source)
    {
        if (exception is null) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var text = new StringBuilder()
                .AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{source}] ===")
                .AppendLine(exception.ToString())
                .AppendLine();
            File.AppendAllText(LogPath, text.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            // Wenn nicht einmal das Protokoll geschrieben werden kann, ist nichts zu retten.
        }
    }
}
