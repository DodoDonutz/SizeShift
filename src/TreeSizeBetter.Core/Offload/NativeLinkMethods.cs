using System.Runtime.InteropServices;

namespace TreeSizeBetter.Core.Offload;

internal static class NativeLinkMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    internal const uint FsctlSetReparsePoint = 0x000900A4;
    internal const uint FsctlGetReparsePoint = 0x000900A8;
    internal const uint FsctlDeleteReparsePoint = 0x000900AC;

    /// <summary>Groesse des Puffers, den ein Reparse-Point hoechstens belegen darf.</summary>
    internal const int MaximumReparseDataBufferSize = 16 * 1024;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Kopf eines Reparse-Points. Der eigentliche Pfad folgt direkt dahinter im
    /// selben Puffer, weshalb hier nur die festen Felder stehen und der Rest von
    /// Hand geschrieben wird.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReparseHeader
    {
        public uint ReparseTag;
        public ushort ReparseDataLength;
        public ushort Reserved;
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;
    }

    /// <summary>Groesse des Kopfes: 8 Byte allgemein plus 8 Byte fuer die vier Laengenangaben.</summary>
    internal const int HeaderSize = 16;
}
