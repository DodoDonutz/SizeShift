using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TreeSizeBetter.App.Shell;

/// <summary>Eigener Eintrag, der zusaetzlich zum Windows-Menue angezeigt wird.</summary>
public sealed record ShellMenuExtra(int Id, string Text);

/// <summary>
/// Zeigt das echte Kontextmenue des Windows-Explorers fuer einen Pfad - mit
/// allem, was installierte Programme dort eingehaengt haben (VLC, Git Bash,
/// Defender, 7-Zip ...). Eigene Eintraege landen, wie bei TreeSize, in einem
/// Untermenue ganz oben.
///
/// Ablauf: Pfad -> PIDL -> uebergeordneter IShellFolder -> IContextMenu fuer das
/// Element -> in ein Win32-Menue fuellen lassen -> anzeigen -> gewaehlten Befehl
/// ausfuehren. Untermenues wie "Senden an" und "Oeffnen mit" werden erst beim
/// Aufklappen von der Shell befuellt; dafuer muessen die Menue-Nachrichten des
/// Fensters an IContextMenu2/3 weitergereicht werden, sonst bleiben sie leer.
/// </summary>
public static class ShellContextMenu
{
    private const uint FirstShellCommand = 1;
    private const uint LastShellCommand = 0x7FFF;
    private const int FirstExtraCommand = 0x8000;

    /// <summary>
    /// Zeigt das Menue an der aktuellen Mausposition. Gibt die Id eines gewaehlten
    /// eigenen Eintrags zurueck, sonst 0 - Shell-Befehle fuehrt die Methode selbst aus.
    /// </summary>
    public static int Show(IntPtr ownerWindow, string path, IReadOnlyList<ShellMenuExtra>? extras = null)
    {
        IntPtr pidl = IntPtr.Zero;
        IntPtr parentFolderPtr = IntPtr.Zero;
        IntPtr contextMenuPtr = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        object? contextMenuObject = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return 0;

            Guid shellFolderId = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref shellFolderId, out parentFolderPtr, out IntPtr childPidl) != 0)
                return 0;

            var parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(parentFolderPtr);
            Guid contextMenuId = typeof(IContextMenu).GUID;
            if (parentFolder.GetUIObjectOf(ownerWindow, 1, new[] { childPidl }, ref contextMenuId,
                    IntPtr.Zero, out contextMenuPtr) != 0)
                return 0;

            contextMenuObject = Marshal.GetObjectForIUnknown(contextMenuPtr);
            var contextMenu = (IContextMenu)contextMenuObject;
            var contextMenu2 = contextMenuObject as IContextMenu2;
            var contextMenu3 = contextMenuObject as IContextMenu3;

            menu = CreatePopupMenu();
            uint flags = CmfNormal | CmfExplore;
            if ((GetKeyState(VkShift) & 0x8000) != 0) flags |= CmfExtendedVerbs;   // wie im Explorer: Shift zeigt mehr

            if (contextMenu.QueryContextMenu(menu, 0, FirstShellCommand, LastShellCommand, flags) < 0)
                return 0;

            if (extras is { Count: > 0 }) InsertExtras(menu, extras);

            source = HwndSource.FromHwnd(ownerWindow);
            if (source is not null && (contextMenu3 is not null || contextMenu2 is not null))
            {
                hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg is not (WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)) return IntPtr.Zero;

                    if (contextMenu3 is not null &&
                        contextMenu3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                    if (contextMenu2 is not null && contextMenu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    {
                        handled = true;
                        return msg == WmInitMenuPopup ? IntPtr.Zero : new IntPtr(1);
                    }
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }

            GetCursorPos(out Point cursor);
            int command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y,
                ownerWindow, IntPtr.Zero);

            if (command <= 0) return 0;
            if (command >= FirstExtraCommand) return command - FirstExtraCommand;

            var invoke = new CmInvokeCommandInfo
            {
                cbSize = Marshal.SizeOf<CmInvokeCommandInfo>(),
                hwnd = ownerWindow,
                lpVerb = new IntPtr(command - (int)FirstShellCommand),   // Befehl als Zahl, nicht als Text
                nShow = SwShowNormal
            };
            contextMenu.InvokeCommand(ref invoke);
            return 0;
        }
        finally
        {
            if (hook is not null) source?.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (contextMenuObject is not null) Marshal.ReleaseComObject(contextMenuObject);
            if (contextMenuPtr != IntPtr.Zero) Marshal.Release(contextMenuPtr);
            if (parentFolderPtr != IntPtr.Zero) Marshal.Release(parentFolderPtr);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static void InsertExtras(IntPtr menu, IReadOnlyList<ShellMenuExtra> extras)
    {
        IntPtr submenu = CreatePopupMenu();
        foreach (ShellMenuExtra extra in extras)
            AppendMenuW(submenu, MfString, (UIntPtr)(uint)(FirstExtraCommand + extra.Id), extra.Text);

        // Ganz oben, gefolgt von einem Trenner - genau wie das TREESIZE-Untermenue.
        InsertMenuW(menu, 0, MfByPosition | MfPopup | MfString, (UIntPtr)(ulong)submenu, "SizeShift");
        InsertMenuW(menu, 1, MfByPosition | MfSeparator, UIntPtr.Zero, null);
    }

    // ------------------------------------------------------------- Win32

    private const uint CmfNormal = 0x00000000;
    private const uint CmfExplore = 0x00000004;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint MfString = 0x0000;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const uint MfByPosition = 0x0400;
    private const int SwShowNormal = 1;
    private const int VkShift = 0x10;

    private const int WmInitMenuPopup = 0x0117;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const int WmMenuChar = 0x0120;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmInvokeCommandInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuW(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIdNewItem,
        string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    // ------------------------------------------------------------- COM

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name,
            out uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, int cch);
    }

    // COM-Schnittstellen erben in C# nicht: die Methoden der Basis muessen in
    // gleicher Reihenfolge wiederholt werden, sonst stimmt die vtable nicht.
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, int cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, int cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
}
