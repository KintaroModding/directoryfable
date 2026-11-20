using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;
using DirectoryFable.Views;

namespace DirectoryFable;

public partial class MainWindow : Window
{
    private readonly PaneViewModel _left = new();
    private readonly PaneViewModel _right = new();
    private readonly FavoritesViewModel _favorites = new();
    private readonly FolderTreeViewModel _tree;

    private PaneViewModel ActivePane => _right.IsActive ? _right : _left;
    private PaneView ActivePaneView => _right.IsActive ? RightPane : LeftPane;

    private WindowState _restoreState = WindowState.Normal;
    private DateTime _deactivatedAt = DateTime.MinValue;

    /// <param name="leftFolder">Start folder of the left pane (command line), or null.</param>
    /// <param name="rightFolder">Start folder of the right pane (command line), or null.</param>
    /// <param name="trayMode">True: no taskbar button, close/minimize hide to the tray. False: a normal window.</param>
    /// <param name="settings">settings.json (start folders).</param>
    /// <param name="session">Last session to restore, or null (none, or "restoreSession": false).</param>
    public MainWindow(string? leftFolder, string? rightFolder, bool trayMode, AppSettings settings, SessionState? session)
    {
        InitializeComponent();
        InitTitleBar();
        InitNotepad();
        InitAstraViewer();
        InitOpusPlayer();
        InitKeeSeek();
        InitGemmaChat();
        InitFlashSync();
        InitSearch();
        InitUpdate(settings);
        InitCompare();

        Shell.ShellDrag.Attach(this); // The drag picture of file drags over the window.
        TrayMode = trayMode;
        ShowInTaskbar = !trayMode;
        AllowClose = !trayMode;
        _startSession = session;
        if (session is not null)
            ApplyWindowState(session);

        LeftPane.DataContext = _left;
        RightPane.DataContext = _right;
        RightPane.ShowsMemory = true; // DF's RAM use once, at the window's bottom right.
        // Quick actions "Copy/Move to other": the folder of the pane that does not hold the file pane.
        FilePane.OtherPaneFolder = pane => (LeftPane.IsAncestorOf(pane) ? _right : _left).CurrentFolder;
        SetActive(_left);

        _favorites.Load();
        FavoritesViewModel.Current = _favorites;
        Favorites.DataContext = _favorites;
        Favorites.CurrentFolder = () => ActivePane.SelectedTab?.CurrentPath;
        Favorites.OpenRequested += OpenFavorite;

        _tree = new FolderTreeViewModel(_favorites) { FollowsPane = session?.TreeFollowsPane ?? true };
        FolderTreeView.DataContext = _tree;
        // Switched back on: show the current folder right away.
        _tree.FollowsPaneChanged += () => { if (_tree.FollowsPane) SyncTree(); };
        FolderTreeView.OpenRequested += OpenFromTree;
        // The tree and the window title follow the active pane's folder.
        _left.CurrentFolderChanged += (_, _) => { if (_left.IsActive) ActiveFolderChanged(); };
        _right.CurrentFolderChanged += (_, _) => { if (_right.IsActive) ActiveFolderChanged(); };

        Actions.UndoRequested += RunUndo;
        Actions.SettingsToggled += open => ShowSettings(open);
        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.CopyColumnsLeftToRight = () =>
        {
            if (LeftPane.VisibleColumns is not { } layout)
                return "The left pane has no columns to copy.";
            int tabs = RightPane.ApplyColumnsToAllTabs(layout);
            return $"Done: {layout.Count} columns copied to {tabs} right tab(s).";
        };
        Actions.RefreshBothRequested += () =>
        {
            _ = LeftPane.RefreshAsync();
            _ = RightPane.RefreshAsync();
        };
        _left.TabClosed += RememberClosedTab;
        _right.TabClosed += RememberClosedTab;
        PreviewKeyDown += Window_PreviewKeyDown;
        Deactivated += (_, _) => _deactivatedAt = DateTime.UtcNow;
        StateChanged += Window_StateChanged;
        Closing += Window_Closing;

        Views.SpaceView.SavedColumns = session?.SpaceColumns;
        Views.SpaceView.ShowBlocks = session?.SpaceBlocks ?? true;
        Views.FilePane.TileSize = session?.TileSize ?? 256;
        Views.SpaceView.BlocksShare = session?.SpaceBlocksShare ?? 0.45;
        RecentFolders.Max = settings.RecentFoldersCount;
        RecentFolders.Restore(session?.RecentFolders);
        SettingsPage.RecentFoldersCountChanged += count => RecentFolders.Max = count;
        Search.FolderSizes.Enabled = settings.FolderSizes;
        Search.FolderSizes.CheckInterval = settings.FolderSizesCheckSeconds;
        SettingsPage.CheckSecondsChanged += seconds => Search.FolderSizes.CheckInterval = seconds;
        Views.FilePane.IconsOnly = settings.QuickBarIconsOnly;
        SettingsPage.IconsOnlyChanged += on => Views.FilePane.IconsOnly = on;
        _tree.ShowNetwork(settings.NetworkInTree);
        Views.PaneView.NewTabThisPc = settings.NewTabThisPc;
        SettingsPage.NetworkInTreeChanged += on =>
        {
            _tree.ShowNetwork(on);
            if (on)
                SyncTree(); // The pane may show a network path already.
        };
        SettingsPage.FolderSizesChanged += ApplyFolderSizes;
        // "Folder sizes" in the column header menu: saved like the settings page does.
        Views.FilePane.SetFolderSizes = on =>
        {
            try
            {
                AppSettings.Update(s => s.FolderSizes = on);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Not saved: it still applies until the app closes.
            }
            ApplyFolderSizes(on);
        };

        Loaded += async (_, _) =>
        {
            string defaultLeft = Environment.ExpandEnvironmentVariables(settings.LeftPath);
            string defaultRight = Environment.ExpandEnvironmentVariables(settings.RightPath);

            // The tabs that come back at the start are not "opened" (This PC's recent folders keep their order).
            RecentFolders.Paused = true;
            if (leftFolder is not null || rightFolder is not null)
            {
                // Folders on the command line win over the saved session (used by tests).
                await Task.WhenAll(_left.AddTabAsync(leftFolder ?? defaultLeft), _right.AddTabAsync(rightFolder ?? defaultRight));
            }
            else
            {
                await Task.WhenAll(_left.RestoreAsync(session?.Left, defaultLeft), _right.RestoreAsync(session?.Right, defaultRight));
            }
            RecentFolders.Paused = false;

            await NotepadPage.RestoreAsync(session?.Notepad);
            await AstraViewerPage.RestoreAsync(session?.Viewer);
            SearchPage.RestoreState(session?.Search);
            PlayerPage.RestoreState(session?.Player);
            KeeSeekPage.CategoryId = session?.KeeSeekCategory;
            GemmaChatPage.CurrentChatId = session?.GemmaChatId;
            FlashSyncPage.SelectedJobId = session?.FlashSyncJob;
            if (session?.NotepadShown == true && NotepadPage.Documents.Any())
                ShowView(MainView.Notepad);
            else if (session?.ViewerShown == true && AstraViewerPage.CurrentPath is not null)
                ShowView(MainView.Viewer);
            else if (session?.SearchShown == true)
                ShowView(MainView.Search);
            else if (session?.PlayerShown == true)
                ShowView(MainView.Player);
            else if (session?.KeeSeekShown == true)
                ShowView(MainView.Vault); // Locked: it asks for the master password.
            else if (session?.GemmaChatShown == true)
                ShowView(MainView.Chat);
            else if (session?.FlashSyncShown == true)
                ShowView(MainView.Sync);

            // Restoring tabs queues "focus this list" steps; activate the saved pane after all of them.
            bool rightActive = leftFolder is null && rightFolder is null && session?.RightPaneActive == true;
            await Dispatcher.InvokeAsync(() =>
            {
                SetActive(rightActive ? _right : _left);
                if (NotepadShown)
                    NotepadPage.FocusEditor();
                else if (ViewerShown)
                    AstraViewerPage.FocusView();
                else if (SearchShown)
                    SearchPage.FocusQuery();
                else
                    ActivePaneView.FocusList();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    /// <summary>Folder sizes on / off: the visible tabs fill or clear them at once.</summary>
    private void ApplyFolderSizes(bool on)
    {
        Search.FolderSizes.Enabled = on;
        foreach (var pane in new[] { _left, _right })
        {
            // Off: every tab clears them. On: the visible tabs read; hidden ones catch up when shown.
            foreach (var tab in pane.Tabs.Where(t => !on || t.IsSelected))
                _ = tab.UpdateFolderSizesAsync();
        }
    }

    // ---------- Session ----------

    /// <summary>Window bounds and splitter positions from the last session.</summary>
    private void ApplyWindowState(SessionState session)
    {
        var bounds = new Rect(session.WindowLeft, session.WindowTop, session.WindowWidth, session.WindowHeight);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(bounds, screen);

        // Only when a good part is still on a monitor (a monitor may be gone since).
        if (bounds.Width >= 400 && bounds.Height >= 300 && !visible.IsEmpty && visible.Width >= 200 && visible.Height >= 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        if (session.TreeWidth >= 80)
            TreeColumn.Width = new GridLength(session.TreeWidth);
        if (session.LeftPaneStars > 0 && session.RightPaneStars > 0)
        {
            LeftColumn.Width = new GridLength(session.LeftPaneStars, GridUnitType.Star);
            RightColumn.Width = new GridLength(session.RightPaneStars, GridUnitType.Star);
        }
    }

    /// <summary>Saves window, splitters and tabs to session.json (on close, hide and exit).</summary>
    public void SaveSession()
    {
        // RestoreBounds = the normal-size bounds, also while maximized or minimized.
        Rect normal = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        bool maximized = WindowState == WindowState.Maximized ||
                         (WindowState == WindowState.Minimized && _restoreState == WindowState.Maximized);
        // Started hidden and never shown: the window has no real sizes yet, so the last session's are kept.
        var kept = !_wasOnScreen && WindowState == WindowState.Minimized ? _startSession : null;
        new SessionState
        {
            WindowLeft = kept?.WindowLeft ?? normal.Left,
            WindowTop = kept?.WindowTop ?? normal.Top,
            WindowWidth = kept?.WindowWidth ?? normal.Width,
            WindowHeight = kept?.WindowHeight ?? normal.Height,
            Maximized = maximized,
            TreeWidth = kept?.TreeWidth ?? TreeColumn.ActualWidth,
            // Actual widths as star sizes keep the split ratio, whatever the window size next time.
            LeftPaneStars = kept?.LeftPaneStars ?? LeftColumn.ActualWidth,
            RightPaneStars = kept?.RightPaneStars ?? RightColumn.ActualWidth,
            RightPaneActive = _right.IsActive,
            TreeFollowsPane = _tree.FollowsPane,
            NotepadShown = NotepadShown,
            Notepad = NotepadPage.CaptureState(),
            ViewerShown = ViewerShown,
            Viewer = AstraViewerPage.CaptureState(),
            SearchShown = SearchShown,
            Search = SearchPage.CaptureState(),
            PlayerShown = PlayerShown,
            Player = PlayerPage.CaptureState(),
            KeeSeekShown = KeeSeekShown,
            KeeSeekCategory = KeeSeekPage.CategoryId,
            GemmaChatShown = GemmaChatShown,
            GemmaChatId = GemmaChatPage.CurrentChatId,
            FlashSyncShown = FlashSyncShown,
            FlashSyncJob = FlashSyncPage.SelectedJobId,
            SpaceColumns = Views.SpaceView.SavedColumns,
            SpaceBlocks = Views.SpaceView.ShowBlocks,
            TileSize = Views.FilePane.TileSize,
            SpaceBlocksShare = Views.SpaceView.BlocksShare,
            RecentFolders = RecentFolders.Capture(),
            Left = _left.CaptureState(),
            Right = _right.CaptureState(),
        }.Save();
    }

    // ---------- Tray behaviour ----------

    /// <summary>Tray mode (see settings.json "windowMode"); false = normal taskbar window.</summary>
    public bool TrayMode { get; }

    /// <summary>When false, closing the window only hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Tray icon left-click: hide when the window was in front, otherwise bring it up.</summary>
    public void ToggleFromTray()
    {
        // Clicking the tray icon deactivates the window first, so "was it in front?" means
        // "was it deactivated just now by this click?".
        bool wasInFront = IsVisible && WindowState != WindowState.Minimized &&
                          (IsActive || DateTime.UtcNow - _deactivatedAt < TimeSpan.FromMilliseconds(400));
        if (wasInFront)
            Hide();
        else
            ShowFromTray();
    }

    /// <summary>
    /// "Start hidden": the window is loaded (the session comes back, so a later exit saves it whole) but not seen.
    /// Tray mode: hidden until the tray icon is clicked. Taskbar mode: minimized; the first restore goes to the
    /// maximized state when that is how it would have started.
    /// </summary>
    public void ShowHiddenAtStart(bool maximized)
    {
        _restoreState = maximized ? WindowState.Maximized : WindowState.Normal;
        _maximizeOnRestore = maximized && !TrayMode;
        ShowActivated = false;
        WindowState = WindowState.Minimized;
        Show();
        if (TrayMode)
            Hide();
        ShowActivated = true;
    }

    private bool _maximizeOnRestore;

    /// <summary>The session the window started from (its sizes are kept when the window was never on screen).</summary>
    private readonly SessionState? _startSession;

    /// <summary>The window was on screen at least once (its sizes are real); see SaveSession.</summary>
    private bool _wasOnScreen;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
        // Activate alone may only flash when another app is in front; a Topmost toggle brings it up.
        Topmost = true;
        Topmost = false;
        (_right.IsActive ? RightPane : LeftPane).FocusList();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Without a taskbar button a minimized window would sit as a stub above the
        // taskbar; in tray mode minimize goes to the tray instead.
        if (WindowState == WindowState.Minimized)
        {
            if (TrayMode)
                Hide();
        }
        else
        {
            _wasOnScreen = true;
            if (_maximizeOnRestore && WindowState == WindowState.Normal)
            {
                // Started minimized ("start hidden"): the first restore goes on to maximized.
                _maximizeOnRestore = false;
                WindowState = WindowState.Maximized;
                return;
            }
            _maximizeOnRestore = false;
            _restoreState = WindowState;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A real exit asks about unsaved Notepad# files first (not at logoff: that would block Windows).
        if (AllowClose && !SkipUnsavedPrompt && (!NotepadPage.ConfirmExit(this) || !AstraViewerPage.ConfirmExit(this) || !FlashSyncPage.ConfirmExit(this)))
        {
            e.Cancel = true;
            return;
        }
        // Saved on every close: in tray mode the app may run for days before it exits.
        SaveSession();
        KeeSeekPage.Shutdown(); // Closing the window always locks the vault (also when it only goes to the tray).
        if (AllowClose)
        {
            GemmaChatPage.Shutdown(); // A running answer stops.
            FlashSyncPage.Shutdown(); // A running job stops (asked above; at logoff without a question).
            PlayerPage.Shutdown(); // VLC stops before the app ends.
            return;
        }
        if (PlayerPage.IsFullScreen)
            PlayerPage.SetFullScreen(false);
        e.Cancel = true;
        Hide();
    }

    // ---------- Favorites bar ----------

    /// <summary>Favorites act on the active pane: same tab, or a new tab (middle-click).</summary>
    private async void OpenFavorite(string folder, bool newTab)
    {
        var pane = ActivePane;
        if (newTab || pane.SelectedTab is null)
            await pane.AddTabAsync(folder);
        else
            await pane.SelectedTab.NavigateAsync(folder);
        ActivePaneView.FocusList();
    }

    // ---------- Folder tree ----------

    /// <summary>Tree click: open in the active pane (focus stays in the tree, like Explorer).</summary>
    private async void OpenFromTree(string folder, bool newTab)
    {
        var pane = ActivePane;
        if (newTab || pane.SelectedTab is null)
            await pane.AddTabAsync(folder);
        else if (!PathHelper.IsSamePath(folder, pane.SelectedTab.CurrentPath))
            await pane.SelectedTab.NavigateAsync(folder); // Same folder: nothing (a reload would drop the selection).
    }

    private void SyncTree()
    {
        if (_tree.FollowsPane && ActivePane.CurrentFolder is { Length: > 0 } folder)
            _ = _tree.SelectPathAsync(folder);
    }

    // ---------- Panes ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Notepad#, AstraViewer, Search and OpusHC have their own keys (Tab, Ctrl+Z, arrows, ...).
        if (NotepadShown || ViewerShown || SearchShown || PlayerShown)
            return;

        // The settings page has its own keys (Tab between controls); Esc closes it.
        if (SettingsPage.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowSettings(false);
            }
            return;
        }

        // Ctrl+F: search in the shown folder (and below).
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            SearchHere();
            return;
        }

        // Tab switches between the two panes (not inside a text box).
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            (_left.IsActive ? RightPane : LeftPane).FocusList();
        }
        // Ctrl+Shift+T: reopen the last closed tab (like a browser).
        else if (e.Key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = ReopenClosedTabAsync();
        }
        // Ctrl+Z: undo the last file operation. Text boxes keep their own undo.
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control &&
                 Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            RunUndo();
        }
    }

