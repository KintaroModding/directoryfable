using System.IO;
using System.Windows;
using DirectoryFable.Views;

namespace DirectoryFable;

/// <summary>
/// AstraViewer, the third title bar view: images opened from DirectoryFable (double-click on the
/// chosen types, "Open in AstraViewer", drops on its tab). Windows' file associations stay as they are.
/// </summary>
public partial class MainWindow
{
    private bool ViewerShown => AstraViewerPage.Visibility == Visibility.Visible;

    private readonly System.Windows.Threading.DispatcherTimer _viewerSessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void InitAstraViewer()
    {
        AstraViewerPage.ActiveChanged += UpdateTitle;
        AstraViewerPage.ShowInExplorerRequested += path => _ = ShowInExplorerAsync(path);
        // History and shown image are part of the session (saved a moment after they change).
        _viewerSessionTimer.Tick += (_, _) =>
        {
            _viewerSessionTimer.Stop();
            SaveSession();
        };
        AstraViewerPage.StateChanged += () =>
        {
            _viewerSessionTimer.Stop();
            _viewerSessionTimer.Start();
        };

        // Explorer: double-click / Enter on an image type, or "Open in AstraViewer".
        FilePane.OpenInViewer = async path =>
        {
            ShowView(MainView.Viewer);
            await AstraViewerPage.OpenAsync(path);
        };

        // Images dragged onto the [AstraViewer] title tab (from the panes, Explorer, ...) open there.
        AstraViewerTab.AllowDrop = true;
        DragEventHandler over = (_, e) =>
        {
            e.Effects = AstraViewerView.DroppedImages(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        AstraViewerTab.DragEnter += over;
        AstraViewerTab.DragOver += over;
        AstraViewerTab.Drop += async (_, e) =>
        {
            e.Handled = true;
            var images = AstraViewerView.DroppedImages(e.Data);
            if (images.Count == 0)
                return;
            ShowView(MainView.Viewer);
            Activate();
            await AstraViewerPage.OpenFilesAsync(images);
        };
    }

    private void AstraViewerTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Viewer);

    /// <summary>"Show in explorer" (Notepad#, AstraViewer): the explorer opens the file's folder with the file selected.</summary>
    private async Task ShowInExplorerAsync(string path)
    {
        ShowView(MainView.Explorer);
        if (ActivePane.SelectedTab is { } tab && Path.GetDirectoryName(path) is { } folder)
            await tab.NavigateAsync(folder, focusPath: path);
        ActivePaneView.FocusList();
    }
}
