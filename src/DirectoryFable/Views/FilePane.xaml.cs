using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.Search;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

public partial class FilePane : UserControl
{
    private TabViewModel? _viewModel;
    private object? _lastFocusedItem;

    public FilePane()
    {
        InitializeComponent();
        InitQuickActions();
        InitTiles();
        InitColumns();
        InitStatusBar();
        DataContextChanged += OnDataContextChanged;

        // Remember the row with the focus rectangle, so switching panes/tabs returns to it.
        FileList.GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is ListViewItem { DataContext: FileItem item })
                _lastFocusedItem = item;
        };
    }

    /// <summary>Asks the pane to open a folder in a new background tab (middle-click, Ctrl+Enter).</summary>
    public event Action<string>? OpenInNewTabRequested;

    /// <summary>
    /// Selects exactly these rows ("Compare": what is only here or newer here) and shows the first one. Rows that
    /// the quick filter hides stay out.
    /// </summary>
    public void SelectItems(IReadOnlyCollection<FileItem> items)
    {
        FileList.SelectedItems.Clear();
        var wanted = new HashSet<FileItem>(items);
        var shown = FileList.Items.Cast<FileItem>().Where(wanted.Contains).ToList();
        if (shown.Count == 0)
            return;
        // All rows: one step (adding thousands of rows one by one is slow).
        if (shown.Count == FileList.Items.Count)
            FileList.SelectAll();
        else
            foreach (var item in shown)
                FileList.SelectedItems.Add(item);
        FileList.ScrollIntoView(shown[0]);
    }

    /// <summary>Moves keyboard focus into the file list, back to the last focused row.</summary>
    public void FocusList()
    {
        if (_viewModel?.IsThisPc == true)
        {
            Drives.FocusList();
            return;
        }
        if (Space.Visibility == Visibility.Visible)
        {
            Space.FocusList();
            return;
        }
        object? item = _lastFocusedItem is not null && FileList.Items.Contains(_lastFocusedItem)
            ? _lastFocusedItem
            : FileList.SelectedItem ?? (FileList.Items.Count > 0 ? FileList.Items[0] : null);

        bool keepEmpty = FileList.SelectedItems.Count == 0;
        if (item is not null && FileList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            container.Focus();
        else
            FileList.Focus();

        // Focusing a row must not select it (see ViewModel_Navigated).
        if (keepEmpty)
            FileList.SelectedItems.Clear();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Navigated -= ViewModel_Navigated;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
        _viewModel = DataContext as TabViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Navigated += ViewModel_Navigated;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
        AttachView(_viewModel);
        AttachStatusBar(_viewModel);
        ShowContent(tabChanged: true);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.IsThisPc) or nameof(TabViewModel.SpaceMode) or nameof(TabViewModel.CanShowSpace):
                ShowContent(tabChanged: false);
                break;
            case nameof(TabViewModel.TileMode):
                ApplyTileMode();
                break;
            case nameof(TabViewModel.Filter):
                ApplyFilter();
                break;
        }
    }

    // ---------- Selection after a folder loads ----------

    private void ViewModel_Navigated(object? sender, NavigatedEventArgs e)
    {
        if (_viewModel?.IsThisPc == true)
        {
            if (AddressEdit.Visibility == Visibility.Visible)
                EndAddressEdit(focusList: false);
            Drives.Show(e.FocusPath); // Up from a drive: that drive is selected.
            if (_viewModel.IsActive)
                Dispatcher.BeginInvoke(Drives.FocusList, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        if (Space.Visibility == Visibility.Visible)
        {
            if (AddressEdit.Visibility == Visibility.Visible)
                EndAddressEdit(focusList: false);
            Space.Show(_viewModel!.CurrentPath, e.FocusPath); // Up: the folder one came from is selected.
            if (_viewModel.IsActive)
                Dispatcher.BeginInvoke(Space.FocusList, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // Any navigation (keys, buttons) closes a half-typed address.
        if (AddressEdit.Visibility == Visibility.Visible)
            EndAddressEdit(focusList: false);

        // A new folder starts with nothing selected; only the
        // focus rectangle shows where the keyboard is. Refresh keeps the selection.
        FileList.SelectedItems.Clear();

        var wanted = new HashSet<string>(e.SelectPaths, StringComparer.OrdinalIgnoreCase);
        FileItem? focus = null;
        foreach (FileItem item in FileList.Items)
        {
            if (focus is null && e.FocusPath is not null &&
                string.Equals(item.FullPath, e.FocusPath, StringComparison.OrdinalIgnoreCase))
                focus = item;

            if (wanted.Contains(item.FullPath))
            {
                FileList.SelectedItems.Add(item);
                focus ??= item;
            }
        }

        focus ??= FileList.Items.Count > 0 ? (FileItem)FileList.Items[0] : null;
        if (focus is null)
            return;

        FileList.ScrollIntoView(focus);

        // Only take focus in the active pane, so the other pane does not steal it.
        if (_viewModel?.IsActive == true)
        {
            // Containers exist only after layout. Focusing a row does not select it.
            bool keepEmpty = FileList.SelectedItems.Count == 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (FileList.ItemContainerGenerator.ContainerFromItem(focus) is ListViewItem container)
                    container.Focus();

                // When the list already had keyboard focus, WPF selects the newly
                // focused row by itself. Undo that so only the focus rectangle shows.
                if (keepEmpty)
                    FileList.SelectedItems.Clear();
            });
        }
    }

    // ---------- Keyboard and mouse ----------

    private async void Pane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        var modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (modifiers == ModifierKeys.Alt)
        {
            switch (key)
            {
                case Key.Left:
                    e.Handled = true;
                    await _viewModel.GoBackAsync();
                    return;
                case Key.Right:
                    e.Handled = true;
                    await _viewModel.GoForwardAsync();
                    return;
                case Key.Up:
                    e.Handled = true;
                    await _viewModel.GoUpAsync();
                    return;
                case Key.D:
                    e.Handled = true;
                    BeginAddressEdit();
                    return;
                case Key.Enter:
                    // Alt+Enter: own properties window for the focused row (like Explorer's shortcut).
                    if ((Keyboard.FocusedElement as ListViewItem)?.DataContext is FileItem focused)
                    {
                        e.Handled = true;
                        ShowProperties(focused);
                    }
                    return;
            }
        }

        if ((modifiers == ModifierKeys.Control && key == Key.L) || (modifiers == ModifierKeys.None && key == Key.F4))
        {
            e.Handled = true;
            BeginAddressEdit();
        }
        else if (modifiers == ModifierKeys.None && key == Key.F5)
        {
            e.Handled = true;
            await RefreshPaneAsync();
        }
        else if (modifiers == ModifierKeys.None && key == Key.Space && _viewModel.Filter.Length > 0 &&
                 FileList.IsKeyboardFocusWithin && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            // A space between filter words (the list would take the key for its selection).
            e.Handled = true;
            _viewModel.Filter += " ";
        }
    }

    private async void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null)
            return;

        // A click anywhere else in this pane closes the address box, like Explorer. Clicks on
        // rows already did (they take the focus); empty list space takes no focus by itself.
        if (AddressEdit.Visibility == Visibility.Visible &&
            !(e.OriginalSource is System.Windows.Media.Visual source && (source == AddressEdit || AddressEdit.IsAncestorOf(source))))
        {
            EndAddressEdit(focusList: true);
        }

        // Mouse side buttons, as in Explorer and browsers.
        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            await _viewModel.GoBackAsync();
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            await _viewModel.GoForwardAsync();
        }
        else if (e.ChangedButton == MouseButton.Middle &&
                 ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem
                 {
                     DataContext: FileItem { IsDirectory: true } folder
                 })
        {
            // Middle-click on a folder opens it in a new background tab.
            e.Handled = true;
            OpenInNewTabRequested?.Invoke(folder.FullPath);
        }
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the column header, empty space or the row's properties button.
        if (e.ChangedButton != MouseButton.Left ||
            ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is not ListViewItem row ||
            IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (row.DataContext is FileItem item)
            await OpenAsync(item);
    }

    private async void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || TryHandleFileKey(e) || TryHandleMenuKey(e))
            return;

        var modifiers = Keyboard.Modifiers;
        // The row with the focus rectangle counts, even when nothing is selected.
        var item = (Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem
                   ?? FileList.SelectedItem as FileItem;

        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && item is not null)
        {
            e.Handled = true;
            await OpenAsync(item);
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && item is { IsDirectory: true })
        {
            e.Handled = true;
            OpenInNewTabRequested?.Invoke(item.FullPath);
        }
        else if (e.Key == Key.Back && _viewModel.Filter.Length > 0 && modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            // Backspace edits the quick filter (Ctrl+Backspace clears it); without one it goes up.
            e.Handled = true;
            _viewModel.Filter = modifiers == ModifierKeys.Control ? "" : _viewModel.Filter[..^1];
        }
        else if (e.Key == Key.Back && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await _viewModel.GoUpAsync();
        }
        else if (e.Key == Key.Escape && modifiers == ModifierKeys.None && ClearFilter())
        {
            e.Handled = true;
        }
    }

    // ---------- Navigation buttons ----------

    private async void Back_Click(object sender, RoutedEventArgs e) => await _viewModel!.GoBackAsync();

    private async void Forward_Click(object sender, RoutedEventArgs e) => await _viewModel!.GoForwardAsync();

    private async void Up_Click(object sender, RoutedEventArgs e) => await _viewModel!.GoUpAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshPaneAsync();

    /// <summary>F5 / the refresh button: the folder again, and its drive's folder sizes (or the Space view) read again.</summary>
    private async Task RefreshPaneAsync()
    {
        if (_viewModel is null)
            return;
        if (Space.Visibility == Visibility.Visible)
        {
            Space.Reread();
            return;
        }
        FolderSizes.Reread(_viewModel.CurrentPath);
        await RefreshKeepSelectionAsync();
    }

    private async void Home_Click(object sender, RoutedEventArgs e) => await _viewModel!.GoHomeAsync();

    /// <summary>Drop-down at the right end of the address bar: recent folders of this pane.</summary>
    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        var menu = new ContextMenu { PlacementTarget = HistoryButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (string path in _viewModel.RecentFolders)
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await _viewModel.NavigateAsync(path);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(no recent folders)", IsEnabled = false });

        menu.IsOpen = true;
    }

    private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BreadcrumbSegment segment } && _viewModel is not null)
            await _viewModel.NavigateAsync(segment.Path);
    }

    private OverflowPanel? _crumbPanel;

    /// <summary>The breadcrumb panel lives in a template: take it when it is created.</summary>
    private void CrumbPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not OverflowPanel panel || panel == _crumbPanel)
            return;
        _crumbPanel = panel;
        panel.OverflowChanged += () =>
            CrumbOverflowButton.Visibility = panel.HasOverflow ? Visibility.Visible : Visibility.Collapsed;
        CrumbOverflowButton.Visibility = panel.HasOverflow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"«": the path parts that do not fit, root first; a click opens that folder.</summary>
    private void CrumbOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = CrumbOverflowButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"),
            FontSize = (double)FindResource("UiFontSize"),
        };
        foreach (var segment in _viewModel?.Breadcrumbs ?? [])
        {
            if (Crumbs.ItemContainerGenerator.ContainerFromItem(segment) is not UIElement container ||
                !OverflowPanel.GetIsOverflowed(container))
                continue;
            var item = new MenuItem { Header = segment.Name, ToolTip = segment.Path };
            item.Click += async (_, _) =>
            {
                if (_viewModel is not null)
                    await _viewModel.NavigateAsync(segment.Path);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
            menu.IsOpen = true;
    }

    // ---------- Properties ----------

    private void RowProperties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItem item })
            ShowProperties(item);
    }

    private void ShowProperties(FileItem item)
    {
        if (item.IsInArchive)
        {
            _viewModel?.ShowStatus("Properties are not available inside a ZIP file.");
            return;
        }
        PropertiesWindow.Open(Window.GetWindow(this), item.FullPath, item.Icon);
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        for (; d is not null and not ListViewItem; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase)
                return true;
        }
        return false;
    }

    // ---------- Address bar edit mode ----------

    private void AddressBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Breadcrumb buttons handle their own clicks; this only fires on empty space.
        if (AddressEdit.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            BeginAddressEdit();
        }
    }

    private void BeginAddressEdit()
    {
        AddressEdit.Text = _viewModel?.CurrentPath ?? "";
        AddressEdit.Visibility = Visibility.Visible;
        BreadcrumbBar.Visibility = Visibility.Hidden;

        // Focus after the text box is laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            AddressEdit.Focus();
            AddressEdit.SelectAll();
        });
    }

    private void EndAddressEdit(bool focusList)
    {
        AddressEdit.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Visibility = Visibility.Visible;
        if (focusList)
            FocusList();
    }

    private async void AddressEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndAddressEdit(focusList: true);
        }
        else if (e.Key == Key.Enter && _viewModel is not null)
        {
            e.Handled = true;
            string input = AddressEdit.Text;
            if (string.IsNullOrWhiteSpace(input))
            {
                EndAddressEdit(focusList: true);
                return;
            }

            // A file path opens the file with its default app, like Explorer.
            string candidate;
            try
            {
                candidate = PathHelper.Normalize(input, _viewModel.CurrentPath);
            }
            catch (Exception)
            {
                candidate = "";
            }

            if (candidate.Length > 0 && File.Exists(candidate))
            {
                EndAddressEdit(focusList: true);
                ShellOpen(candidate);
                return;
            }

            if (await _viewModel.NavigateAsync(input))
                EndAddressEdit(focusList: true);
            else
                AddressEdit.SelectAll(); // Keep the box open; the status bar shows the error.
        }
    }

    private void AddressEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AddressEdit.Visibility == Visibility.Visible)
            EndAddressEdit(focusList: false);
    }

    // ---------- Open ----------

    private async Task OpenAsync(FileItem item)
    {
        if (_viewModel is null)
            return;

        if (item.IsDirectory)
        {
            await _viewModel.NavigateAsync(item.FullPath);
        }
        else if (!item.IsInArchive && ZipBrowser.IsZipFile(item.FullPath))
        {
            // A ZIP file opens like a folder.
            await _viewModel.NavigateAsync(item.FullPath);
        }
        else if (item.IsInArchive)
        {
            // A file inside a ZIP: unpack it to a temp folder, then open it with its program.
            if (await ExtractSelectionAsync([item]) is [string extracted])
                ShellOpen(extracted);
        }
        else if (Viewer.ImageFileTypes.OpensInViewer(item.FullPath) && OpenInViewer is not null)
        {
            // Image types open in AstraViewer (settings; only inside DirectoryFable, Windows' defaults stay).
            OpenInViewer(item.FullPath);
        }
        else if (Player.VideoFileTypes.OpensInPlayer(item.FullPath) && OpenInPlayer is not null)
        {
            // Video types play in OpusHC (settings; only inside DirectoryFable, Windows' defaults stay).
            OpenInPlayer(item.FullPath);
        }
        else if (Editor.EditorFileTypes.OpensInNotepad(item.FullPath) && OpenInNotepad is not null)
        {
            // Text types open in Notepad# (settings; only inside DirectoryFable, Windows' defaults stay).
            OpenInNotepad([item.FullPath]);
        }
        else
        {
            ShellOpen(item.FullPath);
        }
    }

    /// <summary>
    /// Unpacks items of the shown ZIP folder to a new temp folder; returns the unpacked paths
    /// (empty on error, with a status message).
    /// </summary>
    private async Task<List<string>> ExtractSelectionAsync(IReadOnlyList<FileItem> items)
    {
        if (_viewModel is null || !ZipBrowser.TrySplit(_viewModel.CurrentPath, out string zipFile, out string inner))
            return [];
        string prefix = inner.Length > 0 ? inner + "/" : "";
        var innerPaths = items.Select(i => prefix + i.Name).ToList();
        try
        {
            return await Task.Run(() => ZipBrowser.ExtractToTemp(zipFile, innerPaths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _viewModel.ShowStatus($"Cannot unpack from {Path.GetFileName(zipFile)}: {ex.Message}");
            return [];
        }
    }

    private static void ShellOpen(string path)
    {
        try
        {
            Process.Start(Shell.ShellLaunch.For(path));
        }
        catch (Exception)
        {
            // No associated app or launch failed; ignore for now.
        }
    }
}
