using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Scanning;

/// <summary>
/// Layout von WIN32_FIND_DATAW.
///
/// Wichtig: FILETIME ist native eine Struktur aus zwei DWORDs und damit auf 4 Byte
/// ausgerichtet. Wuerde man die drei Zeitstempel hier als long deklarieren, erzwaenge
/// C# auf x64 ein 8-Byte-Alignment und schoebe hinter dwFileAttributes 4 Byte Padding
/// ein, das es native nicht gibt - alle Felder danach waeren verschoben und man wuerde
/// Muell einlesen. Deshalb konsequent uint-Paare: alles ist auf 4 ausgerichtet und die
/// Struktur ist genau 592 Byte gross, wie native.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct Win32FindData
{
    public uint dwFileAttributes;
    public uint ftCreationTimeLow;
    public uint ftCreationTimeHigh;
    public uint ftLastAccessTimeLow;
    public uint ftLastAccessTimeHigh;
    public uint ftLastWriteTimeLow;
    public uint ftLastWriteTimeHigh;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;          // bei Reparse-Points: der Reparse-Tag
    public uint dwReserved1;
    public fixed char cFileName[260];
    public fixed char cAlternateFileName[14];

    public long CreationTime => (long)ftCreationTimeHigh << 32 | ftCreationTimeLow;
    public long LastAccessTime => (long)ftLastAccessTimeHigh << 32 | ftLastAccessTimeLow;
    public long LastWriteTime => (long)ftLastWriteTimeHigh << 32 | ftLastWriteTimeLow;
    public long FileSize => (long)nFileSizeHigh << 32 | nFileSizeLow;
}

internal static unsafe partial class NativeMethods
{
    internal const int FindExInfoBasic = 1;              // ueberspringt den 8.3-Namen, messbar schneller
    internal const int FindExSearchNameMatch = 0;
    internal const int FindFirstExLargeFetch = 2;        // groessere Puffer pro Kernel-Aufruf

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNoMoreFiles = 18;
    internal const int ErrorNotReady = 21;
    internal const int ErrorInvalidName = 123;
    internal const int ErrorDirectory = 267;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    internal const uint FileListDirectory = 0x0001;
    internal const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const int FileFullDirectoryInfo = 14;
    internal const int FileFullDirectoryRestartInfo = 15;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(IntPtr hFile, int fileInformationClass, void* buffer, int bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr FindFirstFileEx(
        string lpFileName,
        int fInfoLevelId,
        Win32FindData* lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextFile(IntPtr hFindFile, Win32FindData* lpFindFileData);

    [LibraryImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(IntPtr hFindFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
