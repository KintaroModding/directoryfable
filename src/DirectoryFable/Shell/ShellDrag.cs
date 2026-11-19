using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DirectoryFable.Shell;

/// <summary>
/// File drags with Windows' own drag picture (the items' icon or thumbnail, a count for several) and the text next
/// to it ("Move to Documents").
/// Source: the drag starts from the shell's data object of the files (SHDoDragDrop with the shell's drop source),
/// so the picture is Windows' own and other programs get every format Explorer gives them.
/// Target: a WPF window does not show that picture by itself; <see cref="Attach"/> lets the main window draw it
/// (IDropTargetHelper) while a drag is over it, and tell what a drop would do (the "DropDescription" format).
/// </summary>
public static class ShellDrag
{
    // ---------- Source ----------

    /// <summary>
    /// Drags the files; false when the shell drag could not start (the caller then falls back to a plain drag).
    /// Returns after the drop or the cancel, like DragDrop.DoDragDrop.
    /// </summary>
    public static bool TryStart(Visual source, IReadOnlyList<string> paths, DragDropEffects allowed)
    {
        if (PresentationSource.FromVisual(source) is not HwndSource window || paths.Count == 0)
            return false;
        var pidls = new List<IntPtr>();
        try
        {
            foreach (string path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    return false;
                pidls.Add(pidl);
            }
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out var array) != 0 || array is null)
                return false;
            var handler = BhidDataObject;
            var iid = typeof(ComDataObject).GUID;
            if (array.BindToHandler(IntPtr.Zero, ref handler, ref iid, out object bound) != 0 || bound is not ComDataObject data)
                return false;
            Mouse.Capture(null); // The drag takes the mouse itself.
            SHDoDragDrop(window.Handle, data, IntPtr.Zero, (uint)allowed, out _);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (IntPtr pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    // ---------- Target ----------

    /// <summary>
    /// What the drop target under the mouse would do, told by the target in its DragOver (the folder it would drop
    /// into); the window turns it into the text at the drag picture. Null: nothing to tell.
    /// </summary>
    public static string? TargetFolder { get; set; }

    private static IDropTargetHelper? _helper;
    private static object? _dragData; // The drag the picture is shown for (one object per drag).
    private static string _described = "";

    /// <summary>
    /// The window shows the drag picture of file drags over it (ours and those from other programs) and the text
    /// for the target. It becomes a drop target itself, so the picture also shows where nothing accepts a drop
    /// (the effect there is "none").
    /// </summary>
    public static void Attach(Window window)
    {
        window.AllowDrop = true;
        // Before the targets look at the drag: forget the last target.
        window.AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler((_, _) => TargetFolder = null), true);
        window.AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler((_, _) => TargetFolder = null), true);
        // After them: the effect is known.
        window.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler((_, e) => Over(window, e)), true);
        window.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) => Over(window, e)), true);
        window.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) => Leave(window, e)), true);
        window.AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, e) => Drop(e)), true);
    }

    private static void Over(Window window, DragEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, window) && !e.Handled)
        {
            // No drop target under the mouse (the event came to the window itself): it only shows the picture.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (e.Data is not ComDataObject data || hwnd == IntPtr.Zero)
            return;
        GetCursorPos(out POINT point);
        try
        {
            _helper ??= (IDropTargetHelper)new DragDropHelper();
            if (!ReferenceEquals(_dragData, e.Data))
            {
                // A new drag (or the first event of this one). A drag that ended inside the window without a
                // drop (Esc) left no "leave": close it first.
                if (_dragData is not null)
                    _helper.DragLeave();
                _dragData = e.Data;
                _described = "";
                _helper.DragEnter(hwnd, data, ref point, (int)e.Effects);
            }
            else
            {
                _helper.DragOver(ref point, (int)e.Effects);
            }
            Describe(data, e.Effects);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or ArgumentException)
        {
            // No picture for this drag.
        }
    }

    /// <summary>WPF reports a leave for every element the mouse leaves; the picture goes only when it left the window.</summary>
    private static void Leave(Window window, DragEventArgs e)
    {
        if (_dragData is null || !ReferenceEquals(_dragData, e.Data))
            return;
        GetCursorPos(out POINT point);
        bool overWindow = GetAncestor(WindowFromPoint(point), 2 /* GA_ROOT */) == new WindowInteropHelper(window).Handle;
        bool buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (overWindow && buttonDown)
            return; // Only from one element of the window to the next.
        End(e.Data as ComDataObject, leave: true);
    }

    private static void Drop(DragEventArgs e)
    {
        if (_dragData is null || e.Data is not ComDataObject data)
            return;
        GetCursorPos(out POINT point);
        try
        {
            _helper?.Drop(data, ref point, (int)e.Effects);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or ArgumentException)
        {
        }
        End(data, leave: false);
    }

    private static void End(ComDataObject? data, bool leave)
    {
        try
        {
            if (leave)
                _helper?.DragLeave();
            if (data is not null && _described.Length > 0)
                SetDescription(data, DropImageInvalid, "", ""); // Other targets tell their own.
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or ArgumentException)
        {
        }
        _dragData = null;
        _described = "";
        TargetFolder = null;
    }

    /// <summary>"Move to Documents" / "Copy to Documents" / "Create link in Documents" at the drag picture.</summary>
    private static void Describe(ComDataObject data, DragDropEffects effect)
    {
        string name = TargetFolder is { Length: > 0 } folder ? DisplayName(folder) : "";
        (int type, string message) = name.Length == 0 ? (effect == DragDropEffects.None ? 0 : DropImageInvalid, "")
            : effect.HasFlag(DragDropEffects.Move) ? (2, "Move to %1")
            : effect.HasFlag(DragDropEffects.Copy) ? (1, "Copy to %1")
            : effect.HasFlag(DragDropEffects.Link) ? (4, "Create link in %1")
            : (0, "");
        string key = $"{type}|{message}|{name}";
        if (key == _described)
            return;
        _described = key;
        SetDescription(data, type, message, message.Length > 0 ? name : "");
    }

    private static string DisplayName(string folder)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed; // A drive: "C:".
    }

    private const int DropImageInvalid = -1;

    private static void SetDescription(ComDataObject data, int type, string message, string insert)
    {
        var description = new DROPDESCRIPTION { type = type, szMessage = message, szInsert = insert };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<DROPDESCRIPTION>());
        Marshal.StructureToPtr(description, memory, false);
        var format = new FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat("DropDescription"),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory, pUnkForRelease = null };
        try
        {
            data.SetData(ref format, ref medium, true); // The data object now owns the memory.
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException or ArgumentException
                                       or InvalidOperationException or NotSupportedException)
        {
            Marshal.FreeHGlobal(memory); // A drag source that takes no extra formats: no text.
        }
    }

    // ---------- Interop ----------

    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DROPDESCRIPTION
    {
        public int type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szMessage;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szInsert;
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr targetWindow, [MarshalAs(UnmanagedType.Interface)] ComDataObject data, ref POINT point, int effect);
        void DragLeave();
        void DragOver(ref POINT point, int effect);
        void Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data, ref POINT point, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, out IShellItemArray array);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr window, [MarshalAs(UnmanagedType.Interface)] ComDataObject data, IntPtr dropSource, uint effects, out uint effect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
