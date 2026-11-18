using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Search;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>One file or folder of the Space view.</summary>
public sealed class SpaceRow
{
    private ImageSource? _icon;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsFolder { get; init; }

    /// <summary>The entry in the drive read (for the block chart).</summary>
    public int Entry { get; init; }

    /// <summary>A file's size; a folder's total (all files below it).</summary>
    public long Size { get; init; }

    /// <summary>Files and folders below a folder (all levels).</summary>
    public int Files { get; init; }
    public int Folders { get; init; }

    public DateTime Modified { get; init; }

    /// <summary>Share of the shown folder's total, 0..1.</summary>
    public double Share { get; init; }

    /// <summary>Asked by the visible rows only (on the UI thread); cached by type.</summary>
    public ImageSource? Icon => _icon ??= ShellInfo.GetIcon(FullPath, IsFolder);

    public string SizeText => DisplayFormat.Size(Size);

    public string ShareText => Size == 0 ? "" : Share < 0.001 ? "< " + 0.001.ToString("P1", CultureInfo.CurrentCulture) : Share.ToString("P1", CultureInfo.CurrentCulture);

    public string ShareToolTip => IsFolder
        ? $"{DisplayFormat.Size(Size)} in {Count(Files, "file")} and {Count(Folders, "folder")}"
        : DisplayFormat.Size(Size);

    public GridLength ShareWidth => new(Math.Max(0.0001, Share), GridUnitType.Star);
    public GridLength RestWidth => new(Math.Max(0.0001, 1 - Share), GridUnitType.Star);
    public string FilesText => IsFolder ? Files.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string FoldersText => IsFolder ? Folders.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string ModifiedText => DisplayFormat.Date(Modified);

    /// <summary>"1 file", "2,345 files".</summary>
    public static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} {word}s";
}

/// <summary>
/// The Space view of a pane: what takes the space in the shown folder. Its files and folders with the
/// size of everything below them, biggest first, each with a bar for its share of the folder; at a
/// drive root also files / other / free space. The data comes from the drive's file table (NTFS, as
/// administrator: a few seconds for a whole drive, shared with SonnetSearch and the folder sizes);
/// elsewhere a folder walk of the shown folder. Opening a folder goes deeper (the pane navigates, so
/// Back, Up and the address bar work as usual); Esc returns to the file list, F5 reads again.
/// </summary>
public partial class SpaceView : UserControl
{
    private const int CmdOpenInNewTab = ShellContextMenu.ExtraCommandFirst + 40;

    /// <summary>Column widths, shared by both panes and saved in the session.</summary>
    public static List<ColumnState>? SavedColumns { get; set; }

    /// <summary>The block chart is shown (both panes; saved in the session).</summary>
    public static bool ShowBlocks { get; set; } = true;

    /// <summary>The block chart's share of the height, 0.15 .. 0.85 (saved in the session).</summary>
    public static double BlocksShare { get; set; } = 0.45;

    private sealed record SpaceData(DriveIndex Index, List<SpaceRow>? Rows, long Total, int Files, int Folders, FolderTotals? Tree, int Entry);

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, string> _labels = [];
    private string _folder = "";
    private string? _focusPath;
    private string? _target;
    private DriveIndex? _shownIndex;
    private string? _shownFolder;
    private int _version;
    private string _sort = "Size";
    private bool _descending = true;
    private List<SpaceRow> _rows = [];
    private bool _applyingColumns;
    private bool _columnsApplied;
    private FolderTotals? _shownTree;
    private int _shownEntry = -1;

