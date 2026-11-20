using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>
/// Columns, sorting and group dividers of a file pane.
/// The list shows a sorted, grouped view of the tab's items.
/// </summary>
public partial class FilePane
{
    private ListCollectionView? _view;
    private TabViewModel? _viewOwner;

    /// <summary>Set by the main window: folder sizes on / off for all panes (saved in settings.json).</summary>
    public static Action<bool>? SetFolderSizes { get; set; }

    /// <summary>All columns in display order (hidden ones are removed from the GridView).</summary>
    private List<(GridViewColumn Column, SortColumn Key, string Title)> _allColumns = [];

    private void InitColumns()
    {
        _allColumns =
        [
            (NameColumn, SortColumn.Name, "Name"),
            (TypeColumn, SortColumn.Type, "Type"),
            (SizeColumn, SortColumn.Size, "Size"),
            (CreatedColumn, SortColumn.Created, "Created"),
            (ModifiedColumn, SortColumn.Modified, "Modified"),
            (ExtensionColumn, SortColumn.Extension, "Extension"),
        ];
        Columns.Columns.Remove(ExtensionColumn); // Default columns: Name, Type, Size, Created, Modified.
        Columns.ColumnHeaderContextMenu = new ContextMenu();
        Columns.ColumnHeaderContextMenu.Opened += (_, _) => FillHeaderMenu(Columns.ColumnHeaderContextMenu);

        // Every change of order (header drag), visibility or width goes to the tab (session).
        Columns.Columns.CollectionChanged += (_, _) => StoreColumnLayout();
        var widthProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var (column, _, _) in _allColumns)
            widthProperty.AddValueChanged(column, (_, _) => StoreColumnLayout());

