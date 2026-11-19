using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// Shared drop logic for file lists and tab headers: which effect a drop gets,
/// whether a target is valid, and running the operation (with the right-drag menu).
/// </summary>
public static class FileDrop
{
    /// <summary>True when the current drag is done with the right mouse button (seen in DragOver).</summary>
    private static bool _rightDrag;

    /// <summary>Paths in a drag (files from our panes, Explorer, other apps). Null: not files.</summary>
    public static string[]? GetPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
            ? paths
            : null;

    /// <summary>
    /// Effect for a drag over <paramref name="targetFolder"/>, Explorer rules:
    /// Ctrl = copy, Shift = move, Alt or Ctrl+Shift = shortcut; no key = move on the
    /// same drive, copy to another drive. Returns None when the drop makes no sense.
    /// </summary>
    public static DragDropEffects ChooseEffect(DragEventArgs e, string[] sources, string targetFolder)
    {
        // Only DragOver sees the button; at drop time it is already released.
        _rightDrag = e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton);
        var effect = ComputeEffect(e, sources, targetFolder, _rightDrag);
        // The text at the drag picture ("Move to <folder>"), see Shell/ShellDrag.cs.
        Shell.ShellDrag.TargetFolder = effect == DragDropEffects.None ? null : targetFolder;
        return effect;
    }

    private static DragDropEffects ComputeEffect(DragEventArgs e, string[] sources, string targetFolder, bool rightDrag)
    {
        if (!IsValidTarget(sources, targetFolder))
            return DragDropEffects.None;

        // Right drag: the menu decides later; show the default as the cursor.
        DragDropEffects wanted = PreferredEffect(e.KeyStates, sources, targetFolder);

        // Right drag back into the items' own folder: accept it as a copy, so the drop arrives
        // (file lists then open the Duplicate dialog, other targets the menu).
        if (rightDrag && wanted == DragDropEffects.Move && sources.All(s => IsInFolder(s, targetFolder)))
            wanted = DragDropEffects.Copy;

        // Moving files into the folder they are already in does nothing.
        if (wanted == DragDropEffects.Move && sources.All(s => IsInFolder(s, targetFolder)))
            return DragDropEffects.None;

        if (e.AllowedEffects.HasFlag(wanted))
            return wanted;
        foreach (var fallback in new[] { DragDropEffects.Copy, DragDropEffects.Move, DragDropEffects.Link })
        {
            if (e.AllowedEffects.HasFlag(fallback))
                return fallback;
        }
        return DragDropEffects.None;
    }

    /// <summary>
    /// Runs the drop. Left drag: the effect from <see cref="ChooseEffect"/>.
    /// Right drag: a menu (Copy here / Move here / Create shortcuts here / Cancel).
    /// The work starts after the drop returns, so the drag source is not blocked.
    /// </summary>
    /// <param name="sameFolderRightDrop">
    /// A right drag dropped into the folder all items are already in calls this instead of
    /// showing the menu (file lists: Duplicate dialog). Null: the menu as usual.
    /// </param>
    public static void Execute(DragEventArgs e, string[] sources, string targetFolder, FrameworkElement owner,
        Action<string[]>? sameFolderRightDrop = null)
    {
        bool rightDrag = _rightDrag;
        _rightDrag = false;
        DragDropEffects effect = ComputeEffect(e, sources, targetFolder, rightDrag);
        DragDropEffects allowed = e.AllowedEffects;

        // We do the work ourselves (IFileOperation), so the source must not do anything.
        // Reporting "Move" could make some drag sources delete the originals afterwards.
        e.Effects = rightDrag || effect == DragDropEffects.Move ? DragDropEffects.None : effect;

        if (!rightDrag && effect == DragDropEffects.None)
            return;

        IntPtr hwnd = Window.GetWindow(owner) is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;

        bool sameFolder = sources.All(s => IsInFolder(s, targetFolder));
        owner.Dispatcher.BeginInvoke(() =>
        {
            if (rightDrag && sameFolder && sameFolderRightDrop is not null)
                sameFolderRightDrop(sources);
            else if (rightDrag)
                ShowDropMenu(owner, hwnd, sources, targetFolder, allowed, effect);
            else
                _ = RunAsync(hwnd, effect, sources, targetFolder);
        });
    }

    private static void ShowDropMenu(
        FrameworkElement owner, IntPtr hwnd, string[] sources, string targetFolder,
        DragDropEffects allowed, DragDropEffects defaultEffect)
    {
        bool sameFolder = sources.All(s => IsInFolder(s, targetFolder));
        var menu = new ContextMenu { PlacementTarget = owner, Placement = PlacementMode.MousePoint };

        void Add(string header, DragDropEffects effect, bool enabled)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled && allowed.HasFlag(effect),
                FontWeight = effect == defaultEffect ? FontWeights.Bold : FontWeights.Normal,
            };
            item.Click += async (_, _) => await RunAsync(hwnd, effect, sources, targetFolder);
            menu.Items.Add(item);
        }

        Add("Copy here", DragDropEffects.Copy, enabled: true);
        Add("Move here", DragDropEffects.Move, enabled: !sameFolder);
        Add("Create shortcuts here", DragDropEffects.Link, enabled: true);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cancel" }); // Closing the menu does nothing.

        menu.IsOpen = true;
    }

    private static async Task RunAsync(IntPtr hwnd, DragDropEffects effect, string[] sources, string targetFolder)
    {
        try
        {
            // Into a ZIP: add the files; a move sends the originals to the Recycle Bin afterwards.
            if (ZipBrowser.TrySplit(targetFolder, out string zipFile, out string inner))
            {
                if (effect is not (DragDropEffects.Copy or DragDropEffects.Move))
                    return;
                await Task.Run(() => ZipBrowser.Add(zipFile, inner, sources));
                if (effect == DragDropEffects.Move) // Not undoable (the ZIP keeps the copies).
                    await FileOperations.DeleteAsync(hwnd, sources, permanent: false, record: false);
                return;
            }

            switch (effect)
            {
                case DragDropEffects.Copy:
                    await FileOperations.CopyAsync(hwnd, sources, targetFolder);
                    break;
                case DragDropEffects.Move:
                    await FileOperations.MoveAsync(hwnd, sources, targetFolder);
                    break;
                case DragDropEffects.Link:
                    await ShortcutCreator.CreateAsync(sources, targetFolder);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageDialog.Show(null, ex.Message, "DirectoryFable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DragDropEffects PreferredEffect(DragDropKeyStates keys, string[] sources, string targetFolder)
    {
        bool ctrl = keys.HasFlag(DragDropKeyStates.ControlKey);
        bool shift = keys.HasFlag(DragDropKeyStates.ShiftKey);
        bool alt = keys.HasFlag(DragDropKeyStates.AltKey);

        // Into a ZIP: copy unless Shift asks for a move; no shortcuts inside a ZIP.
        if (ZipBrowser.IsInArchive(targetFolder))
            return shift && !ctrl ? DragDropEffects.Move : DragDropEffects.Copy;

        if (alt || (ctrl && shift))
            return DragDropEffects.Link;
        if (ctrl)
            return DragDropEffects.Copy;
        if (shift)
            return DragDropEffects.Move;
        return SameDrive(sources[0], targetFolder) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    /// <summary>A folder cannot be dropped onto itself or into one of its own subfolders.</summary>
    private static bool IsValidTarget(string[] sources, string targetFolder)
    {
        // A folder inside a ZIP (or the ZIP's root) is a valid target too.
        if (!Directory.Exists(targetFolder) && !ZipBrowser.IsInArchive(targetFolder))
            return false;
        // 7z / RAR are read only: nothing can be dropped into them.
        if (ZipBrowser.IsInReadOnlyArchive(targetFolder))
            return false;

        string target = targetFolder.TrimEnd('\\') + "\\";
        foreach (string source in sources)
        {
            string s = source.TrimEnd('\\') + "\\";
            if (target.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
}
