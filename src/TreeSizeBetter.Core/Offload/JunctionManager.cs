using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using TreeSizeBetter.Core.Scanning;
using TreeSizeBetter.Core.Util;
using static TreeSizeBetter.Core.Offload.NativeLinkMethods;

namespace TreeSizeBetter.Core.Offload;

/// <summary>
/// Legt Junctions an, liest ihr Ziel und loest sie wieder.
///
/// Junction statt Symlink: funktioniert ohne Admin-Rechte und ohne
/// Entwicklermodus, und praktisch jedes Programm - Spiele, Launcher,
/// Installer - laeuft transparent hindurch. Einschraenkung: das Ziel muss ein
/// lokales Laufwerk sein, Netzpfade gehen nicht.
/// </summary>
public static class JunctionManager
{
    private const string NtPrefix = @"\??\";

    /// <summary>
    /// Legt an <paramref name="linkPath"/> eine Junction auf <paramref name="targetPath"/> an.
    /// Der Pfad darf noch nicht existieren; das Ziel muss ein vorhandener Ordner sein.
    /// </summary>
    public static void Create(string linkPath, string targetPath)
    {
        string target = Path.GetFullPath(targetPath);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException(Loc.F("junction.targetMissing", target));
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
            throw new IOException(Loc.F("junction.exists", linkPath));

        // Laufwerkswurzeln behalten ihren Trenner (D:\), alles andere ohne.
        if (target.Length > 3) target = target.TrimEnd(Path.DirectorySeparatorChar);

        byte[] substitute = Encoding.Unicode.GetBytes(NtPrefix + target);
        byte[] print = Encoding.Unicode.GetBytes(target);

        // Aufbau: 8 Byte Kopf, 8 Byte Laengenangaben, dann beide Namen je mit
        // abschliessendem Nullzeichen (2 Byte in UTF-16).
        int pathBufferLength = substitute.Length + 2 + print.Length + 2;
        int reparseDataLength = 8 + pathBufferLength;
        var buffer = new byte[8 + reparseDataLength];
        Span<byte> span = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], ReparseTags.MountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)reparseDataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);                                 // SubstituteNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)substitute.Length);       // SubstituteNameLength
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(substitute.Length + 2)); // PrintNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)print.Length);            // PrintNameLength
        substitute.CopyTo(span[HeaderSize..]);
        print.CopyTo(span[(HeaderSize + substitute.Length + 2)..]);

        Directory.CreateDirectory(linkPath);
        try
        {
            using SafeFileHandleWrapper handle = Open(linkPath, GenericWrite);
            IntPtr native = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, native, buffer.Length);
                if (!DeviceIoControl(handle.Handle, FsctlSetReparsePoint, native, buffer.Length,
                        IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        Loc.F("junction.failed", linkPath));
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }
        catch
        {
            // Leeren Ordner wieder wegraeumen, damit kein halber Zustand zurueckbleibt.
            try { Directory.Delete(linkPath, recursive: false); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Ziel einer Junction, oder null wenn der Pfad keine Junction ist.</summary>
    public static string? ReadTarget(string linkPath)
    {
        if (!Directory.Exists(linkPath)) return null;
        var attributes = File.GetAttributes(linkPath);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return null;

        using SafeFileHandleWrapper handle = Open(linkPath, GenericRead);
        IntPtr native = Marshal.AllocHGlobal(MaximumReparseDataBufferSize);
        try
        {
            if (!DeviceIoControl(handle.Handle, FsctlGetReparsePoint, IntPtr.Zero, 0,
                    native, MaximumReparseDataBufferSize, out int returned, IntPtr.Zero))
                return null;

            var buffer = new byte[returned];
            Marshal.Copy(native, buffer, 0, returned);
            ReadOnlySpan<byte> span = buffer;

            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != ReparseTags.MountPoint) return null;

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            string substitute = Encoding.Unicode.GetString(span.Slice(HeaderSize + offset, length));
            return substitute.StartsWith(NtPrefix, StringComparison.Ordinal) ? substitute[NtPrefix.Length..] : substitute;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    public static bool IsJunctionTo(string linkPath, string expectedTarget)
    {
        string? actual = ReadTarget(linkPath);
        return actual is not null && PathUtil.IsSameOrBelow(actual, expectedTarget)
                                  && PathUtil.IsSameOrBelow(expectedTarget, actual);
    }

    /// <summary>
    /// Loest eine Junction auf, ohne das Ziel anzufassen. Verweigert, wenn der
    /// Pfad keine Junction ist - ein versehentlich echter Ordner bleibt so heil.
    /// </summary>
    public static void Remove(string linkPath)
    {
        if (ReadTarget(linkPath) is null)
            throw new IOException(Loc.F("junction.notJunction", linkPath));

        // RemoveDirectory auf einer Junction entfernt nur die Verknuepfung.
        // Nie rekursiv - das ist die eine Stelle, an der ein Fehler das Ziel
        // leeren koennte.
        Directory.Delete(linkPath, recursive: false);
    }

    private static SafeFileHandleWrapper Open(string path, uint access)
    {
        IntPtr handle = CreateFileW(
            PathUtil.ToExtended(path), access,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.F("junction.cannotOpen", path));
        return new SafeFileHandleWrapper(handle);
    }

    private readonly struct SafeFileHandleWrapper : IDisposable
    {
        public SafeFileHandleWrapper(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
        public void Dispose() => CloseHandle(Handle);
    }
}
