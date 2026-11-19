using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;

namespace DirectoryFable.Views;

/// <summary>
/// The middle of a file pane: the file list, This PC's drives (Views/DrivesView) or the Space view
/// (Views/SpaceView). This PC follows the path; the Space view is switched on per tab with the
/// "Space" button (or "Space usage" on a drive in This PC) and stays on while the tab moves through
/// folders, until Esc or the button switches it off.
/// </summary>
public partial class FilePane
{
    private bool _contentHooked;

    private void ShowContent(bool tabChanged)
    {
        HookContent();
        ApplyTileMode();
        var tab = _viewModel;
        bool thisPc = tab?.IsThisPc == true;
        bool space = !thisPc && tab is { SpaceMode: true, CanShowSpace: true };
        bool wasSpace = Space.Visibility == Visibility.Visible;
        string? focus = FocusedItemPath();
        Drives.Visibility = thisPc ? Visibility.Visible : Visibility.Collapsed;
        Space.Visibility = space ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = thisPc || space ? Visibility.Collapsed : Visibility.Visible;
        UpdateFilterBox();

        if (space && (!wasSpace || tabChanged))
        {
            Space.Show(tab!.CurrentPath, focus);
            if (tab.IsActive)
                Dispatcher.BeginInvoke(Space.FocusList, DispatcherPriority.Loaded);
        }
        else if (!space && wasSpace && tab?.IsActive == true)
        {
            Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
        }
    }

    /// <summary>The row with the focus rectangle (or the selected one): the Space view selects it too.</summary>
    private string? FocusedItemPath() =>
        ((Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem ?? FileList.SelectedItem as FileItem)?.FullPath;

    private void HookContent()
    {
        if (_contentHooked)
            return;
        _contentHooked = true;
        Drives.OpenRequested += (path, newTab) =>
        {
            if (newTab)
                OpenInNewTabRequested?.Invoke(path);
            else if (_viewModel is not null)
                _ = _viewModel.NavigateAsync(path);
        };
        Drives.BackRequested += () => _ = _viewModel?.GoBackAsync();
        Drives.SpaceRequested += OpenSpace;

        Space.OpenRequested += (path, isFolder, newTab) =>
        {
            if (newTab)
                OpenInNewTabRequested?.Invoke(path);
            else if (isFolder)
                _ = _viewModel?.NavigateAsync(path);
            else
                _ = OpenAsync(new FileItem { Name = Path.GetFileName(path), FullPath = path, IsDirectory = false });
        };
        Space.UpRequested += () => _ = _viewModel?.GoUpAsync();
        Space.CloseRequested += () =>
        {
            if (_viewModel is not null)
                _viewModel.SpaceMode = false;
        };
    }

    /// <summary>A drive's (or folder's) Space view in this tab.</summary>
    private void OpenSpace(string path)
    {
        if (_viewModel is null)
            return;
        _viewModel.SpaceMode = true;
        if (!PathHelper.IsSamePath(path, _viewModel.CurrentPath))
            _ = _viewModel.NavigateAsync(path);
    }

    /// <summary>The "Space" button: on / off; on This PC it opens the selected drive's Space view.</summary>
    private void QuickSpace_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (_viewModel.IsThisPc)
        {
            if (Drives.SelectedRoot is { } root)
                OpenSpace(root);
            return;
        }
        if (!_viewModel.CanShowSpace)
        {
            _viewModel.ShowStatus("The Space view needs a folder on a drive or a network share.");
            return;
        }
        _viewModel.SpaceMode = !_viewModel.SpaceMode;
    }
}
