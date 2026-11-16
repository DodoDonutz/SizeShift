using TreeSizeBetter.Core.Model;

namespace TreeSizeBetter.Core.Analysis;

public enum EntryCategory
{
    Unbekannt = 0,
    Spiel,
    Programm,
    Projekt,
    Cache,
    Installer,
    Medien,
    Dokumente,
    System,
    Benutzerdaten,
    Virtualisierung,
    Papierkorb,
    Verknuepfung
}

/// <summary>
/// Ordnet Pfade grob ein. Das ist bewusst Mustererkennung und keine Wahrheit:
/// sie treibt Vorschlaege und den KI-Bericht an, nie eine Loeschung. Jede
/// Aktion in der App haengt weiterhin daran, dass du sie selbst anhakst.
///
/// Die Reihenfolge der Pruefungen ist wichtig - System schlaegt alles andere,
/// weil ein Cache-Ordner unter C:\Windows trotzdem nicht angefasst werden soll.
/// </summary>
public static class EntryCategorizer
{
    private static readonly string[] SystemMarkers =
    {
        @"\windows\", @"\system32\", @"\winsxs\", @"\$windows.~", @"\system volume information",
        @"\recovery\", @"\boot\", @"\perflogs", @"\$sysreset", @"\windowsapps\"
    };

    private static readonly string[] GameMarkers =
    {
        @"\steamapps\", @"\steam\", @"\epic games\", @"\gog galaxy\", @"\origin games\",
        @"\ea games\", @"\ubisoft\", @"\battle.net\", @"\riot games\", @"\rockstar games\",
        @"\xboxgames\", @"\spiele\", @"\games\", @"\minecraft", @"\curseforge\", @"\battlestate games\"
    };

    private static readonly string[] CacheMarkers =
    {
        @"\cache", @"\caches\", @"\temp\", @"\tmp\", @"\appdata\local\temp",
        @"\shadercache", @"\dxcache", @"\dxccache", @"\gpucache", @"\code cache",
        @"\crashdumps", @"\crashreports", @"\livekernelreports", @"\windows\softwaredistribution",
        @"\node_modules", @"\.gradle\", @"\.nuget\packages", @"\pip\cache", @"\npm-cache",
        @"\steamapps\downloading", @"\steamapps\shadercache", @"\prefetch"
    };

    private static readonly string[] ProjectMarkers =
    {
        @"\.git\", @"\src\", @"\bin\debug", @"\bin\release", @"\obj\debug", @"\obj\release",
        @"\dev\", @"\source\", @"\repos\", @"\projects\", @"\workspace\", @"\.vs\", @"\.idea\"
    };

