using System.IO;
using System.Windows;
using DirectoryFable.Infrastructure;
using DirectoryFable.Views;

namespace DirectoryFable;

/// <summary>
/// Explorer / Notepad# views: the two title bar tabs switch everything below
/// the title bar. The explorer keeps its state while Notepad# is shown, and the other way round.
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
        NotepadPage.ShowInExplorerRequested += async path =>
        {
            ShowNotepad(false);
            if (ActivePane.SelectedTab is { } tab && Path.GetDirectoryName(path) is { } folder)
                await tab.NavigateAsync(folder, focusPath: path);
            ActivePaneView.FocusList();
        };
        // Explorer: "Open in Notepad#" (file menu) and Ctrl+E.
        FilePane.OpenInNotepad = async paths =>
        {
            ShowNotepad(true);
            await NotepadPage.OpenFilesAsync(paths);
        };
    }

    /// <summary>
    /// The explorer parts below the title bar. Hidden (not collapsed: their layout stays) while
    /// Notepad# covers them, so WPF does not draw them under the editor on every scroll step.
    /// </summary>
    private void SetExplorerShown(bool shown)
    {
        var visibility = shown ? Visibility.Visible : Visibility.Hidden;
        foreach (UIElement part in new UIElement[] { Actions, Favorites, FolderTreeView, LeftPane, RightPane, TreeSplitter, PaneSplitter })
            part.Visibility = visibility;
    }

    private void ExplorerTab_Click(object sender, RoutedEventArgs e) => ShowNotepad(false);

    private void NotepadTab_Click(object sender, RoutedEventArgs e) => ShowNotepad(true);

    /// <summary>Shows Notepad# (true) or the explorer (false); a click on the shown tab keeps it.</summary>
    private void ShowNotepad(bool show)
    {
        ExplorerTab.IsChecked = !show;
        NotepadTab.IsChecked = show;
        if (show == NotepadShown)
            return;

        if (show)
        {
            ShowSettings(false); // The explorer's settings page belongs to the explorer.
            NotepadPage.Visibility = Visibility.Visible;
            SetExplorerShown(false);
            NotepadPage.FocusEditor();
        }
        else
        {
            SetExplorerShown(true);
            NotepadPage.Visibility = Visibility.Collapsed;
            ActivePaneView.FocusList();
        }
        UpdateTitle();
    }
}
