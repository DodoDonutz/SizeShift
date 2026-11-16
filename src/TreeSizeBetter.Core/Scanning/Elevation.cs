using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TreeSizeBetter.Core.Scanning;

/// <summary>
/// Admin-Rechte und das Sicherungsrecht.
///
/// Admin-Rechte allein reichen fuer einen vollstaendigen Scan nicht:
/// "System Volume Information" gehoert dem Systemkonto, "WindowsApps" dem
/// TrustedInstaller - beide sperren auch Administratoren aus. Administratoren
/// besitzen aber das Sicherungsrecht (SeBackupPrivilege). Es ist von Haus aus
/// ausgeschaltet und muss vom Programm selbst eingeschaltet werden; danach darf
/// es jeden Ordner lesen, egal was in dessen Rechten steht. Genau dafuer ist das
/// Recht da - Sicherungsprogramme muessen schliesslich auch alles erreichen.
///
/// Das Recht erlaubt nur Lesen. Geschrieben oder geloescht wird damit nichts.
/// </summary>
public static class Elevation
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    private static readonly object Gate = new();
    private static bool _attempted;

    public static bool IsElevated { get; } = CheckElevated();

    /// <summary>true, sobald das Sicherungsrecht fuer diesen Prozess aktiv ist.</summary>
    public static bool BackupPrivilegeEnabled { get; private set; }

    /// <summary>Schaltet das Sicherungsrecht ein, falls der Prozess Admin-Rechte hat. Mehrfacher Aufruf ist harmlos.</summary>
    public static bool TryEnableBackupPrivilege()
    {
        lock (Gate)
        {
            if (_attempted) return BackupPrivilegeEnabled;
            _attempted = true;
            if (!IsElevated) return false;

            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out Luid luid)) return false;

                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;

                // AdjustTokenPrivileges meldet auch dann Erfolg, wenn das Recht gar
                // nicht zugeteilt werden konnte - die Wahrheit steht im Fehlercode.
                BackupPrivilegeEnabled = Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
                return BackupPrivilegeEnabled;
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }

    private static bool CheckElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