    private static readonly string[] MediaExtensions =
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "mp3", "flac", "wav", "ogg", "m4a",
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "raw", "cr2", "nef", "arw", "heic", "psd"
    };

    private static readonly string[] DocumentExtensions =
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp",
        "txt", "md", "rtf", "csv", "epub"
    };

    private static readonly string[] InstallerExtensions =
    {
        "msi", "msp", "msu", "exe", "iso", "cab", "appx", "msix"
    };

    private static readonly string[] VirtualizationExtensions =
    {
        "vhd", "vhdx", "vmdk", "vdi", "qcow2", "img", "ova"
    };

    public static EntryCategory Categorize(ScanTree tree, int index)
    {
        ref FsEntry entry = ref tree.Entries[index];
        return Categorize(tree.GetFullPath(index), entry.IsDirectory, entry.IsReparsePoint);
    }

    /// <summary>
    /// Ueberladung fuer Aufrufer, die den Pfad schon haben. Beim Export waere das
    /// erneute Zusammensetzen ueber die Elternkette bei Hunderttausenden Zeilen
    /// unnoetige Arbeit.
    /// </summary>
    public static EntryCategory Categorize(string fullPath, bool isDirectory, bool isReparsePoint)
    {
        if (isReparsePoint) return EntryCategory.Verknuepfung;

        string path = fullPath.ToLowerInvariant();
        string withTrailing = isDirectory ? path + '\\' : path;

        if (path.Contains(@"\$recycle.bin", StringComparison.Ordinal) ||
            path.Contains(@"\recycler", StringComparison.Ordinal))
            return EntryCategory.Papierkorb;

        if (ContainsAny(withTrailing, SystemMarkers)) return EntryCategory.System;
        if (ContainsAny(withTrailing, CacheMarkers)) return EntryCategory.Cache;
        if (ContainsAny(withTrailing, GameMarkers)) return EntryCategory.Spiel;
        if (ContainsAny(withTrailing, ProjectMarkers)) return EntryCategory.Projekt;

        if (!isDirectory)
        {
            string extension = ExtensionOf(path);
            if (Array.IndexOf(VirtualizationExtensions, extension) >= 0) return EntryCategory.Virtualisierung;
            if (Array.IndexOf(MediaExtensions, extension) >= 0) return EntryCategory.Medien;
            if (Array.IndexOf(DocumentExtensions, extension) >= 0) return EntryCategory.Dokumente;
            if (Array.IndexOf(InstallerExtensions, extension) >= 0 && IsInDownloadLikeFolder(path))
                return EntryCategory.Installer;
        }

        if (withTrailing.Contains(@"\program files", StringComparison.Ordinal) ||
            withTrailing.Contains(@"\programdata\", StringComparison.Ordinal))
            return EntryCategory.Programm;

        if (withTrailing.Contains(@"\users\", StringComparison.Ordinal) ||
            withTrailing.Contains(@"\dokumente\", StringComparison.Ordinal) ||
            withTrailing.Contains(@"\bilder\", StringComparison.Ordinal) ||
            withTrailing.Contains(@"\videos\", StringComparison.Ordinal) ||
            withTrailing.Contains(@"\musik\", StringComparison.Ordinal))
            return EntryCategory.Benutzerdaten;

        return EntryCategory.Unbekannt;
    }

    /// <summary>
    /// Darf ein Ordner gefahrlos auf eine andere Platte umgelagert werden?
    /// Nur eine Vorauswahl fuer Vorschlaege - die harten Sperren beim Umlagern
    /// selbst sind davon unabhaengig und strenger.
    /// </summary>
    public static bool LooksRelocatable(EntryCategory category) => category switch
    {
        EntryCategory.Spiel => true,
        EntryCategory.Medien => true,
        EntryCategory.Virtualisierung => true,
        EntryCategory.Dokumente => true,
        EntryCategory.Benutzerdaten => true,
        EntryCategory.Projekt => true,
        _ => false
    };

    public static string Describe(EntryCategory category) => category switch
    {
        EntryCategory.Spiel => Loc.T("cat.game"),
        EntryCategory.Programm => Loc.T("cat.program"),
        EntryCategory.Projekt => Loc.T("cat.project"),
        EntryCategory.Cache => Loc.T("cat.cache"),
        EntryCategory.Installer => Loc.T("cat.installer"),
        EntryCategory.Medien => Loc.T("cat.media"),
        EntryCategory.Dokumente => Loc.T("cat.documents"),
        EntryCategory.System => Loc.T("cat.system"),
        EntryCategory.Benutzerdaten => Loc.T("cat.userData"),
        EntryCategory.Virtualisierung => Loc.T("cat.virtualDisk"),
        EntryCategory.Papierkorb => Loc.T("cat.recycleBin"),
        EntryCategory.Verknuepfung => Loc.T("cat.link"),
        _ => ""
    };

    private static bool IsInDownloadLikeFolder(string lowerPath)
        => lowerPath.Contains(@"\downloads\", StringComparison.Ordinal)
           || lowerPath.Contains(@"\download\", StringComparison.Ordinal)
           || lowerPath.Contains(@"\installer", StringComparison.Ordinal)
           || lowerPath.Contains(@"\setup", StringComparison.Ordinal);

    private static bool ContainsAny(string haystack, string[] markers)
    {
        for (int i = 0; i < markers.Length; i++)
            if (haystack.Contains(markers[i], StringComparison.Ordinal)) return true;
        return false;
    }

    private static string ExtensionOf(string lowerPath)
    {
        int dot = lowerPath.LastIndexOf('.');
        if (dot <= 0 || dot == lowerPath.Length - 1) return string.Empty;
        int slash = lowerPath.LastIndexOf('\\');
        return dot < slash ? string.Empty : lowerPath[(dot + 1)..];
    }
}
