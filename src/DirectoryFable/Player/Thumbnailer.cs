using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DirectoryFable.Player;

/// <summary>
/// Small pictures of a video at evenly spread times, for OpusHC's filmstrip above the timeline. A second VLC player
/// without sound and subtitles opens the same file, has VLC scale its picture to <see cref="Width"/> pixels, and jumps
/// from point to point (to the nearest key frame: fast), taking the first picture after each jump. The points come
/// in an order that fills the strip evenly (first, middle, quarters, ...). Nothing is written to disk.
/// </summary>
internal sealed class Thumbnailer(LibVLC vlc)
{
    public const int Width = 240;

    /// <summary>
    /// Makes <paramref name="count"/> pictures of a video of <paramref name="lengthMs"/> milliseconds; each one goes to
    /// <paramref name="ready"/> (frozen, from a background thread) as soon as it is there.
    /// </summary>
    public Task RunAsync(string path, long lengthMs, int count, Action<int, BitmapSource> ready, CancellationToken token) =>
        Task.Run(() => Run(path, lengthMs, count, ready, token), token);

    private void Run(string path, long lengthMs, int count, Action<int, BitmapSource> ready, CancellationToken token)
    {
        if (lengthMs <= 0 || count <= 0)
            return;
        var gate = new Lock();
        IntPtr buffer = IntPtr.Zero;
        int width = 0, height = 0, stride = 0;
        int frames = 0;
        using var frameShown = new ManualResetEventSlim(false);

        MediaPlayer.LibVLCVideoFormatCb format = (ref IntPtr opaque, IntPtr chroma, ref uint w, ref uint h, ref uint pitches, ref uint lines) =>
        {
            Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
            double aspect = w > 0 && h > 0 ? (double)w / h : 16.0 / 9;
            w = Width;
            h = (uint)Math.Max(2, Math.Round(Width / aspect));
            pitches = w * 4;
            lines = h;
            lock (gate)
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)(pitches * lines));
                (width, height, stride) = ((int)w, (int)h, (int)pitches);
            }
            return 1;
        };
        MediaPlayer.LibVLCVideoCleanupCb cleanup = (ref IntPtr opaque) => { };
        MediaPlayer.LibVLCVideoLockCb lockFrame = (opaque, planes) =>
        {
            lock (gate)
                Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoDisplayCb display = (opaque, picture) =>
        {
            Interlocked.Increment(ref frames);
            frameShown.Set();
        };

        using var media = new Media(vlc, path, FromType.FromPath);
        media.AddOption(":no-audio");
        media.AddOption(":no-spu");
        media.AddOption(":no-sub-autodetect-file");
        media.AddOption(":input-fast-seek");
        using var player = new MediaPlayer(vlc) { EnableHardwareDecoding = false };
        player.SetVideoFormatCallbacks(format, cleanup);
        player.SetVideoCallbacks(lockFrame, null, display);
        try
        {
            player.Play(media);
            if (!frameShown.Wait(TimeSpan.FromSeconds(8), token))
                return; // No picture (an audio file, a broken file).
            foreach (int index in FillOrder(count))
            {
                token.ThrowIfCancellationRequested();
                long target = (long)((index + 0.5) / count * lengthMs);
                int before = Volatile.Read(ref frames);
                frameShown.Reset();
                player.Time = target;
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(2))
                {
                    frameShown.Wait(TimeSpan.FromMilliseconds(150), token);
                    frameShown.Reset();
                    // The first picture after the jump (not one from before it), close to the wanted time.
                    if (Volatile.Read(ref frames) > before && clock.ElapsedMilliseconds >= 60 && Math.Abs(player.Time - target) < 5000)
                    {
                        if (Capture() is { } image)
                            ready(index, image);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Another video, or the player closed.
        }
        finally
        {
            player.Stop();
            lock (gate)
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
            GC.KeepAlive(format);
            GC.KeepAlive(cleanup);
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(display);
        }

        BitmapSource? Capture()
        {
            lock (gate)
            {
                if (buffer == IntPtr.Zero || width <= 0 || height <= 0)
                    return null;
                var pixels = new byte[stride * height];
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
                image.Freeze();
                return image;
            }
        }
    }

    /// <summary>0, the middle, the quarters, the eighths, ...: the strip fills evenly from the start.</summary>
    private static IEnumerable<int> FillOrder(int count)
    {
        var done = new bool[count];
        for (int step = HighestPowerOfTwo(count); step >= 1; step /= 2)
        {
            for (int i = 0; i < count; i += step)
            {
                if (done[i])
                    continue;
                done[i] = true;
                yield return i;
            }
        }
    }

    private static int HighestPowerOfTwo(int n)
    {
        int p = 1;
        while (p * 2 <= n)
            p *= 2;
        return p;
    }
}