    // ---------- Closed tabs (Ctrl+Shift+T) ----------

    private const int ClosedTabLimit = 20;
    private readonly List<(PaneViewModel Pane, TabState State, int Index)> _closedTabs = [];

    private void RememberClosedTab(PaneViewModel pane, TabState state, int index)
    {
        _closedTabs.Add((pane, state, index));
        if (_closedTabs.Count > ClosedTabLimit)
            _closedTabs.RemoveAt(0);
    }

    /// <summary>The newest closed tab comes back in its own pane, at its old place, and that pane becomes active.</summary>
    private async Task ReopenClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
            return;
        var (pane, state, index) = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        SetActive(pane);
        await pane.ReopenTabAsync(state, index);
        (pane == _left ? LeftPane : RightPane).FocusList();
    }

    // ---------- Settings page ----------

    /// <summary>Opens or closes the settings page. The panes stay as they are below it.</summary>
    private void ShowSettings(bool open)
    {
        if (open == SettingsPage.IsVisible)
            return;
        if (open)
        {
            SettingsPage.Open();
            SettingsPage.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsPage.Commit();
            SettingsPage.Visibility = Visibility.Collapsed;
            ActivePaneView.FocusList();
        }
        Actions.SettingsOpen = open;
    }

    // ---------- Undo ----------

    /// <summary>Undoes the newest file operation; the result goes to the active pane's status bar.</summary>
    private async void RunUndo()
    {
        string? message;
        try
        {
            message = await UndoManager.UndoAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            message = $"Undo failed: {ex.Message}";
        }
        if (message is not null)
            ActivePane.SelectedTab?.ShowStatus(message);
    }

    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ActivatePane(sender);

        // A click on empty list space (or the tab strip, status bar) takes no keyboard focus by
        // itself: the pane looked active, but Ctrl+V still went to the other pane and pasted
        // there. After the click is processed, move the focus into this pane's list unless the
        // click put it somewhere inside the pane (a row, the address box).
        if (sender is PaneView view)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!view.IsKeyboardFocusWithin)
                    view.FocusList();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void Pane_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ActivatePane(sender);

    private void ActivatePane(object sender)
    {
        if (sender is PaneView { DataContext: PaneViewModel vm })
            SetActive(vm);
    }

    private void SetActive(PaneViewModel vm)
    {
        bool changed = !vm.IsActive;
        _left.IsActive = vm == _left;
        _right.IsActive = vm == _right;
        if (changed && _tree is not null)
            ActiveFolderChanged();
    }

    private void ActiveFolderChanged()
    {
        SyncTree();
        UpdateTitle();
    }
}
