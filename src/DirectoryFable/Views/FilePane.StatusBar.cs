using System.Windows;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>
/// Status bar of a file pane:
/// "📁 sel / folders │ 📄 sel / files │ sel size / total size ... ⓘ free [bar]".
/// Hidden files are always shown, so there is no "hidden" counter.
/// </summary>
public partial class FilePane
{
    private TabViewModel? _statusOwner;

    private void InitStatusBar()
    {
        StatusFolderIcon.Source = ShellInfo.GetIcon("folder", isDirectory: true);
        StatusFileIcon.Source = ShellInfo.GetIcon("file", isDirectory: false);
        FileList.SelectionChanged += (_, _) => UpdateStatusBar();
    }

    private void AttachStatusBar(TabViewModel? tab)
    {
        if (_statusOwner is not null)
            _statusOwner.TotalsChanged -= UpdateStatusBar;
        _statusOwner = tab;
        if (tab is not null)
            tab.TotalsChanged += UpdateStatusBar;
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        if (_statusOwner is not { } tab)
            return;

        var selected = FileList.SelectedItems.Cast<FileItem>().ToList();
        int selectedFolders = selected.Count(i => i.IsDirectory);
        long selectedSize = selected.Where(i => !i.IsDirectory).Sum(i => i.Size ?? 0);

        StatusFolders.Text = $"{selectedFolders:N0} / {tab.FolderCount:N0}";
        StatusFiles.Text = $"{selected.Count - selectedFolders:N0} / {tab.FileCount:N0}";
        StatusSizes.Text = $"{DisplayFormat.Size(selectedSize)} / {DisplayFormat.Size(tab.TotalSize)}";

        if (tab.DriveFree is long free && tab.DriveTotal is long total && total > 0)
        {
            StatusSpacePanel.Visibility = Visibility.Visible;
            StatusFree.Text = DisplayFormat.Size(free);
            StatusSpacePanel.ToolTip = $"{DisplayFormat.Size(free)} free of {DisplayFormat.Size(total)}";
            StatusUsedBar.Width = 60.0 * (total - free) / total;
        }
        else
        {
            StatusSpacePanel.Visibility = Visibility.Collapsed;
        }
    }
}
