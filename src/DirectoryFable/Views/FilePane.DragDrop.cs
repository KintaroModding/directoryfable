using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Models;

namespace DirectoryFable.Views;

/// <summary>Drag and drop of a file pane: start drags from the list, accept drops into it.</summary>
public partial class FilePane
{
    private const double AutoScrollZone = 18;

    private Point? _dragStart;
    private FileItem? _dragItem;
    private FileItem? _selectOnMouseUp;
    private FileItem? _dropRow;
    private ScrollViewer? _listScroller;

    // ---------- Drag source ----------

    private void FileList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
            return;

        _dragStart = null;
        _selectOnMouseUp = null;
        if (e.ChangedButton == MouseButton.Right)
            _suppressContextMenu = false;
        if (RowAt(e.OriginalSource) is not { DataContext: FileItem { IsRenaming: false } item } row)
        {
            // Empty list space: clear the selection, like Explorer.
            // Not on the scroll bar, a column header or a group header; Ctrl/Shift keep it.
            if (Keyboard.Modifiers == ModifierKeys.None && FileList.SelectedItems.Count > 0 &&
                !IsOnListChrome(e.OriginalSource))
            {
                FileList.SelectedItems.Clear();
            }
            return;
        }

        _dragStart = e.GetPosition(FileList);
        _dragItem = item;

        // Pressing on a row that is part of a multi-selection must keep the selection,
        // otherwise you could never drag several files. Single-select on mouse-up instead.
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1 &&
            Keyboard.Modifiers == ModifierKeys.None && row.IsSelected && FileList.SelectedItems.Count > 1)
        {
            e.Handled = true;
            _selectOnMouseUp = item;
            if (!row.IsKeyboardFocused)
                row.Focus();
        }
    }

    /// <summary>True for parts of the list that are not "empty space": scroll bars, column and group headers.</summary>
    private bool IsOnListChrome(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != FileList;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader or GroupItem)
                return true;
        }
        return false;
    }

    private void FileList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _selectOnMouseUp is { } item)
        {
            // It was a click, not a drag: now select only this row.
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = item;
        }
        _selectOnMouseUp = null;
        _dragStart = null;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start ||
            (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed))
            return;

        Vector moved = e.GetPosition(FileList) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Drag the selection; if the pressed row is not selected, drag just that row.
        var items = SelectedFiles();
        if (_dragItem is not null && !items.Contains(_dragItem))
            items = [_dragItem];

        _dragStart = null;
        _selectOnMouseUp = null;
        if (items.Count == 0)
            return;

        // A right-button drag ends with a right-button release: that must not open the context menu.
        _suppressContextMenu = e.RightButton == MouseButtonState.Pressed;

        if (items[0].IsInArchive)
        {
            // Out of a ZIP: other programs cannot read inside it, so unpack the items to a temp
            // folder first and drag those (copy only; the ZIP itself stays unchanged).
            var extracted = ExtractForDrag(items);
            if (extracted.Count > 0)
                DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, extracted.ToArray()), DragDropEffects.Copy);
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    /// <summary>
    /// Unpacks items for a drag. This must finish before the drag starts, so it runs on the UI
    /// thread with a wait cursor (fine for normal sizes; very big archives make it slow).
    /// </summary>
    private List<string> ExtractForDrag(List<FileItem> items)
    {
        if (_viewModel is null || !Shell.ZipBrowser.TrySplit(_viewModel.CurrentPath, out string zipFile, out string inner))
            return [];
        string prefix = inner.Length > 0 ? inner + "/" : "";
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            return Shell.ZipBrowser.ExtractToTemp(zipFile, [.. items.Select(i => prefix + i.Name)]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            _viewModel.ShowStatus($"Cannot unpack from {System.IO.Path.GetFileName(zipFile)}: {ex.Message}");
            return [];
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ---------- Drop target ----------

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        string[]? paths = FileDrop.GetPaths(e.Data);
        if (paths is null || _viewModel is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var folderRow = FolderRowAt(e.OriginalSource);
        SetDropRow(folderRow);
        e.Effects = FileDrop.ChooseEffect(e, paths, folderRow?.FullPath ?? _viewModel.CurrentPath);
        AutoScroll(e.GetPosition(FileList));
    }

    private void FileList_DragLeave(object sender, DragEventArgs e) => SetDropRow(null);

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropRow(null);
        string[]? paths = FileDrop.GetPaths(e.Data);
        if (paths is null || _viewModel is null)
            return;

        string target = FolderRowAt(e.OriginalSource)?.FullPath ?? _viewModel.CurrentPath;
        // Right drag back into the folder the items are in: Duplicate dialog instead of the menu.
        FileDrop.Execute(e, paths, target, this, sameFolderRightDrop: InArchive ? null : items => _ = RunSafeAsync(() => DuplicateAsync(items)));
    }

    /// <summary>A folder row under the mouse becomes the drop target (highlighted).</summary>
    private void SetDropRow(FileItem? row)
    {
        if (_dropRow == row)
            return;
        if (_dropRow is not null)
            _dropRow.IsDropTarget = false;
        _dropRow = row;
        if (row is not null)
            row.IsDropTarget = true;
    }

    /// <summary>WPF lists do not scroll during a drag; scroll when the mouse is near the top or bottom.</summary>
    private void AutoScroll(Point pos)
    {
        _listScroller ??= FindChild<ScrollViewer>(FileList);
        if (_listScroller is null)
            return;
        if (pos.Y < AutoScrollZone + 20) // + column header
            _listScroller.LineUp();
        else if (pos.Y > FileList.ActualHeight - AutoScrollZone)
            _listScroller.LineDown();
    }

    // ---------- Helpers ----------

    private ListViewItem? RowAt(object originalSource) =>
        originalSource is DependencyObject d
            ? ItemsControl.ContainerFromElement(FileList, d) as ListViewItem
            : null;

    private FileItem? FolderRowAt(object originalSource) =>
        RowAt(originalSource)?.DataContext is FileItem { IsDirectory: true } folder ? folder : null;

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}
