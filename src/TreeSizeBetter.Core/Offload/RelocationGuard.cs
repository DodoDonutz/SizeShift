using System.Diagnostics;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Localization;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.Offload;

public enum IssueSeverity
{
    /// <summary>Hinweis, haelt nichts auf.</summary>
    Hinweis,
    /// <summary>Geht, aber mit bekanntem Risiko - muss bewusst bestaetigt werden.</summary>
    Warnung,
    /// <summary>Wird nicht gemacht.</summary>
    Sperre
}

public sealed record GuardIssue(IssueSeverity Severity, string Message);

public sealed class RelocationCheck
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public long SourceBytes { get; init; }
    public long SourceFiles { get; init; }
    public long TargetFreeBytes { get; set; }
    public List<GuardIssue> Issues { get; } = new();

    public bool IsBlocked => Issues.Any(i => i.Severity == IssueSeverity.Sperre);
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warnung);
}

/// <summary>
/// Entscheidet, ob ein Ordner umgelagert werden darf. Bewusst streng: im
/// Zweifel wird gesperrt, nicht gewarnt. Ein falsches "nein" kostet einen
/// Klick, ein falsches "ja" kann Windows oder eine Store-App zerlegen.
/// </summary>
public static class RelocationGuard
{
    /// <summary>Unter diesen Pfaden wird nie umgelagert (Systemordner, Store-Apps, Wiederherstellung).</summary>
    private static readonly string[] BlockedBelow =
    {
        @"\Windows",
        @"\Program Files\WindowsApps",
        @"\ProgramData\Microsoft",
        @"\System Volume Information",
        @"\$Recycle.Bin",
        @"\Recovery",
        @"\$SysReset",
        @"\$WinREAgent",
        @"\Config.Msi",
        @"\ProgramData\Package Cache"
    };

    /// <summary>Pfadteile, die unterhalb eines Benutzerprofils gesperrt sind.</summary>
    private static readonly string[] BlockedProfileParts =
    {
        @"\AppData\Local\Packages",          // Store-Apps: deren Container brechen hinter einer Junction
        @"\AppData\Local\Microsoft",         // Windows-Bestandteile: Explorer, Edge, Credentials ...
        @"\AppData\Roaming\Microsoft",
        @"\AppData\Local\Temp",              // wird staendig von allem beschrieben
        @"\OneDrive",
        @"\iCloudDrive",
        @"\Dropbox"                          // Sync-Programme kommen mit Junctions nicht sauber klar
    };

    /// <summary>Diese Ordner selbst nicht - wohl aber Unterordner (mit Warnung bzw. frei).</summary>
    private static readonly string[] BlockedExact =
    {
        @"\Users", @"\Program Files", @"\Program Files (x86)", @"\ProgramData",
        @"\AppData", @"\AppData\Local", @"\AppData\Roaming", @"\AppData\LocalLow",
        @"\Desktop", @"\Documents", @"\Downloads"
    };

    /// <summary>
    /// Ziel einer Umlagerung: der Quellpfad wird unter der Zielwurzel gespiegelt,
    /// z. B. C:\Users\alex\curseforge -> D:\Ausgelagert\C\Users\alex\curseforge.
    /// So gibt es keine Namenskollisionen und man sieht im Explorer sofort, woher etwas stammt.
    /// </summary>
    public static string BuildTargetPath(string sourcePath, string targetRoot)
    {
        string full = Path.GetFullPath(sourcePath).TrimEnd('\\');
        string drive = full[..1].ToUpperInvariant();
        string rest = full.Length > 3 ? full[3..] : string.Empty;
        return Path.Combine(Path.GetFullPath(targetRoot), drive, rest);
    }

    /// <summary>
    /// Ziel, wenn der Benutzer einen Zielordner waehlt: der Quellordner landet
    /// darin unter seinem eigenen Namen, z. B. C:\Games\Witcher + D:\Spiele
    /// -> D:\Spiele\Witcher.
    /// </summary>
    public static string TargetInFolder(string sourcePath, string targetFolder)
    {
        string name = Path.GetFileName(Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(Path.GetFullPath(targetFolder), name);
    }

    /// <summary>
    /// Die reinen Pfadregeln, ohne Zugriff aufs Dateisystem. Getrennt, damit die
    /// Vorschlagsliste mit genau denselben Regeln filtert wie die Pruefung vor dem
    /// Umlagern - eine zweite Kopie der Regeln liefe frueher oder spaeter auseinander.
    /// </summary>
    public static List<GuardIssue> PathRules(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        var issues = new List<GuardIssue>();
        void Block(string message) => issues.Add(new GuardIssue(IssueSeverity.Sperre, message));
        void Warn(string message) => issues.Add(new GuardIssue(IssueSeverity.Warnung, message));
        void Note(string message) => issues.Add(new GuardIssue(IssueSeverity.Hinweis, message));

        if (source.Length <= 3)
        {
            Block(Loc.T("guard.wholeDrive"));
            return issues;
        }

        string withoutDrive = source[2..];   // "C:\Users\..." -> "\Users\..."

        foreach (string blocked in BlockedBelow)
            if (PathUtil.IsSameOrBelow(withoutDrive, blocked))
                Block(Loc.F("guard.systemArea", blocked));

        if (PathUtil.IsSameOrBelow(withoutDrive, @"\Users"))
        {
            // \Users\<name> ist das Profil selbst - gesperrt; darunter gelten die Profilregeln.
            string[] parts = withoutDrive.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2) Block(Loc.T("guard.wholeProfile"));

            string inProfile = parts.Length > 2 ? "\\" + string.Join('\\', parts[2..]) : "\\";
            foreach (string part in BlockedProfileParts)
                if (PathUtil.IsSameOrBelow(inProfile, part))
                    Block(part switch
                    {
                        @"\AppData\Local\Packages" => Loc.T("guard.storeApps"),
                        @"\OneDrive" or @"\iCloudDrive" or @"\Dropbox" => Loc.T("guard.syncFolder"),
                        _ => Loc.F("guard.windowsArea", part)
                    });

            foreach (string exact in BlockedExact)
                if (string.Equals(inProfile, exact, StringComparison.OrdinalIgnoreCase))
                    Block(Loc.F("guard.profileFolderItself", exact.TrimStart('\\')));
        }
        else
        {
            foreach (string exact in BlockedExact)
                if (string.Equals(withoutDrive, exact, StringComparison.OrdinalIgnoreCase))
                    Block(Loc.F("guard.folderItself", exact.TrimStart('\\')));
        }

        if (PathUtil.IsSameOrBelow(withoutDrive, @"\Program Files") ||
            PathUtil.IsSameOrBelow(withoutDrive, @"\Program Files (x86)"))
            Warn(Loc.T("guard.programFiles"));

        if (source.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase) ||
            source.EndsWith(@"\steamapps", StringComparison.OrdinalIgnoreCase))
            Note(Loc.T("guard.steam"));

