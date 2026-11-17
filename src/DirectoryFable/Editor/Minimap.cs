using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DirectoryFable.Editor;

/// <summary>
/// A VS Code-like minimap for Notepad#: every line drawn as tiny blocks
/// (1 device pixel per character, 2 per line) in the file's syntax colors, plus a light box for
/// the part shown in the editor. Click = jump there, drag the box = scroll, wheel = scroll.
/// Longer files scroll along with the editor, as in VS Code. The picture is rebuilt shortly after
/// the text changes (debounced), not on every key.
/// </summary>
public sealed class Minimap : FrameworkElement
{
    // 3 device pixels per line (2 px block + 1 px gap), 180 columns. Very long files fall back to 2 or 1 px per line.
    private const int PreferredPitch = 3;
    private const int MaxColumns = 180;       // Characters drawn per line.
    private int _pitch = PreferredPitch;      // Device pixels per line for the current file.

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    private static readonly Brush BoxBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush BoxActiveBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
    private const int MaxBitmapHeight = 16384;
    private const int MaxColoredLines = 20000; // Larger files: one color (highlighting all lines is slow).

    private static readonly Color BackgroundColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color PlainColor = Color.FromRgb(0x8A, 0x8A, 0x8A);

    private readonly DispatcherTimer _rebuildTimer;
    private TextEditor? _editor;
    private WriteableBitmap? _bitmap;
    private int _lines;
    private bool _dragging;
    private double _grabOffset;
    private readonly FrameSyncedDrag _drag;

    public Minimap()
    {
        _drag = new FrameSyncedDrag(this, y => ScrollBoxTo(y - _grabOffset));
        _rebuildTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
        Cursor = Cursors.Arrow;
        ClipToBounds = true;
    }

