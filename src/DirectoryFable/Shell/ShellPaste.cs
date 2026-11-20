using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Threading;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DirectoryFable.Shell;

/// <summary>
/// Paste of clipboard files that have no path on a disk: files copied in a remote desktop or virtual machine
/// window, mail attachments, items of a phone or of a compressed folder. The clipboard then holds no file list
/// (CF_HDROP); the source app hands out the names ("FileGroupDescriptorW") and the contents ("FileContents") on
/// request. The folder's own drop target of Windows takes the clipboard's data object, the same as a paste in a
/// Windows file window: it reads the contents, shows progress for a long transfer and asks about name conflicts.
/// </summary>
public static class ShellPaste
{
    /// <summary>How a paste went. <see cref="Names"/>: the top-level names the clipboard listed (may be empty).</summary>
    public sealed record Result(bool Ok, IReadOnlyList<string> Names, string? Problem);

    private static readonly uint DescriptorWide = RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly uint DescriptorAnsi = RegisterClipboardFormat("FileGroupDescriptor");
    private static readonly uint ShellIdList = RegisterClipboardFormat("Shell IDList Array");

    /// <summary>True when the clipboard holds files or shell items that are not a plain list of paths.</summary>
    public static bool HasItems() =>
        IsClipboardFormatAvailable(DescriptorWide) || IsClipboardFormatAvailable(DescriptorAnsi) ||
        IsClipboardFormatAvailable(ShellIdList);

    /// <summary>
    /// Copies the clipboard's items into <paramref name="folder"/>. Returns when Windows has taken the job: a
    /// source that can work in the background (a remote session) is then still sending, with Windows' own
    /// progress window. Runs on its own thread, so a slow source does not hold the window. The progress window
    /// has no owner on purpose: an owner would be switched off for the whole transfer.
    /// </summary>
    public static Task<Result> PasteAsync(string folder) =>
        Worker.InvokeAsync(() => Paste(folder)).Task;

    // ---------- The worker thread ----------

    private static readonly Lock WorkerLock = new();
    private static Dispatcher? _worker;

    /// <summary>
    /// One thread with a message loop for all pastes, alive until the app ends: the clipboard's data object lives
    /// on it, and Windows still calls that object from its own copy thread after <see cref="Paste"/> returned.
    /// </summary>
    private static Dispatcher Worker
    {
        get
        {
            lock (WorkerLock)
            {
                if (_worker is not null)
                    return _worker;
                var ready = new TaskCompletionSource<Dispatcher>();
                var thread = new Thread(() =>
                {
                    OleInitialize(IntPtr.Zero);
                    ready.SetResult(Dispatcher.CurrentDispatcher);
                    Dispatcher.Run();
                })
                { IsBackground = true, Name = "Shell paste" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return _worker = ready.Task.Result;
            }
        }
    }

    private static Result Paste(string folder)
    {
        IntPtr folderId = IntPtr.Zero;
        IShellFolder? parent = null;
        IDropTarget? target = null;
        IReadOnlyList<string> names = [];
        try
        {
            if (GetClipboard() is not { } data)
                return new Result(false, names, "The clipboard is in use by another app.");
            names = TopLevelNames(data);

            int hr = SHParseDisplayName(folder, IntPtr.Zero, out folderId, 0, out _);
            if (hr < 0 || folderId == IntPtr.Zero)
                return new Result(false, names, Message(hr));
            Guid folderInterface = typeof(IShellFolder).GUID;
            hr = SHBindToParent(folderId, ref folderInterface, out parent, out IntPtr child);
            if (hr < 0 || parent is null)
                return new Result(false, names, Message(hr));
            Guid targetInterface = typeof(IDropTarget).GUID;
            hr = parent.GetUIObjectOf(IntPtr.Zero, 1, [child], ref targetInterface, IntPtr.Zero, out target);
            if (hr < 0 || target is null)
                return new Result(false, names, Message(hr));

            // Only "copy" is allowed: a paste from another computer or app never removes the source.
            int effect = DropEffectCopy;
            hr = target.DragEnter(data, LeftButton, default, ref effect);
            if (hr < 0 || (effect & DropEffectCopy) == 0)
            {
                target.DragLeave();
                return new Result(false, names, hr < 0 ? Message(hr) : "This folder does not take these clipboard items.");
            }
            effect = DropEffectCopy;
            hr = target.Drop(data, 0, default, ref effect);
            return hr < 0 ? new Result(false, names, Message(hr)) : new Result(true, names, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                   or ArgumentException or System.IO.IOException)
        {
            return new Result(false, names, ex.Message);
        }
        finally
        {
            if (target is not null)
                Marshal.ReleaseComObject(target);
            if (parent is not null)
                Marshal.ReleaseComObject(parent);
            if (folderId != IntPtr.Zero)
                Marshal.FreeCoTaskMem(folderId);
        }
    }

    private static string Message(int hr) =>
        Marshal.GetExceptionForHR(hr)?.Message ?? $"Windows error 0x{hr:X8}";

    /// <summary>The clipboard as a data object; another app can hold the clipboard for a moment.</summary>
    private static IComDataObject? GetClipboard()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (OleGetClipboard(out IComDataObject? data) >= 0 && data is not null)
                return data;
            Thread.Sleep(50);
        }
        return null;
    }

    /// <summary>
    /// The names of the listed files and folders that go straight into the target folder (no "sub\name"), read
    /// from the FILEGROUPDESCRIPTORW block. Empty when the clipboard has no such list (shell items) or it cannot
    /// be read; the paste itself does not need it.
    /// </summary>
    private static IReadOnlyList<string> TopLevelNames(IComDataObject data)
    {
        const int DescriptorSize = 592, NameOffset = 72, NameChars = 260;
        var names = new List<string>();
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)DescriptorWide),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        try
        {
            if (data.QueryGetData(ref format) != 0)
                return names;
            data.GetData(ref format, out STGMEDIUM medium);
            try
            {
                IntPtr block = medium.unionmember;
                long size = (long)GlobalSize(block);
                IntPtr start = GlobalLock(block);
                if (start == IntPtr.Zero)
                    return names;
                try
                {
                    int count = size >= 4 ? Marshal.ReadInt32(start) : 0;
                    for (int i = 0; i < count && 4 + (long)(i + 1) * DescriptorSize <= size; i++)
                    {
                        string name = Marshal.PtrToStringUni(start + 4 + i * DescriptorSize + NameOffset, NameChars);
                        int end = name.IndexOf('\0');
                        if (end >= 0)
                            name = name[..end];
                        if (name.Length > 0 && !name.Contains('\\') && !name.Contains('/'))
                            names.Add(name);
                    }
                }
                finally
                {
                    GlobalUnlock(block);
                }
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException
                                   or OutOfMemoryException or ArgumentException)
        {
            // No list: the paste still runs.
        }
        return names;
    }

    // ---------- Interop ----------

    private const int DropEffectCopy = 1;
    private const int LeftButton = 1; // MK_LBUTTON

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IComDataObject? data);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr block);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr block);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr block);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr id, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr id, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder? parent, out IntPtr child);

    // Method order must match ShObjIdl_core.h exactly.
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out IDropTarget? ppv);
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IComDataObject data, int keyState, POINTL point, ref int effect);
        [PreserveSig] int DragOver(int keyState, POINTL point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IComDataObject data, int keyState, POINTL point, ref int effect);
    }
}
