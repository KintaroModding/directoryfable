using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DirectoryFable.Shell;

/// <summary>What happened after the shell context menu closed.</summary>
public enum ShellMenuOutcome
{
    /// <summary>Menu closed without a choice.</summary>
    Cancelled,
    /// <summary>The shell ran the command (open, delete, properties, 7-Zip, ...).</summary>
    ShellInvoked,
    /// <summary>"Rename" was chosen; the app renames in place instead of the shell.</summary>
    Rename,
    /// <summary>One of the app's own items (see <c>ExtraCommandId</c>) was chosen.</summary>
    Extra,
    /// <summary>
    /// Windows' "New > Folder" in a folder background menu; the app asks for the name instead of the shell (the shell
    /// would make "New folder" and wait for a rename that only Explorer's own window can start).
    /// </summary>
    NewFolder,
}

public sealed record ShellMenuResult(ShellMenuOutcome Outcome, int ExtraCommandId = 0, string? Verb = null);

/// <summary>
/// The native Windows (Explorer) context menu for files, folders or a folder background,
/// including shell extensions and sub-menus ("Open with", "Send to", 7-Zip, ...).
/// Written after the pattern of Vanara's ShellContextMenu (MIT).
/// Must be called on the UI (STA) thread.
/// </summary>
public static class ShellContextMenu
{
    /// <summary>First command id for the app's own items; shell items use 1 .. this - 1.</summary>
    public const int ExtraCommandFirst = 0x7F00;
    private const uint ShellCommandFirst = 1;

    /// <summary>
    /// Shows the menu at <paramref name="screenX"/>/<paramref name="screenY"/> (physical pixels).
    /// <paramref name="items"/> empty = background menu of <paramref name="folder"/>.
    /// <paramref name="extras"/> are inserted at the top (id must be ≥ <see cref="ExtraCommandFirst"/>).
    /// </summary>
    public static ShellMenuResult Show(
        IntPtr hwnd, string folder, IReadOnlyList<string> items, int screenX, int screenY,
        IReadOnlyList<(int Id, string Text)> extras, bool extendedVerbs, bool ctrlDown, bool shiftDown)
    {
        var pidls = new List<IntPtr>();
        IShellFolder? shellFolder = null;
        IContextMenu? menu = null;
        IntPtr hMenu = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            menu = items.Count > 0
                ? MenuForItems(hwnd, items, pidls, out shellFolder)
                : MenuForBackground(hwnd, folder, pidls, out shellFolder);

            hMenu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | (items.Count > 0 ? CMF_CANRENAME : 0) |
                         (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
            Check(menu.QueryContextMenu(hMenu, 0, ShellCommandFirst, ExtraCommandFirst - 1, flags));

            for (int i = extras.Count - 1; i >= 0; i--)
                InsertMenu(hMenu, 0, MF_BYPOSITION | MF_STRING, (UIntPtr)extras[i].Id, extras[i].Text);
            if (extras.Count > 0)
                InsertMenu(hMenu, (uint)extras.Count, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);

            // Sub-menus ("Send to", "Open with", ...) are filled on demand through window messages
            // that must be forwarded to IContextMenu2/3. Owner-drawn items need them too.
            var menu2 = menu as IContextMenu2;
            var menu3 = menu as IContextMenu3;
            hook = (IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
                    return IntPtr.Zero;
                try
                {
                    if (menu3 is not null && menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                    if (menu2 is not null && menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                        handled = true;
                }
                catch (COMException)
                {
                    // A broken shell extension must not take the app down.
                }
                return IntPtr.Zero;
            };
            source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(hook);

            int command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            source?.RemoveHook(hook);
            source = null;

            if (command == 0)
                return new ShellMenuResult(ShellMenuOutcome.Cancelled);
            if (command >= ExtraCommandFirst)
                return new ShellMenuResult(ShellMenuOutcome.Extra, command);

            int offset = command - (int)ShellCommandFirst;
            string? verb = GetVerb(menu, offset);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
                return new ShellMenuResult(ShellMenuOutcome.Rename, Verb: verb);
            if (items.Count == 0 && string.Equals(verb, "NewFolder", StringComparison.OrdinalIgnoreCase))
                return new ShellMenuResult(ShellMenuOutcome.NewFolder, Verb: verb);

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | CMIC_MASK_ASYNCOK |
                        (ctrlDown ? CMIC_MASK_CONTROL_DOWN : 0) | (shiftDown ? CMIC_MASK_SHIFT_DOWN : 0),
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectory = folder,
                lpDirectoryW = folder,
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { X = screenX, Y = screenY },
            };
            Check(menu.InvokeCommand(ref info));
            return new ShellMenuResult(ShellMenuOutcome.ShellInvoked, Verb: verb);
        }
        finally
        {
            if (source is not null && hook is not null)
                source.RemoveHook(hook);
            if (hMenu != IntPtr.Zero)
                DestroyMenu(hMenu);
            if (menu is not null)
                Marshal.ReleaseComObject(menu);
            if (shellFolder is not null)
                Marshal.ReleaseComObject(shellFolder);
            foreach (IntPtr pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Menu for items that share one parent folder (always true in a file pane).</summary>
    private static IContextMenu MenuForItems(IntPtr hwnd, IReadOnlyList<string> items, List<IntPtr> pidls, out IShellFolder parent)
    {
        foreach (string path in items)
        {
            SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _);
            pidls.Add(pidl);
        }

        Guid iidShellFolder = typeof(IShellFolder).GUID;
        SHBindToParent(pidls[0], ref iidShellFolder, out IntPtr parentPtr, out _);
        parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
        Marshal.Release(parentPtr);

        // Child ids point into the absolute ids (no extra allocation, nothing to free).
        IntPtr[] children = [.. pidls.Select(ILFindLastID)];
        Guid iidContextMenu = typeof(IContextMenu).GUID;
        parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidContextMenu, IntPtr.Zero, out IntPtr menuPtr);
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
        Marshal.Release(menuPtr);
        return menu;
    }

    /// <summary>Menu for the empty space of a folder ("New", "Paste", "Open in Terminal", ...).</summary>
    private static IContextMenu MenuForBackground(IntPtr hwnd, string folder, List<IntPtr> pidls, out IShellFolder shellFolder)
    {
        SHParseDisplayName(folder, IntPtr.Zero, out IntPtr pidl, 0, out _);
        pidls.Add(pidl);

        SHGetDesktopFolder(out IShellFolder desktop);
        try
        {
            Guid iidShellFolder = typeof(IShellFolder).GUID;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iidShellFolder, out IntPtr folderPtr);
            shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);
        }
        finally
        {
            Marshal.ReleaseComObject(desktop);
        }

        Guid iidContextMenu = typeof(IContextMenu).GUID;
        shellFolder.CreateViewObject(hwnd, ref iidContextMenu, out IntPtr menuPtr);
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
        Marshal.Release(menuPtr);
        return menu;
    }

    private static string? GetVerb(IContextMenu menu, int offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 260) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    // ---------- Constants ----------

    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXPLORE = 0x4;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint GCS_VERBW = 0x4;
    private const uint CMIC_MASK_ASYNCOK = 0x00100000;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int SW_SHOWNORMAL = 1;
    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint MF_BYPOSITION = 0x400;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_MENUCHAR = 0x0120;

    // ---------- Native ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    // Method order must match ShObjIdl_core.h exactly.
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
