using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Search;

namespace DirectoryFable.Views;

/// <summary>
/// The block chart (treemap) of the Space view: every file and folder below the shown folder is a box whose area
/// is its share of the space, laid out "squarified" (boxes close to squares). Folders hold their contents (with a
/// name strip when there is room), down to boxes of a few pixels; the many tiny items of a folder become one
/// "small items" box. Files are coloured by kind (video, images, archives, programs, ...). The boxes are drawn
/// once into a cached visual; hovering and the selection only redraw a second visual with an outline on top.
/// </summary>
public sealed class Treemap : FrameworkElement
{
    private const double MinArea = 9;        // Smaller boxes are merged into "small items".
    private const double HeaderHeight = 15;  // Name strip of a folder box.
    private const int MaxDepth = 14;

    /// <summary>One drawn box. Entry -2 = the "small items" rest of a folder.</summary>
    private readonly record struct Cell(Rect Rect, int Entry, int Parent, int Depth, bool IsFolder);

    private readonly DrawingVisual _cells = new();
    private readonly DrawingVisual _overlay = new(); // Hover and selection outlines, above the boxes.
    private readonly List<Cell> _layout = [];
    private FolderTotals? _tree;
    private int _root = -1;
    private bool _dirty = true;
    private int _hover = -1;      // Index into _layout.
    private int _selected = -3;   // Entry to outline (the list's selection).

