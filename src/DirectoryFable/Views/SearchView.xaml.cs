using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Search;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>
/// SonnetSearch, the fourth title bar view: names on whole drives or in one folder tree, with filters
/// for type, size and date (also "since the time mark") and the columns Name, Containing Path,
/// Size, Last Modified, Last Accessed, Creation Date, Type. As administrator, NTFS drives are read
/// from their file table (seconds for millions of files); otherwise a folder walk. A drive is
/// read once and kept: typing and filters only search what was read; Enter / F5 / Search read
/// the drives again (so new and changed files show).
/// </summary>
public partial class SearchView : UserControl
{
    private const int CmdShowInExplorer = ShellContextMenu.ExtraCommandFirst + 20;
    private const int CmdCopyPath = ShellContextMenu.ExtraCommandFirst + 21;
    private const int CmdOpenInNotepad = ShellContextMenu.ExtraCommandFirst + 22;
    private const int CmdOpenInViewer = ShellContextMenu.ExtraCommandFirst + 23;

    private static readonly bool IsAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // Read drives and folders are kept in DriveIndexCache (shared with the folder sizes and the Space view).
    private CancellationTokenSource? _queryCancel;

    // "Containing": which files hold the text, kept per file (size and date tell when to read it again) while the
    // text and match case stay the same; a run with other filters then reads only files it has not read yet.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Modified, bool Hit)> _contentHits =
        new(StringComparer.OrdinalIgnoreCase);
    private string _contentKey = "";
    private bool _readingContents;

    // "Duplicates": what each file gave is kept, so a run with other filters reads only new files.
    private readonly DuplicateFinder _duplicates = new();
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer;

    private SearchResult _result = SearchResult.Empty;
    private List<string> _missingFolders = [];
    private ResultSort _sort = ResultSort.Name;
    private bool _descending;
    private long? _mark;
    private bool _filling;
    private Point? _dragStart;
    private readonly Dictionary<string, string> _headerLabels = [];
    private readonly List<string> _defaultColumnOrder = [];

    public SearchView()
    {
        InitializeComponent();
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync(reread: false);
        };
        _progressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => ShowProgress();

