using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Viewer;

/// <summary>
/// Writes WebP files for AstraViewer with Google's libwebp (Windows can read WebP but has no encoder for it).
/// Lossy (a quality from 0 to 100, with transparency) or lossless.
/// </summary>
public static class WebPEncoder
{
    /// <summary>WebP's own limit per side.</summary>
    public const int MaxSide = 16383;

    public const float DefaultQuality = 90;

    /// <summary>The picture as the bytes of a .webp file.</summary>
    public static unsafe byte[] Encode(BitmapSource source, bool lossless, float quality = DefaultQuality)
    {
        if (source.PixelWidth > MaxSide || source.PixelHeight > MaxSide)
            throw new InvalidDataException($"WebP pictures can be at most {MaxSide:N0} pixels wide and high.");
        // libwebp wants plain (not premultiplied) BGRA.
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[(long)stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        IntPtr output = IntPtr.Zero;
        try
        {
            nuint size;
            fixed (byte* first = pixels)
            {
                size = lossless
                    ? WebPEncodeLosslessBGRA((IntPtr)first, width, height, stride, out output)
                    : WebPEncodeBGRA((IntPtr)first, width, height, stride, Math.Clamp(quality, 0, 100), out output);
            }
            if (size == 0 || output == IntPtr.Zero)
                throw new InvalidDataException("The picture could not be written as WebP.");
            var data = new byte[(int)size];
            Marshal.Copy(output, data, 0, data.Length);
            return data;
        }
        finally
        {
            if (output != IntPtr.Zero)
                WebPFree(output);
        }
    }

    /// <summary>
    /// What a .webp file is: lossless (a VP8L picture) and animated (it has animation frames; AstraViewer shows and would
    /// save only the first one). A file that cannot be read counts as lossy and not animated.
    /// </summary>
    public static (bool Lossless, bool Animated) Describe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[12];
            if (stream.Read(header, 0, 12) != 12 || header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' ||
                header[8] != 'W' || header[9] != 'E' || header[10] != 'B' || header[11] != 'P')
                return (false, false);
            bool lossless = false, animated = false;
            var chunk = new byte[8];
            // The chunks of the RIFF container: a name of four letters, a length, the data (padded to an even length).
            for (int guard = 0; guard < 10000 && stream.Read(chunk, 0, 8) == 8; guard++)
            {
                string name = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);
                uint length = BitConverter.ToUInt32(chunk, 4);
                if (name == "VP8L")
                    lossless = true;
                if (name is "ANIM" or "ANMF")
                    animated = true;
                if (name is "VP8 " or "VP8L" or "ANMF")
                    break; // The picture data: nothing more to learn.
                stream.Seek(length + (length & 1), SeekOrigin.Current);
            }
            return (lossless, animated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, false);
        }
    }

    public static bool IsWebP(string path) => string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);

    [DllImport("libwebp")]
    private static extern nuint WebPEncodeBGRA(IntPtr bgra, int width, int height, int stride, float quality, out IntPtr output);

    [DllImport("libwebp")]
    private static extern nuint WebPEncodeLosslessBGRA(IntPtr bgra, int width, int height, int stride, out IntPtr output);

    [DllImport("libwebp")]
    private static extern void WebPFree(IntPtr pointer);
}
