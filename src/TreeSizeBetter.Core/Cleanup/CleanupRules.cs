using TreeSizeBetter.Core.Analysis;
using TreeSizeBetter.Core.Localization;

namespace TreeSizeBetter.Core.Cleanup;

/// <summary>Was mit einem Fund passiert, wenn er angehakt und aufgeräumt wird.</summary>
public enum CleanupAction
{
    /// <summary>Alles im Ordner entfernen, den Ordner selbst stehen lassen (Programme erwarten ihn).</summary>
    InhaltLeeren,
    /// <summary>Den gefundenen Ordner oder die Datei als Ganzes entfernen.</summary>
    GanzEntfernen,
    /// <summary>Einzelne passende Dateien darunter entfernen.</summary>
    DateienEntfernen,
    /// <summary>Den Papierkorb des Laufwerks leeren.</summary>
    PapierkorbLeeren,
    /// <summary>Den aktuellen Inhalt der Zwischenablage leeren (keine Dateien).</summary>
    ZwischenablageLeeren,
    /// <summary>Den DNS-Zwischenspeicher von Windows leeren (keine Dateien).</summary>
    DnsCacheLeeren,
    /// <summary>Nicht von SizeShift lösbar - nur erklären, wie es geht.</summary>
    NurHinweis
}

/// <summary>
/// Eine Aufräum-Regel. Regeln sind reine Beschreibung: wo gesucht wird, wie alt
/// etwas mindestens sein muss und was damit passiert. Gefunden wird im
/// <see cref="CleanupScanner"/>, gelöscht im <see cref="CleanupExecutor"/>.
/// </summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public required FindingSafety Safety { get; init; }
    public required CleanupAction Action { get; init; }

    /// <summary>Absolute Pfade; ein Segment "*" steht für einen beliebigen Ordner (z. B. Browser-Profile).</summary>
    public string[] Locations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Alternativ zu festen Orten: Ordner mit diesem Namen irgendwo im Baum,
    /// optional mit vorgegebenem Elternordner ("steamapps\shadercache").
    /// </summary>
    public string? FolderNameAnywhere { get; init; }

    /// <summary>Nur der oberste Treffer je Zweig (node_modules in node_modules zählt nicht extra).</summary>
    public bool TopmostOnly { get; init; }

    /// <summary>Treffer zählt nur, wenn neben dem Ordner diese Datei liegt (z. B. package.json).</summary>
    public string? RequiredSiblingFile { get; init; }

    /// <summary>Treffer zählt nur unterhalb eines dieser Pfade.</summary>
    public string[]? OnlyUnder { get; init; }

    /// <summary>Treffer unterhalb dieser Pfade zählen nicht.</summary>
    public string[]? NotUnder { get; init; }

    /// <summary>Alter am Elternordner messen statt am Fund selbst (das Projekt um node_modules herum).</summary>
    public bool AgeOfParent { get; init; }

    /// <summary>Nur bei <see cref="CleanupAction.DateienEntfernen"/>: welche Endungen (klein, ohne Punkt).</summary>
    public string[]? FileExtensions { get; init; }

    /// <summary>Was jünger ist (gemessen an der letzten Änderung), bleibt liegen.</summary>
    public int MinAgeDays { get; init; }

    /// <summary>Kleinere Funde werden nicht gezeigt - sie lohnen den Klick nicht.</summary>
    public long MinBytes { get; init; } = 1024 * 1024;

    /// <summary>Nur bei <see cref="CleanupAction.NurHinweis"/>: wie man es selbst erledigt.</summary>
    public string? Advice { get; init; }

    /// <summary>
    /// Privatsphaere statt Speicherplatz: nie vorab angehakt, auch kleine Funde
    /// zaehlen, endgueltig geloescht (im Papierkorb laegen die Daten ja noch),
    /// und der Ort wird nicht fuer andere Regeln beansprucht.
    /// </summary>
    public bool IsPrivacy { get; init; }

    /// <summary>
    /// Nur bei <see cref="CleanupAction.DateienEntfernen"/>: Dateien nach Pfad relativ
    /// zum Fundort, mit "*" als Platzhalter ("History", "Network\Cookies",
    /// "thumbcache_*.db", "Sessions\*"). Ergaenzt die Endungen.
    /// </summary>
    public string[]? FileNames { get; init; }

    /// <summary>Laeuft eines dieser Programme (Prozessname ohne .exe), wird die Regel uebersprungen.</summary>
    public string[]? BlockingProcesses { get; init; }

    /// <summary>Anzeige statt eines Pfads, z. B. bei Zwischenablage und DNS-Cache.</summary>
    public string? DisplayLocation { get; init; }

    /// <summary>Aktionen ohne Dateien - der Fund steht fuer etwas, das Windows im Speicher haelt.</summary>
    public bool IsVirtual => Action is CleanupAction.ZwischenablageLeeren or CleanupAction.DnsCacheLeeren;
}

