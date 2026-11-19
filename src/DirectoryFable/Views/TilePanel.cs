using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace DirectoryFable.Views;

/// <summary>
/// Items panel of the file list's tile view: tiles of one size in rows that fill the width, left to right,
/// in blocks by <see cref="TileCategories"/> (the list is sorted by category first). Each block starts on a
/// new row under a header ("Videos (12)" and a line, as the rows' group dividers), drawn by the panel itself.
/// Virtualizing: only the tiles in view (and one row above and below) have containers, so a folder with
/// tens of thousands of items opens as fast as one with ten. It scrolls itself (IScrollInfo), in pixels.
/// Needs the list's standard virtualization (not recycling).
/// </summary>
public sealed class TilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double),
        typeof(TilePanel), new FrameworkPropertyMetadata(276.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double),
        typeof(TilePanel), new FrameworkPropertyMetadata(216.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    private const double HeaderHeight = 30;
    private const double BlockGap = 6;

    /// <summary>Runs of items of one category: first item, count, category, and the header's top (content coordinates).</summary>
    private readonly List<Block> _blocks = [];
    private bool _blocksDirty = true;
    private int _columns = 1;

    private sealed class Block
    {
        public int Start;
        public int Count;
        public int Category;
        public double Top;
        public double ItemsTop => Top + HeaderHeight;
    }

    private Size _extent;
    private Size _viewport;
    private Vector _offset;

    /// <summary>Tiles in one row at the current width.</summary>
    public int Columns => _columns;

    // ---------- Layout ----------

    protected override Size MeasureOverride(Size available)
    {
        var items = ItemsControl.GetItemsOwner(this)?.Items;
        int count = items?.Count ?? 0;
        double width = double.IsInfinity(available.Width) ? TileWidth : available.Width;
        double height = double.IsInfinity(available.Height) ? TileHeight * 3 : available.Height;
        _columns = Math.Max(1, (int)(width / Math.Max(1, TileWidth)));

        if (_blocksDirty || _blocks.Sum(b => b.Count) != count)
            BuildBlocks(items, count);
        double y = 0;
        foreach (var block in _blocks)
        {
            block.Top = y;
            y += HeaderHeight + Rows(block) * TileHeight + BlockGap;
        }
        UpdateScrollInfo(new Size(width, height), new Size(width, y));

        // The items whose rows are in view (one row more on each side).
        double top = _offset.Y - TileHeight, bottom = _offset.Y + _viewport.Height + TileHeight;
        int first = int.MaxValue, last = -1;
        foreach (var block in _blocks)
        {
            int rows = Rows(block);
            int firstRow = Math.Max(0, (int)Math.Floor((top - block.ItemsTop) / TileHeight));
            int lastRow = Math.Min(rows - 1, (int)Math.Floor((bottom - block.ItemsTop) / TileHeight));
            if (firstRow > lastRow)
                continue;
            first = Math.Min(first, block.Start + firstRow * _columns);
            last = Math.Max(last, Math.Min(block.Start + block.Count - 1, block.Start + (lastRow + 1) * _columns - 1));
        }

        _ = InternalChildren; // Makes the generator available.
        IItemContainerGenerator generator = ItemContainerGenerator;
        if (last >= 0)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int child = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, child++)
                {
                    if (generator.GenerateNext(out bool isNew) is not UIElement element)
                        continue;
                    if (isNew)
                    {
                        if (child >= InternalChildren.Count)
                            AddInternalChild(element);
                        else
                            InsertInternalChild(child, element);
                        generator.PrepareItemContainer(element);
                    }
                    element.Measure(new Size(TileWidth, TileHeight));
                }
            }
        }
        RemoveOutside(first, last);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0 && Position(index) is { } at)
                InternalChildren[i].Arrange(new Rect(at.X - _offset.X, at.Y - _offset.Y, TileWidth, TileHeight));
        }
        InvalidateVisual(); // The headers move with the scroll position.
        return finalSize;
    }

    private int Rows(Block block) => (block.Count + _columns - 1) / _columns;

    private void BuildBlocks(ItemCollection? items, int count)
    {
        _blocks.Clear();
        _blocksDirty = false;
        for (int i = 0; i < count; i++)
        {
            int category = TileCategories.Of(items![i]);
            if (_blocks.Count > 0 && _blocks[^1].Category == category)
                _blocks[^1].Count++;
            else
                _blocks.Add(new Block { Start = i, Count = 1, Category = category });
        }
    }

    private Block? BlockOf(int index) => _blocks.LastOrDefault(b => b.Start <= index);

    /// <summary>The top left corner of an item's tile in content coordinates.</summary>
    private Point? Position(int index)
    {
        if (BlockOf(index) is not { } block || index >= block.Start + block.Count)
            return null;
        int local = index - block.Start;
        return new Point(local % _columns * TileWidth, block.ItemsTop + local / _columns * TileHeight);
    }

    /// <summary>
    /// The item an arrow key moves to: Left / Right the previous / next item; Up / Down the same column in the
    /// row above / below, across the category blocks (a shorter row: its last item).
    /// </summary>
    public int Neighbor(int index, int dx, int dy)
    {
        int count = _blocks.Sum(b => b.Count);
        if (count == 0)
            return -1;
        index = Math.Clamp(index, 0, count - 1);
        if (dx != 0)
            return Math.Clamp(index + dx, 0, count - 1);
        int b = _blocks.FindLastIndex(x => x.Start <= index);
        if (b < 0)
            return index;
        var block = _blocks[b];
        int local = index - block.Start, column = local % _columns, row = local / _columns;
        if (dy < 0)
        {
            if (row > 0)
                return index - _columns;
            if (b == 0)
                return index;
            var above = _blocks[b - 1];
            int lastRowStart = above.Start + (Rows(above) - 1) * _columns;
            return Math.Min(lastRowStart + column, above.Start + above.Count - 1);
        }
        if (index + _columns < block.Start + block.Count)
            return index + _columns;
        if (row < Rows(block) - 1)
            return block.Start + block.Count - 1;
        if (b == _blocks.Count - 1)
            return index;
        var below = _blocks[b + 1];
        return below.Start + Math.Min(column, below.Count - 1);
    }

    /// <summary>Containers outside first..last go (the focused one stays, so the keyboard keeps its place).</summary>
    private void RemoveOutside(int first, int last)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last)
                continue;
            if (InternalChildren[i].IsKeyboardFocusWithin)
                continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        _blocksDirty = true;
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    /// <summary>The category headers: name and count in the list's text colour, then a fine line to the right edge.</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var text = TryFindResource("ListTextBrush") as Brush ?? Brushes.White;
        var line = new Pen(TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray, 1);
        line.Freeze();
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var block in _blocks)
        {
            double y = block.Top - _offset.Y;
            if (y + HeaderHeight < 0 || y > ActualHeight)
                continue;
            var label = new FormattedText($"{TileCategories.Name(block.Category)} ({block.Count:N0})", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 14, text, pixelsPerDip);
            double textTop = y + (HeaderHeight - label.Height) / 2;
            dc.DrawText(label, new Point(9, textTop));
            double lineY = Math.Round(y + HeaderHeight / 2 + 1) + 0.5;
            double from = 9 + label.Width + 8, to = ActualWidth - 6;
            if (to > from)
                dc.DrawLine(line, new Point(from, lineY), new Point(to, lineY));
        }
    }

    /// <summary>Keyboard and ScrollIntoView: the row of the item comes into view (the first row with its header).</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (Position(index) is not { } at || BlockOf(index) is not { } block)
            return;
        double top = index - block.Start < _columns ? block.Top : at.Y;
        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (at.Y + TileHeight > _offset.Y + _viewport.Height)
            SetVerticalOffset(at.Y + TileHeight - _viewport.Height);
    }

    // ---------- IScrollInfo ----------

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > max)
        {
            _offset.Y = max;
            changed = true;
        }
        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    private double Wheel => Math.Max(48, TileHeight / 2);

    public void LineUp() => SetVerticalOffset(_offset.Y - 48);
    public void LineDown() => SetVerticalOffset(_offset.Y + 48);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Wheel);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Wheel);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.5)
            return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] != visual && !InternalChildren[i].IsAncestorOf(visual))
                continue;
            BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            break;
        }
        return rectangle;
    }
}
