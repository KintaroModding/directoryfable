using System.IO;
using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>One section of the properties window ("File", "Image", "Origin", ...).</summary>
public sealed record PropertyGroup(string Name, IReadOnlyList<PropertyLine> Lines);

public sealed record PropertyLine(string Name, string Value);

/// <summary>
/// The same properties as the "Details" tab of Windows' file properties: Windows keeps a
/// "full details" property list per file type (System.PropList.FullDetails), so images,
/// music or documents show their own fields. Values are formatted by Windows
/// (PSFormatForDisplay), empty ones are left out. Runs on its own STA thread (property
/// handlers can be slow, e.g. for videos).
/// </summary>
public static class FileProperties
{
    public static Task<List<PropertyGroup>> ReadAsync(string path)
    {
        var tcs = new TaskCompletionSource<List<PropertyGroup>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Read(path));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "FileProperties",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static List<PropertyGroup> Read(string path)
    {
        Guid itemIid = typeof(IShellItem2).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemIid, out IShellItem2 item);
        try
        {
            Guid storeIid = typeof(IPropertyStore).GUID;
            item.GetPropertyStore(GPS_BESTEFFORT, ref storeIid, out IPropertyStore store);
            Guid listIid = typeof(IPropertyDescriptionList).GUID;
            var fullDetails = PKEY_PropList_FullDetails;
            item.GetPropertyDescriptionList(ref fullDetails, ref listIid, out IPropertyDescriptionList list);
            try
            {
                return ReadList(store, list);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
                Marshal.ReleaseComObject(store);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static List<PropertyGroup> ReadList(IPropertyStore store, IPropertyDescriptionList list)
    {
        var groups = new List<PropertyGroup>();
        string groupName = "";
        var lines = new List<PropertyLine>();
        Guid descIid = typeof(IPropertyDescription).GUID;

        list.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            IPropertyDescription? description = null;
            try
            {
                list.GetAt(i, ref descIid, out description);
                string canonical = TakeString(description.GetCanonicalName) ?? "";
                string label = TryTakeString(description.GetDisplayName) ?? canonical;

                // "System.PropGroup.*" entries are the section headers.
                if (canonical.StartsWith("System.PropGroup.", StringComparison.Ordinal))
                {
                    if (lines.Count > 0)
                        groups.Add(new PropertyGroup(groupName, lines));
                    groupName = label;
                    lines = [];
                    continue;
                }

                description.GetPropertyKey(out PropertyKey key);
                if (FormatValue(store, key) is { Length: > 0 } value)
                    lines.Add(new PropertyLine(label, value));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                // One property that cannot be read must not hide the others.
            }
            finally
            {
                if (description is not null)
                    Marshal.ReleaseComObject(description);
            }
        }
        if (lines.Count > 0)
            groups.Add(new PropertyGroup(groupName, lines));
        return groups;
    }

    /// <summary>The value as Windows shows it, or null when empty.</summary>
    private static string? FormatValue(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out PropVariant value);
        try
        {
            if (value.vt == 0) // VT_EMPTY
                return null;
            if (PSFormatForDisplayAlloc(ref key, ref value, PDFF_DEFAULT, out IntPtr text) < 0 || text == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(text)?.Trim();
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private delegate void StringGetter(out IntPtr text);

    private static string? TakeString(StringGetter getter)
    {
        getter(out IntPtr text);
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string? TryTakeString(StringGetter getter)
    {
        try
        {
            return TakeString(getter);
        }
        catch (COMException)
        {
            return null; // Some properties have no display name.
        }
    }

    // ---------- Interop ----------

    private const int GPS_BESTEFFORT = 0x40;
    private const int PDFF_DEFAULT = 0;

    // System.PropList.FullDetails: the list behind the "Details" tab.
    private static readonly PropertyKey PKEY_PropList_FullDetails = new(new Guid("C9944A21-A406-48FE-8225-AEC7E24C211B"), 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    /// <summary>PROPVARIANT: only passed on to Windows (format, clear), never read here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr value1;
        public IntPtr value2;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

    [DllImport("propsys.dll")]
    private static extern int PSFormatForDisplayAlloc(ref PropertyKey key, ref PropVariant propvar, int pdff, out IntPtr ppszDisplay);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    // Method order must match shobjidl_core.h / propsys.h exactly (only the used prefix is declared).
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IntPtr ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IntPtr psi, uint hint, out int piOrder);
        // IShellItem2
        void GetPropertyStore(int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PropertyKey keyType, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyDescriptionList ppv);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [ComImport, Guid("1f9fc1d0-c39b-4b26-817f-011967d3440e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyDescriptionList
    {
        void GetCount(out uint pcElem);
        void GetAt(uint iElem, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyDescription ppv);
    }

    [ComImport, Guid("6f79d558-3e96-4549-a1d1-7d75d2288814"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyDescription
    {
        void GetPropertyKey(out PropertyKey pkey);
        void GetCanonicalName(out IntPtr ppszName);
        void GetPropertyType(out ushort pvartype);
        void GetDisplayName(out IntPtr ppszName);
    }
}
