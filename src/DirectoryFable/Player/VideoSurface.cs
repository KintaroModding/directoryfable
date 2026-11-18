using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DirectoryFable.Player;

/// <summary>
/// The picture of a VLC media player as a WPF image: VLC decodes each frame into a block of shared memory
/// (its "vmem" output, 32-bit BGRA), and an <see cref="InteropBitmap"/> on that memory shows it; after each frame
/// the bitmap is told to read it again (at most once per screen refresh). So the video is a normal WPF element:
/// the controls can lie on top of it and full screen is only a layout change. A new size (the next video) makes a
/// new block of memory.
/// </summary>
internal sealed class VideoSurface
{
    private readonly Dispatcher _ui;
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private Frame? _frame;   // The memory VLC writes into now.
    private int _pending;    // 1 while a redraw waits on the UI thread.

    /// <summary>One block of shared memory and the bitmap on it.</summary>
    private sealed class Frame(IntPtr section, IntPtr view, int width, int height, int stride)
    {
        public IntPtr Section = section;
        public IntPtr View = view;
        public int Width = width, Height = height, Stride = stride;
        public InteropBitmap? Bitmap;
    }

    public VideoSurface(Dispatcher ui)
    {
        _ui = ui;
        // Kept in fields: VLC calls them from its own threads, they must not be collected.
        _format = Format;
        _cleanup = Cleanup;
        _lock = Lock;
        _display = Display;
    }

    /// <summary>The picture to show (null = none); raised on the UI thread.</summary>
    public event Action<ImageSource?>? SourceChanged;

    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lock, null, _display);
    }

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4); // 32-bit BGRA.
        pitches = width * 4;
        lines = height;
        long size = (long)pitches * lines;
        IntPtr section = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04 /* PAGE_READWRITE */, (uint)(size >> 32), (uint)size, null);
        IntPtr view = section == IntPtr.Zero ? IntPtr.Zero : MapViewOfFile(section, 0xF001F /* FILE_MAP_ALL_ACCESS */, 0, 0, (UIntPtr)size);
        if (view == IntPtr.Zero)
        {
            if (section != IntPtr.Zero)
                CloseHandle(section);
            return 0; // No picture: VLC plays the sound only.
        }
        var frame = new Frame(section, view, (int)width, (int)height, (int)pitches);
        _frame = frame;
        _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_frame, frame))
                return;
            frame.Bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(frame.Section, frame.Width, frame.Height,
                PixelFormats.Bgr32, frame.Stride, 0);
            SourceChanged?.Invoke(frame.Bitmap);
        });
        return 1; // One picture buffer.
    }

    private void Cleanup(ref IntPtr opaque)
    {
        var frame = _frame;
        _frame = null;
        if (frame is null)
            return;
        // Freed on the UI thread, after the bitmap on it is gone.
        _ui.BeginInvoke(() =>
        {
            if (_frame is null)
                SourceChanged?.Invoke(null);
            frame.Bitmap = null;
            UnmapViewOfFile(frame.View);
            CloseHandle(frame.Section);
        });
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _frame?.View ?? IntPtr.Zero);
        return IntPtr.Zero;
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return; // A redraw is already waiting: it shows this frame.
        _ui.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            _frame?.Bitmap?.Invalidate();
        }, DispatcherPriority.Render);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
