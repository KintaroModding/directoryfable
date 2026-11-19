using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Shell;

/// <summary>
/// Small icons and type names from the Windows shell, cached by extension.
/// Must be called on the UI thread.
/// </summary>
public static partial class ShellInfo
{
    private const string FolderKey = "<folder>";

    private static readonly Dictionary<string, (ImageSource? Icon, string TypeName)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path, bool isDirectory) => Get(path, isDirectory).Icon;

    public static string GetTypeName(string path, bool isDirectory) => Get(path, isDirectory).TypeName;

    private static readonly Dictionary<string, ImageSource?> FolderIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folder's own icon (Desktop, Downloads, drives and folders with desktop.ini have
    /// special ones), cached per path. Network paths get the generic folder icon, so an
    /// offline server cannot block the UI.
    /// </summary>
    public static ImageSource? GetFolderIcon(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return GetNetworkIcon(path) ?? GetIcon(path, isDirectory: true);
        if (!Directory.Exists(path))
            return GetIcon(path, isDirectory: true);
        if (FolderIconCache.TryGetValue(path, out var cached))
            return cached;

        ImageSource? icon = null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON) != IntPtr.Zero &&
            info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                icon = bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        icon ??= GetIcon(path, isDirectory: true);
        FolderIconCache[path] = icon;
        return icon;
    }

    /// <summary>
    /// Folder icon without touching the disk: network icons for a server or share root,
    /// the generic folder icon for everything else (fast; used for tree and list rows).
    /// </summary>
    public static ImageSource? GetFolderIconFast(string path) =>
        (path.StartsWith(@"\\", StringComparison.Ordinal) ? GetNetworkIcon(path) : null)
        ?? GetIcon(path, isDirectory: true);

    private static readonly Dictionary<uint, ImageSource?> StockIconCache = [];

    /// <summary>
    /// "\\SERVER" → the network computer icon, "\\server\share" → the network share icon
    /// (Windows stock icons, as in Explorer). Folders inside a share: null (normal folder).
    /// </summary>
    public static ImageSource? GetNetworkIcon(string path)
    {
        string trimmed = path.TrimEnd('\\');
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        int parts = trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        return parts switch
        {
            1 => GetStockIcon(SIID_SERVER),
            2 => GetStockIcon(ShareIconId),
            _ => null,
        };
    }

    private static ImageSource? GetStockIcon(uint id)
    {
        if (StockIconCache.TryGetValue(id, out var cached))
            return cached;

        ImageSource? icon = null;
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(id, SHGSI_ICON | SHGSI_SMALLICON, ref info) == 0 && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                icon = bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        StockIconCache[id] = icon;
        return icon;
    }

    private const uint SIID_SERVER = 15;
    // Shares use stock icon 13 (SIID_WORLD, shown as a monitor on Windows 11), chosen after a
    // preview of the network stock icons.
    private const uint ShareIconId = 13; // SIID_WORLD
    private const uint SHGSI_ICON = 0x000000100;
    private const uint SHGSI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    private static readonly Dictionary<int, ImageSource?> SpecialFolderIcons = [];

    /// <summary>The "This PC" icon (a virtual folder: looked up by its shell id, not a path).</summary>
    public static ImageSource? GetThisPcIcon() => GetSpecialFolderIcon(CSIDL_DRIVES);

    /// <summary>The "Network" folder icon (the folder tree's Network group).</summary>
    public static ImageSource? GetNetworkFolderIcon() => GetSpecialFolderIcon(CSIDL_NETWORK);

    private static ImageSource? GetSpecialFolderIcon(int csidl)
    {
        if (SpecialFolderIcons.TryGetValue(csidl, out var cached) && cached is not null)
            return cached;
        if (SHGetSpecialFolderLocation(IntPtr.Zero, csidl, out IntPtr pidl) != 0)
            return null;
        try
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON | SHGFI_SMALLICON) == IntPtr.Zero ||
                info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return SpecialFolderIcons[csidl] = bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const int CSIDL_DRIVES = 0x0011;
    private const int CSIDL_NETWORK = 0x0012;
    private const uint SHGFI_PIDL = 0x000000008;

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr ppidl);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private static (ImageSource? Icon, string TypeName) Get(string path, bool isDirectory)
    {
        string key = isDirectory ? FolderKey : Path.GetExtension(path);
        if (Cache.TryGetValue(key, out var hit))
            return hit;

        // SHGFI_USEFILEATTRIBUTES: do not touch the disk, look up by name/attributes only.
        // This is fast, but .exe/.lnk/.ico files get a generic icon (refined later).
        string probe = isDirectory ? "folder" : "file" + key;
        uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES;

        ImageSource? icon = null;
        // "File Folder" (Windows says "File folder"); also the group divider name.
        string typeName = isDirectory ? "File Folder" : FallbackTypeName(key);

        if (SHGetFileInfo(probe, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero)
        {
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon = bmp;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }

            if (!isDirectory && !string.IsNullOrWhiteSpace(info.szTypeName))
                typeName = info.szTypeName;
        }

        var result = (icon, typeName);
        Cache[key] = result;
        return result;
    }

    private static string FallbackTypeName(string extension) =>
        string.IsNullOrEmpty(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant() + " File";

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_TYPENAME = 0x000000400;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
