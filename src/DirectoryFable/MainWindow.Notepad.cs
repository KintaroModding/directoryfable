using System.IO;
using System.Windows;
using DirectoryFable.Infrastructure;
using DirectoryFable.Views;

namespace DirectoryFable;

/// <summary>
/// Explorer / Notepad# / AstraViewer views: the title bar tabs switch everything below
/// the title bar. Each view keeps its state while another one is shown.
/// </summary>
public partial class MainWindow
{
    /// <summary>Set at logoff/shutdown: no "save changes?" question then (it would block Windows).</summary>
    public bool SkipUnsavedPrompt { get; set; }

    /// <summary>True after the window really closed (an exit can be cancelled by unsaved Notepad# files).</summary>
    public bool IsClosed { get; private set; }

    private bool NotepadShown => NotepadPage.Visibility == Visibility.Visible;

    private void InitNotepad()
    {
        Closed += (_, _) => IsClosed = true;
        NotepadPage.ActiveChanged += UpdateTitle;
        NotepadPage.ShowInExplorerRequested += path => _ = ShowInExplorerAsync(path);
        // Explorer: "Open in Notepad#" (file menu) and Ctrl+E.
        FilePane.OpenInNotepad = async paths =>
        {
            ShowNotepad(true);
            await NotepadPage.OpenFilesAsync(paths);
        };
        // Unsaved Notepad# texts are kept a moment after typing stops: the session is saved with them.
        NotepadPage.BackupRequested += SaveSession;

        // Files dragged onto the [Notepad#] title tab (from the panes, Explorer, ...) open there.
        NotepadTab.AllowDrop = true;
        DragEventHandler over = (_, e) =>
        {
            e.Effects = NotepadView.DroppableFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        NotepadTab.DragEnter += over;
        NotepadTab.DragOver += over;
        NotepadTab.Drop += async (_, e) =>
        {
            e.Handled = true;
            var files = NotepadView.DroppableFiles(e.Data);
            if (files.Count == 0)
                return;
            ShowNotepad(true);
            Activate();
            await NotepadPage.OpenFilesAsync(files);
        };
    }

    /// <summary>
    /// The explorer parts below the title bar. Hidden (not collapsed: their layout stays) while
    /// Notepad# or AstraViewer covers them, so WPF does not draw them under it on every scroll step.
    /// </summary>
    private void SetExplorerShown(bool shown)
    {
        var visibility = shown ? Visibility.Visible : Visibility.Hidden;
        foreach (UIElement part in new UIElement[] { Actions, Favorites, FolderTreeView, LeftPane, RightPane, TreeSplitter, PaneSplitter })
            part.Visibility = visibility;
    }

    private void ExplorerTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Explorer);

    private void NotepadTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Notepad);

    /// <summary>Shows Notepad# (true) or the explorer (false).</summary>
    private void ShowNotepad(bool show) => ShowView(show ? MainView.Notepad : MainView.Explorer);

    /// <summary>The views below the title bar, one per title bar tab.</summary>
    private enum MainView { Explorer, Notepad, Viewer, Search, Player, Vault, Chat, Sync }

    private MainView ShownView => NotepadShown ? MainView.Notepad : ViewerShown ? MainView.Viewer
        : SearchShown ? MainView.Search : PlayerShown ? MainView.Player : KeeSeekShown ? MainView.Vault
        : GemmaChatShown ? MainView.Chat : FlashSyncShown ? MainView.Sync : MainView.Explorer;

    /// <summary>
    /// Shows one view below the title bar; the others keep their state. A click on the tab of
    /// the shown view changes nothing (the focus stays where it is, e.g. on a settings page).
    /// </summary>
    private void ShowView(MainView view)
    {
        ExplorerTab.IsChecked = view == MainView.Explorer;
        NotepadTab.IsChecked = view == MainView.Notepad;
        AstraViewerTab.IsChecked = view == MainView.Viewer;
        SearchTab.IsChecked = view == MainView.Search;
        PlayerTab.IsChecked = view == MainView.Player;
        KeeSeekTab.IsChecked = view == MainView.Vault;
        GemmaChatTab.IsChecked = view == MainView.Chat;
        FlashSyncTab.IsChecked = view == MainView.Sync;
        if (view == ShownView)
            return;
        if (view != MainView.Player && PlayerPage.IsFullScreen)
            PlayerPage.SetFullScreen(false);
        if (view != MainView.Vault && KeeSeekShown)
            KeeSeekPage.OnHidden(); // With its timer off, KeeSeek locks when it is left.

        if (view != MainView.Explorer)
            ShowSettings(false); // The explorer's settings page belongs to the explorer.
        NotepadPage.Visibility = view == MainView.Notepad ? Visibility.Visible : Visibility.Collapsed;
        AstraViewerPage.Visibility = view == MainView.Viewer ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = view == MainView.Search ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = view == MainView.Player ? Visibility.Visible : Visibility.Collapsed;
        KeeSeekPage.Visibility = view == MainView.Vault ? Visibility.Visible : Visibility.Collapsed;
        GemmaChatPage.Visibility = view == MainView.Chat ? Visibility.Visible : Visibility.Collapsed;
        FlashSyncPage.Visibility = view == MainView.Sync ? Visibility.Visible : Visibility.Collapsed;
        SetExplorerShown(view == MainView.Explorer);
        switch (view)
        {
            case MainView.Notepad:
                NotepadPage.FocusEditor();
                break;
            case MainView.Viewer:
                AstraViewerPage.FocusView();
                break;
            case MainView.Search:
                SearchPage.FocusQuery();
                break;
            case MainView.Player:
                PlayerPage.FocusView();
                PlayerShownFirstTime();
                break;
            case MainView.Vault:
                KeeSeekPage.OnShown();
                break;
            case MainView.Chat:
                GemmaChatPage.OnShown();
                break;
            case MainView.Sync:
                FlashSyncPage.OnShown();
                break;
            default:
                ActivePaneView.FocusList();
                break;
        }
        UpdateTitle();
    }
}
