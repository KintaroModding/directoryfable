using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// An Image shows an .ico's first picture (usually 16 px) and scales it, which looks blurry at any other size or at
/// a display scale above 100 %. These helpers pick the icon's own picture for the size on screen instead.
/// </summary>
public static class IconFrames
{
    /// <summary>The smallest picture of a multi-picture icon that is at least <paramref name="pixels"/> wide (else the largest).</summary>
    public static ImageSource Best(ImageSource source, double pixels)
    {
        if (source is not BitmapFrame { Decoder: { } decoder } || decoder.Frames.Count < 2)
            return source;
        return decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= pixels)
               ?? decoder.Frames.MaxBy(f => f.PixelWidth)!;
    }

    /// <summary>Gives an Image with a fixed width the picture of its icon that fits its size in device pixels.</summary>
    public static void Sharpen(Image image)
    {
        if (image.Source is null || double.IsNaN(image.Width))
            return;
        double pixels = image.Width * VisualTreeHelper.GetDpi(image).DpiScaleX;
        image.Source = Best(image.Source, Math.Ceiling(pixels));
    }
}