        // A click on a group divider (its ToggleButton) is remembered in the tab.
        FileList.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, e) => RememberGroup(e, collapsed: false)));
        FileList.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, e) => RememberGroup(e, collapsed: true)));
    }

    private void RememberGroup(RoutedEventArgs e, bool collapsed)
    {
        if (_viewOwner is not { } tab ||
            e.OriginalSource is not FrameworkElement { DataContext: CollectionViewGroup { Name: string name } })
            return;
        if (collapsed)
            tab.CollapsedGroups.Add(name);
        else
            tab.CollapsedGroups.Remove(name);
    }

    private bool _applyingLayout;

    /// <summary>Shows the tab's saved columns (order, widths); default columns when it has none.</summary>
    private void ApplyColumnLayout(TabViewModel tab)
    {
        if (tab.ColumnLayout is not { Count: > 0 } layout)
        {
            StoreColumnLayout(); // Remember the defaults, so new tabs and the session have them.
            return;
        }

        _applyingLayout = true;
        try
        {
            Columns.Columns.Clear();
            foreach (var state in layout)
            {
                var match = _allColumns.FirstOrDefault(c => c.Key.ToString() == state.Key);
                if (match.Column is null || Columns.Columns.Contains(match.Column))
                    continue;
                if (state.Width >= 20)
                    match.Column.Width = state.Width;
                Columns.Columns.Add(match.Column);
            }
            if (!Columns.Columns.Contains(NameColumn))
                Columns.Columns.Insert(0, NameColumn); // Name is always shown.
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    /// <summary>Takes another column layout (settings page "copy left columns to the right").</summary>
    public void ApplyColumns(List<ColumnState> layout)
    {
        if (_viewOwner is not { } tab)
            return;
        tab.ColumnLayout = [.. layout.Select(c => new ColumnState { Key = c.Key, Width = c.Width })];
        ApplyColumnLayout(tab);
    }

    private void StoreColumnLayout()
    {
        if (_applyingLayout || _viewOwner is not { } tab)
            return;
        tab.ColumnLayout = [.. Columns.Columns.Select(c => new ColumnState
        {
            Key = _allColumns.First(a => a.Column == c).Key.ToString(),
            Width = double.IsNaN(c.Width) ? c.ActualWidth : c.Width,
        })];
    }

    /// <summary>Connects the list to a tab: a sorted, grouped view of its items.</summary>
    private void AttachView(TabViewModel? tab)
    {
        if (_viewOwner is not null)
        {
            _viewOwner.ItemsResetStarting -= OnItemsResetStarting;
            _viewOwner.ItemsResetFinished -= OnItemsResetFinished;
            _viewOwner.FolderSizesChanged -= OnFolderSizesChanged;
        }
        _viewOwner = tab;
        _view?.DetachFromSourceCollection();
        if (tab is null)
        {
            FileList.ItemsSource = null;
            _view = null;
            return;
        }

        ApplyColumnLayout(tab);
        _view = new ListCollectionView(tab.Items);
        ApplySortAndGroup();
        ApplyFilter();
        FileList.ItemsSource = _view;
        tab.ItemsResetStarting += OnItemsResetStarting;
        tab.ItemsResetFinished += OnItemsResetFinished;
        tab.FolderSizesChanged += OnFolderSizesChanged;
    }

    /// <summary>
    /// The first folder sizes arrived: a list sorted by size sorts again. Later live
    /// changes only change the numbers: sorting again every few seconds would make the list jump.
    /// </summary>
    private void OnFolderSizesChanged(bool first)
    {
        if (!first || _view is null || _viewOwner is not { SortColumn: SortColumn.Size })
            return;
        bool hadFocus = FileList.IsKeyboardFocusWithin;
        _view.Refresh();
        if (hadFocus)
            Dispatcher.BeginInvoke(FocusList, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // A folder load replaces all items. A sorted view would re-sort on every Add, and WPF
    // forbids changes while a view's refresh is deferred, so: disconnect the list, let the
    // items be swapped, then build a fresh view (sorted and grouped once).
    private void OnItemsResetStarting()
    {
        FileList.ItemsSource = null;
        _view?.DetachFromSourceCollection();
        _view = null;
    }

    private void OnItemsResetFinished()
    {
        if (_viewOwner is null)
            return;
        _view = new ListCollectionView(_viewOwner.Items);
        ApplySortAndGroup();
        ApplyFilter();
        FileList.ItemsSource = _view;
    }

    private void ApplySortAndGroup()
    {
        if (_view is null || _viewOwner is not { } tab)
            return;

        using (_view.DeferRefresh())
        {
            bool groups = tab.GroupByType && !tab.TileMode; // The tile view has its own categories.
            var sort = new FileItemComparer(tab.SortColumn, tab.SortDescending, groups);
            // Tiles: folders, videos, images, other files (TilePanel draws the headers).
            _view.CustomSort = tab.TileMode ? new TileCategories.Comparer(sort) : sort;
            _view.GroupDescriptions.Clear();
            if (groups)
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileItem.TypeName)));
        }
        UpdateHeaders();
    }

    /// <summary>Click a column header: sort by it; click again: reverse.</summary>
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column, Role: not GridViewColumnHeaderRole.Padding } ||
            _viewOwner is not { } tab)
            return;

        var key = _allColumns.First(c => c.Column == column).Key;
        if (tab.SortColumn == key)
        {
            tab.SortDescending = !tab.SortDescending;
        }
        else
        {
            tab.SortColumn = key;
            tab.SortDescending = false;
        }
        ApplySortAndGroup();
    }

    /// <summary>Header text plus a small arrow on the sorted column (up = ascending).</summary>
    private void UpdateHeaders()
    {
        if (_viewOwner is not { } tab)
            return;

        foreach (var (column, key, title) in _allColumns)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = title });
            if (key == tab.SortColumn)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = tab.SortDescending ? "" : "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 7,
                    Margin = new Thickness(6, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            column.Header = panel;
        }
    }

    /// <summary>Right-click on the column header: group by type, show / hide columns.</summary>
    private void FillHeaderMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_viewOwner is not { } tab)
            return;

        var group = new MenuItem { Header = "Group by type", IsCheckable = true, IsChecked = tab.GroupByType };
        group.Click += (_, _) =>
        {
            tab.GroupByType = !tab.GroupByType;
            ApplySortAndGroup();
        };
        menu.Items.Add(group);
        // The same switch as on the settings page (all panes, saved).
        var sizes = new MenuItem
        {
            Header = "Folder sizes",
            IsCheckable = true,
            IsChecked = Search.FolderSizes.Enabled,
            ToolTip = "Folders show the size of all files below them (NTFS drives, as administrator). Off: no drive is read for it.",
        };
        sizes.Click += (_, _) => SetFolderSizes?.Invoke(!Search.FolderSizes.Enabled);
        menu.Items.Add(sizes);
        menu.Items.Add(new Separator());

        foreach (var (column, _, title) in _allColumns.Skip(1)) // Name is always shown.
        {
            bool visible = Columns.Columns.Contains(column);
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = visible };
            item.Click += (_, _) => ToggleColumn(column);
            menu.Items.Add(item);
        }
    }

    private void ToggleColumn(GridViewColumn column)
    {
        if (!Columns.Columns.Remove(column))
        {
            // Re-insert at its place in the default order.
            int order = _allColumns.FindIndex(c => c.Column == column);
            int index = Columns.Columns.Count(c => _allColumns.FindIndex(a => a.Column == c) < order);
            Columns.Columns.Insert(index, column);
        }
        if (column == SizeColumn)
            _ = _viewOwner?.UpdateFolderSizesAsync(); // Folder sizes only while the Size column is shown.
    }
}
