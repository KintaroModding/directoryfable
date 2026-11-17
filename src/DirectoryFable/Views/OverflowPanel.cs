using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DirectoryFable.Views;

/// <summary>
/// A horizontal panel that shows as many children as fit. Children that do not fit are
/// arranged outside the panel (clipped) and marked with <see cref="IsOverflowedProperty"/>;
/// <see cref="HasOverflow"/> tells the bar to show its "»" button.
/// Between visible children it leaves <see cref="Gap"/> and draws <see cref="Separator"/>
/// there (favorites bar: "Desktop / Documents / Downloads"); the separator is not part of
/// the children, so drag pictures and hit tests only see the favorite itself.
/// </summary>
public sealed class OverflowPanel : Panel
{
    public static readonly DependencyProperty IsOverflowedProperty = DependencyProperty.RegisterAttached(
        "IsOverflowed", typeof(bool), typeof(OverflowPanel), new PropertyMetadata(false));

    public static bool GetIsOverflowed(DependencyObject d) => (bool)d.GetValue(IsOverflowedProperty);

    private static void SetIsOverflowed(DependencyObject d, bool value) => d.SetValue(IsOverflowedProperty, value);

    public static readonly DependencyProperty HasOverflowProperty = DependencyProperty.Register(
        nameof(HasOverflow), typeof(bool), typeof(OverflowPanel), new PropertyMetadata(false));

    public bool HasOverflow
    {
        get => (bool)GetValue(HasOverflowProperty);
        private set => SetValue(HasOverflowProperty, value);
    }

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(OverflowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Space between two children (the separator is drawn in its middle).</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public static readonly DependencyProperty SeparatorProperty = DependencyProperty.Register(
        nameof(Separator), typeof(string), typeof(OverflowPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Separator
    {
        get => (string)GetValue(SeparatorProperty);
        set => SetValue(SeparatorProperty, value);
    }

    public static readonly DependencyProperty SeparatorBrushProperty = DependencyProperty.Register(
        nameof(SeparatorBrush), typeof(Brush), typeof(OverflowPanel),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush SeparatorBrush
    {
        get => (Brush)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    public static readonly DependencyProperty KeepEndProperty = DependencyProperty.Register(
        nameof(KeepEnd), typeof(bool), typeof(OverflowPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>
    /// False: the first children stay, the last ones overflow (favorites bar).
    /// True: the last children stay, the first ones overflow (address bar: the current folder
    /// must stay visible, like Explorer).
    /// </summary>
    public bool KeepEnd
    {
        get => (bool)GetValue(KeepEndProperty);
        set => SetValue(KeepEndProperty, value);
    }

    /// <summary>Raised after an arrange that changed <see cref="HasOverflow"/>.</summary>
    public event Action? OverflowChanged;

    /// <summary>X centres of the gaps between visible children (from the last arrange).</summary>
    private readonly List<double> _separatorCenters = [];

    public OverflowPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (width > 0)
                width += Gap;
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool hadOverflow = HasOverflow;
        if (KeepEnd)
        {
            ArrangeKeepEnd(finalSize);
            if (hadOverflow != HasOverflow)
                OverflowChanged?.Invoke();
            return finalSize;
        }

        double x = 0;
        bool overflow = false;
        _separatorCenters.Clear();
        foreach (UIElement child in InternalChildren)
        {
            double w = child.DesiredSize.Width;
            double start = x > 0 ? x + Gap : 0;
            bool fits = !overflow && start + w <= finalSize.Width;
            overflow |= !fits;
            // Hidden children are placed past the right edge; ClipToBounds hides them.
            child.Arrange(new Rect(fits ? start : finalSize.Width + 1, 0, w, finalSize.Height));
            SetIsOverflowed(child, !fits);
            if (fits)
            {
                if (x > 0)
                    _separatorCenters.Add(x + Gap / 2);
                x = start + w;
            }
        }
        HasOverflow = overflow;
        InvalidateVisual();
        if (hadOverflow != HasOverflow)
            OverflowChanged?.Invoke();
        return finalSize;
    }

    /// <summary>
    /// Shows the longest run of last children that fits, from the left edge; the children
    /// before it are placed left of the panel (clipped). The last child always shows.
    /// </summary>
    private void ArrangeKeepEnd(Size finalSize)
    {
        _separatorCenters.Clear();
        var children = InternalChildren.Cast<UIElement>().ToList();
        double used = 0;
        int first = children.Count; // Index of the first visible child.
        for (int i = children.Count - 1; i >= 0; i--)
        {
            double w = children[i].DesiredSize.Width + (i < children.Count - 1 ? Gap : 0);
            if (first < children.Count && used + w > finalSize.Width)
                break;
            used += w;
            first = i;
        }

        double x = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            double w = child.DesiredSize.Width;
            bool visible = i >= first;
            if (visible && i > first)
                x += Gap;
            child.Arrange(new Rect(visible ? x : -w - 1, 0, w, finalSize.Height));
            SetIsOverflowed(child, !visible);
            if (visible)
                x += w;
        }
        HasOverflow = first > 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (string.IsNullOrEmpty(Separator) || _separatorCenters.Count == 0)
            return;

        var text = new FormattedText(Separator, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            TextElement.GetFontSize(this), SeparatorBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double y = (ActualHeight - text.Height) / 2;
        foreach (double center in _separatorCenters)
            dc.DrawText(text, new Point(Math.Round(center - text.Width / 2), Math.Round(y)));
    }
}