    public SpaceView()
    {
        InitializeComponent();
        _progressTimer.Tick += (_, _) => ShowProgress();
        foreach (var header in Headers())
            _labels[(string)header.Tag] = (string)header.Content;
        UpdateHeaders();
        var widthProperty = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var column in Columns.Columns)
            widthProperty.AddValueChanged(column, (_, _) => StoreColumns());
        Columns.Columns.CollectionChanged += (_, _) => StoreColumns();
        DriveIndexCache.Loaded += OnIndexLoaded;
        List.SelectionChanged += (_, _) => Blocks.Select((List.SelectedItem as SpaceRow)?.Entry ?? -3);
        Blocks.HoverChanged += text => HintText.Text = text ?? "Point at a box to see what it is. Click: select it in the list; double-click: open.";
        Blocks.ItemClicked += SelectFromBlocks;
        Blocks.ItemOpened += entry =>
        {
            if (_shownTree is { } tree)
                OpenRequested?.Invoke(tree.Index.FullPath(entry), tree.Index.IsDirectory(entry), false);
        };
        Blocks.ItemMenu += (entry, screen) =>
        {
            if (_shownTree is { } tree)
                ShowMenu([tree.Index.FullPath(entry)], tree.Index.IsDirectory(entry), screen);
        };
        ApplyBlocksLayout();
        // Closed (Esc, another view): the drive read is let go; opening the view again reads it again.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                return;
            _version++;
            _progressTimer.Stop();
            Clear();
            _shownIndex = null;
            _shownTree = null;
            Blocks.Show(null, -1);
            DriveIndexCache.Release(this);
        };
    }

    /// <summary>Open a folder (navigate) or a file; bool = in a new tab (folders only).</summary>
    public event Action<string, bool, bool>? OpenRequested;

    /// <summary>Backspace: the folder above.</summary>
    public event Action? UpRequested;

    /// <summary>Esc: back to the file list.</summary>
    public event Action? CloseRequested;

    /// <summary>Shows a folder; <paramref name="focusPath"/> (the folder one came up from) is selected.</summary>
    public void Show(string folder, string? focusPath)
    {
        ApplySavedColumns();
        ApplyBlocksLayout();
        _folder = folder;
        _focusPath = focusPath;
        _ = LoadAsync(reread: false);
    }

    /// <summary>F5: reads the drive (or the walked folder) again.</summary>
    public void Reread() => _ = LoadAsync(reread: true);

    public void FocusList()
    {
        if (List.SelectedItem is null && List.Items.Count > 0)
            List.SelectedIndex = 0;
        if (List.SelectedItem is not null && List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListViewItem item)
            item.Focus();
        else
            List.Focus();
    }

    // ---------- Reading ----------

    private async Task LoadAsync(bool reread)
    {
        int version = ++_version;
        string folder = _folder;
        if (folder.Length == 0)
            return;
        // The same folder again (F5, a new read): keep the selected row.
        string? keep = PathHelper.IsSamePath(folder, _shownFolder ?? "") ? (List.SelectedItem as SpaceRow)?.Name : null;

        DriveIndex index;
        try
        {
            var load = await SourceAsync(folder, reread);
            if (version != _version)
                return;
            if (!load.IsCompleted)
            {
                if (!PathHelper.IsSamePath(folder, _shownFolder ?? ""))
                    Clear();
                _progressTimer.Start();
                ShowProgress();
            }
            index = await load;
        }
        catch (OperationCanceledException)
        {
            if (version == _version)
                ShowMessage("Reading was stopped. F5 reads again.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            if (version == _version)
                ShowMessage($"Cannot read {folder}: {ex.Message}");
            return;
        }
        finally
        {
            if (version == _version)
                _progressTimer.Stop();
        }
        if (version != _version)
            return;
        var tree = await index.TotalsAsync();
        var data = await Task.Run(() => Build(tree, folder));
        (long Free, long Total)? space = PathHelper.IsDriveRoot(folder) ? await Task.Run(() => DriveSpace.Get(folder)) : null;
        if (version == _version)
            Apply(folder, data, space, keep);
    }

    /// <summary>The drive's file table when it can be read; else a walked tree that holds the folder; else a walk of the folder.</summary>
    private async Task<Task<DriveIndex>> SourceAsync(string folder, bool reread)
    {
        if (await FolderSizes.TableRootAsync(folder) is { } root)
        {
            _target = root;
            return DriveIndexCache.Load(root, reread, walkFolder: false, owner: this);
        }
        if (!reread && DriveIndexCache.Covering(folder) is { } covering)
        {
            _target = null;
            return Task.FromResult(covering);
        }
        string target = Path.TrimEndingDirectorySeparator(folder);
        _target = target;
        return DriveIndexCache.Load(target, reread, walkFolder: true, owner: this);
    }

    private static SpaceData Build(FolderTotals tree, string folder)
    {
        var index = tree.Index;
        if (tree.Find(folder) is not int entry)
            return new SpaceData(index, null, 0, 0, 0, null, -1);
        long total = tree.Total(entry);
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        var rows = new List<SpaceRow>();
        foreach (int c in tree.Children(entry))
        {
            long size = tree.Total(c);
            string name = index.Name(c).ToString();
            rows.Add(new SpaceRow
            {
                Name = name,
                FullPath = prefix + name,
                IsFolder = index.IsDirectory(c),
                Entry = c,
                Size = size,
                Files = tree.Files(c),
                Folders = tree.Folders(c),
                Modified = ToLocal(index.Modified[c]),
                Share = total > 0 ? (double)size / total : 0,
            });
        }
        return new SpaceData(index, rows, total, tree.Files(entry), tree.Folders(entry), tree, entry);
    }

    private static DateTime ToLocal(long fileTime) =>
        fileTime > 0 && fileTime < 2650467743999999999 /* year 9000 */ ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime() : DateTime.MinValue;

    private void ShowProgress()
    {
        if (_target is null || !DriveIndexCache.Progress.TryGetValue(_target, out double value))
            return;
        string where = _target.TrimEnd('\\');
        ShowMessage(value >= 0
            ? $"Reading {where} from its file table … {value * 100:0} %"
            : $"Reading {where} … {-value:N0} objects");
    }

    private void Clear()
    {
        Blocks.Show(null, -1);
        List.ItemsSource = null;
        _rows = [];
        _shownFolder = null;
        TotalRun.Text = "";
        CountsRun.Text = "";
        AgeText.Text = "";
        DrivePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Apply(string folder, SpaceData data, (long Free, long Total)? space, string? keep)
    {
        _shownIndex = data.Index;
        _shownFolder = folder;
        var index = data.Index;
        string method = index.Method == "MFT" ? "file table" : "folder walk";
        AgeText.Text = $"Read at {index.ReadAt:HH:mm:ss} ({method}, {index.ReadTime.TotalSeconds:0.0} s)   ·   F5 reads again   ·   Esc: file list";
        if (data.Rows is null)
        {
            Clear();
            _shownFolder = folder;
            AgeText.Text = $"Read at {index.ReadAt:HH:mm:ss} ({method})";
            ShowMessage("This folder is not in the last read of the drive (new or renamed since then?).\nF5 reads it again.");
            return;
        }

        _rows = data.Rows;
        _shownTree = data.Tree;
        _shownEntry = data.Entry;
        Blocks.Show(data.Tree, data.Entry);
        TotalRun.Text = DisplayFormat.Size(data.Total);
        CountsRun.Text = $"   in {SpaceRow.Count(data.Files, "file")} and {SpaceRow.Count(data.Folders, "folder")}";
        ShowDrive(data.Total, space);
        SortRows();
        ShowMessage(_rows.Count == 0 ? "This folder is empty." : null);

        string? wanted = keep ?? (_focusPath is { } focus ? Path.GetFileName(Path.TrimEndingDirectorySeparator(focus)) : null);
        _focusPath = null;
        var select = wanted is null ? null : _rows.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (select is null && List.Items.Count > 0)
            select = (SpaceRow)List.Items[0];
        List.SelectedItem = select;
        if (select is not null)
            List.ScrollIntoView(select);
        if (IsKeyboardFocusWithin)
            Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
    }

    /// <summary>At a drive root: how the used space splits into files and the rest, and the free space.</summary>
    private void ShowDrive(long files, (long Free, long Total)? space)
    {
        if (space is not { Total: > 0 } drive)
        {
            DrivePanel.Visibility = Visibility.Collapsed;
            return;
        }
        long used = drive.Total - drive.Free;
        long other = Math.Max(0, used - files);
        long shownFiles = Math.Min(files, used);
        FilesPart.Width = new GridLength(Math.Max(0.0001, shownFiles), GridUnitType.Star);
        OtherPart.Width = new GridLength(Math.Max(0.0001, other), GridUnitType.Star);
        FreePart.Width = new GridLength(Math.Max(0.0001, drive.Free), GridUnitType.Star);
        FilesRun.Text = $"Files {DisplayFormat.Size(files)}";
        bool showOther = other > drive.Total / 1000;
        OtherMark.Text = showOther ? "■" : "";
        OtherRun.Text = showOther ? $"Other {DisplayFormat.Size(other)}" : "";
        FreeRun.Text = $"Free {DisplayFormat.Size(drive.Free)} of {DisplayFormat.Size(drive.Total)}";
        DrivePanel.ToolTip = "Other: used space that the file sizes do not explain, such as the drive's own file table " +
                             "and log, unused parts of clusters, and hard links (counted once). Compressed files can make it smaller.";
        DrivePanel.Visibility = Visibility.Visible;
    }

    /// <summary>A drive or folder was read again: the shown folder takes the new values.</summary>
    private void OnIndexLoaded(DriveIndex index)
    {
        if (IsVisible && _shownIndex is not null && string.Equals(index.Root, _shownIndex.Root, StringComparison.OrdinalIgnoreCase) &&
            !ReferenceEquals(index, _shownIndex))
            _ = LoadAsync(reread: false);
    }

    // ---------- Sorting ----------

    private IEnumerable<GridViewColumnHeader> Headers() =>
        Columns.Columns.Select(c => c.Header).OfType<GridViewColumnHeader>();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag, Role: not GridViewColumnHeaderRole.Padding })
            return;
        string key = tag == "Share" ? "Size" : tag;
        if (key == _sort)
        {
            _descending = !_descending;
        }
        else
        {
            _sort = key;
            _descending = key != "Name"; // Biggest, most and newest first; names A-Z.
        }
        UpdateHeaders();
        object? selected = List.SelectedItem;
        SortRows();
        List.SelectedItem = selected;
        if (selected is not null)
            List.ScrollIntoView(selected);
    }

    private void SortRows()
    {
        Comparison<SpaceRow> compare = _sort switch
        {
            "Name" => (a, b) => FileItemComparer.NaturalCompare(a.Name, b.Name),
            "Files" => (a, b) => a.Files.CompareTo(b.Files),
            "Folders" => (a, b) => a.Folders.CompareTo(b.Folders),
            "Modified" => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => a.Size.CompareTo(b.Size),
        };
        var sorted = _rows.ToList();
        sorted.Sort((a, b) =>
        {
            int result = compare(a, b);
            if (_descending)
                result = -result;
            return result != 0 ? result : FileItemComparer.NaturalCompare(a.Name, b.Name);
        });
        List.ItemsSource = sorted;
    }

    /// <summary>Header text plus a small arrow on the sorted column.</summary>
    private void UpdateHeaders()
    {
        foreach (var header in Headers())
        {
            string tag = (string)header.Tag;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = _labels[tag] });
            if ((tag == "Share" ? "Size" : tag) == _sort && tag != "Share")
            {
                panel.Children.Add(new TextBlock
                {
                    Text = _descending ? "" : "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 7,
                    Margin = new Thickness(6, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            header.Content = panel;
        }
    }

    // ---------- Columns (session) ----------

    private void ApplySavedColumns()
    {
        if (_columnsApplied)
            return;
        _columnsApplied = true;
        if (SavedColumns is not { Count: > 0 } saved)
            return;
        _applyingColumns = true;
        try
        {
            foreach (var column in Columns.Columns)
            {
                if (column.Header is GridViewColumnHeader { Tag: string tag } &&
                    saved.FirstOrDefault(c => c.Key == tag) is { Width: >= 20 } state)
                    column.Width = state.Width;
            }
        }
        finally
        {
            _applyingColumns = false;
        }
    }

    private void StoreColumns()
    {
        if (_applyingColumns || !_columnsApplied)
            return;
        SavedColumns = [.. Columns.Columns.Select(c => new ColumnState
        {
            Key = c.Header is GridViewColumnHeader { Tag: string tag } ? tag : "",
            Width = double.IsNaN(c.Width) ? c.ActualWidth : c.Width,
        })];
    }

    // ---------- Input ----------

    private SpaceRow? RowAt(object source) =>
        ItemsControl.ContainerFromElement(List, (DependencyObject)source) is ListViewItem { DataContext: SpaceRow row } ? row : null;

    private void Open(SpaceRow row, bool newTab)
    {
        if (newTab && !row.IsFolder)
            return;
        OpenRequested?.Invoke(row.FullPath, row.IsFolder, newTab);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && RowAt(e.OriginalSource) is { } row)
            Open(row, newTab: false);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && RowAt(e.OriginalSource) is { IsFolder: true } row)
        {
            e.Handled = true;
            Open(row, newTab: true);
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var row = (Keyboard.FocusedElement as ListViewItem)?.DataContext as SpaceRow ?? List.SelectedItem as SpaceRow;
        if (e.Key == Key.Enter && row is not null)
        {
            e.Handled = true;
            Open(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        else if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            UpRequested?.Invoke();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    /// <summary>Windows' menu for the selected rows (Open, Delete, Properties, ...), plus "Open in new tab" for one folder.</summary>
    private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row)
            return;
        e.Handled = true;
        if (!List.SelectedItems.Contains(row))
            List.SelectedItem = row;
        var selected = List.SelectedItems.Cast<SpaceRow>().ToList();
        ShowMenu([.. selected.Select(r => r.FullPath)], selected is [{ IsFolder: true }], PointToScreen(e.GetPosition(this)));
    }

    /// <summary>Windows' menu for items of one folder (+ "Open in new tab" for a single folder).</summary>
    private void ShowMenu(List<string> paths, bool singleFolder, Point screen)
    {
        if (paths.Count == 0)
            return;
        IntPtr hwnd = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        var extras = singleFolder && paths.Count == 1 ? new List<(int, string)> { (CmdOpenInNewTab, "Open in new tab") } : [];
        string folder = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? _folder;
        try
        {
            var result = ShellContextMenu.Show(hwnd, folder, paths, (int)screen.X, (int)screen.Y, extras,
                extendedVerbs: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                ctrlDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                shiftDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdOpenInNewTab)
                OpenRequested?.Invoke(paths[0], true, true);
        }
        catch (Exception)
        {
            // A failing shell extension must not take the app down.
        }
    }

    // ---------- Block chart ----------

    /// <summary>A click on a box: the row of the shown folder that holds it is selected.</summary>
    private void SelectFromBlocks(int entry)
    {
        if (_shownTree is not { } tree)
            return;
        var parents = tree.Index.Parent;
        int top = entry;
        for (int guard = 0; guard < 4096 && top >= 0 && parents[top] != _shownEntry; guard++)
            top = parents[top];
        if (top < 0 || List.ItemsSource is not IEnumerable<SpaceRow> rows || rows.FirstOrDefault(r => r.Entry == top) is not { } row)
            return;
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    private void BlocksToggle_Click(object sender, RoutedEventArgs e)
    {
        ShowBlocks = BlocksToggle.IsChecked == true;
        ApplyBlocksLayout();
    }

    /// <summary>The chart's row: hidden, or its saved share of the height.</summary>
    private void ApplyBlocksLayout()
    {
        BlocksToggle.IsChecked = ShowBlocks;
        double share = Math.Clamp(BlocksShare, 0.15, 0.85);
        ListRow.Height = new GridLength(ShowBlocks ? 1 - share : 1, GridUnitType.Star);
        BlocksRow.Height = ShowBlocks ? new GridLength(share, GridUnitType.Star) : new GridLength(0);
        BlocksRow.MinHeight = ShowBlocks ? 60 : 0;
        SplitterRow.Height = new GridLength(ShowBlocks ? 5 : 0);
        Splitter.Visibility = BlocksPanel.Visibility = ShowBlocks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        double list = ListRow.ActualHeight, blocks = BlocksRow.ActualHeight;
        if (list + blocks > 0)
            BlocksShare = Math.Clamp(blocks / (list + blocks), 0.15, 0.85);
        ApplyBlocksLayout();
    }
}
