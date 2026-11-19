using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>The shared folder tree. Selecting a folder opens it in the active pane.</summary>
public partial class FolderTree : UserControl
{
    private const int CmdOpenInNewTab = ShellContextMenu.ExtraCommandFirst;
    private const int CmdAddFavorite = ShellContextMenu.ExtraCommandFirst + 1;

    private FolderTreeViewModel? _viewModel;
    private FolderNodeViewModel? _dropNode;
    private readonly DispatcherTimer _hoverExpand = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public FolderTree()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.Synced -= OnSynced;
            _viewModel = DataContext as FolderTreeViewModel;
            if (_viewModel is not null)
                _viewModel.Synced += OnSynced;
        };
        _hoverExpand.Tick += (_, _) =>
        {
            // Hovering a folder during a drag opens it, so you can drop deeper (Explorer does this).
            _hoverExpand.Stop();
            if (_dropNode is not null)
                _dropNode.IsExpanded = true;
        };
    }

    /// <summary>Open a folder in the active pane; bool = in a new tab.</summary>
    public event Action<string, bool>? OpenRequested;

    // ---------- Open ----------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel is null || e.NewValue is not FolderNodeViewModel node)
            return;
        _viewModel.SetSelected(node);

        // Selections made by the tree itself (following the pane) must not navigate again.
        if (_viewModel.IsSyncing || node.IsPlaceholder || OpenPath(node) is not { } target)
            return;

        // Collapsing a folder that contains the selected one makes WPF select the collapsed
        // folder. The user did not choose it: the pane stays where it is (a later click on the
        // now selected folder opens it, see Tree_PreviewMouseLeftButtonUp). WPF selects it
        // before IsExpanded = false reaches the node, so decide after the change is complete.
        var old = e.OldValue as FolderNodeViewModel;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            bool selectedByCollapse = old is not null && !node.IsExpanded && node.ContainsLoaded(old);
            if (!selectedByCollapse && node.IsSelected)
                OpenRequested?.Invoke(target, false);
        });
    }

    /// <summary>What a click on a node opens: its folder, This PC for the This PC node, nothing for other virtual nodes.</summary>
    private string? OpenPath(FolderNodeViewModel node) =>
        node == _viewModel?.ThisPc ? Infrastructure.PathHelper.ThisPc : node.IsVirtual ? null : node.Path;

    /// <summary>Row that was already selected when the left button went down on it (not on its expander).</summary>
    private FolderNodeViewModel? _pressedSelected;

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _pressedSelected = !IsOnExpander(e.OriginalSource) && NodeAt(e.OriginalSource) is { IsSelected: true } node ? node : null;

    /// <summary>
    /// A click on the already selected row raises no SelectedItemChanged, but it must still open
    /// the folder (after a collapse moved the selection there, the pane shows another folder).
    /// </summary>
    private void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedSelected;
        _pressedSelected = null;
        if (pressed is { IsPlaceholder: false } && NodeAt(e.OriginalSource) == pressed && OpenPath(pressed) is { } target)
            OpenRequested?.Invoke(target, false);
    }

    private static bool IsOnExpander(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton)
                return true;
            if (d is TreeViewItem)
                return false;
        }
        return false;
    }

    /// <summary>Header button: close every open folder (the pane is not changed).</summary>
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => _viewModel?.CollapseAll();

    private void Tree_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && NodeAt(e.OriginalSource) is { IsVirtual: false } node)
        {
            e.Handled = true;
            OpenRequested?.Invoke(node.Path, true);
        }
    }

    private async void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        // F5: read the selected folder's subfolders again (the tree has no auto-refresh yet).
        if (e.Key == Key.F5 && Tree.SelectedItem is FolderNodeViewModel node)
        {
            e.Handled = true;
            await node.ReloadAsync();
        }
    }

    // ---------- Follow the active pane ----------

    /// <summary>After the tree selected a folder by itself: scroll it into view.</summary>
    private void OnSynced(IReadOnlyList<FolderNodeViewModel> chain) => ScrollToNode(chain, attempt: 0);

    /// <summary>
    /// Scrolls the selected row into view. Only the row line itself (template part "Bd") is
    /// brought into view: a whole TreeViewItem includes all its open sub-rows. Freshly expanded
    /// rows may not exist yet, so it retries a few times.
    /// </summary>
    private void ScrollToNode(IReadOnlyList<FolderNodeViewModel> chain, int attempt)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            ItemsControl parent = Tree;
            TreeViewItem? item = null;
            foreach (var node in chain)
            {
                parent.UpdateLayout();
                if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem next)
                {
                    if (attempt < 5)
                        ScrollToNode(chain, attempt + 1);
                    return;
                }
                item = next;
                parent = next;
            }

            if (item?.Template.FindName("Bd", item) is FrameworkElement row)
                row.BringIntoView();
            else
                item?.BringIntoView();
        });
    }

    // ---------- Shell context menu ----------

    private void Tree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (NodeAt(e.OriginalSource) is not { IsVirtual: false } node)
            return;
        e.Handled = true;

        IntPtr hwnd = Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        Point screen = PointToScreen(e.GetPosition(this));
        string parent = System.IO.Path.GetDirectoryName(node.Path.TrimEnd('\\')) ?? node.Path;
        try
        {
            var extras = new List<(int, string)> { (CmdOpenInNewTab, "Open in new tab") };
            if (FavoritesViewModel.Current?.Contains(node.Path) == false)
                extras.Add((CmdAddFavorite, "Add to favorites"));
            var result = ShellContextMenu.Show(hwnd, parent, [node.Path], (int)screen.X, (int)screen.Y,
                extras,
                extendedVerbs: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                ctrlDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                shiftDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdOpenInNewTab)
                OpenRequested?.Invoke(node.Path, true);
            else if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdAddFavorite)
                FavoritesViewModel.Current?.Add(node.Path);
        }
        catch (Exception)
        {
            // A failing shell extension must not take the app down.
        }
    }

    // ---------- Drop files on a tree folder ----------

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var node = NodeAt(e.OriginalSource);
        // Not on a network computer: it holds shares, not files.
        SetDropNode(node is { IsVirtual: false, IsPlaceholder: false } && !Infrastructure.PathHelper.IsServerPath(node.Path) ? node : null);
        e.Effects = _dropNode is not null && FileDrop.GetPaths(e.Data) is { } paths
            ? FileDrop.ChooseEffect(e, paths, _dropNode.Path)
            : DragDropEffects.None;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => SetDropNode(null);

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var node = _dropNode;
        SetDropNode(null);
        if (node is not null && FileDrop.GetPaths(e.Data) is { } paths)
            FileDrop.Execute(e, paths, node.Path, this);
    }

    private void SetDropNode(FolderNodeViewModel? node)
    {
        if (_dropNode == node)
            return;
        if (_dropNode is not null)
            _dropNode.IsDropTarget = false;
        _dropNode = node;
        _hoverExpand.Stop();
        if (node is not null)
        {
            node.IsDropTarget = true;
            _hoverExpand.Start();
        }
    }

    private FolderNodeViewModel? NodeAt(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != Tree; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem { DataContext: FolderNodeViewModel node })
                return node;
        }
        return null;
    }
}
