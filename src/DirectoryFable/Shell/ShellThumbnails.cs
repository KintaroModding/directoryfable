using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Shell;

/// <summary>
/// Pictures for the file list's tile view, made by Windows itself (IShellItemImageFactory): the thumbnail a
/// thumbnail handler gives - videos, images, documents, as Windows' own windows show them, from Windows'
/// thumbnail cache - else the item's icon. Video files also get their length (System.Media.Duration).
/// Two STA threads take the newest requests first, so the tiles on screen come before those scrolled past;
/// a request whose tile has gone is dropped before it starts. The last 600 pictures stay in memory. The cache
/// is used from the UI thread only.
/// </summary>
public static class ShellThumbnails
{
    /// <summary>The picture (null: none), whether it is a real thumbnail (not an icon), and a video's length ("23:40").</summary>
    public sealed record Result(BitmapSource? Image, bool IsThumbnail, string? Duration);

    private const int MaxEntries = 600;
    private const int Threads = 2;

    private static readonly BlockingCollection<Action> Work = new(new ConcurrentStack<Action>());
    private static readonly Dictionary<string, (Result Value, LinkedListNode<string> Node)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new(); // Most recently used first.
    private static bool _started;

    /// <summary>
    /// The picture of an item at <paramref name="size"/> device pixels (the longer side; icons at
    /// <paramref name="iconSize"/>). Throws OperationCanceledException when <paramref name="token"/> is cancelled.
    /// </summary>
    public static async Task<Result?> GetAsync(string path, DateTime modified, int size, int iconSize, bool video, CancellationToken token)
    {
        string key = $"{path}|{size}|{iconSize}|{modified.Ticks}";
        if (Cache.TryGetValue(key, out var hit))
        {
            Order.Remove(hit.Node);
            Order.AddFirst(hit.Node);
            return hit.Value;
        }

        Start();
        var done = new TaskCompletionSource<Result?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            if (token.IsCancellationRequested)
            {
                done.TrySetCanceled(token);
                return;
            }
            try
            {
                done.TrySetResult(Make(path, size, iconSize, video));
            }
            catch (Exception)
            {
                done.TrySetResult(null); // Gone, locked, a broken handler: no picture (the tile stays empty).
            }
        });

        Result? result;
        using (token.Register(() => done.TrySetCanceled(token)))
            result = await done.Task;
        if (result is not null && !Cache.ContainsKey(key))
        {
            Cache[key] = (result, Order.AddFirst(key));
            while (Order.Count > MaxEntries && Order.Last is { } oldest)
            {
                Cache.Remove(oldest.Value);
                Order.RemoveLast();
            }
        }
        return result;
    }

    /// <summary>F5: changed files get new pictures.</summary>
    public static void Clear()
    {
        Cache.Clear();
        Order.Clear();
    }

    private static void Start()
    {
        if (_started)
            return;
        _started = true;
        for (int i = 0; i < Threads; i++)
        {
            var thread = new Thread(() =>
            {
                foreach (var job in Work.GetConsumingEnumerable())
                    job();
            })
            {
                IsBackground = true,
                Name = "Tile pictures",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.SetApartmentState(ApartmentState.STA); // Some thumbnail handlers need STA.
            thread.Start();
        }
    }

    // ---------- On a worker thread ----------

    private static Result Make(string path, int size, int iconSize, bool video)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            bool thumbnail = factory.GetImage(new NativeSize(size, size), SiigbfThumbnailOnly, out IntPtr bitmap) == 0;
            if (!thumbnail && factory.GetImage(new NativeSize(iconSize, iconSize), SiigbfIconOnly, out bitmap) != 0)
                bitmap = IntPtr.Zero;
            BitmapSource? image = bitmap != IntPtr.Zero ? ToBitmapSource(bitmap) : null;
            return new Result(image, thumbnail && image is not null, video ? Duration(path) : null);
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>A 32-bit HBITMAP (deleted here) as a frozen BitmapSource, top-down.</summary>
    private static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        try
        {
            if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0)
                return null;
            int width = info.bmWidth, height = Math.Abs(info.bmHeight);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Top-down.
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[width * height * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                    return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
            // Many thumbnails come with an alpha channel of 0 although they are opaque.
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }
            if (!anyAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 255;
            }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    /// <summary>"23:40" or "1:02:03" from the System.Media.Duration property; null when Windows does not know it.</summary>
    private static string? Duration(string path)
    {
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, typeof(IPropertyStore).GUID, out var store) != 0 || store is null)
            return null;
        try
        {
            var key = new PropertyKey { fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), pid = 3 };
            if (store.GetValue(ref key, out PropVariant value) != 0)
                return null;
            try
            {
                if (value.vt != VtUi8 || value.value == 0)
                    return null;
                var length = TimeSpan.FromTicks((long)value.value); // 100 ns units.
                return length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss");
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // ---------- Interop ----------

    private const int SiigbfIconOnly = 0x4;
    private const int SiigbfThumbnailOnly = 0x8;
    private const ushort VtUi8 = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public ulong value;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
