using System.IO;
using System.Windows;

namespace DirectoryFable;

/// <summary>
/// SonnetSearch, the fourth title bar view: names on whole drives or in one folder tree
/// (<see cref="Views.SearchView"/>). Ctrl+F in the explorer searches the shown folder.
/// </summary>
public partial class MainWindow
{
    private bool SearchShown => SearchPage.Visibility == Visibility.Visible;

    private readonly System.Windows.Threading.DispatcherTimer _searchSessionTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitSearch()
    {
        SearchPage.TitleChanged += UpdateTitle;
        SearchPage.ShowInExplorerRequested += path => _ = ShowInExplorerAsync(path);
        SearchPage.OpenFolderRequested += async folder =>
        {
            ShowView(MainView.Explorer);
            if (ActivePane.SelectedTab is { } tab)
                await tab.NavigateAsync(folder);
            ActivePaneView.FocusList();
        };
        // Drives, text, filters and the mark are part of the session (saved a moment after a change).
        _searchSessionTimer.Tick += (_, _) =>
        {
            _searchSessionTimer.Stop();
            SaveSession();
        };
        SearchPage.StateChanged += () =>
        {
            _searchSessionTimer.Stop();
            _searchSessionTimer.Start();
        };
        // The SonnetSearch button in the panes' action bar.
        Views.FilePane.SearchInFolder = SearchFolder;
    }

    private void SearchTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Search);

    /// <summary>Ctrl+F in the explorer: SonnetSearch, limited to the shown folder (not inside archives).</summary>
    private void SearchHere() => SearchFolder(ActivePane.CurrentFolder);

    /// <summary>Shows SonnetSearch limited to a folder and its subfolders; just the search line when it is no folder on a drive.</summary>
    private void SearchFolder(string? folder)
    {
        ShowView(MainView.Search);
        if (folder is { Length: > 0 } && Directory.Exists(folder))
            SearchPage.SearchIn(folder);
        else
            SearchPage.FocusQuery();
    }
}
