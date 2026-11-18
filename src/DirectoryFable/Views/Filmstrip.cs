using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Views;

/// <summary>
/// OpusHC's filmstrip above the timeline (a minimap of the video): the whole video as a row of small pictures, each
/// in its own slot of time, so the scenes can be seen at a glance. The pictures arrive one by one
/// (<see cref="Player.Thumbnailer"/>); an empty slot is dark. Pointing at it tells the time under the mouse; a click
/// jumps there.
/// </summary>
public sealed class Filmstrip : FrameworkElement
{
    private BitmapSource?[] _pictures = [];
    private static readonly Brush Empty = Frozen(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Pen Gap = FrozenPen(Color.FromRgb(0x10, 0x10, 0x10), 1);

    /// <summary>The mouse is over the strip: the share of the video under it (0..1), or null when it left.</summary>
    public event Action<double?>? Hovered;

    /// <summary>A click: jump to this share of the video (0..1).</summary>
    public event Action<double>? Clicked;

    public int Count => _pictures.Length;

    /// <summary>A new video: <paramref name="count"/> empty slots.</summary>
    public void Reset(int count)
    {
        _pictures = new BitmapSource?[count];
        InvalidateVisual();
    }

    /// <summary>The slots of a video met before (the pictures are kept).</summary>
    public void Show(BitmapSource?[] pictures)
    {
        _pictures = pictures;
        InvalidateVisual();
    }

    public void Set(int index, BitmapSource picture)
    {
        if (index < 0 || index >= _pictures.Length)
            return;
        _pictures[index] = picture;
        InvalidateVisual();
    }

    /// <summary>The picture nearest to a share of the video (for the bigger preview), or null.</summary>
    public BitmapSource? PictureAt(double share)
    {
        if (_pictures.Length == 0)
            return null;
        int index = Math.Clamp((int)(share * _pictures.Length), 0, _pictures.Length - 1);
        // The nearest one that is there already (the strip fills from the middle outwards).
        for (int d = 0; d < _pictures.Length; d++)
        {
            if (index - d >= 0 && _pictures[index - d] is { } before)
                return before;
            if (index + d < _pictures.Length && _pictures[index + d] is { } after)
                return after;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); // The whole strip takes the mouse.
        if (_pictures.Length == 0 || width < 2 || height < 2)
            return;
        double slot = width / _pictures.Length;
        for (int i = 0; i < _pictures.Length; i++)
        {
            var rect = new Rect(i * slot, 0, slot, height);
            if (_pictures[i] is not { } picture)
            {
                dc.DrawRectangle(Empty, null, rect);
                continue;
            }
            // Fill the slot (the middle of the picture), keeping its shape.
            double scale = Math.Max(rect.Width / picture.PixelWidth, rect.Height / picture.PixelHeight);
            double w = picture.PixelWidth * scale, h = picture.PixelHeight * scale;
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawImage(picture, new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h));
            dc.Pop();
            dc.DrawLine(Gap, rect.TopRight, rect.BottomRight);
        }
    }

    private double ShareAt(Point point) => ActualWidth > 0 ? Math.Clamp(point.X / ActualWidth, 0, 1) : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Hovered?.Invoke(ShareAt(e.GetPosition(this)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Hovered?.Invoke(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        Clicked?.Invoke(ShareAt(e.GetPosition(this)));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }
}
