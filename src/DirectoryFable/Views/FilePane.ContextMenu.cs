using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DirectoryFable.Models;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>Native Windows context menu of a file pane (right-click, Shift+F10, menu key).</summary>
public partial class FilePane
{
    private const int CmdOpenInNewTab = ShellContextMenu.ExtraCommandFirst;
    private const int CmdRefresh = ShellContextMenu.ExtraCommandFirst + 1;
    private const int CmdPaste = ShellContextMenu.ExtraCommandFirst + 2;
    private const int CmdNewFolder = ShellContextMenu.ExtraCommandFirst + 3;
    private const int CmdAddFavorite = ShellContextMenu.ExtraCommandFirst + 4;
    private const int CmdOpenInNotepad = ShellContextMenu.ExtraCommandFirst + 5;
    private const int CmdOpenInViewer = ShellContextMenu.ExtraCommandFirst + 6;
    private const int CmdOpenInPlayer = ShellContextMenu.ExtraCommandFirst + 7;
    private const int CmdOpenInChat = ShellContextMenu.ExtraCommandFirst + 8;

    /// <summary>Set by the main window: a new GemmaChat chat that works in this folder.</summary>
    public static Action<string>? OpenInChat { get; set; }

    /// <summary>Set when a right-button drag started; the next right-button release opens no menu.</summary>
    private bool _suppressContextMenu;

    private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressContextMenu)
        {
            _suppressContextMenu = false;
            return;
        }

        // The column header has its own menu (group by type, columns).
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != FileList; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is GridViewColumnHeader)
                return;
        }

        e.Handled = true;
        var row = RowAt(e.OriginalSource);
        if (row?.DataContext is FileItem item)
        {
            // Right-click on a row that is not selected: it becomes the only selection.
            if (!row.IsSelected)
            {
                FileList.SelectedItems.Clear();
                FileList.SelectedItem = item;
            }
        }
        else
        {
            // Empty space: the folder's own menu ("New", "Paste", ...).
            FileList.SelectedItems.Clear();
        }

        Point screen = FileList.PointToScreen(e.GetPosition(FileList));
        ShowShellMenu(screen);
    }

    /// <summary>Shift+F10 or the menu key: open the menu at the focused row.</summary>
    private bool TryHandleMenuKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool menuKey = key == Key.Apps || (key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift);
        if (!menuKey)
            return false;

        e.Handled = true;
        FrameworkElement anchor = Keyboard.FocusedElement as ListViewItem ?? (FrameworkElement)FileList;
        Point screen = anchor.PointToScreen(new Point(24, anchor == FileList ? 24 : anchor.ActualHeight));
        ShowShellMenu(screen);
        return true;
    }

    private void ShowShellMenu(Point screen)
    {
        if (_viewModel is null)
            return;

        var selected = SelectedFiles();
        var extras = new List<(int, string)>();
        if (selected is [{ IsDirectory: true } folder])
        {
            extras.Add((CmdOpenInNewTab, "Open in new tab"));
            if (FavoritesViewModel.Current?.Contains(folder.FullPath) == false)
                extras.Add((CmdAddFavorite, "Add to favorites"));
            if (!InArchive && OpenInChat is not null)
                extras.Add((CmdOpenInChat, "Open in GemmaChat (let the AI work in it)"));
        }

        // Files (no folders, not inside an archive): open them in Notepad#.
        if (selected.Count > 0 && selected.All(f => !f.IsDirectory) && !InArchive)
            extras.Add((CmdOpenInNotepad, "Open in Notepad#\tCtrl+E"));
        // One image (of the AstraViewer types, not inside an archive): open it in AstraViewer.
        if (selected is [{ IsDirectory: false } image] && !InArchive && Viewer.ImageFileTypes.IsImage(image.FullPath))
            extras.Add((CmdOpenInViewer, "Open in AstraViewer"));
        // One video (of the OpusHC types, not inside an archive): play it there.
        if (selected is [{ IsDirectory: false } video] && !InArchive && Player.VideoFileTypes.IsVideo(video.FullPath))
            extras.Add((CmdOpenInPlayer, "Open in OpusHC"));

        // The folder background menu from the shell lacks the items Explorer adds itself.
        if (selected.Count == 0)
        {
            extras.Add((CmdRefresh, "Refresh\tF5"));
            if (FileClipboard.GetFiles() is not null)
                extras.Add((CmdPaste, "Paste\tCtrl+V"));
            extras.Add((CmdNewFolder, "New folder\tCtrl+Shift+N"));
            if (FavoritesViewModel.Current?.Contains(_viewModel.CurrentPath) == false)
                extras.Add((CmdAddFavorite, "Add this folder to favorites"));
            if (!InArchive && OpenInChat is not null && System.IO.Directory.Exists(_viewModel.CurrentPath))
                extras.Add((CmdOpenInChat, "Open this folder in GemmaChat"));
        }

        ShellMenuResult result;
        try
        {
            result = ShellContextMenu.Show(
                OwnerHandle, _viewModel.CurrentPath, [.. selected.Select(f => f.FullPath)],
                (int)screen.X, (int)screen.Y, extras,
                extendedVerbs: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                ctrlDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                shiftDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        catch (Exception ex)
        {
            _viewModel.ShowStatus($"Context menu failed: {ex.Message}");
            return;
        }

        switch (result.Outcome)
        {
            case ShellMenuOutcome.Rename when selected.Count > 0:
                BeginRename(selected[0]);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInNewTab:
                OpenInNewTabRequested?.Invoke(selected[0].FullPath);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdRefresh:
                _ = RefreshKeepSelectionAsync();
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdPaste:
                _ = RunSafeAsync(PasteAsync);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdNewFolder:
                _ = RunSafeAsync(NewFolderAsync);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInNotepad:
                OpenSelectedInNotepad();
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInViewer && selected is [var picked]:
                OpenInViewer?.Invoke(picked.FullPath);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInPlayer && selected is [var toPlay]:
                OpenInPlayer?.Invoke(toPlay.FullPath);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInChat:
                OpenInChat?.Invoke(selected.Count == 1 ? selected[0].FullPath : _viewModel.CurrentPath);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdAddFavorite:
                FavoritesViewModel.Current?.Add(selected.Count == 1 ? selected[0].FullPath : _viewModel.CurrentPath);
                break;
            // ShellInvoked: nothing to do; auto-refresh shows what the shell changed.
        }
    }
}
