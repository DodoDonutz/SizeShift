using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Util;

/// <summary>
/// Eindeutige Kennung einer Datei auf einem Volume: Seriennummer des Volumes
/// plus Dateinummer. Zwei Pfade mit derselben Kennung sind nicht zwei Dateien,
/// sondern zwei Namen fuer dieselben Daten.
/// </summary>
public readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount)
{
    public bool IsHardLinked => LinkCount > 1;
}

/// <summary>
/// Erkennt Hardlinks.
///
/// Das ist kein Detail: Windows legt den Komponentenspeicher (C:\Windows\WinSxS)
/// und den Treiberspeicher fast vollstaendig als Hardlinks an. Ein Duplikatsucher,
/// der nur Name und Groesse vergleicht, meldet dort hunderte MB angeblich
/// verschwendeten Platz, den es gar nicht gibt - und wer danach loescht,
/// beschaedigt Windows, ohne ein Byte zu gewinnen.
///
/// Die Pruefung oeffnet Dateien einzeln und ist entsprechend langsam. Sie laeuft
/// deshalb nur ueber die wenigen Verdachtsfaelle, nie ueber den ganzen Bestand.
/// </summary>
public static class FileIdentityReader
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public static bool TryRead(string path, out FileIdentity identity)
    {
        identity = default;

        IntPtr handle = CreateFileW(
            PathUtil.ToExtended(path),
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle == new IntPtr(-1)) return false;

        try
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info)) return false;

            identity = new FileIdentity(
                info.dwVolumeSerialNumber,
                (ulong)info.nFileIndexHigh << 32 | info.nFileIndexLow,
                info.nNumberOfLinks);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Fasst Pfade zusammen, die auf dieselben Daten zeigen. Gibt die Zahl der
    /// tatsaechlich eigenstaendigen Kopien zurueck; konnte eine Datei nicht
    /// geoeffnet werden, zaehlt sie vorsichtshalber als eigenstaendig.
    /// </summary>
    public static int CountDistinctFiles(IReadOnlyList<string> paths, out bool allVerified)
    {
        allVerified = true;
        var identities = new HashSet<FileIdentity>();
        int unverified = 0;

        foreach (string path in paths)
        {
            if (TryRead(path, out FileIdentity identity)) identities.Add(identity);
            else
            {
                unverified++;
                allVerified = false;
            }
        }

        return identities.Count + unverified;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow;
        public uint ftCreationTimeHigh;
        public uint ftLastAccessTimeLow;
        public uint ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow;
        public uint ftLastWriteTimeHigh;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
}
