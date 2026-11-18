using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Editor;
using DirectoryFable.Infrastructure;
using DirectoryFable.Search;

namespace DirectoryFable.Views;

/// <summary>
/// SonnetSearch's scroll rail, in place of the result list's scroll bar. With millions of rows a
/// normal thumb is a dot that is hard to grab; this thumb keeps a usable size, a click anywhere on
/// the rail jumps there and keeps dragging, and the rail knows the sort order: while the mouse is
/// on it, a panel to its left shows where each section starts ("Today", "Oct 2026", "≥ 1 GB", "A",
/// "C:\Users") and a bubble shows the value and row at the cursor. A click on a label puts that
/// section's first row at the top of the list. Where sections crowd together (a few big files above
/// millions of small ones), their labels move down a little so more of them stay readable.
/// </summary>
public sealed class ResultScrubber : FrameworkElement
{
    private const double ThumbMinHeight = 40;
    private const double ThumbInset = 3;        // Gap between the rail's sides and the thumb.
    private const double SampleStep = 2;        // DIP between the rows looked at for section starts.
    private const double LabelFontSize = 12;
    private const double PanelPadding = 10;
    private const double MaxLabelPush = 2.5;    // Label heights a crowded label may move down.

    private static readonly Brush RailBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly Pen RailEdgePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)), 1));
    private static readonly Brush ThumbBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0x4E, 0x4E)));
    private static readonly Brush ThumbHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x68, 0x68, 0x68)));
    private static readonly Brush ThumbDragBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78)));
    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C)));
    private static readonly Pen PanelEdgePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 1));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    private static readonly Brush LabelActiveBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush BubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
    private static readonly Pen BubbleEdgePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), 1));
    private static readonly Pen CursorLinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x9B, 0xE6)), 2));
    private static readonly Pen ConnectorPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58)), 1));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private readonly FrameSyncedDrag _drag;
    private ListView? _list;
    private ScrollViewer? _scroll;
    private FrameworkElement? _content;     // The rows' area (below the column headers).
    private SearchResult _result = SearchResult.Empty;
    private ResultSort _sort = ResultSort.Name;

    private bool _dragging;
    private double _grabOffset;             // Cursor Y minus the thumb's top while dragging.
    private double _dragThumbTop;           // The thumb follows the cursor while dragging, not the list.
    private Point? _hover;                  // Mouse position while it is on the rail or the panel.

    // Cached for the current result and geometry: where the sections start, and their placed labels.
    private List<Section>? _sections;
    private List<Placed> _placed = [];
    private double _panelWidth;
    private (SearchResult, ResultSort, double, double, double, double) _sectionsKey;

    /// <summary>A section start: its place on the rail, its first row, its label.</summary>
    private sealed record Section(double Y, int Row, string Label);

    /// <summary>A shown label: its section, its text and its top (moved down when crowded).</summary>
    private sealed record Placed(Section Section, FormattedText Text, double LabelTop);

    public ResultScrubber()
    {
        _drag = new FrameSyncedDrag(this, y => DragTo(y));
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
    }

    /// <summary>The list this rail scrolls; its own vertical scroll bar should be hidden.</summary>
    public void Attach(ListView list)
    {
        _list = list;
        list.Loaded += (_, _) => Hook();
        list.SizeChanged += (_, _) => InvalidateVisual();
        Hook();
    }

    /// <summary>The shown result and the column it is sorted by (for the section labels).</summary>
    public void SetResult(SearchResult result, ResultSort sort)
    {
        _result = result;
        _sort = sort;
        _sections = null;
        InvalidateVisual();
    }

    private void Hook()
    {
        if (_scroll is not null || _list is null)
            return;
        _list.ApplyTemplate();
        _scroll = FindDescendant<ScrollViewer>(_list);
        if (_scroll is null)
            return;
        _scroll.ApplyTemplate();
        _content = _scroll.Template?.FindName("PART_ScrollContentPresenter", _scroll) as FrameworkElement;
        _scroll.ScrollChanged += (_, _) => InvalidateVisual();
        InvalidateVisual();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    // ---------- Geometry ----------

    /// <summary>The rail part next to the rows (below the column headers, above a horizontal scroll bar).</summary>
    private (double Top, double Height) Track()
    {
        if (_content is { ActualHeight: > 0 } content)
        {
            try
            {
                double top = content.TranslatePoint(new Point(0, 0), this).Y;
                return (Math.Max(0, top), Math.Max(0, Math.Min(content.ActualHeight, ActualHeight - top)));
            }
            catch (InvalidOperationException)
            {
                // Not in the same visual tree (yet).
            }
        }
        return (0, ActualHeight);
    }

    /// <summary>Rows: first shown, shown, and the largest first-shown row; null when all rows fit.</summary>
    private (double Offset, double Viewport, double MaxOffset)? Scroll()
    {
        if (_scroll is null || _result.Items.Length == 0)
            return null;
        double max = _scroll.ExtentHeight - _scroll.ViewportHeight;
        return max <= 0 ? null : (_scroll.VerticalOffset, _scroll.ViewportHeight, max);
    }

    private static double ThumbHeight(double trackHeight, double viewport, double extent) =>
        Math.Min(trackHeight, Math.Max(ThumbMinHeight, trackHeight * viewport / extent));

    /// <summary>The first shown row when the thumb's middle is at <paramref name="y"/>.</summary>
    private int RowAt(double y, double top, double height, double thumb, double maxOffset)
    {
        double room = height - thumb;
        double f = room <= 0 ? 0 : Math.Clamp((y - top - thumb / 2) / room, 0, 1);
        return (int)Math.Clamp(Math.Round(f * maxOffset), 0, _result.Items.Length - 1);
    }

    // ---------- Drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        var (top, height) = Track();
        // Above the track: continue the column header bar.
        if (top > 0)
        {
            dc.DrawRectangle(TryFindResource("ColumnHeaderBackgroundBrush") as Brush ?? RailBrush, null, new Rect(0, 0, width, top));
            if (TryFindResource("ColumnHeaderSeparatorBrush") is Brush line)
                dc.DrawRectangle(line, null, new Rect(0, top - 1, width, 1));
        }
        dc.DrawRectangle(RailBrush, null, new Rect(0, top, width, Math.Max(0, ActualHeight - top)));
        dc.DrawLine(RailEdgePen, new Point(0.5, top), new Point(0.5, ActualHeight));

        if (Scroll() is not { } s || height <= 0)
            return;
        double thumb = ThumbHeight(height, s.Viewport, s.MaxOffset + s.Viewport);
        double room = height - thumb;
        double thumbTop = _dragging ? _dragThumbTop : top + (room <= 0 ? 0 : s.Offset / s.MaxOffset * room);

        Sections(top, height, thumb, s.MaxOffset);
        foreach (var placed in _placed)
            dc.DrawRectangle(TickBrush, null, new Rect(2, Math.Round(placed.Section.Y), 5, 1));

        bool hoverThumb = _hover is Point h && h.X >= 0 && h.Y >= thumbTop && h.Y <= thumbTop + thumb;
        var thumbBrush = _dragging ? ThumbDragBrush : hoverThumb ? ThumbHoverBrush : ThumbBrush;
        dc.DrawRoundedRectangle(thumbBrush, null, new Rect(ThumbInset, thumbTop + 1, Math.Max(2, width - 2 * ThumbInset), Math.Max(2, thumb - 2)), 3, 3);

        // Mouse on the rail or the panel, or dragging: the section panel and the bubble.
        Placed? label = null;
        double y;
        int row;
        if (_dragging)
        {
            y = _dragThumbTop + thumb / 2;
            row = RowAt(y, top, height, thumb, s.MaxOffset);
        }
        else if (_hover is Point p)
        {
            label = LabelAt(p);
            y = label?.Section.Y ?? p.Y;
            row = label?.Section.Row ?? RowAt(y, top, height, thumb, s.MaxOffset);
        }
        else
        {
            return;
        }
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawPanel(dc, top, height, row, label);
        DrawBubble(dc, y, row, top, height, dip);
    }

    private void DrawPanel(DrawingContext dc, double top, double height, int row, Placed? hovered)
    {
        if (_panelWidth <= 0)
            return;
        dc.DrawRectangle(PanelBrush, PanelEdgePen, new Rect(-_panelWidth - 0.5, top + 0.5, _panelWidth, Math.Max(0, height - 1)));
        // Highlighted: the label under the mouse, else the section of the row at the cursor.
        Section? current = hovered?.Section;
        if (current is null && _sections is not null)
            foreach (var section in _sections)
                if (section.Row <= row)
                    current = section;
        foreach (var placed in _placed)
        {
            var text = placed.Text;
            text.SetForegroundBrush(placed.Section == current ? LabelActiveBrush : LabelBrush);
            dc.DrawText(text, new Point(-PanelPadding - text.Width, placed.LabelTop));
            // A label moved down by crowding: a thin line to where its section starts.
            double middle = placed.LabelTop + text.Height / 2;
            if (Math.Abs(middle - placed.Section.Y) > 2)
                dc.DrawLine(ConnectorPen, new Point(-PanelPadding + 3, middle), new Point(-1, placed.Section.Y));
        }
    }

    private void DrawBubble(DrawingContext dc, double y, int row, double top, double height, double dip)
    {
        var value = Text(ValueText(row), 13, LabelActiveBrush, dip);
        value.MaxTextWidth = 560;
        value.MaxLineCount = 1;
        value.Trimming = TextTrimming.CharacterEllipsis;
        var info = Text($"Row {row + 1:N0} of {_result.Items.Length:N0}", 11, LabelBrush, dip);
        double w = Math.Max(value.WidthIncludingTrailingWhitespace, info.Width) + 20;
        double h = value.Height + info.Height + 10;
        double bubbleTop = Math.Clamp(y - h / 2, top, Math.Max(top, top + height - h));
        // Left of the label panel, so the labels stay readable.
        double right = -_panelWidth - 6;
        var box = new Rect(right - w, bubbleTop, w, h);
        dc.DrawRoundedRectangle(BubbleBrush, BubbleEdgePen, box, 4, 4);
        dc.DrawText(value, new Point(box.X + 10, box.Y + 5));
        dc.DrawText(info, new Point(box.X + 10, box.Y + 5 + value.Height));
        // Short marks beside the bubble and on the rail show the exact place.
        double line = Math.Round(y);
        dc.DrawLine(CursorLinePen, new Point(right, line), new Point(right + 6, line));
        dc.DrawLine(CursorLinePen, new Point(0, line), new Point(7, line));
    }

    /// <summary>The shown label nearest to a point of the panel (a few pixels around it count), if any.</summary>
    private Placed? LabelAt(Point p)
    {
        const double slop = 4;
        if (p.X >= 0 || p.X < -_panelWidth)
            return null;
        Placed? best = null;
        double bestDistance = slop;
        foreach (var placed in _placed)
        {
            double distance = Math.Max(0, Math.Max(placed.LabelTop - p.Y, p.Y - (placed.LabelTop + placed.Text.Height)));
            if (distance <= bestDistance)
            {
                best = placed;
                bestDistance = distance;
            }
        }
        return best;
    }

    private FormattedText Text(string text, double size, Brush brush, double dip) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, dip);

    // ---------- Sections (sort-aware labels) ----------

    /// <summary>
    /// Where the sections of the sort column start along the rail. A row is looked at every few
    /// pixels; where the section changed between two of them, a binary search finds the exact first
    /// row (the rows are sorted by that column, so each section is one block of rows). Small
    /// sections between two looked-at rows are found the same way.
    /// </summary>
    private List<Section> Sections(double top, double height, double thumb, double maxOffset)
    {
        var cacheKey = (_result, _sort, top, height, thumb, maxOffset);
        if (_sections is not null && _sectionsKey == cacheKey)
            return _sections;
        var list = new List<Section>();
        double room = height - thumb;
        double YOf(int row) => top + thumb / 2 + (room <= 0 || maxOffset <= 0 ? 0 : Math.Min(1, row / maxOffset) * room);

        var (previousKey, firstLabel) = SectionOf(0);
        list.Add(new Section(YOf(0), 0, firstLabel));
        int previousRow = 0;
        void LookAt(int row)
        {
            if (row <= previousRow)
                return;
            string key = SectionOf(row).Key;
            while (key != previousKey)
            {
                int start = FirstChange(previousRow, row, previousKey);
                var (startKey, label) = SectionOf(start);
                list.Add(new Section(YOf(start), start, label));
                previousKey = startKey;
                previousRow = start;
            }
            previousRow = row;
        }
        for (double y = top + thumb / 2 + SampleStep; y <= top + height - thumb / 2 + 0.01; y += SampleStep)
            LookAt(RowAt(y, top, height, thumb, maxOffset));
        LookAt(_result.Items.Length - 1); // The rows of the last screen.

        _sections = list;
        _sectionsKey = cacheKey;
        PlaceLabels(list, top, height);
        return list;
    }

    /// <summary>The first row after <paramref name="after"/> (up to <paramref name="last"/>, whose section differs) not in section <paramref name="key"/>.</summary>
    private int FirstChange(int after, int last, string key)
    {
        int low = after + 1, high = last;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (SectionOf(middle).Key != key)
                high = middle;
            else
                low = middle + 1;
        }
        return low;
    }

    /// <summary>
    /// Labels beside their section start; a label that would overlap the one above moves down, at
    /// most <see cref="MaxLabelPush"/> label heights, else it is left out.
    /// </summary>
    private void PlaceLabels(List<Section> sections, double top, double height)
    {
        _placed = [];
        _panelWidth = 0;
        if (sections.Count < 2)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double h = Text("Ag", LabelFontSize, LabelBrush, dip).Height; // One line; the same for all labels.
        double free = top;
        foreach (var section in sections)
        {
            double wanted = Math.Clamp(section.Y - h / 2, top, top + height - h);
            double labelTop = Math.Max(wanted, free);
            if (labelTop - wanted > MaxLabelPush * h || labelTop + h > top + height)
                continue;
            // Long labels (odd "extensions" like ".0, culture=neutral, ...") would make the panel too wide.
            string shown = section.Label.Length > 28 ? section.Label[..27] + "…" : section.Label;
            _placed.Add(new Placed(section, Text(shown, LabelFontSize, LabelBrush, dip), labelTop));
            free = labelTop + h;
        }
        if (_placed.Count > 0)
            _panelWidth = _placed.Max(x => x.Text.Width) + 2 * PanelPadding;
    }

    private (DriveIndex Index, int Entry) Entry(int row)
    {
        long item = _result.Items[row];
        return (_result.IndexOf(item), SearchResult.EntryOf(item));
    }

    private (string Key, string Label) SectionOf(int row)
    {
        var (index, i) = Entry(row);
        switch (_sort)
        {
            case ResultSort.Size:
                return SizeSection(index.IsDirectory(i) ? -1 : index.Size[i]);
            case ResultSort.Modified:
                return DateSection(index.Modified[i]);
            case ResultSort.Accessed:
                return DateSection(index.Accessed[i]);
            case ResultSort.Created:
                return DateSection(index.Created[i]);
            case ResultSort.Path:
                string top = TopFolders(index.FolderPath(i));
                return (top.ToUpperInvariant(), top);
            case ResultSort.Type:
                if (index.IsDirectory(i))
                    return ("<folder>", "Folders");
                string extension = Path.GetExtension(index.Name(i)).ToString();
                return (extension.ToUpperInvariant(), extension.Length == 0 ? "No extension" : extension.ToLowerInvariant());
            default:
                var name = index.Name(i);
                if (name.IsEmpty)
                    return ("", "");
                char c = char.ToUpperInvariant(name[0]);
                return char.IsDigit(c) ? ("0", "0-9") : (c.ToString(), c.ToString());
        }
    }

    /// <summary>The value of the sort column at a row, for the bubble.</summary>
    private string ValueText(int row)
    {
        var (index, i) = Entry(row);
        bool folder = index.IsDirectory(i);
        string name = index.Name(i).ToString();
        return _sort switch
        {
            ResultSort.Size => folder ? $"Folder   {name}" : $"{DisplayFormat.Size(index.Size[i])}   {name}",
            ResultSort.Modified => $"{DateText(index.Modified[i])}   {name}",
            ResultSort.Accessed => $"{DateText(index.Accessed[i])}   {name}",
            ResultSort.Created => $"{DateText(index.Created[i])}   {name}",
            ResultSort.Path => index.FolderPath(i) + name,
            _ => name,
        };
    }

    private static string DateText(long fileTime) =>
        fileTime <= 0 ? "No date" : DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    private static (string, string) SizeSection(long size)
    {
        if (size < 0)
            return ("folder", "Folders");
        if (size == 0)
            return ("0", "0 bytes");
        if (size < 1024)
            return ("b", "< 1 KB");
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = size / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        int floor = value >= 100 ? 100 : value >= 10 ? 10 : 1;
        return ($"{unit}:{floor}", $"≥ {floor} {units[unit]}");
    }

    private static (string, string) DateSection(long fileTime)
    {
        if (fileTime <= 0)
            return ("none", "No date");
        var date = DateTime.FromFileTimeUtc(fileTime).ToLocalTime().Date;
        var today = DateTime.Today;
        if (date > today)
            return ("future", "Future");
        if (date == today)
            return ("today", "Today");
        if (date == today.AddDays(-1))
            return ("yesterday", "Yesterday");
        if (date > today.AddDays(-7))
            return ("week", "Last 7 days");
        var firstMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        if (date >= firstMonth)
            return ($"m{date:yyyy-MM}", date.ToString("MMM yyyy", CultureInfo.InvariantCulture));
        return ($"y{date.Year}", date.Year.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"D:\Work\Docs\Notes\" -> "D:\Work"; "D:\" stays "D:\".</summary>
    private static string TopFolders(string folder)
    {
        int first = folder.IndexOf('\\');
        if (first < 0)
            return folder;
        int second = folder.IndexOf('\\', first + 1);
        return second < 0 ? folder : folder[..second];
    }

    // ---------- Mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double y = e.GetPosition(this).Y;
        if (_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            // Also on the mouse event itself, so WPF draws the next frame at once (see FrameSyncedDrag).
            DragTo(y);
            return;
        }
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragging)
            return;
        _hover = null;
        InvalidateVisual();
    }

    /// <summary>On a label: its section's first row goes to the top. On the thumb: drag it. Elsewhere: jump there, then drag.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Scroll() is not { } s || _scroll is null)
            return;
        e.Handled = true;
        if (LabelAt(e.GetPosition(this)) is { } label)
        {
            _scroll.ScrollToVerticalOffset(Math.Min(label.Section.Row, s.MaxOffset));
            return;
        }
        var (top, height) = Track();
        double thumb = ThumbHeight(height, s.Viewport, s.MaxOffset + s.Viewport);
        double room = height - thumb;
        double thumbTop = top + (room <= 0 ? 0 : s.Offset / s.MaxOffset * room);
        double y = e.GetPosition(this).Y;
        bool onThumb = e.GetPosition(this).X >= 0 && y >= thumbTop && y <= thumbTop + thumb;
        _grabOffset = onThumb ? y - thumbTop : thumb / 2;
        _dragThumbTop = thumbTop;
        _dragging = true;
        CaptureMouse();
        DragTo(y);
        _drag.Start(); // The thumb follows the cursor once per frame.
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag();

    protected override void OnLostMouseCapture(MouseEventArgs e) => EndDrag();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_scroll is null)
            return;
        e.Handled = true;
        int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - Math.Sign(e.Delta) * lines);
    }

    private void DragTo(double y)
    {
        if (!_dragging || Scroll() is not { } s || _scroll is null)
            return;
        var (top, height) = Track();
        double thumb = ThumbHeight(height, s.Viewport, s.MaxOffset + s.Viewport);
        double room = height - thumb;
        _dragThumbTop = Math.Clamp(y - _grabOffset, top, top + Math.Max(0, room));
        double f = room <= 0 ? 0 : (_dragThumbTop - top) / room;
        _scroll.ScrollToVerticalOffset(Math.Round(f * s.MaxOffset));
        InvalidateVisual();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _drag.Stop();
        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _hover = IsMouseOver ? Mouse.GetPosition(this) : null;
        InvalidateVisual();
    }
}
