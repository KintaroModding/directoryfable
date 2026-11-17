using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Views;

/// <summary>
/// A see-through picture of a dragged item (favorite, tab) that follows the mouse,
/// so the user sees what is being dragged. Used for reordering inside the app.
/// </summary>
public sealed class DragGhost
{
    private readonly Popup _popup;
    private readonly Point _grab;

    /// <param name="source">The item being dragged (a snapshot of it is shown).</param>
    /// <param name="relativeTo">Element whose coordinates <see cref="Move"/> receives.</param>
    /// <param name="grabPoint">Where inside <paramref name="source"/> the mouse grabbed it.</param>
    public DragGhost(FrameworkElement source, FrameworkElement relativeTo, Point grabPoint)
    {
        _grab = grabPoint;

        // Snapshot in device pixels, so the picture is sharp at 125 % and other scalings.
        DpiScale dpi = VisualTreeHelper.GetDpi(source);
        int w = Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(source);
        bitmap.Freeze();

        var picture = new Border
        {
            Background = (Brush)Application.Current.FindResource("SubtleHoverBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("GroupChevronBrush"),
            BorderThickness = new Thickness(1),
            Opacity = 0.85,
            Child = new Image { Source = bitmap, Width = source.ActualWidth, Height = source.ActualHeight },
        };

        _popup = new Popup
        {
            Child = picture,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            PlacementTarget = relativeTo,
            Placement = PlacementMode.Relative,
        };
        _popup.Opened += (_, _) => MakeClickThrough(picture);
        _popup.IsOpen = true;
        MakeClickThrough(picture);
    }

    /// <summary>
    /// The picture's popup window sits right under the mouse. Without WS_EX_TRANSPARENT,
    /// Windows gives it the mouse messages, so the drop (button up) never reached the list
    /// and the drag stayed half done (seen in the Notepad# list). IsHitTestVisible alone only
    /// affects WPF inside the popup, not Windows.
    /// </summary>
    private static void MakeClickThrough(Visual picture)
    {
        if (PresentationSource.FromVisual(picture) is not System.Windows.Interop.HwndSource source)
            return;
        int style = GetWindowLong(source.Handle, GWL_EXSTYLE);
        if ((style & WS_EX_TRANSPARENT) == 0)
            SetWindowLong(source.Handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    /// <summary>Moves the picture so the grab point stays under the mouse.</summary>
    public void Move(Point mouseInRelativeTo)
    {
        _popup.HorizontalOffset = mouseInRelativeTo.X - _grab.X;
        _popup.VerticalOffset = mouseInRelativeTo.Y - _grab.Y;
    }

    public void Close() => _popup.IsOpen = false;
}
