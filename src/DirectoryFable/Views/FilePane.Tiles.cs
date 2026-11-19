using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DirectoryFable.Models;

namespace DirectoryFable.Views;

/// <summary>
/// Tile view of the file list ("Thumbnails" in the quick bar): the same list - selection, keys, menus, drag and
/// drop, the quick filter all stay - with large tiles instead of rows: Windows' thumbnail of each item (videos
/// with their length), the name and the size. Per tab, stays on in other folders. The list swaps its view
/// (no columns), item style, template and panel (<see cref="TilePanel"/>); group dividers are left out. The
/// size (picture width 128 to 512 DIP) is one for both panes: the slider next to the button, or Ctrl+mouse
/// wheel in the list.
/// </summary>
public partial class FilePane
{
    public const double MinTileSize = 128;
    public const double MaxTileSize = 512;

    private static double _tileSize = 256;

    /// <summary>Raised when <see cref="TileSize"/> changes; both panes follow.</summary>
    private static event Action? TileSizeChanged;

    /// <summary>The width of a tile's picture (DIP); saved in the session.</summary>
    public static double TileSize
    {
        get => _tileSize;
        set
        {
            value = Math.Clamp(Math.Round(value), MinTileSize, MaxTileSize);
            if (value == _tileSize)
                return;
            _tileSize = value;
            TileSizeChanged?.Invoke();
        }
    }

    private bool _tilesShown;
    private ItemsPanelTemplate? _rowsPanel;

    private void InitTiles()
    {
        ApplyTileSize();
        TileSizeChanged += ApplyTileSize; // The panes live as long as the window.
        FileList.PreviewKeyDown += FileList_TilesPreviewKeyDown;
        FileList.SelectionChanged += (_, _) =>
        {
            // A click on one tile sets the start of a Shift+arrow range.
            if (_tilesShown && FileList.SelectedItems.Count == 1 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _tileAnchor = FileList.SelectedIndex;
        };
    }

    private int? _tileAnchor;

    /// <summary>
    /// Arrow keys in the tile view follow the tiles' order (WPF's own moves go by screen position, which jumps
    /// between the category blocks): Left / Right previous / next, Up / Down the row above / below. Shift selects
    /// the range from the last plain move or click, Ctrl moves the focus only.
    /// </summary>
    private void FileList_TilesPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_tilesShown || e.OriginalSource is TextBox)
            return;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if ((dx, dy) == (0, 0) || FindChild<TilePanel>(FileList) is not { } panel || FileList.Items.Count == 0)
            return;
        e.Handled = true;

        int current = Keyboard.FocusedElement is ListViewItem focused
            ? FileList.ItemContainerGenerator.IndexFromContainer(focused)
            : FileList.SelectedIndex;
        int target = current < 0 ? 0 : panel.Neighbor(current, dx, dy);
        if (target < 0)
            return;
        var item = FileList.Items[target];
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            int anchor = Math.Clamp(_tileAnchor ?? Math.Max(current, 0), 0, FileList.Items.Count - 1);
            FileList.SelectedItems.Clear();
            for (int i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
                FileList.SelectedItems.Add(FileList.Items[i]);
        }
        else if (!modifiers.HasFlag(ModifierKeys.Control))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = item;
            _tileAnchor = target;
        }
        FileList.ScrollIntoView(item);
        Dispatcher.BeginInvoke(() => (FileList.ItemContainerGenerator.ContainerFromIndex(target) as ListViewItem)?.Focus(),
            DispatcherPriority.Loaded);
    }

    private void ApplyTileSize()
    {
        double width = TileSize;
        double height = Math.Round(width * 9 / 16); // Videos are 16:9; other pictures fit inside.
        Resources["TilePictureWidth"] = width;
        Resources["TilePictureHeight"] = height;
        Resources["TileWidth"] = width + 20;   // Picture, margins, border.
        Resources["TileHeight"] = height + 74; // Picture, two lines of name, the size.
        if (TileSizeSlider.Value != width)
            TileSizeSlider.Value = width;
    }

    /// <summary>Rows or tiles, as the tab wants.</summary>
    private void ApplyTileMode()
    {
        bool on = _viewModel?.TileMode == true;
        if (on == _tilesShown)
            return;
        _tilesShown = on;
        _rowsPanel ??= FileList.ItemsPanel;
        var keep = FileList.SelectedItem as FileItem;

        FileList.View = on ? null : Columns;
        FileList.ItemContainerStyle = (Style)FindResource(on ? "FileTileStyle" : "FileRowStyle");
        FileList.ItemTemplate = on ? (DataTemplate)FindResource("FileTileTemplate") : null;
        FileList.ItemsPanel = on ? (ItemsPanelTemplate)FindResource("TilePanelTemplate") : _rowsPanel;
        // The tile panel creates its own containers; recycling is for the rows.
        VirtualizingPanel.SetVirtualizationMode(FileList, on ? VirtualizationMode.Standard : VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(FileList, on ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _listScroller = null; // The list's template changed.
        ApplySortAndGroup(); // No group dividers in the tile view.

        if (keep is not null)
            Dispatcher.BeginInvoke(() => FileList.ScrollIntoView(keep), DispatcherPriority.Loaded);
    }

    /// <summary>The "Thumbnails" button: on / off (the Space view closes, the tiles take its place).</summary>
    private void QuickTiles_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        _viewModel.TileMode = !_viewModel.TileMode;
        if (_viewModel.TileMode)
            _viewModel.SpaceMode = false;
        if (_viewModel.IsActive)
            Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
    }

    private void TileSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            TileSize = e.NewValue;
    }

    /// <summary>Ctrl+mouse wheel in the tile view: bigger or smaller tiles.</summary>
    private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_tilesShown || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        TileSize += e.Delta > 0 ? 32 : -32;
        e.Handled = true;
    }
}
