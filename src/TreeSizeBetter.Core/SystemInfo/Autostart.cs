using System.Diagnostics;
using Microsoft.Win32;
using TreeSizeBetter.Core.Util;

namespace TreeSizeBetter.Core.SystemInfo;

/// <summary>Wo ein Autostart-Eintrag steht.</summary>
public enum AutostartLocation
{
    /// <summary>HKCU\...\Run - nur fuer diesen Benutzer.</summary>
    BenutzerRegistry,

    /// <summary>HKLM\...\Run - fuer alle Benutzer (64 Bit).</summary>
    AlleRegistry,

    /// <summary>HKLM\WOW6432Node\...\Run - fuer alle Benutzer (32-Bit-Programme).</summary>
    AlleRegistry32,

    /// <summary>Autostart-Ordner im Startmenue dieses Benutzers.</summary>
    BenutzerOrdner,

    /// <summary>Autostart-Ordner fuer alle Benutzer.</summary>
    AlleOrdner,

    /// <summary>Startaufgabe einer Store-App (App-Paket) - nur fuer diesen Benutzer.</summary>
    StoreApp
}

public sealed record AutostartEntry(
    string Name,
    string Command,
    AutostartLocation Location,
    bool Enabled,
    string? TargetPath,
    string Publisher,
    bool FileMissing,
    string? StoreTaskKey = null,
    bool PolicyLocked = false)
{
    /// <summary>Eintraege fuer alle Benutzer lassen sich nur mit Administratorrechten schalten.</summary>
    public bool NeedsAdmin => Location is AutostartLocation.AlleRegistry or AutostartLocation.AlleRegistry32 or AutostartLocation.AlleOrdner;
}

