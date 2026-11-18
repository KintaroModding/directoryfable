using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// Quick actions bar between the navigation bar and the file list:
/// paths to the clipboard as text, Copy / Cut / Paste / Duplicate, copy or move to the other
/// pane, terminal here, open in Explorer. File actions use the same code as the keys, so ZIP
/// folders work the same way.
/// </summary>
public partial class FilePane
{
    /// <summary>Set by the main window: the folder shown by the other pane (null: none).</summary>
    public static Func<FilePane, string?>? OtherPaneFolder { get; set; }

    private void InitQuickActions()
    {
        FileList.SelectionChanged += (_, _) => UpdateQuickActions();
        UpdateQuickActions();
    }

    /// <summary>The file buttons need a selection.</summary>
    private void UpdateQuickActions()
    {
        bool any = FileList.SelectedItems.Count > 0;
        QuickFilePath.IsEnabled = any;
        Quick7z.IsEnabled = any && SevenZip.GuiPath is not null;
        QuickDuplicate.IsEnabled = any;
        QuickCopyOther.IsEnabled = any;
        QuickMoveOther.IsEnabled = any;
    }

    // ---------- Paths as text ----------

    private void QuickFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CurrentPath is { Length: > 0 } path)
            CopyText(path, $"Folder path copied: {path}");
    }

    private void QuickFilePath_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0)
            return;
        string text = string.Join(Environment.NewLine, files.Select(f => f.FullPath));
        CopyText(text, files.Count == 1 ? $"Path copied: {files[0].FullPath}" : $"{files.Count:N0} paths copied");
    }

    /// <summary>Puts text on the clipboard; another app holding the clipboard is only reported.</summary>
    private void CopyText(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            _viewModel?.ShowStatus(message);
        }
        catch (COMException ex)
        {
            _viewModel?.ShowStatus($"The clipboard is busy: {ex.Message}");
        }
    }

    // ---------- 7-Zip ----------

    /// <summary>Opens 7-Zip's "Add to archive" window for the selection (like its shell menu entry).</summary>
    private void Quick7z_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedFiles();
        if (_viewModel is null || files.Count == 0)
            return;
        if (InArchive)
        {
            _viewModel.ShowStatus("Use 7-Zip on files outside the ZIP file.");
            return;
        }
        try
        {
            SevenZip.AddToArchive(_viewModel.CurrentPath, [.. files.Select(f => f.FullPath)]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _viewModel.ShowStatus($"Cannot start 7-Zip: {ex.Message}");
        }
    }

    // ---------- Duplicate ----------

    private void QuickDuplicate_Click(object sender, RoutedEventArgs e) => _ = RunSafeAsync(DuplicateSelectedAsync);

    /// <summary>
    /// One "Duplicate" dialog per selected item (Skip / Cancel with several); each copy goes into
    /// this folder under the new name. Afterwards the copies are selected.
    /// </summary>
    private Task DuplicateSelectedAsync() => DuplicateAsync([.. SelectedFiles().Select(f => f.FullPath)]);

    /// <summary>
    /// One Duplicate dialog per item, each copy into this folder under its new name (quick action
    /// "Duplicate", and a right-button drag dropped into the folder the items are already in).
    /// </summary>
    private async Task DuplicateAsync(IReadOnlyList<string> paths)
    {
        if (_viewModel is null || paths.Count == 0)
            return;
        if (InArchive)
        {
            _viewModel.ShowStatus("Duplicate is not possible inside a ZIP file.");
            return;
        }

        string folder = _viewModel.CurrentPath;
        var created = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i].TrimEnd('\\');
            var (choice, newName) = DuplicateDialog.Ask(Window.GetWindow(this), Path.GetFileName(path), folder,
                Directory.Exists(path), canSkip: i < paths.Count - 1);
            if (choice == DuplicateChoice.Cancel)
                break;
            if (choice == DuplicateChoice.Skip)
                continue;
            if (await FileOperations.CopyAsAsync(OwnerHandle, path, folder, newName))
                created.Add(Path.Combine(folder, newName));
        }

        if (created.Count > 0)
        {
            await _viewModel.RefreshAsync(created);
            _viewModel.ShowStatus(created.Count == 1 ? $"Duplicated as {Path.GetFileName(created[0])}" : $"{created.Count:N0} items duplicated");
        }
    }

    // ---------- Copy / move to the other pane ----------

    private void QuickCopyOther_Click(object sender, RoutedEventArgs e) => _ = RunSafeAsync(() => TransferToOtherPaneAsync(move: false));

    private void QuickMoveOther_Click(object sender, RoutedEventArgs e) => _ = RunSafeAsync(() => TransferToOtherPaneAsync(move: true));

    /// <summary>The tooltip names the real target folder.</summary>
    private void QuickOther_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is Button button)
        {
            string verb = button == QuickMoveOther ? "Move" : "Copy";
            string target = OtherPaneFolder?.Invoke(this) ?? "";
            button.ToolTip = target.Length > 0 ? $"{verb} the selection to the other pane: {target}" : $"{verb} the selection to the other pane";
        }
    }

    private async Task TransferToOtherPaneAsync(bool move)
    {
        var files = SelectedFiles();
        if (_viewModel is null || files.Count == 0)
            return;

        string? target = OtherPaneFolder?.Invoke(this);
        if (string.IsNullOrEmpty(target) || PathHelper.IsServerPath(target))
        {
            _viewModel.ShowStatus("The other pane shows no folder to copy into.");
            return;
        }
        if (PathHelper.IsSamePath(target, _viewModel.CurrentPath))
        {
            _viewModel.ShowStatus("Both panes show the same folder.");
            return;
        }
        if (ZipBrowser.IsInArchive(target))
        {
            _viewModel.ShowStatus("The other pane is inside a ZIP file: use Copy and Paste there.");
            return;
        }

        IReadOnlyList<string> sources;
        if (InArchive)
        {
            if (move)
            {
                _viewModel.ShowStatus("Move is not possible out of a ZIP file. Copy works.");
                return;
            }
            sources = await ExtractSelectionAsync(files); // Unpack to temp first, as for Ctrl+C.
            if (sources.Count == 0)
                return;
        }
        else
        {
            sources = [.. files.Select(f => f.FullPath)];
        }

        bool ok = move
            ? await FileOperations.MoveAsync(OwnerHandle, sources, target)
            : await FileOperations.CopyAsync(OwnerHandle, sources, target);
        if (ok)
            _viewModel.ShowStatus($"{sources.Count:N0} item(s) {(move ? "moved" : "copied")} to {target}");
        // Both lists follow by themselves (auto-refresh).
    }

    // ---------- Terminal and Explorer ----------

    /// <summary>The folder for a terminal: inside a ZIP file, the folder that holds the ZIP file.</summary>
    private string? RealFolder()
    {
        string path = _viewModel?.CurrentPath ?? "";
        if (ZipBrowser.TrySplit(path, out string zipFile, out _))
            path = Path.GetDirectoryName(zipFile) ?? "";
        return path.Length > 0 && !PathHelper.IsServerPath(path) ? path : null;
    }

    /// <summary>Asks which shell to open: cmd or PowerShell.</summary>
    private void QuickTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (RealFolder() is not { } folder)
        {
            _viewModel?.ShowStatus("No folder to open a terminal in.");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = QuickTerminal,
            Placement = PlacementMode.Bottom,
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = (double)FindResource("UiFontSize"),
        };
        var cmd = new MenuItem { Header = "Command Prompt" };
        cmd.Click += (_, _) => StartTerminal(folder, powerShell: false);
        var ps = new MenuItem { Header = FindPowerShell().Name };
        ps.Click += (_, _) => StartTerminal(folder, powerShell: true);
        menu.Items.Add(cmd);
        menu.Items.Add(ps);
        menu.IsOpen = true;
    }

    /// <summary>PowerShell 7 (pwsh) when installed, else Windows PowerShell.</summary>
    private static (string Exe, string Name) FindPowerShell()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? (pwsh, "PowerShell 7") : ("powershell.exe", "Windows PowerShell");
    }

    private void StartTerminal(string folder, bool powerShell)
    {
        try
        {
            ProcessStartInfo info;
            if (powerShell)
            {
                // Set-Location also works for network paths (\\server\share).
                string quoted = folder.Replace("'", "''");
                info = new ProcessStartInfo(FindPowerShell().Exe, $"-NoExit -Command \"Set-Location -LiteralPath '{quoted}'\"");
            }
            else if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // cmd cannot start in a network path; pushd maps a temporary drive letter.
                info = new ProcessStartInfo("cmd.exe", $"/k pushd \"{folder}\"");
            }
            else
            {
                info = new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder };
            }
            info.UseShellExecute = true; // Its own console window.
            Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _viewModel?.ShowStatus($"Cannot start the terminal: {ex.Message}");
        }
    }

    /// <summary>SonnetSearch in this folder and its subfolders (like Ctrl+F).</summary>
    private void QuickSearch_Click(object sender, RoutedEventArgs e)
    {
        string path = _viewModel?.CurrentPath ?? "";
        if (path.Length == 0)
            return;
        if (InArchive)
        {
            _viewModel?.ShowStatus("SonnetSearch looks in folders on the drives, not inside archives.");
            return;
        }
        SearchInFolder?.Invoke(path);
    }

    private void QuickExplorer_Click(object sender, RoutedEventArgs e)
    {
        string path = _viewModel?.CurrentPath ?? "";
        if (path.Length == 0)
            return;
        try
        {
            // Explorer opens ZIP folders and \\server too.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _viewModel?.ShowStatus($"Cannot open Explorer: {ex.Message}");
        }
    }
}
