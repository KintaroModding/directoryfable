using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Models;

namespace DirectoryFable.Views;

/// <summary>
/// Rectangle selection, like Windows Explorer: hold the left button on empty list space, or on a row outside
/// its Name cell (the Name cell drags the files), and drag. Rows the rectangle touches are selected; with
/// Ctrl they are added to the selection there was before. The list scrolls when the mouse is beyond its top
/// or bottom edge. Rows scrolled out of view keep the state they had when they were last seen (the list
/// only has containers for the rows it shows).
/// </summary>
public partial class FilePane
{
    private Point? _marqueeStart;            // In content coordinates (scroll offsets added).
    private bool _marqueeActive;
    private HashSet<FileItem> _marqueeBase = [];
    private readonly HashSet<FileItem> _marqueeHits = [];
    private MarqueeAdorner? _marqueeAdorner;
    private DispatcherTimer? _marqueeScrollTimer;

    /// <summary>A press that may become a rectangle selection (called by the list's mouse-down handler).</summary>
    private void MarqueeCandidate(MouseButtonEventArgs e)
    {
        _listScroller ??= FindChild<ScrollViewer>(FileList);
        if (_listScroller is null)
            return;
        var p = e.GetPosition(FileList);
        _marqueeStart = new Point(p.X + _listScroller.HorizontalOffset, p.Y + _listScroller.VerticalOffset);
        // Ctrl adds to the selection there was before the press.
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? [.. FileList.SelectedItems.OfType<FileItem>()] : [];
    }