        _filling = true;
        TypeBox.SelectedIndex = SizeBox.SelectedIndex = DateColumnBox.SelectedIndex = RangeBox.SelectedIndex = 0;
        _filling = false;
        foreach (var header in Headers())
            _headerLabels[(string)header.Tag] = (string)header.Content;
        _defaultColumnOrder.AddRange(ResultColumns.Select(ColumnKey));
        // Column widths and order (header drag) are part of the session.
        var widthProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var column in ResultColumns)
            widthProperty.AddValueChanged(column, (_, _) => ColumnsChanged());
        ResultColumns.CollectionChanged += (_, _) => ColumnsChanged();
        FolderChips.ItemsSource = _chips;
        Scrubber.Attach(Results);
        BuildLocations(null, [], []);
        UpdateMark();
        UpdateHeaders();
        EmptyText.Text = "Type a name (or only set filters), then press Enter.\nThe first search reads the chosen drives: a few seconds as administrator.";
    }

    /// <summary>Shown title (window title, tab): the search text.</summary>
    public string TitleText => QueryBox.Text.Trim();

    public event Action? TitleChanged;

    /// <summary>"Show in explorer": the explorer opens the file's folder with the file selected.</summary>
    public event Action<string>? ShowInExplorerRequested;

    /// <summary>A found folder was opened: the explorer shows it.</summary>
    public event Action<string>? OpenFolderRequested;

    /// <summary>The search settings changed (the main window saves the session a moment later).</summary>
    public event Action? StateChanged;

    public void FocusQuery() => Dispatcher.BeginInvoke(() =>
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Ctrl+F in the explorer: search only this folder (and below). A new search: Duplicates is off.</summary>
    public void SearchIn(string folder)
    {
        string path = Path.GetFullPath(folder);
        _filling = true;
        foreach (var drive in _top.Where(n => n.IsDrive))
            drive.IsChecked = false;
        DuplicatesToggle.IsChecked = false;
        _filling = false;
        foreach (string other in _checkedFolders.ToList())
            SetFolderChecked(other, false);
        SetFolderChecked(path, true);
        AfterLocationsChanged();
        FocusQuery();
    }

    // ---------- Where: drives and folders ----------

    /// <summary>Drives (first) and pinned folders: the first level of the location list.</summary>
    private readonly ObservableCollection<LocationNode> _top = [];
    private readonly HashSet<string> _checkedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LocationNode> _chips = [];
    private readonly Dictionary<(DriveIndex, string), int?> _scopes = [];
    private DateTime _popupClosedAt;

    private List<string> CheckedDrives => [.. _top.Where(n => n.IsDrive && n.IsChecked).Select(n => n.Path)];

    /// <summary>Ticked folders, without those inside a ticked drive or another ticked folder (no double results).</summary>
    private List<string> CheckedFolders()
    {
        var drives = CheckedDrives;
        var result = new List<string>();
        foreach (string folder in _checkedFolders.OrderBy(f => f.Length))
        {
            if (drives.Any(d => folder.StartsWith(d, StringComparison.OrdinalIgnoreCase)) ||
                result.Any(r => folder.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(folder);
        }
        return result;
    }

    /// <summary>The location list: every ready drive (<paramref name="drives"/> null = fixed drives ticked), then the pinned folders.</summary>
    private void BuildLocations(IReadOnlyCollection<string>? drives, IEnumerable<string> pinned, IEnumerable<string> checkedFolders)
    {
        _top.Clear();
        _checkedFolders.Clear();
        foreach (string folder in checkedFolders)
            _checkedFolders.Add(folder);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                continue;
            string label;
            try
            {
                if (!drive.IsReady)
                    continue;
                label = drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            string root = drive.RootDirectory.FullName;
            string letter = root.TrimEnd('\\');
            var node = new LocationNode(root, label.Length > 0 ? $"{label} ({letter})" : letter, isDrive: true, isTopLevel: true, OnNodeChecked)
            {
                IsPathChecked = _checkedFolders.Contains,
            };
            node.SetCheckedQuietly(drives is null ? drive.DriveType == DriveType.Fixed : drives.Contains(root, StringComparer.OrdinalIgnoreCase));
            node.ReadSpace();
            _top.Add(node);
        }
        foreach (string folder in pinned.Concat(_checkedFolders).Distinct(StringComparer.OrdinalIgnoreCase))
            AddPinned(folder);
        LocationTree.ItemsSource = _top;
        BuildDriveBar();
        RefreshChips();
    }

    /// <summary>A folder row on the first level (once per path).</summary>
    private LocationNode AddPinned(string folder)
    {
        var existing = _top.FirstOrDefault(n => !n.IsDrive && PathHelper.IsSamePath(n.Path, folder));
        if (existing is not null)
            return existing;
        var node = new LocationNode(folder, folder, isDrive: false, isTopLevel: true, OnNodeChecked)
        {
            IsPathChecked = _checkedFolders.Contains,
        };
        node.SetCheckedQuietly(_checkedFolders.Contains(folder));
        node.ReadSpace();
        _top.Add(node);
        return node;
    }

    /// <summary>The drive toggles in the bar are the drive rows of the list (one tick for both).</summary>
    private void BuildDriveBar()
    {
        DriveBar.Items.Clear();
        foreach (var node in _top.Where(n => n.IsDrive))
        {
            bool fast = IsAdministrator && MftReader.IsNtfs(node.Path);
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("DriveToggle"),
                Content = node.Label,
                ToolTip = $"{node.Label}\n{node.FreeText} free of {node.TotalText}\n" +
                          (fast ? "Read from its file table (fast)." : "Read with a folder walk (slower)."),
            };
            toggle.SetBinding(ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding(nameof(LocationNode.IsChecked)) { Source = node, Mode = System.Windows.Data.BindingMode.TwoWay });
            DriveBar.Items.Add(toggle);
        }
    }

    /// <summary>A tick in the list or the bar.</summary>
    private void OnNodeChecked(LocationNode node)
    {
        if (_filling)
            return;
        if (!node.IsDrive)
            SetFolderChecked(node.Path, node.IsChecked);
        AfterLocationsChanged();
    }

    /// <summary>Ticks / unticks a folder everywhere it shows; a ticked folder is pinned to the first level.</summary>
    private void SetFolderChecked(string path, bool value)
    {
        if (value)
        {
            _checkedFolders.Add(path);
            AddPinned(path);
        }
        else
        {
            _checkedFolders.Remove(path);
        }
        foreach (var row in _top.ToList().SelectMany(n => n.LoadedRows()))
        {
            if (!row.IsDrive && PathHelper.IsSamePath(row.Path, path))
                row.SetCheckedQuietly(value);
        }
    }

    private void AfterLocationsChanged()
    {
        RefreshChips();
        ScheduleRun();
        StateChanged?.Invoke();
    }

    /// <summary>The ticked folders as chips in the bar (next to the drive toggles).</summary>
    private void RefreshChips()
    {
        _chips.Clear();
        foreach (string folder in CheckedFolders())
            _chips.Add(AddPinned(folder));
    }

    private void ChipRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocationNode node })
        {
            SetFolderChecked(node.Path, false);
            AfterLocationsChanged();
        }
    }

    private void RemovePinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocationNode { CanRemove: true } node })
        {
            SetFolderChecked(node.Path, false);
            _top.Remove(node);
            AfterLocationsChanged();
        }
    }

    private void Locations_Click(object sender, RoutedEventArgs e)
    {
        // A click on the button while the list is open closes it (the list closes on the press already).
        if (DateTime.UtcNow - _popupClosedAt < TimeSpan.FromMilliseconds(300))
        {
            LocationsButton.IsChecked = false;
            return;
        }
        if (LocationsButton.IsChecked == true)
        {
            foreach (var node in _top)
                node.ReadSpace();
            LocationTree.ItemsSource = null;
            LocationTree.ItemsSource = _top;
        }
        LocationsPopup.IsOpen = LocationsButton.IsChecked == true;
    }

    private void LocationsPopup_Closed(object sender, EventArgs e)
    {
        _popupClosedAt = DateTime.UtcNow;
        LocationsButton.IsChecked = false;
    }

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        LocationsPopup.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Search in this folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        SetFolderChecked(Path.GetFullPath(dialog.FolderName), true);
        AfterLocationsChanged();
    }

    /// <summary>Reset: empty text and all filters back to "any". Places, sorting and the mark stay.</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _queryCancel?.Cancel();
        _filling = true;
        try
        {
            QueryBox.Text = "";
            CaseToggle.IsChecked = RegexToggle.IsChecked = false;
            TypeBox.SelectedIndex = SizeBox.SelectedIndex = DateColumnBox.SelectedIndex = RangeBox.SelectedIndex = 0;
            ExtensionsBox.Text = "";
            ExtensionsBox.Visibility = Visibility.Collapsed;
            ContentBox.Text = "";
            DuplicatesToggle.IsChecked = false;
        }
        finally
        {
            _filling = false;
        }
        _debounce.Stop();
        ShowResult(SearchResult.Empty);
        StatusText.Text = "";
        TitleChanged?.Invoke();
        StateChanged?.Invoke();
        FocusQuery();
    }

    // ---------- Filters ----------

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Other text is a new search: Duplicates goes off (it would hold back the results); it is only ever
        // switched on by hand, after the text (the filters keep it on: they narrow the duplicates).
        if (!_filling)
            DuplicatesToggle.IsChecked = false;
        QueryHint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleChanged?.Invoke();
        ScheduleRun();
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => ScheduleRun();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExtensionsBox is not null)
            ExtensionsBox.Visibility = TypeBox.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleRun();
    }

    private void Extensions_TextChanged(object sender, TextChangedEventArgs e) => ScheduleRun();

    /// <summary>The text inside files is searched on Enter only (reading files takes time); emptied: at once.</summary>
    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ContentBox.Text.Length == 0)
            ScheduleRun();
        else
            StateChanged?.Invoke();
    }

    private void ContentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RunAsync(reread: false);
        }
    }

    /// <summary>
    /// Keeps the files of the result that contain the text (folders go): read four at a time, the progress in the
    /// status line, Esc stops. Files read before (same size and date) are not read again.
    /// </summary>
    private async Task<SearchResult> FilterByContentAsync(SearchResult result, string text, bool matchCase, CancellationToken token)
    {
        var pattern = ContentSearch.Build(text, matchCase, wholeWords: false, regex: false)!;
        string key = (matchCase ? "1" : "0") + text;
        if (key != _contentKey)
        {
            _contentHits.Clear();
            _contentKey = key;
        }
        var items = result.Items;
        var keep = new bool[items.Length];
        int files = items.Count(item => !result.IndexOf(item).IsDirectory(SearchResult.EntryOf(item)));
        int done = 0;
        var progress = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        progress.Tick += (_, _) => StatusText.Text = $"Searching inside the files: {done:N0} of {files:N0}   (Esc stops)";
        progress.Start();
        _readingContents = true;
        try
        {
            await Task.Run(() => Parallel.For(0, items.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, k =>
            {
                var index = result.IndexOf(items[k]);
                int i = SearchResult.EntryOf(items[k]);
                if (index.IsDirectory(i))
                    return;
                string path = index.FolderPath(i) + index.Name(i).ToString();
                long size = index.Size[i], modified = index.Modified[i];
                bool hit;
                if (_contentHits.TryGetValue(path, out var known) && known.Size == size && known.Modified == modified)
                {
                    hit = known.Hit;
                }
                else
                {
                    hit = ContentSearch.Contains(path, pattern);
                    _contentHits[path] = (size, modified, hit);
                }
                keep[k] = hit;
                Interlocked.Increment(ref done);
            }), token);
        }
        finally
        {
            progress.Stop();
            _readingContents = false;
        }
        var filtered = new List<long>();
        for (int k = 0; k < items.Length; k++)
        {
            if (keep[k])
                filtered.Add(items[k]);
        }
        return new SearchResult(result.Indexes, [.. filtered], result.Searched, result.Totals);
    }

    /// <summary>"Duplicates" switched: the search runs at once (on: the found files of the same size are compared).</summary>
    private void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        StateChanged?.Invoke();
        _ = RunAsync(reread: false);
    }

    /// <summary>Keeps the files that have the same content as another file of the result, in groups; Esc stops.</summary>
    private async Task<SearchResult> FindDuplicatesAsync(SearchResult result, CancellationToken token)
    {
        var progress = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        progress.Tick += (_, _) => StatusText.Text = _duplicates.ProgressText + "   (Esc stops)";
        progress.Start();
        StatusText.Text = "Duplicates: grouping the files by size...";
        var sort = _sort;
        bool descending = _descending;
        _readingContents = true;
        try
        {
            return await Task.Run(() =>
            {
                var found = _duplicates.Find(result, token);
                if (sort != ResultSort.None && found.Groups is { } groups)
                    SearchRunner.SortGroups(found.Items, groups, found.Indexes, found.Totals, sort, descending, token);
                return found;
            }, token);
        }
        finally
        {
            progress.Stop();
            _readingContents = false;
        }
    }

    /// <summary>Selects all files of each group but its first row (what can go, one copy stays).</summary>
    private void KeepOne_Click(object sender, RoutedEventArgs e)
    {
        if (Results.ItemsSource is not SearchRowList rows || rows.Result.Groups is not { } groups)
            return;
        Results.SelectedItems.Clear();
        int selected = 0;
        for (int p = 1; p < groups.Length; p++)
        {
            if (groups[p] != groups[p - 1])
                continue;
            Results.SelectedItems.Add(rows[p]);
            selected++;
        }
        Results.Focus();
        StatusText.Text = $"{selected:N0} files selected: all but the first of each group. Delete moves them to the Recycle Bin.";
    }

    /// <summary>
    /// Moves the rows' files and folders to the Recycle Bin (as Delete in the explorer; Undo brings them back), then
    /// takes what is gone off the list. Duplicates: a group with one file left goes too.
    /// </summary>
    private async Task DeleteAsync(List<SearchRow> rows)
    {
        var paths = rows.Select(r => r.FullPath).ToList();
        if (paths.Count == 0)
            return;
        await FileOperations.DeleteAsync(new WindowInteropHelper(Window.GetWindow(this)).Handle, paths, permanent: false);
        var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0)
            return;
        RemoveFromResult(gone);
        StatusText.Text = $"{gone.Count:N0} item(s) moved to the Recycle Bin.   " + StatusText.Text;
    }

    private void RemoveFromResult(HashSet<string> gone)
    {
        var current = _result;
        var items = new List<long>(current.Items.Length);
        var groups = current.Groups is null ? null : new List<int>(current.Items.Length);
        for (int p = 0; p < current.Items.Length; p++)
        {
            long item = current.Items[p];
            if (gone.Contains(current.IndexOf(item).FullPath(SearchResult.EntryOf(item))))
                continue;
            items.Add(item);
            groups?.Add(current.Groups![p]);
        }
        if (groups is not null)
        {
            // Groups with one file left are no duplicates any more; the others are numbered again.
            var counts = groups.GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
            var numbers = new Dictionary<int, int>();
            var keptItems = new List<long>();
            var keptGroups = new List<int>();
            for (int p = 0; p < items.Count; p++)
            {
                if (counts[groups[p]] < 2)
                    continue;
                if (!numbers.TryGetValue(groups[p], out int number))
                    numbers[groups[p]] = number = numbers.Count + 1;
                keptItems.Add(items[p]);
                keptGroups.Add(number);
            }
            items = keptItems;
            groups = keptGroups;
        }
        ShowResult(new SearchResult(current.Indexes, [.. items], current.Searched, current.Totals, groups?.ToArray()));
    }

    private void ScheduleRun()
    {
        if (_filling || !IsLoaded)
            return;
        _debounce.Stop();
        _debounce.Start();
        StateChanged?.Invoke();
    }

    private SearchQuery BuildQuery()
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        long? since = RangeBox.SelectedIndex switch
        {
            1 => DateTime.UtcNow.AddMinutes(-10).ToFileTimeUtc(),
            2 => DateTime.UtcNow.AddHours(-1).ToFileTimeUtc(),
            3 => DateTime.Today.ToUniversalTime().ToFileTimeUtc(),
            4 => DateTime.UtcNow.AddDays(-7).ToFileTimeUtc(),
            5 => DateTime.UtcNow.AddDays(-30).ToFileTimeUtc(),
            6 => _mark,
            _ => null,
        };
        (long? min, long? max) = SizeBox.SelectedIndex switch
        {
            1 => (0L, 0L),
            2 => ((long?)null, 1024L * 1024 - 1),
            3 => (1024L * 1024, (long?)null),
            4 => (10L * 1024 * 1024, null),
            5 => (100L * 1024 * 1024, null),
            6 => (1024L * 1024 * 1024, null),
            _ => ((long?)null, (long?)null),
        };
        return new SearchQuery
        {
            Text = QueryBox.Text,
            MatchCase = CaseToggle.IsChecked == true,
            UseRegex = RegexToggle.IsChecked == true,
            Type = (SearchType)Math.Max(0, TypeBox.SelectedIndex),
            Extensions = TypeBox.SelectedIndex == 5 ? Editor.EditorFileTypes.Parse(ExtensionsBox.Text) : null,
            MinSize = min,
            MaxSize = max,
            DateColumn = (DateColumn)Math.Max(0, DateColumnBox.SelectedIndex),
            Since = since,
        };
    }

    // ---------- Time mark ----------

    private void SetMark_Click(object sender, RoutedEventArgs e)
    {
        _mark = DateTime.UtcNow.ToFileTimeUtc();
        UpdateMark();
        _filling = true;
        DateColumnBox.SelectedIndex = 0; // Any of the three dates.
        RangeBox.SelectedIndex = 6;      // Since the mark.
        _filling = false;
        ScheduleRun();
    }

    private void ClearMark_Click(object sender, RoutedEventArgs e)
    {
        _mark = null;
        UpdateMark();
        if (RangeBox.SelectedIndex == 6)
            RangeBox.SelectedIndex = 0;
        ScheduleRun();
    }

    private void UpdateMark()
    {
        MarkText.Text = _mark is long mark ? $"Mark: {DateTime.FromFileTimeUtc(mark).ToLocalTime():G}" : "No mark";
        ClearMarkButton.Visibility = _mark is null ? Visibility.Collapsed : Visibility.Visible;
        SinceMarkItem.IsEnabled = _mark is not null;
    }

    // ---------- Search ----------

    private void Search_Click(object sender, RoutedEventArgs e) => _ = RunAsync(reread: true);

    /// <summary>
    /// Reads the drives that are not read yet (or all again with <paramref name="reread"/>), then
    /// searches them. A newer search cancels an older one; reading goes on (only Esc stops it).
    /// </summary>
    private async Task RunAsync(bool reread)
    {
        _debounce.Stop();
        _queryCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _queryCancel = cancel;
        var token = cancel.Token;

        SearchQuery query;
        try
        {
            query = BuildQuery();
            query.Compile();
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = $"The regular expression is not valid: {ex.Message}";
            return;
        }

        var drives = CheckedDrives;
        var folders = CheckedFolders();
        if (drives.Count + folders.Count == 0)
        {
            ShowResult(SearchResult.Empty);
            EmptyText.Text = "Tick at least one drive or folder (in the bar or under Places).";
            return;
        }

        try
        {
            // Drives: their whole index. Folders: the drive's file table limited to the folder, or a folder walk.
            var jobs = new List<(Task<DriveIndex> Load, string? Folder)>();
            foreach (string drive in drives)
                jobs.Add((Load(drive, reread, walkFolder: false), null));
            foreach (string folder in folders)
            {
                string root = Path.GetPathRoot(folder) ?? folder;
                bool table = !root.StartsWith(@"\\", StringComparison.Ordinal) && MftReader.CanRead(root);
                jobs.Add(table ? (Load(root, reread, walkFolder: false), folder) : (Load(folder, reread, walkFolder: true), null));
            }
            await Task.WhenAll(jobs.Select(j => j.Load));

            var indexes = new List<DriveIndex>();
            var scopes = new List<int?>();
            var missing = new List<string>();
            foreach (var (load, folder) in jobs)
            {
                var index = load.Result;
                int? scope = null;
                if (folder is not null)
                {
                    if (!_scopes.TryGetValue((index, folder), out scope))
                        _scopes[(index, folder)] = scope = await Task.Run(() => index.FindFolder(folder));
                    if (scope is null)
                    {
                        missing.Add(folder);
                        continue;
                    }
                }
                indexes.Add(index);
                scopes.Add(scope);
            }
            _missingFolders = missing;
            if (token.IsCancellationRequested)
                return;
            // Folder sizes (the setting of the file panes' Size column): counted once per read drive, then kept.
            IReadOnlyList<FolderTotals>? totals = null;
            if (FolderSizes.Enabled)
            {
                var counting = indexes.Select(i => i.TotalsAsync()).ToList();
                if (counting.Any(t => !t.IsCompleted))
                    StatusText.Text = "Counting folder sizes...";
                totals = await Task.WhenAll(counting);
                if (token.IsCancellationRequested)
                    return;
            }
            var result = await Task.Run(() => SearchRunner.Run(indexes, scopes, query, _sort, _descending, totals, token), token);
            string content = ContentBox.Text;
            if (content.Length > 0 && !token.IsCancellationRequested)
                result = await FilterByContentAsync(result, content, CaseToggle.IsChecked == true, token);
            if (DuplicatesToggle.IsChecked == true && !token.IsCancellationRequested)
                result = await FindDuplicatesAsync(result, token);
            if (!token.IsCancellationRequested)
                ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            if (DriveIndexCache.WasCancelled)
                StatusText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            // A drive that cannot be read, a regular expression that runs too long, ...: shown, never thrown.
            StatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    /// <summary>A drive or folder from the shared cache (one read at a time per target; a search while it is read waits for it).</summary>
    private Task<DriveIndex> Load(string target, bool reread, bool walkFolder)
    {
        var task = DriveIndexCache.Load(target, reread, walkFolder, owner: this); // Kept for the next search.
        if (DriveIndexCache.IsReading)
        {
            _progressTimer.Start();
            ShowProgress();
        }
        return task;
    }

    private void ShowProgress()
    {
        var progress = DriveIndexCache.Progress;
        if (progress.IsEmpty)
        {
            _progressTimer.Stop();
            ShowStatusLine(); // Reads that ended after the search (another pane's folder sizes).
            return;
        }
        var parts = progress.OrderBy(p => p.Key).Select(p => p.Value >= 0
            ? $"{p.Key.TrimEnd('\\')} {p.Value * 100:0} %"
            : $"{p.Key.TrimEnd('\\')} {-p.Value:N0} objects");
        StatusText.Text = "Reading " + string.Join("  ·  ", parts) + "   (Esc stops)";
    }

    private void ShowResult(SearchResult result)
    {
        _result = result;
        // Duplicates: the rows of a group share one background, and "#" is the group number.
        bool groups = result.Groups is not null;
        Results.ItemContainerStyle = (Style)FindResource(groups ? "DuplicateRowStyle" : "AlternatingFileRowStyle");
        _headerLabels["Number"] = groups ? "Group" : "#";
        UpdateHeaders();
        KeepOneButton.Visibility = groups && result.Items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.ItemsSource = new SearchRowList(result);
        // Duplicates stand in group order, the largest files first: the scroll rail shows size steps then.
        Scrubber.SetResult(result, groups ? ResultSort.Size : _sort);
        EmptyText.Text = result.Items.Length == 0 && result.Searched > 0 ? (groups ? "No duplicates found." : "Nothing found.") : "";
        if (result.Items.Length == 0 && result.Searched == 0 && EmptyText.Text.Length == 0)
            EmptyText.Text = "Press Enter to search.";
        if (!DriveIndexCache.IsReading)
            ShowStatusLine();
    }

    /// <summary>The status line of the shown result (objects, sources, age).</summary>
    private void ShowStatusLine()
    {
        var result = _result;
        if (result.Searched == 0)
        {
            StatusText.Text = "";
            AgeText.Text = "";
            return;
        }
        var sources = result.Indexes.Select(i => $"{i.Root.TrimEnd('\\')} {(i.Method == "MFT" ? "file table" : "folder walk")} {i.ReadTime.TotalSeconds:0.0} s");
        string hint = result.Indexes.Any(i => i.Method != "MFT") && !IsAdministrator
            ? "   ·   Not administrator: drives are read with a folder walk (slow). Run DirectoryFable as administrator for the fast search."
            : "";
        string gone = _missingFolders.Count > 0 ? $"   ·   Not found (moved or deleted?): {string.Join(", ", _missingFolders)}" : "";
        string containing = ContentBox.Text.Length > 0 ? $" containing \"{ContentBox.Text}\"" : "";
        string found = $"{result.Items.Length:N0} of {result.Searched:N0} objects{containing}";
        if (result.Groups is not null)
        {
            var (groups, files, freeable) = DuplicateFinder.Summarize(result);
            found = groups == 0
                ? $"No duplicates in {result.Searched:N0} objects{containing}"
                : $"{files:N0} duplicate files in {groups:N0} group{(groups == 1 ? "" : "s")}, {DisplayFormat.Size(freeable)} to free (one file of each group kept), " +
                  $"of {result.Searched:N0} objects{containing}";
        }
        StatusText.Text = $"{found}   ·   {string.Join("   ·   ", sources.Distinct())}{hint}{gone}";
        var oldest = result.Indexes.Min(i => i.ReadAt);
        AgeText.Text = $"Read at {oldest:HH:mm:ss}  ·  Enter reads again";
    }

    // ---------- Sorting ----------

    private GridViewColumnCollection ResultColumns => ((GridView)Results.View).Columns;

    private IEnumerable<GridViewColumnHeader> Headers() =>
        ResultColumns.Select(c => c.Header).OfType<GridViewColumnHeader>();

    private static string ColumnKey(GridViewColumn column) =>
        column.Header is GridViewColumnHeader { Tag: string tag } ? tag : "";

    private void ColumnsChanged()
    {
        if (!_filling)
            StateChanged?.Invoke();
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string tag } || !Enum.TryParse(tag, out ResultSort column))
            return;
        if (column == _sort)
            _descending = !_descending;
        else
        {
            _sort = column;
            // Size and dates: biggest / newest first, as that is what one looks for.
            _descending = column is ResultSort.Size or ResultSort.Modified or ResultSort.Accessed or ResultSort.Created;
        }
        UpdateHeaders();
        _ = ResortAsync();
        StateChanged?.Invoke();
    }

    private void UpdateHeaders()
    {
        foreach (var header in Headers())
        {
            string tag = (string)header.Tag;
            string label = _headerLabels.GetValueOrDefault(tag, tag);
            header.Content = Enum.TryParse(tag, out ResultSort column) && column == _sort ? $"{label}  {(_descending ? "▼" : "▲")}" : label;
        }
    }

    private async Task ResortAsync()
    {
        var current = _result;
        if (current.Items.Length == 0)
            return;
        var items = (long[])current.Items.Clone();
        var sort = _sort;
        bool descending = _descending;
        StatusText.Text = "Sorting...";
        // Duplicates: inside each group only, so the groups stay together.
        await Task.Run(() =>
        {
            if (current.Groups is { } groups)
                SearchRunner.SortGroups(items, groups, current.Indexes, current.Totals, sort, descending, CancellationToken.None);
            else
                SearchRunner.Sort(items, current.Indexes, current.Totals, sort, descending, CancellationToken.None);
        });
        if (_result == current)
            ShowResult(new SearchResult(current.Indexes, items, current.Searched, current.Totals, current.Groups));
    }

    // ---------- Results: open, menu, drag ----------

    private List<SearchRow> SelectedRows() => [.. Results.SelectedItems.OfType<SearchRow>()];

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListViewItem { DataContext: SearchRow row })
            Open(row);
    }

    /// <summary>Like a double-click in the explorer: folders open there; files in Notepad# / AstraViewer (chosen types) or their program.</summary>
    private void Open(SearchRow row)
    {
        string path = row.FullPath;
        if (row.IsDirectory)
        {
            OpenFolderRequested?.Invoke(path);
            return;
        }
        if (!File.Exists(path))
        {
            StatusText.Text = $"{row.Name} no longer exists. Enter reads the drives again.";
            return;
        }
        if (Viewer.ImageFileTypes.OpensInViewer(path) && FilePane.OpenInViewer is { } viewer)
            viewer(path);
        else if (Editor.EditorFileTypes.OpensInNotepad(path) && FilePane.OpenInNotepad is { } notepad)
            notepad([path]);
        else
            ShellOpen(path);
    }

    private static void ShellOpen(string path)
    {
        try
        {
            Process.Start(Shell.ShellLaunch.For(path));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No program for this type: Windows shows nothing; the user can use "Open with" in the menu.
        }
    }

    private void Results_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is not ListViewItem { DataContext: SearchRow clicked })
            return;
        e.Handled = true;
        if (!Results.SelectedItems.Contains(clicked))
        {
            Results.SelectedItems.Clear();
            Results.SelectedItem = clicked;
        }
        var rows = SelectedRows();
        var paths = rows.Select(r => r.FullPath).ToList();
        bool oneFolder = rows.All(r => string.Equals(r.Folder, rows[0].Folder, StringComparison.OrdinalIgnoreCase));
        Point screen = PointToScreen(e.GetPosition(this));

        if (!oneFolder)
        {
            // The Windows menu needs items of one folder: a small own menu for mixed results.
            var menu = new ContextMenu { FontFamily = (FontFamily)FindResource("UiFont"), FontSize = (double)FindResource("UiFontSize") };
            void Add(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            Add("Copy (files)", () => FileClipboard.SetFiles(paths, cut: false));
            Add("Copy paths", () => CopyText(string.Join(Environment.NewLine, paths)));
            Add($"Show {clicked.Name} in explorer", () => ShowInExplorerRequested?.Invoke(clicked.FullPath));
            menu.Items.Add(new Separator());
            Add("Delete (to the Recycle Bin)", () => _ = DeleteAsync(rows));
            menu.IsOpen = true;
            return;
        }

        var extras = new List<(int, string)> { (CmdShowInExplorer, "Show in explorer"), (CmdCopyPath, "Copy path") };
        if (rows.All(r => !r.IsDirectory))
            extras.Add((CmdOpenInNotepad, "Open in Notepad#"));
        if (rows is [{ IsDirectory: false } image] && Viewer.ImageFileTypes.IsImage(image.FullPath))
            extras.Add((CmdOpenInViewer, "Open in AstraViewer"));
        ShellMenuResult result;
        try
        {
            result = ShellContextMenu.Show(new WindowInteropHelper(Window.GetWindow(this)).Handle, rows[0].Folder, paths,
                (int)screen.X, (int)screen.Y, extras,
                extendedVerbs: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                ctrlDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                shiftDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Context menu failed: {ex.Message}";
            return;
        }
        switch (result.Outcome)
        {
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdShowInExplorer:
                ShowInExplorerRequested?.Invoke(clicked.FullPath);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdCopyPath:
                CopyText(string.Join(Environment.NewLine, paths));
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInNotepad:
                FilePane.OpenInNotepad?.Invoke(paths);
                break;
            case ShellMenuOutcome.Extra when result.ExtraCommandId == CmdOpenInViewer:
                FilePane.OpenInViewer?.Invoke(paths[0]);
                break;
            case ShellMenuOutcome.Rename:
                StatusText.Text = "Rename it in the explorer (Show in explorer, then F2).";
                break;
        }
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
        }
    }

    private void Results_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListViewItem ? e.GetPosition(Results) : null;

    /// <summary>Found files can be dragged to the panes, Explorer or other programs.</summary>
    private void Results_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(Results);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;
        var paths = SelectedRows().Select(r => r.FullPath).ToArray();
        if (paths.Length == 0)
            return;
        // With Windows' drag picture; a plain drag when that cannot start.
        var effects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        if (!Shell.ShellDrag.TryStart(Results, paths, effects))
            DragDrop.DoDragDrop(Results, new DataObject(DataFormats.FileDrop, paths), effects);
    }

    // ---------- Keys ----------

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape)
        {
            if (LocationsPopup.IsOpen)
            {
                e.Handled = true;
                LocationsPopup.IsOpen = false;
            }
            else if (DriveIndexCache.IsReading)
            {
                e.Handled = true;
                DriveIndexCache.CancelReads();
            }
            else if (_readingContents)
            {
                e.Handled = true;
                _queryCancel?.Cancel();
                StatusText.Text = "Stopped.";
            }
            return;
        }
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && Keyboard.FocusedElement == ContentBox)
        {
            e.Handled = true;
            _ = RunAsync(reread: false); // "Containing": search with the places as read.
            return;
        }
        if ((e.Key == Key.Enter && Keyboard.FocusedElement is not ListViewItem && modifiers == ModifierKeys.None) ||
            (e.Key == Key.F5 && modifiers == ModifierKeys.None))
        {
            e.Handled = true;
            _ = RunAsync(reread: true);
            return;
        }
        if (e.Key == Key.F && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusQuery();
            return;
        }
        if (!Results.IsKeyboardFocusWithin)
            return;
        var rows = SelectedRows();
        if (e.Key == Key.Enter && rows.Count > 0)
        {
            e.Handled = true;
            foreach (var row in rows.Take(10))
                Open(row);
        }
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None && rows.Count > 0)
        {
            e.Handled = true;
            _ = DeleteAsync(rows);
        }
        else if (e.Key == Key.C && modifiers == ModifierKeys.Control && rows.Count > 0)
        {
            e.Handled = true;
            FileClipboard.SetFiles([.. rows.Select(r => r.FullPath)], cut: false);
            StatusText.Text = $"{rows.Count:N0} item(s) copied to the clipboard.";
        }
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && rows.Count > 0)
        {
            e.Handled = true;
            CopyText(string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));
            StatusText.Text = $"{rows.Count:N0} path(s) copied as text.";
        }
    }

    // ---------- Session ----------

    public SearchState CaptureState() => new()
    {
        Drives = CheckedDrives,
        Folders = [.. _top.Where(n => !n.IsDrive).Select(n => n.Path)],
        CheckedFolders = [.. _checkedFolders],
        Text = QueryBox.Text,
        MatchCase = CaseToggle.IsChecked == true,
        UseRegex = RegexToggle.IsChecked == true,
        Type = TypeBox.SelectedIndex,
        Extensions = ExtensionsBox.Text,
        Size = SizeBox.SelectedIndex,
        DateColumn = DateColumnBox.SelectedIndex,
        Range = RangeBox.SelectedIndex,
        Content = ContentBox.Text,
        Sort = (int)_sort,
        Descending = _descending,
        // Width is NaN for an auto-sized column; the session file cannot hold NaN.
        Columns = [.. ResultColumns.Select(c => new ColumnState
        {
            Key = ColumnKey(c),
            Width = double.IsNaN(c.Width) ? c.ActualWidth : c.Width,
        })],
        Mark = _mark,
    };

    /// <summary>Puts back the saved column order and widths; unknown keys are skipped.</summary>
    private void RestoreColumns(List<ColumnState>? saved)
    {
        if (saved is not { Count: > 0 })
            return;
        int target = 0;
        foreach (var state in saved)
        {
            int index = -1;
            for (int i = target; i < ResultColumns.Count; i++)
                if (ColumnKey(ResultColumns[i]) == state.Key)
                    index = i;
            if (index < 0)
                continue;
            if (index != target)
                ResultColumns.Move(index, target);
            if (state.Width >= 20)
                ResultColumns[target].Width = state.Width;
            target++;
        }
        // Columns the saved session does not know yet (added later, like "#"): back to their default place.
        for (int d = 0; d < _defaultColumnOrder.Count; d++)
        {
            string key = _defaultColumnOrder[d];
            if (saved.Any(s => s.Key == key))
                continue;
            int index = -1;
            for (int i = 0; i < ResultColumns.Count; i++)
                if (ColumnKey(ResultColumns[i]) == key)
                    index = i;
            if (index >= 0 && index != Math.Min(d, ResultColumns.Count - 1))
                ResultColumns.Move(index, Math.Min(d, ResultColumns.Count - 1));
        }
    }

    /// <summary>Puts back the last search settings; nothing is read until the user searches.</summary>
    public void RestoreState(SearchState? state)
    {
        if (state is null)
            return;
        _filling = true;
        try
        {
            var checkedFolders = (state.CheckedFolders ?? []).ToList();
            var pinned = (state.Folders ?? []).ToList();
            if (state.Place is { Length: > 0 } place) // Older sessions: one place.
            {
                checkedFolders.Add(place);
                pinned.Add(place);
            }
            BuildLocations(state.Drives, pinned, checkedFolders);
            _mark = state.Mark;
            UpdateMark();
            QueryBox.Text = state.Text;
            CaseToggle.IsChecked = state.MatchCase;
            RegexToggle.IsChecked = state.UseRegex;
            ExtensionsBox.Text = state.Extensions;
            ContentBox.Text = state.Content ?? "";
            TypeBox.SelectedIndex = Math.Clamp(state.Type, 0, TypeBox.Items.Count - 1);
            SizeBox.SelectedIndex = Math.Clamp(state.Size, 0, SizeBox.Items.Count - 1);
            DateColumnBox.SelectedIndex = Math.Clamp(state.DateColumn, 0, DateColumnBox.Items.Count - 1);
            RangeBox.SelectedIndex = state.Range == 6 && _mark is null ? 0 : Math.Clamp(state.Range, 0, RangeBox.Items.Count - 1);
            ExtensionsBox.Visibility = TypeBox.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
            _sort = Enum.IsDefined((ResultSort)state.Sort) ? (ResultSort)state.Sort : ResultSort.Name;
            _descending = state.Descending;
            UpdateHeaders();
            RestoreColumns(state.Columns);
        }
        finally
        {
            _filling = false;
        }
    }
}
