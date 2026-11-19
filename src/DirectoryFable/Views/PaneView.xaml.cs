using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Shell;
using DirectoryFable.Infrastructure;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>One side of the window: tab strip on top, the selected tab's file list below.</summary>
public partial class PaneView : UserControl
{
    private readonly Dictionary<TabViewModel, FilePane> _panes = [];
    private PaneViewModel? _viewModel;

    // Tab drag-to-reorder state.
    private TabViewModel? _dragTab;
    private Point _dragStart;
    private bool _dragging;
    private DragGhost? _ghost;

    public PaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => LimitTabStripWidth();
    }

    private bool _showsMemory;

    /// <summary>The tabs' status bars show DirectoryFable's RAM use (set for one side of the window).</summary>
    public bool ShowsMemory
    {
        get => _showsMemory;
        set
        {
            _showsMemory = value;
            foreach (var pane in _panes.Values)
                pane.ShowsMemory = value;
        }
    }

    /// <summary>Moves keyboard focus into the visible tab's file list.</summary>
    public void FocusList()
    {
        if (_viewModel?.SelectedTab is { } tab && _panes.TryGetValue(tab, out var pane))
            pane.FocusList();
    }

    /// <summary>Reloads the visible tab and keeps its selection (action bar "Refresh both").</summary>
    public Task RefreshAsync() =>
        _viewModel?.SelectedTab is { } tab && _panes.TryGetValue(tab, out var pane)
            ? pane.RefreshKeepSelectionAsync()
            : Task.CompletedTask;

    /// <summary>The visible tab's columns (shown columns in order, with widths), or null.</summary>
    public List<ColumnState>? VisibleColumns =>
        _viewModel?.SelectedTab?.ColumnLayout is { Count: > 0 } layout
            ? [.. layout.Select(c => new ColumnState { Key = c.Key, Width = c.Width })]
            : null;

    /// <summary>Gives every tab of this pane these columns (lists that exist are updated at once).</summary>
    public int ApplyColumnsToAllTabs(List<ColumnState> layout)
    {
        if (_viewModel is null)
            return 0;
        foreach (var tab in _viewModel.Tabs)
        {
            if (_panes.TryGetValue(tab, out var pane))
                pane.ApplyColumns(layout);
            else
                tab.ColumnLayout = [.. layout.Select(c => new ColumnState { Key = c.Key, Width = c.Width })];
        }
        return _viewModel.Tabs.Count;
    }

    // ---------- Keep one FilePane per tab ----------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Tabs.CollectionChanged -= Tabs_CollectionChanged;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        foreach (var old in _panes.Values)
            old.ShowsMemory = false;
        TabHost.Children.Clear();
        _panes.Clear();

        _viewModel = DataContext as PaneViewModel;
        if (_viewModel is null)
            return;

        _viewModel.Tabs.CollectionChanged += Tabs_CollectionChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        foreach (var tab in _viewModel.Tabs)
            AddPane(tab);
        ShowSelectedPane();
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move)
            return; // Order only matters in the tab strip.

        // Simple and safe: sync the pane set with the tab list.
        var current = _viewModel!.Tabs.ToHashSet();
        foreach (var gone in _panes.Keys.Where(t => !current.Contains(t)).ToList())
        {
            _panes[gone].ShowsMemory = false; // Lets go of the memory timer.
            TabHost.Children.Remove(_panes[gone]);
            _panes.Remove(gone);
        }
        foreach (var tab in _viewModel.Tabs.Where(t => !_panes.ContainsKey(t)))
            AddPane(tab);

        ShowSelectedPane();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.SelectedTab))
        {
            ShowSelectedPane();
            if (_viewModel!.IsActive)
                Dispatcher.BeginInvoke(FocusList, System.Windows.Threading.DispatcherPriority.Loaded);
            ScrollSelectedTabIntoView();
        }
    }

    private void AddPane(TabViewModel tab)
    {
        var pane = new FilePane { DataContext = tab, Visibility = Visibility.Collapsed, ShowsMemory = _showsMemory };
        pane.OpenInNewTabRequested += async path =>
        {
            if (_viewModel is not null)
                await _viewModel.AddTabAsync(path, select: false);
        };
        _panes[tab] = pane;
        TabHost.Children.Add(pane);
    }

    private void ShowSelectedPane()
    {
        foreach (var (tab, pane) in _panes)
            pane.Visibility = tab == _viewModel?.SelectedTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Keyboard ----------

    private async void Pane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Tab && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _viewModel.SelectNextTab(+1);
        }
        else if (e.Key == Key.Tab && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            _viewModel.SelectNextTab(-1);
        }
        else if (e.Key == Key.T && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await _viewModel.DuplicateTabAsync();
        }
        else if (e.Key == Key.W && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_viewModel.SelectedTab is { } tab)
                _viewModel.CloseTab(tab);
        }
    }

    // ---------- Tab strip mouse handling ----------

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel tab } element)
            return;

        e.Handled = true; // Do not let the strip see this as a click on empty space.
        _viewModel!.SelectedTab = tab;
        _dragTab = tab;
        _dragStart = e.GetPosition(TabItems);
        _dragging = false;
        element.CaptureMouse();
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point pos = e.GetPosition(TabItems);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;
            _dragging = true;

            // A picture of the tab follows the mouse; the original spot is faded.
            if (sender is FrameworkElement source)
                _ghost = new DragGhost(source, TabItems, grabPoint: TabItems.TranslatePoint(_dragStart, source));
            _dragTab.IsDragging = true;
        }
        _ghost?.Move(pos);

        // Find the tab under the mouse and move the dragged tab to its slot.
        for (int i = 0; i < TabItems.Items.Count; i++)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            Point topLeft = container.TranslatePoint(new Point(0, 0), TabItems);
            if (pos.X >= topLeft.X && pos.X < topLeft.X + container.ActualWidth)
            {
                _viewModel!.MoveTab(_dragTab, i);
                break;
            }
        }
    }

    private void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        (sender as UIElement)?.ReleaseMouseCapture();
        _ghost?.Close();
        _ghost = null;
        if (_dragTab is not null)
            _dragTab.IsDragging = false;
        _dragTab = null;
        _dragging = false;
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes a tab, as in browsers.
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            e.Handled = true;
            _viewModel?.CloseTab(tab);
        }
    }

    private async void TabStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click on empty strip space opens a new tab.
        if (e.ClickCount == 2 && _viewModel is not null)
        {
            e.Handled = true;
            await _viewModel.DuplicateTabAsync();
        }
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.DuplicateTabAsync();
    }

    // ---------- Drop files on a tab header ----------

    private System.Windows.Threading.DispatcherTimer? _tabHoverTimer;
    private TabViewModel? _tabHoverTarget;

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: TabViewModel tab } ||
            FileDrop.GetPaths(e.Data) is not { } paths)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = FileDrop.ChooseEffect(e, paths, tab.CurrentPath);

        // Hovering on a tab for a moment opens it, so you can drop into its list (browser style).
        if (_tabHoverTarget != tab)
        {
            _tabHoverTarget = tab;
            _tabHoverTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _tabHoverTimer.Stop();
            _tabHoverTimer.Tick -= TabHoverTimer_Tick;
            _tabHoverTimer.Tick += TabHoverTimer_Tick;
            _tabHoverTimer.Start();
        }
    }

    private void TabHoverTimer_Tick(object? sender, EventArgs e)
    {
        _tabHoverTimer?.Stop();
        if (_tabHoverTarget is not null && _viewModel is not null)
            _viewModel.SelectedTab = _tabHoverTarget;
    }

    private void Tab_DragLeave(object sender, DragEventArgs e)
    {
        _tabHoverTimer?.Stop();
        _tabHoverTarget = null;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _tabHoverTimer?.Stop();
        _tabHoverTarget = null;
        if (sender is FrameworkElement { DataContext: TabViewModel tab } &&
            FileDrop.GetPaths(e.Data) is { } paths)
            FileDrop.Execute(e, paths, tab.CurrentPath, this);
    }

    // ---------- Tab context menu ----------

    private static TabViewModel? MenuTab(object sender) =>
        (sender as FrameworkElement)?.DataContext as TabViewModel;

    private async void TabMenu_New(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.DuplicateTabAsync();
    }

    private async void TabMenu_Duplicate(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && MenuTab(sender) is { } tab)
            await _viewModel.DuplicateTabAsync(tab);
    }

    private void TabMenu_Close(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab)
            _viewModel?.CloseTab(tab);
    }

    private void TabMenu_CloseOthers(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab)
            _viewModel?.CloseOtherTabs(tab);
    }

    private void TabMenu_AddFavorite(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { CurrentPath.Length: > 0 } tab)
            FavoritesViewModel.Current?.Add(tab.CurrentPath);
    }

    /// <summary>"Add to favorites" says when the tab's folder already is a favorite.</summary>
    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: TabViewModel tab } menu)
            return;
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "AddFavorite")))
            (item.Header, item.IsEnabled) = FavoritesViewModel.AddMenuText(tab.CurrentPath);
    }

    // ---------- Layout helpers ----------

    /// <summary>Keeps the "+" button visible: the tab list may not take the full strip width.</summary>
    private void LimitTabStripWidth() =>
        TabScroller.MaxWidth = Math.Max(0, ActualWidth - 28);

    private void ScrollSelectedTabIntoView()
    {
        if (_viewModel?.SelectedTab is not { } tab)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (TabItems.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container)
                container.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
