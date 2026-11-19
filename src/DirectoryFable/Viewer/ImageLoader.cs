using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Viewer;

/// <summary>What AstraViewer shows below the thumbnails (the size as shown, after the EXIF orientation).</summary>
public sealed record ImageDetails(
    int PixelWidth, int PixelHeight, int BitsPerPixel, double DpiX, string Format, int Frames,
    string? DateTaken, string? Camera, long FileSize, DateTime Modified, string? FramesNote);

/// <summary>A decoded image. <see cref="Reduced"/>: very large images are decoded smaller than their real size.</summary>
public sealed record LoadedImage(BitmapSource Image, ImageDetails Details, bool Reduced);

/// <summary>
/// Decodes images for AstraViewer with Windows' own codecs (WIC), off the UI thread. The file
/// is read into memory first, so it is never kept open or locked. JPEG / TIFF orientation
/// (EXIF) is applied; icons show their largest size; multi-page images show the first page.
/// Errors come back as exceptions; callers show them as text (a broken image never crashes the app).
/// </summary>
public static class ImageLoader
{
    private const long MaxFileBytes = 1L << 30;      // 1 GB
    private const long MaxPixels = 150_000_000;     // Above this, the image is decoded smaller (memory).

    public static Task<LoadedImage> LoadAsync(string path, CancellationToken token) =>
        Task.Run(() => Load(path, token), token);

    private static LoadedImage Load(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The file no longer exists.", path);
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException($"The file is too large for AstraViewer ({DisplayFormat.Size(file.Length)}).");
        byte[] bytes = File.ReadAllBytes(path);
        token.ThrowIfCancellationRequested();

        var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var first = decoder.Frames[0];
        int orientation = Orientation(first);
        long pixels = (long)first.PixelWidth * first.PixelHeight;
        bool reduced = pixels > MaxPixels;

        BitmapSource image;
        BitmapSource sized = first; // The frame whose size and depth are shown.
        bool icon = IsIcon(path);
        if (icon)
        {
            image = sized = LargestFrame(bytes);
            orientation = 1;
        }
        else
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes, writable: false);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (reduced)
                bitmap.DecodePixelWidth = (int)(first.PixelWidth * Math.Sqrt((double)MaxPixels / pixels));
            bitmap.EndInit();
            bitmap.Freeze();
            image = Orient(bitmap, orientation);
        }
        token.ThrowIfCancellationRequested();

        bool turned = orientation >= 5;
        var meta = Metadata(first);
        int frames = FrameCount(decoder);
        string? framesNote = frames <= 1 ? null : icon ? $"{frames} sizes (largest shown)" : $"{frames} frames (first shown)";
        var details = new ImageDetails(
            turned ? sized.PixelHeight : sized.PixelWidth,
            turned ? sized.PixelWidth : sized.PixelHeight,
            sized.Format.BitsPerPixel, sized.DpiX, FormatName(decoder, path), frames,
            meta.DateTaken, meta.Camera, file.Length, file.LastWriteTime, framesNote);
        return new LoadedImage(image, details, reduced);
    }

    /// <summary>A small picture (about <paramref name="height"/> device pixels high) for the thumbnail strip and the history.</summary>
    public static BitmapSource LoadThumbnail(string path, int height)
    {
        if (ImageFileTypes.IsPdf(path))
            return PdfFile.FirstPage(path, height);
        byte[] bytes = File.ReadAllBytes(path);
        if (IsIcon(path))
            return LargestFrame(bytes);

        var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var first = decoder.Frames[0];
        int orientation = Orientation(first);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes, writable: false);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // Decoded small at once (fast for JPEG); a turned photo is decoded by its other side.
        if ((orientation >= 5 ? first.PixelWidth : first.PixelHeight) > height)
        {
            if (orientation >= 5)
                bitmap.DecodePixelWidth = height;
            else
                bitmap.DecodePixelHeight = height;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return Orient(bitmap, orientation);
    }

    private static bool IsIcon(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ico" or ".cur";

    /// <summary>Icons hold several sizes: the largest (and deepest) one.</summary>
    private static BitmapSource LargestFrame(byte[] bytes)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).First();
        best.Freeze();
        return best;
    }

    /// <summary>EXIF orientation 1-8 (1 = as stored). Unknown or broken metadata counts as 1.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata)
                return 1;
            foreach (string query in (string[])["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"])
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                {
                    int number = value switch
                    {
                        ushort u => u,
                        short s => s,
                        int i => i,
                        uint u => (int)Math.Min(u, 9),
                        _ => 1,
                    };
                    return number is >= 1 and <= 8 ? number : 1;
                }
            }
        }
        catch (Exception)
        {
            // Metadata is optional: any problem reading it means "as stored".
        }
        return 1;
    }

    private static BitmapSource Orient(BitmapSource image, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (transform is null)
            return image;
        var turned = new TransformedBitmap(image, transform);
        turned.Freeze();
        return turned;
    }

    private static (string? DateTaken, string? Camera) Metadata(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                string? camera = string.Join(" ", new[] { metadata.CameraManufacturer, metadata.CameraModel }
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct());
                return (metadata.DateTaken, camera.Length > 0 ? camera : null);
            }
        }
        catch (Exception)
        {
            // Optional details only.
        }
        return (null, null);
    }

    private static int FrameCount(BitmapDecoder decoder)
    {
        try
        {
            return decoder.Frames.Count;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>"JPEG", "PNG", "WebP" ... from the codec ("JPEG Decoder"), else the extension.</summary>
    private static string FormatName(BitmapDecoder decoder, string path)
    {
        try
        {
            string? name = decoder.CodecInfo?.FriendlyName;
            if (!string.IsNullOrWhiteSpace(name))
                return name.Replace(" Decoder", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        catch (Exception)
        {
        }
        return Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
    }
}