/// <summary>Die Ordner, aus denen die Regeln ihre Pfade bauen. Für Tests austauschbar.</summary>
public sealed record KnownFolders(
    string SystemDrive,
    string Windows,
    string ProgramData,
    string UserProfile,
    string LocalAppData,
    string RoamingAppData)
{
    /// <summary>Fuer den Demo-Modus (Screenshots mit erfundenen Daten): ersetzt die echten Ordner.</summary>
    public static KnownFolders? Override { get; set; }

    public static KnownFolders Current()
    {
        if (Override is { } demo) return demo;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new KnownFolders(
            SystemDrive: Path.GetPathRoot(windows)!.TrimEnd('\\'),
            Windows: windows,
            ProgramData: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            UserProfile: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }
}

/// <summary>
/// Der Regelkatalog. Die Reihenfolge zählt: spezielle Regeln stehen vor
/// allgemeinen, und ein Ort, den schon eine Regel beansprucht hat, wird von
/// keiner späteren noch einmal gezählt.
///
/// Grundsatz: "Sicher" ist nur, was sich nachweislich von selbst wieder aufbaut
/// (Caches, Temp, Fehlerberichte). Alles andere ist "Vorsichtig" und geht
/// grundsätzlich in den Papierkorb.
/// </summary>
public static class CleanupCatalog
{
    public static string GroupWindows => Loc.T("group.windows");
    public static string GroupBrowser => Loc.T("group.browser");
    public static string GroupGames => Loc.T("group.games");
    public static string GroupApps => Loc.T("group.apps");
    public static string GroupDev => Loc.T("group.dev");
    public static string GroupDownloads => Loc.T("group.downloads");
    public static string GroupHints => Loc.T("group.hints");

    public static IReadOnlyList<CleanupRule> Build(KnownFolders f)
    {
        string L = f.LocalAppData, R = f.RoamingAppData, U = f.UserProfile, W = f.Windows, P = f.ProgramData, S = f.SystemDrive;

        string[] Chromium(string userData) => new[]
        {
            $@"{userData}\*\Cache", $@"{userData}\*\Code Cache", $@"{userData}\*\GPUCache",
            $@"{userData}\*\Service Worker\CacheStorage", $@"{userData}\ShaderCache", $@"{userData}\GrShaderCache"
        };

        return new List<CleanupRule>
        {
            // ------------------------------------------------------------ Windows
            new()
            {
                Id = "temp-user", Group = GroupWindows, Title = Loc.T("rule.temp-user.title"),
                Explanation = Loc.T("rule.temp-user.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren, MinAgeDays = 2,
                Locations = new[] { $@"{L}\Temp" }
            },
            new()
            {
                Id = "temp-windows", Group = GroupWindows, Title = Loc.T("rule.temp-windows.title"),
                Explanation = Loc.T("rule.temp-windows.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren, MinAgeDays = 2,
                Locations = new[] { $@"{W}\Temp" }
            },
            new()
            {
                Id = "windows-update", Group = GroupWindows, Title = Loc.T("rule.windows-update.title"),
                Explanation = Loc.T("rule.windows-update.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren, MinAgeDays = 10,
                Locations = new[] { $@"{W}\SoftwareDistribution\Download" }
            },
            new()
            {
                Id = "delivery-optimization", Group = GroupWindows, Title = Loc.T("rule.delivery-optimization.title"),
                Explanation = Loc.T("rule.delivery-optimization.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{W}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" }
            },
            new()
            {
                Id = "error-reports", Group = GroupWindows, Title = Loc.T("rule.error-reports.title"),
                Explanation = Loc.T("rule.error-reports.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[]
                {
                    $@"{L}\CrashDumps", $@"{L}\Microsoft\Windows\WER", $@"{P}\Microsoft\Windows\WER",
                    $@"{W}\Minidump", $@"{W}\LiveKernelReports"
                }
            },
            new()
            {
                Id = "memory-dump", Group = GroupWindows, Title = Loc.T("rule.memory-dump.title"),
                Explanation = Loc.T("rule.memory-dump.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.GanzEntfernen,
                Locations = new[] { $@"{W}\MEMORY.DMP" }
            },
            new()
            {
                Id = "recycle-bin", Group = GroupWindows, Title = Loc.T("rule.recycle-bin.title"),
                Explanation = Loc.T("rule.recycle-bin.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.PapierkorbLeeren,
                Locations = new[] { $@"{S}\$Recycle.Bin" }
            },

            // ------------------------------------------------------------ Browser
            new()
            {
                Id = "browser-chrome", Group = GroupBrowser, Title = Loc.T("rule.browser-chrome.title"),
                Explanation = Loc.T("rule.browser-chrome.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = Chromium($@"{L}\Google\Chrome\User Data")
            },
            new()
            {
                Id = "browser-edge", Group = GroupBrowser, Title = Loc.T("rule.browser-edge.title"),
                Explanation = Loc.T("rule.browser-edge.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = Chromium($@"{L}\Microsoft\Edge\User Data")
            },
            new()
            {
                Id = "browser-brave", Group = GroupBrowser, Title = Loc.T("rule.browser-brave.title"),
                Explanation = Loc.T("rule.browser-brave.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = Chromium($@"{L}\BraveSoftware\Brave-Browser\User Data")
            },
            new()
            {
                Id = "browser-firefox", Group = GroupBrowser, Title = Loc.T("rule.browser-firefox.title"),
                Explanation = Loc.T("rule.browser-firefox.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{L}\Mozilla\Firefox\Profiles\*\cache2" }
            },

            // ------------------------------------------------------------ Grafik und Spiele
            new()
            {
                Id = "shader-cache", Group = GroupGames, Title = Loc.T("rule.shader-cache.title"),
                Explanation = Loc.T("rule.shader-cache.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[]
                {
                    $@"{L}\D3DSCache", $@"{L}\NVIDIA\DXCache", $@"{L}\NVIDIA\GLCache", $@"{P}\NVIDIA Corporation\NV_Cache",
                    $@"{L}\AMD\DxCache", $@"{L}\AMD\DxcCache", $@"{L}\AMD\VkCache", $@"{L}\AMD\GLCache", $@"{L}\AMD\OglCache"
                }
            },
            new()
            {
                Id = "steam-shadercache", Group = GroupGames, Title = Loc.T("rule.steam-shadercache.title"),
                Explanation = Loc.T("rule.steam-shadercache.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                FolderNameAnywhere = @"steamapps\shadercache"
            },
            new()
            {
                Id = "steam-temp", Group = GroupGames, Title = Loc.T("rule.steam-temp.title"),
                Explanation = Loc.T("rule.steam-temp.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren, MinAgeDays = 2,
                FolderNameAnywhere = @"steamapps\temp"
            },
            new()
            {
                Id = "steam-downloading", Group = GroupGames, Title = Loc.T("rule.steam-downloading.title"),
                Explanation = Loc.T("rule.steam-downloading.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.InhaltLeeren, MinAgeDays = 2,
                FolderNameAnywhere = @"steamapps\downloading"
            },
            new()
            {
                Id = "driver-downloads", Group = GroupGames, Title = Loc.T("rule.driver-downloads.title"),
                Explanation = Loc.T("rule.driver-downloads.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.InhaltLeeren, MinAgeDays = 14,
                Locations = new[] { $@"{P}\NVIDIA Corporation\Downloader", $@"{S}\AMD", $@"{S}\NVIDIA" }
            },

            // ------------------------------------------------------------ Programme
            new()
            {
                Id = "app-discord", Group = GroupApps, Title = Loc.T("rule.app-discord.title"),
                Explanation = Loc.T("rule.app-discord.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{R}\discord\Cache", $@"{R}\discord\Code Cache", $@"{R}\discord\GPUCache" }
            },
            new()
            {
                Id = "app-spotify", Group = GroupApps, Title = Loc.T("rule.app-spotify.title"),
                Explanation = Loc.T("rule.app-spotify.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{L}\Spotify\Data", $@"{L}\Spotify\Browser\Cache" }
            },
            new()
            {
                Id = "app-electron", Group = GroupApps, Title = Loc.T("rule.app-electron.title"),
                Explanation = Loc.T("rule.app-electron.text"),
                Safety = FindingSafety.Sicher, Action = CleanupAction.InhaltLeeren, MinBytes = 20L * 1024 * 1024,
                // "Cache" allein ist zu allgemein (TeamSpeak legt dort Abzeichen ab).
                // Chromium-Caches erkennt man sicher am Unterordner Cache_Data.
                Locations = new[] { $@"{R}\*\Cache\Cache_Data", $@"{R}\*\Code Cache", $@"{R}\*\GPUCache", $@"{R}\*\CachedData" }
            },

            // ------------------------------------------------------------ Entwicklung
            new()
            {
                Id = "dev-package-caches", Group = GroupDev, Title = Loc.T("rule.dev-package-caches.title"),
                Explanation = Loc.T("rule.dev-package-caches.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{L}\npm-cache", $@"{L}\pip\cache", $@"{L}\Yarn\Cache", $@"{U}\.gradle\caches" }
            },
            new()
            {
                Id = "dev-nuget", Group = GroupDev, Title = Loc.T("rule.dev-nuget.title"),
                Explanation = Loc.T("rule.dev-nuget.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.InhaltLeeren,
                Locations = new[] { $@"{U}\.nuget\packages" }
            },
            new()
            {
                Id = "dev-node-modules", Group = GroupDev, Title = Loc.T("rule.dev-node-modules.title"),
                Explanation = Loc.T("rule.dev-node-modules.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.GanzEntfernen, MinAgeDays = 90,
                MinBytes = 20L * 1024 * 1024, FolderNameAnywhere = "node_modules",
                TopmostOnly = true, RequiredSiblingFile = "package.json", AgeOfParent = true,
                // Nur eigene Projekte: node_modules von installierten Programmen
                // (Node.js selbst, VS Code, Electron-Apps) gehören zum Programm.
                OnlyUnder = new[] { U }, NotUnder = new[] { L, R }
            },

            // ------------------------------------------------------------ Downloads
            new()
            {
                Id = "old-installers", Group = GroupDownloads, Title = Loc.T("rule.old-installers.title"),
                Explanation = Loc.T("rule.old-installers.text"),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.DateienEntfernen, MinAgeDays = 180,
                FileExtensions = new[] { "exe", "msi", "msix", "msixbundle", "appx", "iso" },
                Locations = new[] { $@"{U}\Downloads" }
            },

            // ------------------------------------------------------------ Nur Hinweise
            new()
            {
                Id = "hint-hiberfil", Group = GroupHints, Title = Loc.T("rule.hint-hiberfil.title"),
                Explanation = Loc.T("rule.hint-hiberfil.text"),
                Safety = FindingSafety.Riskant, Action = CleanupAction.NurHinweis,
                Advice = Loc.T("rule.hint-hiberfil.advice"),
                Locations = new[] { $@"{S}\hiberfil.sys" }
            },
            new()
            {
                Id = "hint-windows-old", Group = GroupHints, Title = Loc.T("rule.hint-windows-old.title"),
                Explanation = Loc.T("rule.hint-windows-old.text"),
                Safety = FindingSafety.Riskant, Action = CleanupAction.NurHinweis,
                Advice = Loc.T("rule.hint-windows-old.advice"),
                Locations = new[] { $@"{S}\Windows.old", $@"{S}\$Windows.~BT", $@"{S}\$Windows.~WS" }
            },
            new()
            {
                Id = "hint-winsxs", Group = GroupHints, Title = Loc.T("rule.hint-winsxs.title"),
                Explanation = Loc.T("rule.hint-winsxs.text"),
                Safety = FindingSafety.Riskant, Action = CleanupAction.NurHinweis, MinBytes = 2L * 1024 * 1024 * 1024,
                Advice = Loc.T("rule.hint-winsxs.advice"),
                Locations = new[] { $@"{W}\WinSxS" }
            },
            new()
            {
                Id = "hint-restore-points", Group = GroupHints, Title = Loc.T("rule.hint-restore-points.title"),
                Explanation = Loc.T("rule.hint-restore-points.text"),
                Safety = FindingSafety.Riskant, Action = CleanupAction.NurHinweis, MinBytes = 1024L * 1024 * 1024,
                Advice = Loc.T("rule.hint-restore-points.advice"),
                Locations = new[] { $@"{S}\System Volume Information" }
            }
        }
        .Concat(Privacy(f))
        .ToList();
    }

    public static string GroupPrivacy => Loc.T("group.privacy");

    /// <summary>
    /// Verlaeufe und Spuren. Leitplanken: nie Passwoerter (Login Data, logins.json,
    /// key4.db), Lesezeichen, Erweiterungen oder Einstellungen - ein Test prueft das.
    /// Firefox speichert Verlauf und Lesezeichen in derselben Datei (places.sqlite);
    /// dort bleibt der Verlauf deshalb liegen, und die Erklaerung sagt es offen.
    /// </summary>
    private static IEnumerable<CleanupRule> Privacy(KnownFolders f)
    {
        string L = f.LocalAppData, R = f.RoamingAppData, W = f.Windows;

        var chromium = new (string Id, string Name, string UserData, string Process)[]
        {
            ("chrome", "Chrome", $@"{L}\Google\Chrome\User Data", "chrome"),
            ("edge", "Edge", $@"{L}\Microsoft\Edge\User Data", "msedge"),
            ("brave", "Brave", $@"{L}\BraveSoftware\Brave-Browser\User Data", "brave")
        };

        foreach (var b in chromium)
        {
            yield return new CleanupRule
            {
                Id = $"privacy-history-{b.Id}", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
                Title = Loc.F("rule.privacy-history.title", b.Name),
                Explanation = Loc.F("rule.privacy-history.text", b.Name),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.DateienEntfernen,
                Locations = new[] { $@"{b.UserData}\*" },
                FileNames = new[]
                {
                    "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal",
                    "Shortcuts", "Shortcuts-journal", @"Sessions\*", "Current Session", "Current Tabs", "Last Session", "Last Tabs"
                },
                BlockingProcesses = new[] { b.Process }
            };
            yield return new CleanupRule
            {
                Id = $"privacy-cookies-{b.Id}", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
                Title = Loc.F("rule.privacy-cookies.title", b.Name),
                Explanation = Loc.F("rule.privacy-cookies.text", b.Name),
                Safety = FindingSafety.Vorsichtig, Action = CleanupAction.DateienEntfernen,
                Locations = new[] { $@"{b.UserData}\*" },
                FileNames = new[] { @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal" },
                BlockingProcesses = new[] { b.Process }
            };
        }

        string firefox = $@"{R}\Mozilla\Firefox\Profiles\*";
        yield return new CleanupRule
        {
            Id = "privacy-cookies-firefox", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.F("rule.privacy-cookies.title", "Firefox"),
            Explanation = Loc.F("rule.privacy-cookies.text", "Firefox"),
            Safety = FindingSafety.Vorsichtig, Action = CleanupAction.DateienEntfernen,
            Locations = new[] { firefox },
            FileNames = new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm" },
            BlockingProcesses = new[] { "firefox" }
        };
        yield return new CleanupRule
        {
            Id = "privacy-forms-firefox", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.T("rule.privacy-forms-firefox.title"),
            Explanation = Loc.T("rule.privacy-forms-firefox.text"),
            Safety = FindingSafety.Vorsichtig, Action = CleanupAction.DateienEntfernen,
            Locations = new[] { firefox },
            FileNames = new[]
            {
                "formhistory.sqlite", "formhistory.sqlite-wal", "formhistory.sqlite-shm",
                "sessionstore.jsonlz4", @"sessionstore-backups\*"
            },
            BlockingProcesses = new[] { "firefox" }
        };

        // ------------------------------------------------------------ Windows-Spuren
        yield return new CleanupRule
        {
            Id = "privacy-recent", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.T("rule.privacy-recent.title"),
            Explanation = Loc.T("rule.privacy-recent.text"),
            Safety = FindingSafety.Sicher, Action = CleanupAction.DateienEntfernen,
            Locations = new[] { $@"{R}\Microsoft\Windows\Recent" },
            FileExtensions = new[] { "lnk", "automaticdestinations-ms", "customdestinations-ms" }
        };
        yield return new CleanupRule
        {
            Id = "privacy-thumbnails", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.T("rule.privacy-thumbnails.title"),
            Explanation = Loc.T("rule.privacy-thumbnails.text"),
            Safety = FindingSafety.Sicher, Action = CleanupAction.DateienEntfernen,
            Locations = new[] { $@"{L}\Microsoft\Windows\Explorer" },
            FileNames = new[] { "thumbcache_*.db" }
        };
        yield return new CleanupRule
        {
            Id = "privacy-clipboard", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.T("rule.privacy-clipboard.title"),
            Explanation = Loc.T("rule.privacy-clipboard.text"),
            Advice = Loc.T("rule.privacy-clipboard.advice"),
            DisplayLocation = Loc.T("rule.privacy-clipboard.where"),
            Safety = FindingSafety.Sicher, Action = CleanupAction.ZwischenablageLeeren,
            Locations = new[] { W }
        };
        yield return new CleanupRule
        {
            Id = "privacy-dns", Group = GroupPrivacy, IsPrivacy = true, MinBytes = 0,
            Title = Loc.T("rule.privacy-dns.title"),
            Explanation = Loc.T("rule.privacy-dns.text"),
            DisplayLocation = Loc.T("rule.privacy-dns.where"),
            Safety = FindingSafety.Sicher, Action = CleanupAction.DnsCacheLeeren,
            Locations = new[] { W }
        };
    }
}