        return issues;
    }

    /// <param name="targetPath">Vollstaendiger Zielpfad des Ordners, siehe
    /// <see cref="BuildTargetPath"/> und <see cref="TargetInFolder"/>.</param>
    public static RelocationCheck Check(string sourcePath, string targetPath, ScanResult? sourceScan = null)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd('\\');
        string target = Path.GetFullPath(targetPath).TrimEnd('\\');
        var check = new RelocationCheck
        {
            SourcePath = source,
            TargetPath = target,
            SourceBytes = sourceScan?.TotalBytesOnDisk ?? 0,
            SourceFiles = sourceScan?.FileCount ?? 0
        };

        void Block(string message) => check.Issues.Add(new GuardIssue(IssueSeverity.Sperre, message));

        // --- Quelle -----------------------------------------------------
        if (!Directory.Exists(source))
        {
            Block(Loc.T("guard.missing"));
            return check;
        }

        if (source.Length <= 3)
        {
            Block(Loc.T("guard.wholeDrive"));
            return check;
        }

        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            string? existing = JunctionManager.ReadTarget(source);
            Block(existing is null
                ? Loc.T("guard.isLink")
                : Loc.F("guard.alreadyRelocated", existing));
            return check;
        }

        check.Issues.AddRange(PathRules(source));

        // --- Ziel -------------------------------------------------------
        string? sourceRoot = Path.GetPathRoot(source);
        string? targetDrive = Path.GetPathRoot(target);
        if (targetDrive is null || sourceRoot is null)
        {
            Block(Loc.T("guard.driveUnknown"));
            return check;
        }

        if (string.Equals(sourceRoot, targetDrive, StringComparison.OrdinalIgnoreCase))
            Block(Loc.T("guard.sameDrive"));

        if (PathUtil.IsSameOrBelow(target, source) || PathUtil.IsSameOrBelow(source, target))
            Block(Loc.T("guard.nested"));

        try
        {
            var drive = new DriveInfo(targetDrive);
            if (!drive.IsReady) Block(Loc.F("guard.driveNotReady", targetDrive));
            else
            {
                if (drive.DriveType != DriveType.Fixed)
                    Block(Loc.F("guard.notFixed", targetDrive, drive.DriveType));
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    Block(Loc.F("guard.notNtfs", drive.DriveFormat));

                check.TargetFreeBytes = drive.AvailableFreeSpace;
                if (check.SourceBytes > 0)
                {
                    long needed = check.SourceBytes + check.SourceBytes / 20 + 1024L * 1024 * 1024;
                    if (drive.AvailableFreeSpace < needed)
                        Block(Loc.F("guard.noSpace", targetDrive, SizeFormat.Bytes(drive.AvailableFreeSpace), SizeFormat.Bytes(needed)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Block(Loc.F("guard.driveUnreadable", targetDrive, ex.Message));
        }

        if (File.Exists(target))
            Block(Loc.F("guard.targetFileExists", target));
        else if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            Block(Loc.F("guard.targetFolderExists", target));

        // --- Laufende Programme ----------------------------------------
        foreach (string running in ProcessesRunningFrom(source))
            Block(Loc.F("guard.running", running));

        return check;
    }

    /// <summary>
    /// Programme, deren Exe unterhalb des Ordners liegt. Der Klassiker: das Spiel
    /// laeuft noch, waehrend man es verschieben will. Prozesse, deren Pfad wir
    /// nicht lesen duerfen (Dienste, geschuetzte Prozesse), werden uebersprungen.
    /// </summary>
    public static IReadOnlyList<string> ProcessesRunningFrom(string folder)
    {
        var hits = new List<string>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? exe = process.MainModule?.FileName;
                if (exe is not null && PathUtil.IsSameOrBelow(exe, folder))
                    hits.Add($"{process.ProcessName} (PID {process.Id})");
            }
            catch (Exception)
            {
                // Zugriff verweigert oder Prozess gerade beendet - kein Befund.
            }
            finally
            {
                process.Dispose();
            }
        }
        return hits;
    }
}