    /// <summary>The editor this minimap shows and scrolls.</summary>
    public void Attach(TextEditor editor)
    {
        _editor = editor;
        editor.TextChanged += (_, _) => ScheduleRebuild();
        editor.DocumentChanged += (_, _) => Rebuild();
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => InvalidateVisual();
        // Also when the visible height changes (first layout, window size): the box height follows it.
        editor.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => InvalidateVisual()), handledEventsToo: true);
        editor.SizeChanged += (_, _) => InvalidateVisual();
        DependencyPropertyDescriptor.FromProperty(TextEditor.SyntaxHighlightingProperty, typeof(TextEditor))
            .AddValueChanged(editor, (_, _) => Rebuild());
        Rebuild();
    }

    private void ScheduleRebuild()
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    // ---------- Picture ----------

    /// <summary>Draws all lines into a bitmap (device pixels), with the syntax colors when the file is not huge.</summary>
    private void Rebuild()
    {
        _rebuildTimer.Stop();
        var document = _editor?.Document;
        if (document is null)
        {
            _bitmap = null;
            _lines = 0;
            InvalidateVisual();
            return;
        }

        _lines = document.LineCount;
        _pitch = _lines * PreferredPitch <= MaxBitmapHeight ? PreferredPitch : _lines * 2 <= MaxBitmapHeight ? 2 : 1;
        int block = Math.Min(_pitch, 2); // Block height; the third pixel is the gap between lines.
        int width = MaxColumns;
        int height = Math.Clamp(_lines * _pitch, 1, MaxBitmapHeight);
        int drawnLines = Math.Min(_lines, height / _pitch);
        var pixels = new int[width * height];
        Array.Fill(pixels, ToBgra(BackgroundColor));

        var definition = _editor!.SyntaxHighlighting;
        using var highlighter = definition is not null && _lines <= MaxColoredLines
            ? new ICSharpCode.AvalonEdit.Highlighting.DocumentHighlighter(document, definition)
            : null;
        int plain = ToBgra(PlainColor);
        var charColors = new int[4096];

        for (int lineNumber = 1; lineNumber <= drawnLines; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            // Only the characters that can show (tabs count 4 columns; MaxColumns * 4 is enough).
            int length = Math.Min(line.Length, Math.Min(charColors.Length, MaxColumns * 4));
            string text = document.GetText(line.Offset, length);
            Array.Fill(charColors, plain, 0, length);

            // Syntax colors per character (later, inner sections override outer ones).
            if (highlighter is not null)
            {
                HighlightedLine? highlighted = null;
                try
                {
                    highlighted = highlighter.HighlightLine(lineNumber);
                }
                catch (Exception)
                {
                    // A broken rule must not break the minimap: plain color for this line.
                }
                foreach (var section in highlighted?.Sections ?? [])
                {
                    if (section.Color?.Foreground?.GetColor(null) is not Color color)
                        continue;
                    int from = Math.Max(0, section.Offset - line.Offset);
                    int to = Math.Min(section.Offset - line.Offset + section.Length, length);
                    if (from < to)
                        Array.Fill(charColors, ToBgra(Dim(color)), from, to - from);
                }
            }

            // Blocks: one pixel per character, tabs = 4 columns, spaces stay empty.
            int column = 0;
            int y = (lineNumber - 1) * _pitch;
            for (int i = 0; i < length && column < MaxColumns; i++)
            {
                char c = text[i];
                if (c == '\t')
                {
                    column += 4 - column % 4;
                    continue;
                }
                if (!char.IsWhiteSpace(c))
                {
                    for (int row = 0; row < block; row++)
                        pixels[(y + row) * width + column] = charColors[i];
                }
                column++;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        _bitmap = bitmap;
        InvalidateVisual();
    }

    /// <summary>Minimap colors are a little darker than the text, so the box stays readable.</summary>
    private static Color Dim(Color c) =>
        Color.FromRgb((byte)(c.R * 0.8), (byte)(c.G * 0.8), (byte)(c.B * 0.8));

    private static int ToBgra(Color c) => (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;

    // ---------- Layout and drawing (DIP) ----------

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleY;

    /// <summary>Height of one minimap line in DIP.</summary>
    private double LineHeight => _pitch / Scale;

    private double ContentHeight => _lines * LineHeight;

    // The minimap draws one row per document line. With word wrap a document line can fill
    // several editor rows, so positions go through document lines (fractional, 0-based):
    // LineAt(editor offset) and OffsetOf(line) are each other's inverse. Without word wrap they
    // are simply offset / line height.

    /// <summary>The document line (0-based, with the fraction inside the line) at an editor offset (DIP).</summary>
    private double LineAt(double offset)
    {
        var view = _editor!.TextArea.TextView;
        var document = _editor.Document;
        if (document is null || document.LineCount == 0)
            return 0;
        var line = view.GetDocumentLineByVisualTop(Math.Max(0, offset));
        double top = view.GetVisualTopByDocumentLine(line.LineNumber);
        double bottom = line.LineNumber < document.LineCount
            ? view.GetVisualTopByDocumentLine(line.LineNumber + 1)
            : Math.Max(top + view.DefaultLineHeight, view.DocumentHeight);
        double fraction = bottom > top ? Math.Clamp((offset - top) / (bottom - top), 0, 1) : 0;
        return line.LineNumber - 1 + fraction;
    }

    /// <summary>The editor offset (DIP) of a document line (0-based, fractional).</summary>
    private double OffsetOf(double line)
    {
        var view = _editor!.TextArea.TextView;
        var document = _editor.Document;
        if (document is null || document.LineCount == 0)
            return 0;
        line = Math.Clamp(line, 0, document.LineCount);
        int number = Math.Min((int)Math.Floor(line) + 1, document.LineCount);
        double top = view.GetVisualTopByDocumentLine(number);
        double bottom = number < document.LineCount
            ? view.GetVisualTopByDocumentLine(number + 1)
            : Math.Max(top + view.DefaultLineHeight, view.DocumentHeight);
        return top + (line - (number - 1)) * (bottom - top);
    }

    /// <summary>First shown document line and how many document lines the editor shows.</summary>
    private (double First, double Count) ShownLines()
    {
        double first = LineAt(_editor!.VerticalOffset);
        double last = LineAt(_editor.VerticalOffset + _editor.ViewportHeight);
        return (first, Math.Max(0, last - first));
    }

    /// <summary>How far the minimap itself is scrolled (long files), in DIP.</summary>
    private double MapOffset
    {
        get
        {
            if (_editor?.Document is null || ContentHeight <= ActualHeight)
                return 0;
            var (first, count) = ShownLines();
            double scrollableLines = Math.Max(1, _lines - count);
            return Math.Clamp(first / scrollableLines, 0, 1) * (ContentHeight - ActualHeight);
        }
    }

    /// <summary>The light box: top and height (DIP, minimap coordinates) of the lines shown in the editor.</summary>
    private (double Top, double Height) ViewportBox()
    {
        if (_editor?.Document is null)
            return (0, 0);
        var (first, count) = ShownLines();
        return (first * LineHeight - MapOffset, Math.Max(4, count * LineHeight));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap is null || _editor?.Document is null)
            return;

        double scale = Scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.DrawImage(_bitmap, new Rect(4, -MapOffset, _bitmap.PixelWidth / scale, _bitmap.PixelHeight / scale));

        var (top, height) = ViewportBox();
        dc.DrawRectangle(_dragging || IsMouseOver ? BoxActiveBrush : BoxBrush, null, new Rect(0, top, ActualWidth, height));
        dc.Pop();
    }

    // ---------- Mouse ----------

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    /// <summary>Click in the box: drag it. Click elsewhere: that line goes to the middle of the editor, then drag.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_editor?.Document is null)
            return;
        e.Handled = true;
        double y = e.GetPosition(this).Y;
        var (top, height) = ViewportBox();
        if (y < top || y > top + height)
        {
            JumpTo(y);
            // The editor applies the new offset in its next layout; do it now, so the box (and
            // the grab point below) is at the new place. Otherwise the drag jumps back at once.
            _editor.UpdateLayout();
            (top, height) = ViewportBox();
        }
        _grabOffset = y - top;
        _dragging = true;
        CaptureMouse();
        _drag.Start(); // The box follows the cursor once per frame (see FrameSyncedDrag).
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        // Also scroll on the mouse event itself: a pending change makes WPF draw the next frame at
        // once. With the per-frame update alone, WPF drew only every 8-10 ms (measured).
        ScrollBoxTo(e.GetPosition(this).Y - _grabOffset);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag();

    protected override void OnLostMouseCapture(MouseEventArgs e) => EndDrag();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_editor is null)
            return;
        e.Handled = true;
        _editor.ScrollToVerticalOffset(_editor.VerticalOffset - e.Delta);
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _drag.Stop();
        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    /// <summary>A click outside the box: the clicked line goes to the middle of the editor.</summary>
    private void JumpTo(double y)
    {
        if (_editor?.Document is null)
            return;
        double line = (y + MapOffset) / LineHeight;
        double scrollable = Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight);
        _editor.ScrollToVerticalOffset(Math.Clamp(OffsetOf(line) - _editor.ViewportHeight / 2, 0, scrollable));
    }

    /// <summary>
    /// Dragging the box. A file that fits: the box follows the mouse line by line. A longer file
    /// (the minimap scrolls too): like a scroll bar - the mouse position over the minimap height
    /// is the position in the whole file.
    /// </summary>
    private void ScrollBoxTo(double boxTop)
    {
        if (_editor?.Document is null)
            return;
        double scrollable = Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight);
        double firstLine;
        if (ContentHeight <= ActualHeight)
        {
            firstLine = boxTop / LineHeight;
        }
        else
        {
            // The inverse of MapOffset / ViewportBox: box top = fraction * (height - box height).
            var (_, count) = ShownLines();
            double boxHeight = Math.Max(4, count * LineHeight);
            firstLine = Math.Clamp(boxTop / Math.Max(1, ActualHeight - boxHeight), 0, 1) * Math.Max(0, _lines - count);
        }
        _editor.ScrollToVerticalOffset(Math.Clamp(OffsetOf(firstLine), 0, scrollable));
    }
}