    /// <summary>Is the press on a row's Name cell (there it drags the files) rather than another column?</summary>
    private bool IsOnNameCell(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != FileList; d = VisualTreeHelper.GetParent(d))
        {
            if (VisualTreeHelper.GetParent(d) is GridViewRowPresenter presenter)
            {
                int index = -1;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(presenter); i++)
                    if (VisualTreeHelper.GetChild(presenter, i) == d)
                        index = i;
                return index >= 0 && index < Columns.Columns.Count && Columns.Columns[index] == NameColumn;
            }
        }
        return false;
    }

    /// <summary>Mouse move with the button held: starts the rectangle after the drag distance, then updates it.</summary>
    private bool MarqueeMove(MouseEventArgs e)
    {
        if (_marqueeStart is not { } start || _listScroller is null)
            return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMarquee();
            return false;
        }
        var p = e.GetPosition(FileList);
        var now = new Point(p.X + _listScroller.HorizontalOffset, p.Y + _listScroller.VerticalOffset);
        if (!_marqueeActive)
        {
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return true;
            BeginMarquee();
        }
        UpdateMarquee();
        return true;
    }

    private void BeginMarquee()
    {
        _marqueeActive = true;
        _marqueeHits.Clear();
        FileList.CaptureMouse();
        FileList.LostMouseCapture += Marquee_LostMouseCapture;
        if (AdornerLayer.GetAdornerLayer(FileList) is { } layer)
        {
            _marqueeAdorner = new MarqueeAdorner(FileList);
            layer.Add(_marqueeAdorner);
        }
        _marqueeScrollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _marqueeScrollTimer.Tick += (_, _) => MarqueeAutoScroll();
        _marqueeScrollTimer.Start();
        FileList.Focus();
    }

    /// <summary>The rectangle in list coordinates, the rows it touches, the selection.</summary>
    private void UpdateMarquee()
    {
        if (_marqueeStart is not { } start || _listScroller is null)
            return;
        var p = Mouse.GetPosition(FileList);
        var viewStart = new Point(start.X - _listScroller.HorizontalOffset, start.Y - _listScroller.VerticalOffset);
        var rect = new Rect(viewStart, p);
        var viewport = Viewport();
        if (_marqueeAdorner is not null)
        {
            _marqueeAdorner.Rectangle = rect;
            _marqueeAdorner.Clip = viewport;
        }

        // Rows on screen: in or out of the rectangle; rows off screen keep what they had.
        foreach (var row in RealizedRows())
        {
            if (row.DataContext is not FileItem item)
                continue;
            var bounds = row.TransformToAncestor(FileList).TransformBounds(new Rect(row.RenderSize));
            if (!bounds.IntersectsWith(viewport))
                continue;
            if (bounds.IntersectsWith(rect))
                _marqueeHits.Add(item);
            else
                _marqueeHits.Remove(item);
        }

        var wanted = new HashSet<FileItem>(_marqueeBase);
        wanted.UnionWith(_marqueeHits);
        foreach (var item in FileList.SelectedItems.OfType<FileItem>().Where(i => !wanted.Contains(i)).ToList())
            FileList.SelectedItems.Remove(item);
        foreach (var item in wanted)
            if (!FileList.SelectedItems.Contains(item))
                FileList.SelectedItems.Add(item);
    }

    /// <summary>Beyond the top or bottom edge: scroll, faster the farther the mouse is.</summary>
    private void MarqueeAutoScroll()
    {
        if (!_marqueeActive || _listScroller is null)
            return;
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndMarquee();
            return;
        }
        var viewport = Viewport();
        double y = Mouse.GetPosition(FileList).Y;
        double step = y < viewport.Top ? -(viewport.Top - y) : y > viewport.Bottom ? y - viewport.Bottom : 0;
        if (step == 0)
            return;
        _listScroller.ScrollToVerticalOffset(_listScroller.VerticalOffset + Math.Clamp(step, -60, 60));
        _listScroller.UpdateLayout();
        UpdateMarquee();
    }

    private void EndMarquee()
    {
        _marqueeStart = null;
        if (!_marqueeActive)
            return;
        _marqueeActive = false;
        FileList.LostMouseCapture -= Marquee_LostMouseCapture;
        _marqueeScrollTimer?.Stop();
        _marqueeScrollTimer = null;
        if (_marqueeAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(FileList)?.Remove(_marqueeAdorner);
            _marqueeAdorner = null;
        }
        if (FileList.IsMouseCaptured)
            FileList.ReleaseMouseCapture();
    }

    /// <summary>Another window or a click elsewhere took the mouse: the rectangle ends where it is.</summary>
    private void Marquee_LostMouseCapture(object sender, MouseEventArgs e) => EndMarquee();

    /// <summary>The part of the list that shows rows (below the column headers), in list coordinates.</summary>
    private Rect Viewport()
    {
        if (_listScroller?.Template?.FindName("PART_ScrollContentPresenter", _listScroller) is FrameworkElement content &&
            content.IsVisible)
            return content.TransformToAncestor(FileList).TransformBounds(new Rect(content.RenderSize));
        return new Rect(FileList.RenderSize);
    }

    private IEnumerable<ListViewItem> RealizedRows()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(FileList);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is ListViewItem row)
                    yield return row;
                else
                    stack.Push(child);
            }
        }
    }

    /// <summary>The selection rectangle: a light accent fill with a thin accent border, clipped to the rows' area.</summary>
    private sealed class MarqueeAdorner(UIElement element) : Adorner(element)
    {
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0x33, 0x99, 0xFF)));
        private static readonly Pen Border = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)), 1));
        private Rect _rectangle;
        private Rect _clip;

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        public Rect Rectangle
        {
            set
            {
                _rectangle = value;
                InvalidateVisual();
            }
        }

        public new Rect Clip
        {
            set => _clip = value;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.PushClip(new RectangleGeometry(_clip));
            var r = new Rect(Math.Round(_rectangle.X) + 0.5, Math.Round(_rectangle.Y) + 0.5, Math.Round(_rectangle.Width), Math.Round(_rectangle.Height));
            dc.DrawRectangle(Fill, Border, r);
            dc.Pop();
        }

        // The rectangle must not catch the mouse: the list keeps getting it.
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
}
