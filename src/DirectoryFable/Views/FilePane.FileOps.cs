using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DirectoryFable.Models;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>File operations of a file pane: clipboard, delete, rename, new folder.</summary>
public partial class FilePane
{
    private IntPtr OwnerHandle => Window.GetWindow(this) is { } window
        ? new WindowInteropHelper(window).Handle
        : IntPtr.Zero;

    /// <summary>Handles file operation keys. Returns true when the key was used.</summary>
    private bool TryHandleFileKey(KeyEventArgs e)
    {
        const ModifierKeys none = ModifierKeys.None;
        const ModifierKeys ctrl = ModifierKeys.Control;

        Func<Task>? action = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.C, ctrl) => () => CopyToClipboard(cut: false),
            (Key.X, ctrl) => () => CopyToClipboard(cut: true),
            (Key.V, ctrl) => PasteAsync,
            (Key.Delete, none) => () => DeleteSelectedAsync(permanent: false),
            (Key.Delete, ModifierKeys.Shift) => () => DeleteSelectedAsync(permanent: true),
            (Key.F2, none) => BeginRenameFocused,
            (Key.N, ctrl | ModifierKeys.Shift) => NewFolderAsync,
            (Key.E, ctrl) => () => { OpenSelectedInNotepad(); return Task.CompletedTask; },
            _ => null,
        };

        if (action is null)
            return false;

        e.Handled = true;
        _ = RunSafeAsync(action);
        return true;
    }

    /// <summary>Errors go to the status line instead of crashing the app.</summary>
    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _viewModel?.ShowStatus(ex.Message);
        }
    }

    private List<FileItem> SelectedFiles() => [.. FileList.SelectedItems.Cast<FileItem>()];

    /// <summary>Set by the main window: opens an image in AstraViewer (and puts it at the top of its history).</summary>
    public static Action<string>? OpenInViewer { get; set; }

    /// <summary>Set by the main window: plays a video in OpusHC.</summary>
    public static Action<string>? OpenInPlayer { get; set; }

    /// <summary>Set by the main window: opens files in Notepad#.</summary>
    public static Action<IReadOnlyList<string>>? OpenInNotepad { get; set; }

    /// <summary>Set by the main window: shows SonnetSearch, limited to a folder and its subfolders.</summary>
    public static Action<string>? SearchInFolder { get; set; }

    /// <summary>Ctrl+E / "Open in Notepad#": the selected files (folders are skipped).</summary>
    private void OpenSelectedInNotepad()
    {
        if (InArchive)
        {
            _viewModel?.ShowStatus("Copy the file out of the archive first, then open it in Notepad#.");
            return;
        }
        var files = SelectedFiles().Where(f => !f.IsDirectory).Select(f => f.FullPath).ToList();
        if (files.Count > 0)
            OpenInNotepad?.Invoke(files);
    }

    /// <summary>Reloads this tab and keeps the current selection (F5, refresh button, other panes' changes).</summary>
    public Task RefreshKeepSelectionAsync()
    {
        var keep = SelectedFiles().Select(i => i.FullPath).ToList();
        return _viewModel?.RefreshAsync(keep) ?? Task.CompletedTask;
    }

    // ---------- Clipboard ----------

    /// <summary>True when the shown folder is inside a ZIP file.</summary>
    private bool InArchive => _viewModel is not null && ZipBrowser.IsInArchive(_viewModel.CurrentPath);

    private async Task CopyToClipboard(bool cut)
    {
        var files = SelectedFiles();
        if (files.Count == 0)
            return;

        if (InArchive)
        {
            if (cut)
            {
                _viewModel?.ShowStatus("Cut is not possible inside a ZIP file. Copy (Ctrl+C) works.");
                return;
            }
            // Other programs cannot read inside the ZIP: unpack to a temp folder, put those files on the clipboard.
            var extracted = await ExtractSelectionAsync(files);
            if (extracted.Count > 0)
            {
                FileClipboard.SetFiles(extracted, cut: false);
                _viewModel?.ShowStatus($"{extracted.Count:N0} item(s) copied from the ZIP file to the clipboard");
            }
            return;
        }

        FileClipboard.SetFiles([.. files.Select(f => f.FullPath)], cut);
        _viewModel?.ShowStatus($"{files.Count:N0} item(s) {(cut ? "cut" : "copied")} to the clipboard");
    }

    /// <summary>
    /// Adds files to the shown ZIP folder. With <paramref name="move"/> the originals go to the
    /// Recycle Bin afterwards (only when adding worked).
    /// </summary>
    private async Task<bool> AddToArchiveAsync(IReadOnlyList<string> paths, bool move)
    {
        if (_viewModel is null || !ZipBrowser.TrySplit(_viewModel.CurrentPath, out string zipFile, out string inner))
            return false;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await Task.Run(() => ZipBrowser.Add(zipFile, inner, paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _viewModel.ShowStatus($"Cannot add to {Path.GetFileName(zipFile)}: {ex.Message}");
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (move) // Moved into a ZIP file: not undoable (the ZIP keeps the copies).
            await FileOperations.DeleteAsync(OwnerHandle, paths, permanent: false, record: false);
        return true;
    }

    private async Task PasteAsync()
    {
        if (_viewModel is null)
            return;

        if (FileClipboard.GetFiles() is not var (paths, cut))
        {
            // No list of paths: files of a remote session, a mail or a phone come as items without a path.
            if (ShellPaste.HasItems())
                await PasteItemsAsync();
            else
                _viewModel.ShowStatus("The clipboard has no files.");
            return;
        }

        string target = _viewModel.CurrentPath;
        if (InArchive)
        {
            if (await AddToArchiveAsync(paths, move: cut) && cut)
                FileClipboard.Clear();
            await _viewModel.RefreshAsync([.. paths.Select(p => Path.Combine(target, Path.GetFileName(p.TrimEnd('\\'))))]);
            return;
        }

        if (cut && paths.All(p => string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase)))
        {
            _viewModel.ShowStatus("The files are already in this folder.");
            return;
        }

        // Names before the paste: afterwards the new ones are selected. A paste into the same
        // folder makes "name - Copy", which the source names would not find.
        var before = await Task.Run(() => EntryNames(target));

        bool ok = cut
            ? await FileOperations.MoveAsync(OwnerHandle, paths, target)
            : await FileOperations.CopyAsync(OwnerHandle, paths, target);

        // Explorer empties the clipboard after a cut + paste.
        if (cut && ok)
            FileClipboard.Clear();

        var created = (await Task.Run(() => EntryNames(target))).Except(before, StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(target, name)).ToList();
        var pasted = created.Count > 0 ? created : [.. paths.Select(p => Path.Combine(target, Path.GetFileName(p)))];
        await _viewModel.RefreshAsync(pasted);
    }

    /// <summary>
    /// Pastes clipboard files that have no path on a disk (copied in a remote desktop or virtual machine window,
    /// a mail, a phone): Windows' drop target of the folder gets them, see <see cref="ShellPaste"/>.
    /// </summary>
    private async Task PasteItemsAsync()
    {
        if (_viewModel is null)
            return;
        string target = _viewModel.CurrentPath;
        if (InArchive || !Directory.Exists(target))
        {
            _viewModel.ShowStatus("These clipboard files have no path on a disk. Paste them into a normal folder.");
            return;
        }

        _viewModel.ShowStatus("Paste: Windows gets the files from the other computer or app.");
        var result = await ShellPaste.PasteAsync(target);
        if (!result.Ok)
        {
            _viewModel.ShowStatus($"The paste did not work: {result.Problem}");
            return;
        }
        // Files that are there already are selected. A source that sends in the background (a remote session)
        // is still at work: the folder watcher shows its files as they come.
        var pasted = await Task.Run(() => result.Names.Select(name => Path.Combine(target, name))
            .Where(path => File.Exists(path) || Directory.Exists(path)).ToList());
        await _viewModel.RefreshAsync(pasted);
        _viewModel.ShowStatus(pasted.Count > 0 && pasted.Count == result.Names.Count
            ? $"Pasted {pasted.Count} {(pasted.Count == 1 ? "item" : "items")} from the clipboard."
            : "Windows copies the clipboard files in the background.");
    }

    /// <summary>Names of the files and folders in a folder (empty when it cannot be read).</summary>
    private static HashSet<string> EntryNames(string folder)
    {
        try
        {
            return new HashSet<string>(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---------- Delete ----------

    private async Task DeleteSelectedAsync(bool permanent)
    {
        var files = SelectedFiles();
        if (_viewModel is null || files.Count == 0)
            return;

        // Afterwards the focus goes to the row after the deleted block (or before it at the end).
        var selected = files.ToHashSet();
        var all = FileList.Items.Cast<FileItem>().ToList();
        int last = files.Max(all.IndexOf);
        string? focusAfter = all.Skip(last + 1).FirstOrDefault(i => !selected.Contains(i))?.FullPath
                             ?? all.Take(last).LastOrDefault(i => !selected.Contains(i))?.FullPath;

        if (InArchive)
        {
            await DeleteFromArchiveAsync(files);
        }
        else
        {
            if (permanent)
            {
                // DirectoryFable's own (dark) question instead of Windows' one.
                int folders = files.Count(f => f.IsDirectory);
                string what = files.Count == 1 ? $"\"{files[0].DisplayName}\""
                    : folders == 0 ? $"these {files.Count:N0} files"
                    : folders == files.Count ? $"these {files.Count:N0} folders and everything in them"
                    : $"these {files.Count:N0} items (folders with everything in them)";
                var answer = MessageDialog.Show(Window.GetWindow(this),
                    $"Delete {what} permanently?\n\nThey do not go to the Recycle Bin, and Undo cannot bring them back.",
                    "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
                if (answer != MessageBoxResult.Yes)
                    return;
            }
            await FileOperations.DeleteAsync(OwnerHandle, [.. files.Select(f => f.FullPath)], permanent);
        }
        await _viewModel.RefreshAsync([], focusAfter);
    }

    /// <summary>Removes items from the shown ZIP folder. There is no Recycle Bin inside a ZIP: ask first.</summary>
    private async Task DeleteFromArchiveAsync(List<FileItem> files)
    {
        if (_viewModel is null || !ZipBrowser.TrySplit(_viewModel.CurrentPath, out string zipFile, out string inner))
            return;
        if (ZipBrowser.IsInReadOnlyArchive(zipFile))
        {
            _viewModel.ShowStatus("7z and RAR archives can only be read here (look inside, open, copy out).");
            return;
        }

        string what = files.Count == 1 ? $"\"{files[0].Name}\"" : $"{files.Count:N0} items";
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"Delete {what} from {Path.GetFileName(zipFile)}?\n\nThis cannot be undone (no Recycle Bin inside a ZIP file).",
            "DirectoryFable", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        string prefix = inner.Length > 0 ? inner + "/" : "";
        try
        {
            await Task.Run(() => ZipBrowser.Delete(zipFile, [.. files.Select(f => prefix + f.Name)]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _viewModel.ShowStatus($"Cannot delete from {Path.GetFileName(zipFile)}: {ex.Message}");
        }
    }

    // ---------- New folder ----------

    private async Task NewFolderAsync()
    {
        if (_viewModel is null)
            return;
        if (InArchive)
        {
            _viewModel.ShowStatus("New folder is not possible inside a ZIP file yet.");
            return;
        }

        string folder = _viewModel.CurrentPath;
        if (_viewModel.IsThisPc || !Directory.Exists(folder))
        {
            _viewModel.ShowStatus("Open a folder first: a new folder is made in the folder that is shown.");
            return;
        }

        // The name first (own dialog), then the folder is made with it and selected.
        if (NameDialog.AskNewFolder(Window.GetWindow(this), folder) is not { } name)
        {
            FocusList();
            return;
        }
        string path = Path.Combine(folder, name);
        try
        {
            await Task.Run(() => Directory.CreateDirectory(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.ShowStatus($"The folder \"{name}\" could not be made: {ex.Message}");
            return;
        }
        await _viewModel.RefreshAsync([path], path);
        FocusList();
    }

    // ---------- Rename (F2) ----------

    private Task BeginRenameFocused()
    {
        var item = (Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem
                   ?? FileList.SelectedItem as FileItem;
        if (InArchive)
        {
            _viewModel?.ShowStatus("Rename is not possible inside a ZIP file yet.");
            return Task.CompletedTask;
        }
        return item is null ? Task.CompletedTask : RenameAsync(item);
    }

    /// <summary>Asks for the new name in the name dialog, then renames through Windows (undoable) and selects the item.</summary>
    private async Task RenameAsync(FileItem item)
    {
        if (_viewModel is null)
            return;
        FileList.SelectedItems.Clear();
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);

        // Shortcuts are named without their hidden ".lnk" (it is added back), like Explorer.
        if (NameDialog.AskRename(Window.GetWindow(this), item.FullPath, item.IsDirectory, item.HiddenExtension) is not { } newName)
        {
            FocusList();
            return;
        }
        bool ok = await FileOperations.RenameAsync(OwnerHandle, item.FullPath, newName);
        string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName);
        string select = ok ? newPath : item.FullPath;
        await _viewModel.RefreshAsync([select], select);
        FocusList();
    }
}
