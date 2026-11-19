using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Chat;

/// <summary>A picture in a message: what the chat shows and its file.</summary>
public sealed record ChatPicture(ImageSource Image, string Path);

/// <summary>An attached file in a message: its name and the copy kept with the chat.</summary>
public sealed record ChatFileChip(string Name, string Path);

/// <summary>A picture for the next message: its file bytes, the file type and a small preview.</summary>
public sealed record PendingImage(byte[] Bytes, string Extension, ImageSource Preview);

/// <summary>
/// Pictures in GemmaChat: from the clipboard (a screenshot: the "PNG" format when there, else the bitmap) or from
/// picture files. Pictures larger than <see cref="MaxEdge"/> pixels on their long side are made smaller (the AIs
/// scale them down anyway); a PNG over 3.5 MB becomes a JPEG. Saved next to the chat (data\gemmachat\images\{chat}\),
/// not inside the chat file, and sent as base64 with each request.
/// </summary>
public static class ChatImages
{
    public const int MaxEdge = 2000;
    private const int MaxPngBytes = 3_500_000;
    private static readonly HashSet<string> PictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    public static bool IsPicture(string path) => PictureTypes.Contains(Path.GetExtension(path));

    /// <summary>The picture in the clipboard, or null.</summary>
    public static PendingImage? FromClipboard()
    {
        try
        {
            // Screenshots (Win+Shift+S, most tools) also put a PNG there: it keeps the colors and transparency exactly.
            if (Clipboard.GetData("PNG") is MemoryStream png && Decode(png) is { } fromPng)
                return FromBitmap(fromPng);
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
                return FromBitmap(bitmap);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or IOException
                                       or InvalidOperationException or ArgumentException)
        {
            // The clipboard is busy or holds something else.
        }
        return null;
    }

    /// <summary>A picture file, or null when it is no picture WPF can read.</summary>
    public static PendingImage? FromFile(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            if (Decode(stream) is not { } bitmap)
                return null;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            // PNG, JPEG and GIF of a fitting size go as they are; anything else (BMP, TIFF, WebP, large) as PNG / JPEG.
            if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" && Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) <= MaxEdge
                && bytes.Length <= MaxPngBytes)
                return new PendingImage(bytes, extension == ".jpeg" ? ".jpg" : extension, Preview(bitmap));
            return FromBitmap(bitmap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(Stream stream)
    {
        try
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A bitmap as PNG (JPEG when the PNG is too large), smaller when its long side is over MaxEdge.</summary>
    private static PendingImage FromBitmap(BitmapSource bitmap)
    {
        int edge = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (edge > MaxEdge)
        {
            double scale = (double)MaxEdge / edge;
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        }
        // 96 DPI: the AI sees pixels, and the preview is not scaled by the screen's DPI.
        if (Math.Abs(bitmap.DpiX - 96) > 0.5)
        {
            var copy = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int stride = copy.PixelWidth * 4;
            var pixels = new byte[stride * copy.PixelHeight];
            copy.CopyPixels(pixels, stride, 0);
            bitmap = BitmapSource.Create(copy.PixelWidth, copy.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }
        bitmap.Freeze();
        byte[] bytes = Encode(new PngBitmapEncoder(), bitmap);
        string extension = ".png";
        if (bytes.Length > MaxPngBytes)
        {
            bytes = Encode(new JpegBitmapEncoder { QualityLevel = 90 }, new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0));
            extension = ".jpg";
        }
        return new PendingImage(bytes, extension, Preview(bitmap));
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A small picture for the strip above the input (and the message): at most 120 pixels high.</summary>
    private static ImageSource Preview(BitmapSource bitmap)
    {
        if (bitmap.PixelHeight <= 120)
            return bitmap;
        double scale = 120.0 / bitmap.PixelHeight;
        var small = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }

    /// <summary>A saved picture of a message for the chat (decoded small), or null when the file is gone.</summary>
    public static ImageSource? Load(string name, int height)
    {
        string path = ChatStore.ImagePath(name);
        if (!File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad; // The file is not kept open.
            image.DecodePixelHeight = height;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>A saved picture for the API: its media type and the bytes as base64, or null when the file is gone.</summary>
    public static (string MediaType, string Base64)? ForApi(string name)
    {
        string path = ChatStore.ImagePath(name);
        try
        {
            if (!File.Exists(path))
                return null;
            string type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png",
            };
            return (type, Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
