using System.Windows;
using System.Windows.Media;

namespace DirectoryFable.Views;

/// <summary>
/// A highlight on a PDF page in AstraViewer (selected text, search hits): rectangles given in the page's 0..1
/// square, drawn at the element's size (so they fit every zoom without being made again).
/// </summary>
public sealed class PdfSelectionOverlay : FrameworkElement
{
    public static readonly DependencyProperty RectsProperty = DependencyProperty.Register(
        nameof(Rects), typeof(IReadOnlyList<Rect>), typeof(PdfSelectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The color of the rectangles; not set = the blue of a text selection.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PdfSelectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush SelectionFill = Frozen(Color.FromArgb(0x66, 0x33, 0x90, 0xFF));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IReadOnlyList<Rect>? Rects
    {
        get => (IReadOnlyList<Rect>?)GetValue(RectsProperty);
        set => SetValue(RectsProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Rects is not { Count: > 0 } rects)
            return;
        double width = ActualWidth, height = ActualHeight;
        var fill = Fill ?? SelectionFill;
        foreach (var rect in rects)
            context.DrawRectangle(fill, null, new Rect(rect.X * width, rect.Y * height, rect.Width * width, rect.Height * height));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