    private static readonly Brush FolderFill = Frozen(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Pen FolderPen = FrozenPen(Color.FromRgb(0x0F, 0x0F, 0x0F), 1);
    private static readonly Pen CellPen = FrozenPen(Color.FromArgb(0x90, 0x10, 0x10, 0x10), 1);
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush HeaderLabelBrush = Frozen(Color.FromRgb(0xB4, 0xB4, 0xB4));
    private static readonly Brush SmallFill = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Pen HoverPen = FrozenPen(Colors.White, 2);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0x33, 0x99, 0xFF), 2);
    private static readonly Typeface Face = new("Segoe UI");

    /// <summary>File kinds and their colours (muted, for the dark background).</summary>
    private static readonly (string Kind, Color Color, string[] Extensions)[] Kinds =
    [
        ("Video", Color.FromRgb(0x8A, 0x5A, 0xA6), [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".mpg", ".mpeg"]),
        ("Audio", Color.FromRgb(0x3C, 0x93, 0x93), [".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".bank", ".wem"]),
        ("Images", Color.FromRgb(0x5E, 0x96, 0x4C), [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".psd", ".raw", ".dds", ".tga", ".exr", ".avif", ".ico", ".svg"]),
        ("Archives", Color.FromRgb(0xB3, 0x7E, 0x3A), [".zip", ".7z", ".rar", ".tar", ".gz", ".xz", ".bz2", ".zst", ".cab", ".iso", ".img", ".vhd", ".vhdx", ".wim", ".esd", ".pak", ".utoc", ".ucas", ".vpk"]),
        ("Programs", Color.FromRgb(0xA8, 0x4E, 0x4E), [".exe", ".dll", ".sys", ".msi", ".msix", ".appx", ".so", ".pdb", ".lib", ".ocx", ".drv"]),
        ("Documents", Color.FromRgb(0x4A, 0x72, 0xAD), [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".log", ".csv", ".json", ".xml", ".html", ".htm", ".epub", ".odt", ".rtf"]),
        ("Code", Color.FromRgb(0x9A, 0x8F, 0x3E), [".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".java", ".rs", ".go", ".lua", ".ps1", ".bat", ".cmd", ".sh", ".xaml", ".css", ".sql", ".uasset", ".umap"]),
        ("Data", Color.FromRgb(0x55, 0x6E, 0x86), [".bin", ".dat", ".db", ".sqlite", ".mdf", ".ldf", ".vmdk", ".sav", ".cache", ".idx", ".pack", ".blob", ".edb", ".etl", ".tmp"]),
    ];

    private static readonly Dictionary<string, Brush> KindBrushes = BuildKindBrushes();
    private static readonly Brush OtherFill = Frozen(Color.FromRgb(0x6B, 0x70, 0x78));

    public Treemap()
    {
        AddVisualChild(_cells);
        AddVisualChild(_overlay);
        ClipToBounds = true;
        Focusable = false;
        MouseLeave += (_, _) => SetHover(-1);
    }

    /// <summary>The box under the mouse changed: its path, size and kind, or null.</summary>
    public event Action<string?>? HoverChanged;

    /// <summary>A click: the item (entry of the index; -1 none).</summary>
    public event Action<int>? ItemClicked;

    /// <summary>A double-click: open the item.</summary>
    public event Action<int>? ItemOpened;

    /// <summary>A right-click on an item: Windows' menu (screen point in pixels).</summary>
    public event Action<int, Point>? ItemMenu;

    /// <summary>The folder (entry, -1 = the index's root) whose contents are shown.</summary>
    public void Show(FolderTotals? tree, int root)
    {
        _tree = tree;
        _root = root;
        _hover = -1;
        _dirty = true;
        InvalidateVisual();
    }

    /// <summary>Outlines the item that is selected in the list (entry; below the root, any depth).</summary>
    public void Select(int entry)
    {
        if (_selected == entry)
            return;
        _selected = entry;
        DrawOverlay();
    }

    public static string KindOf(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        foreach (var (kind, _, extensions) in Kinds)
        {
            if (extensions.Contains(extension))
                return kind;
        }
        return "Other";
    }

    // ---------- Visual ----------

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _cells : _overlay;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // The whole area takes the mouse.
        if (_dirty)
        {
            _dirty = false;
            Layout();
            DrawCells();
        }
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        foreach (var cell in _layout)
        {
            if (cell.Entry == _selected)
            {
                dc.DrawRectangle(null, SelectedPen, Inset(cell.Rect, 1));
                break;
            }
        }
        if (_hover >= 0 && _hover < _layout.Count)
            dc.DrawRectangle(null, HoverPen, Inset(_layout[_hover].Rect, 1));
    }

    private static Rect Inset(Rect rect, double by) =>
        rect.Width > 2 * by && rect.Height > 2 * by ? new Rect(rect.X + by, rect.Y + by, rect.Width - 2 * by, rect.Height - 2 * by) : rect;

    // ---------- Layout (squarified) ----------

    private void Layout()
    {
        _layout.Clear();
        if (_tree is null || ActualWidth < 4 || ActualHeight < 4)
            return;
        LayoutChildren(_root, new Rect(0, 0, ActualWidth, ActualHeight), 1);
    }

    /// <summary>The contents of a folder in <paramref name="area"/>; folders that are big enough get their contents too.</summary>
    private void LayoutChildren(int folder, Rect area, int depth)
    {
        var tree = _tree!;
        long total = tree.Total(folder);
        if (total <= 0 || area.Width < 1 || area.Height < 1)
            return;
        double scale = area.Width * area.Height / total;
        var items = new List<(int Entry, double Area)>();
        double rest = 0;
        int restCount = 0;
        foreach (int child in tree.Children(folder))
        {
            double childArea = tree.Total(child) * scale;
            if (childArea <= 0)
                continue;
            if (childArea < MinArea)
            {
                rest += childArea;
                restCount++;
            }
            else
            {
                items.Add((child, childArea));
            }
        }
        items.Sort((a, b) => b.Area.CompareTo(a.Area));
        if (restCount > 0)
            items.Add((-2, rest));

        int first = _layout.Count;
        Squarify(items, area, folder, depth);
        int last = _layout.Count;
        for (int i = first; i < last; i++)
        {
            var cell = _layout[i];
            if (!cell.IsFolder || depth >= MaxDepth)
                continue;
            var inner = Inset(cell.Rect, 1);
            if (inner.Width >= 40 && inner.Height >= HeaderHeight + 12)
                inner = new Rect(inner.X, inner.Y + HeaderHeight, inner.Width, inner.Height - HeaderHeight);
            if (inner.Width * inner.Height >= 4 * MinArea)
                LayoutChildren(cell.Entry, inner, depth + 1);
        }
    }

    /// <summary>Rows of boxes along the shorter side, each row as long as it keeps the boxes closest to squares.</summary>
    private void Squarify(List<(int Entry, double Area)> items, Rect area, int parent, int depth)
    {
        var tree = _tree!;
        int start = 0;
        double sum = items.Sum(i => i.Area);
        // The areas were scaled to the whole folder area; the sum may differ a little (empty items, rounding).
        double fix = sum > 0 ? area.Width * area.Height / sum : 1;
        while (start < items.Count && area.Width > 0.5 && area.Height > 0.5)
        {
            double side = Math.Min(area.Width, area.Height);
            int end = start;
            double rowArea = 0, worst = double.MaxValue;
            while (end < items.Count)
            {
                double a = items[end].Area * fix;
                double next = rowArea + a;
                double first = items[start].Area * fix;
                double ratio = Math.Max(side * side * first / (next * next), next * next / (side * side * a));
                if (end > start && ratio > worst)
                    break;
                rowArea = next;
                worst = ratio;
                end++;
            }
            if (area.Width >= area.Height)
            {
                double width = Math.Min(area.Width, rowArea / area.Height);
                double y = area.Y;
                for (int i = start; i < end; i++)
                {
                    double height = items[i].Area * fix / width;
                    Add(new Rect(area.X, y, width, height), items[i].Entry, parent, depth);
                    y += height;
                }
                area = new Rect(area.X + width, area.Y, Math.Max(0, area.Width - width), area.Height);
            }
            else
            {
                double height = Math.Min(area.Height, rowArea / area.Width);
                double x = area.X;
                for (int i = start; i < end; i++)
                {
                    double width = items[i].Area * fix / height;
                    Add(new Rect(x, area.Y, width, height), items[i].Entry, parent, depth);
                    x += width;
                }
                area = new Rect(area.X, area.Y + height, area.Width, Math.Max(0, area.Height - height));
            }
            start = end;
        }

        void Add(Rect rect, int entry, int parentEntry, int level) =>
            _layout.Add(new Cell(rect, entry, parentEntry, level, entry >= 0 && tree.Index.IsDirectory(entry)));
    }

    // ---------- Drawing ----------

    private void DrawCells()
    {
        using var dc = _cells.RenderOpen();
        if (_tree is null)
            return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var cell in _layout)
        {
            var rect = cell.Rect;
            if (cell.Entry == -2)
            {
                dc.DrawRectangle(SmallFill, CellPen, rect);
                continue;
            }
            string name = _tree.Index.Name(cell.Entry).ToString();
            if (cell.IsFolder)
            {
                dc.DrawRectangle(FolderFill, FolderPen, rect);
                if (rect.Width >= 40 && rect.Height >= HeaderHeight + 12)
                    Label(dc, name, new Rect(rect.X + 4, rect.Y + 1, rect.Width - 8, HeaderHeight - 1), HeaderLabelBrush, dpi, 10.5);
            }
            else
            {
                dc.DrawRectangle(KindBrushes.GetValueOrDefault(KindOf(name), OtherFill), CellPen, rect);
                if (rect.Width >= 56 && rect.Height >= 18)
                    Label(dc, name, new Rect(rect.X + 4, rect.Y + 2, rect.Width - 8, rect.Height - 4), LabelBrush, dpi, 11);
            }
        }
    }

    private static void Label(DrawingContext dc, string text, Rect box, Brush brush, double dpi, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi)
        {
            MaxTextWidth = Math.Max(1, box.Width),
            MaxTextHeight = Math.Max(1, box.Height),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        dc.DrawText(formatted, box.TopLeft);
    }

    // ---------- Mouse ----------

    /// <summary>The deepest box under the point (drawn last), or -1.</summary>
    private int CellAt(Point point)
    {
        for (int i = _layout.Count - 1; i >= 0; i--)
        {
            if (_layout[i].Rect.Contains(point))
                return i;
        }
        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover)
            return;
        _hover = index;
        DrawOverlay();
        HoverChanged?.Invoke(index >= 0 ? Describe(_layout[index]) : null);
    }

    private string Describe(Cell cell)
    {
        var tree = _tree!;
        if (cell.Entry == -2)
        {
            string folder = cell.Parent < 0 ? tree.Index.Root : tree.Index.FullPath(cell.Parent);
            return $"Small items in {folder}";
        }
        string kind = cell.IsFolder ? $"{tree.Files(cell.Entry):N0} files, {tree.Folders(cell.Entry):N0} folders" : KindOf(tree.Index.Name(cell.Entry).ToString());
        return $"{tree.Index.FullPath(cell.Entry)}   ·   {DisplayFormat.Size(tree.Total(cell.Entry))}   ·   {kind}";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(CellAt(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int index = CellAt(e.GetPosition(this));
        int entry = index >= 0 ? _layout[index].Entry : -1;
        if (entry < 0)
            return;
        e.Handled = true;
        if (e.ClickCount >= 2)
            ItemOpened?.Invoke(entry);
        else
            ItemClicked?.Invoke(entry);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        int index = CellAt(e.GetPosition(this));
        if (index < 0 || _layout[index].Entry < 0)
            return;
        e.Handled = true;
        ItemMenu?.Invoke(_layout[index].Entry, PointToScreen(e.GetPosition(this)));
    }

    // ---------- Brushes ----------

    private static Dictionary<string, Brush> BuildKindBrushes()
    {
        var brushes = new Dictionary<string, Brush>();
        foreach (var (kind, color, _) in Kinds)
            brushes[kind] = Frozen(color);
        return brushes;
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
