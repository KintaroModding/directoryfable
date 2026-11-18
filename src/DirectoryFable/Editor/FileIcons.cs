using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Editor;

/// <summary>
/// An icon the user can give a file in the Notepad# list (right-click, Icon). Its pictures are
/// Assets/NotepadIcons/&lt;id&gt;-&lt;size&gt;.png (16, 20, 24, 32, 40, 48, 64 px); while they are missing, a symbol of
/// the icon font in the icon's color stands in.
/// </summary>
public sealed class FileIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64];
    private readonly Dictionary<int, ImageSource?> _images = [];

    internal FileIcon(string id, string name, string glyph, string color)
    {
        Id = id;
        Name = name;
        Glyph = glyph;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Brush = brush;
    }

    public string Id { get; }

    /// <summary>Shown as the tooltip in the icon menu.</summary>
    public string Name { get; }

    /// <summary>Stand-in symbol (icon font) while the picture is missing.</summary>
    public string Glyph { get; }

    public Brush Brush { get; }

    /// <summary>The picture for the list row (16 DIP); null while the picture files are missing.</summary>
    public ImageSource? Image => ImageFor(16);

    public bool HasImage => Image is not null;

    /// <summary>
    /// The picture for <paramref name="dip"/> DIP: the smallest file at least that many screen pixels wide (so it
    /// is never enlarged), else the largest there is; null when there is none.
    /// </summary>
    public ImageSource? ImageFor(double dip)
    {
        double scale = Application.Current?.MainWindow is { } window ? VisualTreeHelper.GetDpi(window).DpiScaleX : 1;
        int pixels = (int)Math.Ceiling(dip * scale);
        if (_images.TryGetValue(pixels, out var cached))
            return cached;
        ImageSource? found = null;
        foreach (int size in Sizes.Where(s => s >= pixels).Concat(Sizes.Where(s => s < pixels).Reverse()))
        {
            if ((found = Load(size)) is not null)
                break;
        }
        _images[pixels] = found;
        return found;
    }

    private ImageSource? Load(int size)
    {
        var uri = new Uri($"pack://application:,,,/Assets/NotepadIcons/{Id}-{size}.png");
        try
        {
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null)
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return null; // Not built in (yet).
        }
    }
}

/// <summary>The icons of the Notepad# file list, in the order of the icon menu (6 per row).</summary>
public static class FileIcons
{
    public static IReadOnlyList<FileIcon> All { get; } =
    [
        new("star", "Star", "", "#FFC83D"),
        new("heart", "Heart", "", "#F25C7A"),
        new("flag", "Flag", "", "#F28B30"),
        new("bookmark", "Bookmark", "", "#4CA3F5"),
        new("pin", "Pin", "", "#E05252"),
        new("check", "Done", "", "#5CC46B"),
        new("warning", "Warning", "", "#F2C744"),
        new("bug", "Bug", "", "#E0663D"),
        new("idea", "Idea", "", "#FFD54A"),
        new("question", "Question", "", "#6AB0F3"),
        new("bolt", "Important", "", "#B38CF5"),
        new("eye", "Watch", "", "#7FB2F0"),
        new("gear", "Settings", "", "#9AA4AE"),
        new("key", "Key", "", "#E0B04A"),
        new("lock", "Locked", "", "#C9A04A"),
        new("code", "Code", "", "#4EC9B0"),
        new("terminal", "Terminal", "", "#9CDCFE"),
        new("folder", "Folder", "", "#E8B44A"),
        new("globe", "Web", "", "#4DB6E8"),
        new("book", "Book", "", "#8B6FD9"),
        new("calendar", "Calendar", "", "#E86A6A"),
        new("clock", "Later", "", "#66BBDD"),
        new("checklist", "To-do list", "", "#5CC46B"),
        new("package", "Package", "", "#C08850"),
    ];

    public static FileIcon? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
}
