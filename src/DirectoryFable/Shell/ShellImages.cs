using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Shell;

/// <summary>Large shell icons (48 px, Windows' "extra large" system image list), for the drives of This PC.</summary>
public static class ShellImages
{
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int SHIL_EXTRALARGE = 2;
    private const int ILD_TRANSPARENT = 0x1;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The 48 px icon of a file, folder or drive (UI thread); null when Windows has none.</summary>
    public static ImageSource? ExtraLarge(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
            return cached;
        ImageSource? image = null;
        try
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX) != IntPtr.Zero)
            {
                Guid iid = typeof(IImageList).GUID;
                if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) == 0)
                {
                    IntPtr icon = IntPtr.Zero;
                    if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref icon) == 0 && icon != IntPtr.Zero)
                    {
                        try
                        {
                            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                            image = source;
                        }
                        finally
                        {
                            DestroyIcon(icon);
                        }
                    }
                    Marshal.ReleaseComObject(list);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No large icon: the caller shows none.
        }
        // Drive icons can change (a disc is inserted): only folders and files are cached.
        if (path.Length > 3)
            Cache[path] = image;
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // Only the methods up to GetIcon are used; their order must match commoncontrols.h.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