/// <summary>
/// Liest und schaltet Autostart-Eintraege so, wie es der Taskmanager tut:
/// Der Eintrag selbst bleibt immer stehen; "aus" vermerkt Windows in einem
/// eigenen Schluessel (Explorer\StartupApproved). Dadurch ist jede Aenderung
/// umkehrbar, und Taskmanager und SizeShift zeigen dasselbe.
///
/// Registry-Wurzeln und Ordner sind austauschbar, damit Tests in einem
/// eigenen Testschluessel laufen und nie den echten Autostart beruehren.
/// </summary>
public sealed class AutostartManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly RegistryKey _currentUser;
    private readonly RegistryKey _localMachine;
    private readonly string _userFolder;
    private readonly string _commonFolder;

    public AutostartManager(RegistryKey currentUser, RegistryKey localMachine, string userFolder, string commonFolder)
    {
        _currentUser = currentUser;
        _localMachine = localMachine;
        _userFolder = userFolder;
        _commonFolder = commonFolder;
    }

    /// <summary>Der echte Autostart dieses PCs.</summary>
    public static AutostartManager ForThisPc() => new(
        RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

    // ------------------------------------------------------------- Lesen

    public IReadOnlyList<AutostartEntry> ReadAll()
    {
        var result = new List<AutostartEntry>();
        ReadRegistry(_currentUser, RunPath, "Run", AutostartLocation.BenutzerRegistry, result);
        ReadRegistry(_localMachine, RunPath, "Run", AutostartLocation.AlleRegistry, result);
        ReadRegistry(_localMachine, Run32Path, "Run32", AutostartLocation.AlleRegistry32, result);
        ReadFolder(_userFolder, _currentUser, AutostartLocation.BenutzerOrdner, result);
        ReadFolder(_commonFolder, _localMachine, AutostartLocation.AlleOrdner, result);
        ReadStoreApps(_currentUser, result);
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRegistry(RegistryKey root, string path, string approvedName, AutostartLocation location, List<AutostartEntry> into)
    {
        try
        {
            using RegistryKey? run = root.OpenSubKey(path);
            if (run is null) return;
            using RegistryKey? approved = root.OpenSubKey($@"{ApprovedPath}\{approvedName}");
            foreach (string name in run.GetValueNames())
            {
                if (name.Length == 0 || run.GetValue(name) is not string command) continue;
                string? target = ExecutableOf(command);
                into.Add(new AutostartEntry(name, command, location, IsEnabled(approved, name), target,
                    PublisherOf(target), IsMissing(target)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ReadFolder(string folder, RegistryKey approvedRoot, AutostartLocation location, List<AutostartEntry> into)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            using RegistryKey? approved = approvedRoot.OpenSubKey($@"{ApprovedPath}\StartupFolder");
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string? target = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShortcutTarget(file)
                    : file;
                into.Add(new AutostartEntry(Path.GetFileNameWithoutExtension(file), file, location,
                    IsEnabled(approved, fileName), target, PublisherOf(target),
                    target is not null && !File.Exists(target) && !Directory.Exists(target)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>
    /// Kein Eintrag im StartupApproved-Schluessel heisst "an". Sonst entscheidet das
    /// erste Byte: gerade (02, 06) = an, ungerade (03, 07) = aus - wie im Taskmanager.
    /// </summary>
    private static bool IsEnabled(RegistryKey? approved, string valueName)
        => approved?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;

    private const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string PackagesPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// Store-Apps melden ihren Autostart als Startaufgabe ihres Pakets an. Windows
    /// fuehrt den Zustand je Aufgabe als Zahl: 0 = aus (Standard), 1 = vom Benutzer
    /// aus, 2 = an, 3/4 = per Richtlinie aus/an (nicht schaltbar). So zeigt es auch
    /// der Taskmanager.
    /// </summary>
    private static void ReadStoreApps(RegistryKey root, List<AutostartEntry> into)
    {
        try
        {
            using RegistryKey? tasks = root.OpenSubKey(StoreTasksPath);
            if (tasks is null) return;
            using RegistryKey? packages = root.OpenSubKey(PackagesPath);

            foreach (string family in tasks.GetSubKeyNames())
            {
                using RegistryKey? package = tasks.OpenSubKey(family);
                if (package is null) continue;

                var found = new List<(string TaskId, int State)>();
                foreach (string taskId in package.GetSubKeyNames())
                {
                    using RegistryKey? task = package.OpenSubKey(taskId);
                    if (task?.GetValue("State") is int state) found.Add((taskId, state));
                }
                if (found.Count == 0) continue;

                (string name, string? folder, string fullName) = StoreAppName(packages, family);
                string publisher = StorePublisher(folder);
                Dictionary<string, (string Name, string Executable)>? declared = StoreTasksFromManifest(folder, fullName, family);

                // Steht im Manifest, welche Startaufgaben die App hat, gelten nur diese - uebrige
                // Eintraege sind Reste aelterer Versionen, die auch der Taskmanager nicht zeigt.
                if (declared is not null) found = found.Where(f => declared.ContainsKey(f.TaskId)).ToList();

                foreach (var (taskId, state) in found)
                {
                    (string Name, string Executable) info = declared?.GetValueOrDefault(taskId) ?? (string.Empty, string.Empty);
                    string display = found.Count == 1 || info.Name.Length == 0 ? name : info.Name;
                    if (found.Count > 1 && info.Name.Length == 0) display = $"{name} ({taskId})";
                    string command = info.Executable.Length > 0 && folder is not null ? Path.Combine(folder, info.Executable) : family;
                    into.Add(new AutostartEntry(display, command, AutostartLocation.StoreApp,
                        state is 2 or 4, null, publisher, false,
                        StoreTaskKey: $@"{family}\{taskId}", PolicyLocked: state is 3 or 4));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>Anzeigename des Pakets; "ms-resource:..." wird ueber die Windows-Shell aufgeloest.</summary>
    private static (string Name, string? Folder, string FullName) StoreAppName(RegistryKey? packages, string family)
    {
        string fallback = family.Split('_')[0];
        if (packages is null) return (fallback, null, string.Empty);

        // Paketname "Name_Version_Arch_Ressource_Herausgeber"; die Familie ist "Name_Herausgeber".
        // Das Hauptpaket hat keine Ressourcen-Kennung ("__").
        string[] parts = family.Split('_');
        if (parts.Length < 2) return (fallback, null, string.Empty);
        string fullName = packages.GetSubKeyNames()
            .Where(n => n.StartsWith(parts[0] + "_", StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith("_" + parts[^1], StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Contains("__") ? 0 : 1)
            .ThenByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
        if (fullName.Length == 0) return (fallback, null, string.Empty);

        using RegistryKey? key = packages.OpenSubKey(fullName);
        string? folder = key?.GetValue("PackageRootFolder") as string;
        string? display = key?.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(display)) return (fallback, folder, fullName);
        string resolved = Resolve(display, fullName, parts[0]);
        return (string.IsNullOrWhiteSpace(resolved) ? fallback : resolved, folder, fullName);
    }

    /// <summary>Text aus dem Manifest; "ms-resource:..." wird ueber die Windows-Shell aufgeloest.</summary>
    private static string Resolve(string value, string fullName, string packageName)
    {
        if (value.StartsWith("@{")) return ResolveIndirect(value);
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
        return fullName.Length == 0 ? string.Empty : ResolveIndirect($"@{{{fullName}?{ResourceUri(packageName, value)}}}");
    }

    /// <summary>Startaufgaben laut App-Manifest: Kennung -> (Anzeigename, Programmdatei). null, wenn nicht lesbar.</summary>
    private static Dictionary<string, (string Name, string Executable)>? StoreTasksFromManifest(string? folder, string fullName, string family)
    {
        if (folder is null) return null;
        try
        {
            string manifest = Path.Combine(folder, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            var doc = System.Xml.Linq.XDocument.Load(manifest);
            string packageName = family.Split('_')[0];
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in doc.Descendants().Where(e => e.Name.LocalName == "StartupTask"))
            {
                string? id = task.Attribute("TaskId")?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                string name = Resolve(task.Attribute("DisplayName")?.Value ?? string.Empty, fullName, packageName);
                string exe = task.Parent?.Attribute("Executable")?.Value ?? string.Empty;
                result[id] = (name, exe);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string ResourceUri(string packageName, string value)
    {
        string rest = value["ms-resource:".Length..];
        if (rest.StartsWith("//")) return value;
        if (rest.StartsWith("/")) return $"ms-resource://{packageName}{rest}";
        return $"ms-resource://{packageName}/Resources/{rest}";
    }

    private static string ResolveIndirect(string source)
    {
        var buffer = new System.Text.StringBuilder(512);
        return SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : string.Empty;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder outBuffer, int outBufferSize, IntPtr reserved);

    /// <summary>Herausgeber aus dem App-Manifest, wenn lesbar.</summary>
    private static string StorePublisher(string? folder)
    {
        if (folder is null) return string.Empty;
        try
        {
            string manifest = Path.Combine(folder, "AppxManifest.xml");
            if (!File.Exists(manifest)) return string.Empty;
            var doc = System.Xml.Linq.XDocument.Load(manifest);
            string? publisher = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value?.Trim();
            return publisher is null || publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? string.Empty : publisher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------- Schalten

    /// <summary>
    /// Schaltet einen Eintrag an oder aus. Der Eintrag selbst wird nie geaendert
    /// oder geloescht. Liefert null bei Erfolg, sonst eine Fehlermeldung.
    /// </summary>
    public string? SetEnabled(AutostartEntry entry, bool enabled)
    {
        if (entry.Location == AutostartLocation.StoreApp)
            return SetStoreTask(entry, enabled);

        RegistryKey root = entry.NeedsAdmin ? _localMachine : _currentUser;
        string sub = entry.Location switch
        {
            AutostartLocation.AlleRegistry32 => "Run32",
            AutostartLocation.BenutzerOrdner or AutostartLocation.AlleOrdner => "StartupFolder",
            _ => "Run"
        };
        string valueName = entry.Location is AutostartLocation.BenutzerOrdner or AutostartLocation.AlleOrdner
            ? Path.GetFileName(entry.Command)
            : entry.Name;

        // 12 Byte wie beim Taskmanager: Zustand, drei Nullbytes, dann der Zeitpunkt des Abschaltens.
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

        try
        {
            using RegistryKey approved = root.CreateSubKey($@"{ApprovedPath}\{sub}", writable: true);
            approved.SetValue(valueName, data, RegistryValueKind.Binary);
            ActivityLog.Write("Autostart", $"{(enabled ? "an" : "aus")}: {entry.Name}  ({entry.Location}, {entry.Command})");
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>Store-App: 2 = an, 1 = vom Benutzer aus. Wie im Taskmanager und in den Windows-Einstellungen.</summary>
    private string? SetStoreTask(AutostartEntry entry, bool enabled)
    {
        if (entry.PolicyLocked || entry.StoreTaskKey is null) return "Policy";
        try
        {
            using RegistryKey? task = _currentUser.OpenSubKey($@"{StoreTasksPath}\{entry.StoreTaskKey}", writable: true);
            if (task is null) return entry.StoreTaskKey;
            task.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
            ActivityLog.Write("Autostart", $"{(enabled ? "an" : "aus")}: {entry.Name}  (Store-App {entry.StoreTaskKey})");
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------- Hilfen

    /// <summary>Programmdatei aus einer Befehlszeile: in Anfuehrungszeichen, sonst bis ".exe".</summary>
    public static string? ExecutableOf(string command)
    {
        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return text[..(exe + 4)];
        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Fehlt die Programmdatei? Unter WindowsApps darf man ohne Sonderrechte nicht
    /// nachsehen - dort gilt eine Datei als vorhanden, statt falsch "fehlt" zu melden.
    /// </summary>
    private static bool IsMissing(string? target)
    {
        if (target is null) return false;
        if (target.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
        return !File.Exists(target) && !Directory.Exists(target);
    }

    private static string PublisherOf(string? path)
    {
        if (path is null || !File.Exists(path)) return string.Empty;
        try
        {
            return FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>Ziel einer Verknuepfung (.lnk) ueber die Windows-Shell.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }
}
