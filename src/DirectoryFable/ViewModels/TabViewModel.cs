using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.Search;
using DirectoryFable.Shell;

namespace DirectoryFable.ViewModels;

/// <summary>Raised after a folder is loaded. The view uses it to set selection and focus.</summary>
public sealed class NavigatedEventArgs(IReadOnlyCollection<string> selectPaths, string? focusPath) : EventArgs
{
    /// <summary>Items to select after loading (full paths). Usually empty.</summary>
    public IReadOnlyCollection<string> SelectPaths { get; } = selectPaths;

    /// <summary>Item that gets the focus rectangle. Null: first selected item, else first item.</summary>
    public string? FocusPath { get; } = focusPath;
}

/// <summary>One tab: current folder, its contents and its own navigation history.</summary>
public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private FolderWatcher? _watcher;
    private string _watchedPath = "";
    private int _loadVersion;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    private string _currentPath = "";
    private string _statusText = "";
    private bool _isActive;
    private bool _isSelected;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<FileItem> Items { get; } = [];

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    public event EventHandler<NavigatedEventArgs>? Navigated;

    /// <summary>Before / after <see cref="Items"/> is replaced as a whole (folder load).</summary>
    public event Action? ItemsResetStarting;
    public event Action? ItemsResetFinished;

    // ---------- Sorting and grouping (per tab) ----------

    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }

    /// <summary>Group dividers by type.</summary>
    public bool GroupByType { get; set; } = true;

    /// <summary>
    /// Shown columns in display order with widths (kept up to date by the file list;
    /// saved in the session). Null = default columns.
    /// </summary>
    public List<ColumnState>? ColumnLayout { get; set; }

    /// <summary>Group dividers the user closed in this tab (type names, e.g. "TXT File"); saved in the session.</summary>
    public HashSet<string> CollapsedGroups { get; } = new(StringComparer.CurrentCultureIgnoreCase);

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!SetField(ref _currentPath, value))
                return;

            Breadcrumbs.Clear();
            var segments = PathHelper.GetSegments(value);
            for (int i = 0; i < segments.Count; i++)
                Breadcrumbs.Add(i == segments.Count - 1 ? segments[i] with { IsLast = true } : segments[i]);

            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(FolderIcon));
            IsInArchive = ZipBrowser.IsInArchive(value);
            OnPropertyChanged(nameof(IsInArchive));
            IsNetwork = IsNetworkPath(value);
            OnPropertyChanged(nameof(IsNetwork));
            OnPropertyChanged(nameof(IsThisPc));
            OnPropertyChanged(nameof(CanShowSpace));
            if (_spaceMode && !CanShowSpace)
                SpaceMode = false; // This PC, a ZIP file, a server: the normal view.
        }
    }

    // ---------- Quick filter ----------

    private string _filter = "";

    /// <summary>
    /// Quick filter of the list: only names with these words stay visible (typed in the list or the
    /// filter box; same rules as SonnetSearch's names). Cleared when the tab opens another folder.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set => SetField(ref _filter, value ?? "");
    }

    // ---------- Space view ----------

    private bool _spaceMode;

    /// <summary>The pane shows the Space view (what takes the space, sorted by size) instead of the file list.</summary>
    public bool SpaceMode
    {
        get => _spaceMode;
        set => SetField(ref _spaceMode, value);
    }

    // ---------- Tile view ----------

    private bool _tileMode;

    /// <summary>The file list shows large tiles with Windows' thumbnails instead of rows (stays on in other folders).</summary>
    public bool TileMode
    {
        get => _tileMode;
        set => SetField(ref _tileMode, value);
    }

    /// <summary>A folder on a drive or share (not This PC, a ZIP file or a server's share list).</summary>
    public bool CanShowSpace =>
        CurrentPath.Length > 0 && !PathHelper.IsThisPc(CurrentPath) && !IsInArchive && !PathHelper.IsServerPath(CurrentPath);

    /// <summary>The tab shows This PC: the drives view instead of the file list.</summary>
    public bool IsThisPc => PathHelper.IsThisPc(CurrentPath);

    /// <summary>True on a network path: "\\server\share\..." or a mapped network drive (the address bar is blue).</summary>
    public bool IsNetwork { get; private set; }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            try
            {
                return new System.IO.DriveInfo(path[..1]).DriveType == System.IO.DriveType.Network;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Icon for the tab and the address bar: network icons for a server or share root.</summary>
    public System.Windows.Media.ImageSource? FolderIcon =>
        PathHelper.IsThisPc(CurrentPath) ? ShellInfo.GetThisPcIcon()
        : ZipBrowser.TrySplit(CurrentPath, out string zip, out string inner) && inner.Length == 0
            ? ShellInfo.GetIcon(zip, isDirectory: false)
            : ShellInfo.GetFolderIconFast(CurrentPath);

    /// <summary>True while the tab shows a folder inside a ZIP file (the list is tinted).</summary>
    public bool IsInArchive { get; private set; }

    /// <summary>Tab label: "(T:) docs", "(C:)" at a drive root.</summary>
    public string Title => PathHelper.GetTabTitle(CurrentPath);

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>True when this tab is the visible tab of the active pane (has the keyboard).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    /// <summary>True when this tab is the visible tab of its pane.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value))
                return;

            // Only the visible tab watches its folder. A tab that comes back into view
            // catches up on what changed while it was hidden.
            UpdateWatcher();
            if (value && CurrentPath.Length > 0)
                _ = MergeRefreshAsync();
        }
    }


    private bool _isDragging;

    /// <summary>True while this tab is dragged to a new place in the strip (shown faded).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => PathHelper.GetParent(CurrentPath) is not null;

    /// <summary>Opens a folder and adds the current one to the Back history.</summary>
    public Task<bool> NavigateAsync(string path, string? focusPath = null)
    {
        string target;
        try
        {
            target = PathHelper.Normalize(path, CurrentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText = $"Invalid path \"{path}\": {ex.Message}";
            return Task.FromResult(false);
        }

        if (PathHelper.IsSamePath(target, CurrentPath))
            return RefreshAsync([]);

        return LoadAsync(target, [], focusPath, previous =>
        {
            if (previous.Length > 0)
                _back.Push(previous);
            _forward.Clear();
        });
    }

    public Task<bool> GoBackAsync()
    {
        if (_back.Count == 0)
            return Task.FromResult(false);

        // Focus the folder we leave, in case it is a child of the target (common case).
        return LoadAsync(_back.Peek(), [], CurrentPath, previous =>
        {
            _back.Pop();
            _forward.Push(previous);
        });
    }

    public Task<bool> GoForwardAsync()
    {
        if (_forward.Count == 0)
            return Task.FromResult(false);

        return LoadAsync(_forward.Peek(), [], CurrentPath, previous =>
        {
            _forward.Pop();
            _back.Push(previous);
        });
    }

    public Task<bool> GoUpAsync()
    {
        string? parent = PathHelper.GetParent(CurrentPath);
        return parent is null
            ? Task.FromResult(false)
            : NavigateAsync(parent, focusPath: CurrentPath);
    }

    public Task<bool> GoHomeAsync() =>
        NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Back history, most recent first (for the address bar drop-down).</summary>
    public IReadOnlyList<string> RecentFolders => [.. _back.Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reloads the current folder. History does not change.</summary>
    public Task<bool> RefreshAsync(IReadOnlyCollection<string> select, string? focusPath = null) =>
        CurrentPath.Length == 0
            ? Task.FromResult(false)
            : LoadAsync(CurrentPath, select, focusPath, commitHistory: null);

    /// <summary>Shows a message in the status line (errors from file operations etc.).</summary>
    public void ShowStatus(string text) => StatusText = text;

    /// <summary>
    /// Reads the folder off the UI thread, then swaps the list.
    /// <paramref name="commitHistory"/> runs only on success, with the previous path.
    /// </summary>
    private async Task<bool> LoadAsync(
        string path, IReadOnlyCollection<string> selectPaths, string? focusPath, Action<string>? commitHistory)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _loadVersion++;

        List<FileItem> entries;
        try
        {
            entries = await Task.Run(() => ReadFolder(path, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException)
        {
            StatusText = $"Cannot open \"{path}\": {ex.Message}";
            return false;
        }

        if (cts.IsCancellationRequested)
            return false;

        // Icons and type names come from the shell on the UI thread (cached by extension).
        foreach (var item in entries)
        {
            item.Icon = item.IsShare ? ShellInfo.GetFolderIconFast(item.FullPath) : ShellInfo.GetIcon(item.FullPath, item.IsDirectory);
            item.TypeName = item.IsShare ? "Network share" : ShellInfo.GetTypeName(item.FullPath, item.IsDirectory);
            item.IsCut = FileClipboard.IsCut(item.FullPath);
        }

        commitHistory?.Invoke(CurrentPath);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        // Another folder: the quick filter starts empty (before the list is rebuilt); This PC lists it as recent.
        if (commitHistory is not null && !PathHelper.IsSamePath(path, CurrentPath))
        {
            Filter = "";
            Infrastructure.RecentFolders.Note(path); // (RecentFolders here is this tab's own Back list.)
        }
        CurrentPath = path;
        // The view sorts and groups; it is rebuilt around the bulk swap (else every Add re-sorts).
        ItemsResetStarting?.Invoke();
        try
        {
            Items.Clear();
            foreach (var item in entries)
                Items.Add(item);
        }
        finally
        {
            ItemsResetFinished?.Invoke();
        }

        UpdateWatcher();
        if (commitHistory is not null)
            StatusText = ""; // A new folder: old messages ("3 items copied", errors) go away.
        UpdateStatus();
        Navigated?.Invoke(this, new NavigatedEventArgs(selectPaths, focusPath));
        // .exe/.lnk/.ico rows: their own icons replace the extension icon in the background.
        _ = ShellInfo.LoadOwnIconsAsync(entries, cts.Token);
        _ = UpdateFolderSizesAsync();
        return true;
    }

    // ---------- Auto-refresh ----------

    private void UpdateWatcher()
    {
        string want = IsSelected && !PathHelper.IsThisPc(CurrentPath) ? CurrentPath : ""; // This PC: the drives view updates itself.
        if (string.Equals(want, _watchedPath, StringComparison.OrdinalIgnoreCase) && (_watcher is not null) == (want.Length > 0))
            return;

        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = want;
        if (want.Length == 0)
            return;
        // Inside a ZIP: watch the ZIP file (its folder, filtered to the file name).
        _watcher = ZipBrowser.TrySplit(want, out string zipFile, out _)
            ? new FolderWatcher(Path.GetDirectoryName(zipFile)!, _dispatcher, () => _ = MergeRefreshAsync(), Path.GetFileName(zipFile))
            : new FolderWatcher(want, _dispatcher, () =>
            {
                FolderSizes.NoteChange(want); // The folder sizes of this drive are read again soon.
                _ = MergeRefreshAsync();
            });
    }

    /// <summary>
    /// Re-reads the folder and changes only what differs: new rows are inserted, gone rows
    /// removed, changed rows updated in place. Selection, focus and scroll position stay.
    /// </summary>
    public async Task MergeRefreshAsync()
    {
        string path = CurrentPath;
        if (path.Length == 0)
            return;

        int version = _loadVersion;
        List<FileItem> fresh;
        try
        {
            fresh = await Task.Run(() => ReadFolder(path, CancellationToken.None));
        }
        catch (DirectoryNotFoundException)
        {
            // The folder itself was deleted or renamed: go to the nearest folder that still exists.
            if (version == _loadVersion && FindExistingParent(path) is { } parent)
                await NavigateAsync(parent);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return;
        }

        // A navigation started meanwhile: its result wins.
        if (version != _loadVersion || !PathHelper.IsSamePath(path, CurrentPath))
            return;

        var existing = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
            existing[item.FullPath] = item;

        var final = new List<FileItem>(fresh.Count);
        var needIcons = new List<FileItem>(); // New rows, and files that changed (a rebuilt exe).
        foreach (var item in fresh)
        {
            if (existing.TryGetValue(item.FullPath, out var old) && old.IsDirectory == item.IsDirectory)
            {
                if (old.DateModified != item.DateModified)
                    needIcons.Add(old);
                old.UpdateFrom(item);
                final.Add(old);
            }
            else
            {
                item.Icon = item.IsShare ? ShellInfo.GetFolderIconFast(item.FullPath) : ShellInfo.GetIcon(item.FullPath, item.IsDirectory);
                item.TypeName = item.IsShare ? "Network share" : ShellInfo.GetTypeName(item.FullPath, item.IsDirectory);
                item.IsCut = FileClipboard.IsCut(item.FullPath);
                needIcons.Add(item);
                final.Add(item);
            }
        }

        SyncItems(final);
        UpdateStatus();
        if (needIcons.Count > 0)
            _ = ShellInfo.LoadOwnIconsAsync(needIcons, _loadCts?.Token ?? CancellationToken.None);
        _ = UpdateFolderSizesAsync();
    }

    /// <summary>Makes <see cref="Items"/> equal to <paramref name="final"/> with few collection changes.</summary>
    private void SyncItems(List<FileItem> final)
    {
        var keep = new HashSet<FileItem>(final);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Items[i]))
                Items.RemoveAt(i);
        }

        for (int i = 0; i < final.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], final[i]))
                continue;

            int from = -1;
            for (int j = i + 1; j < Items.Count; j++)
            {
                if (ReferenceEquals(Items[j], final[i]))
                {
                    from = j;
                    break;
                }
            }

            if (from >= 0)
                Items.Move(from, i);
            else
                Items.Insert(i, final[i]);
        }
    }

    private static string? FindExistingParent(string path)
    {
        for (string? p = PathHelper.GetParent(path); p is not null; p = PathHelper.GetParent(p))
        {
            if (Directory.Exists(p) || PathHelper.IsThisPc(p)) // A drive that is gone: This PC.
                return p;
        }
        return null;
    }

    public TabViewModel()
    {
        FileClipboard.CutChanged += UpdateCutState;
        FolderSizes.Changed += OnFolderSizesRenewed;
    }

    /// <summary>Fades the rows that are on the clipboard as "cut" (and un-fades the others).</summary>
    private void UpdateCutState()
    {
        foreach (var item in Items)
            item.IsCut = FileClipboard.IsCut(item.FullPath);
    }

    public void Dispose()
    {
        FileClipboard.CutChanged -= UpdateCutState;
        FolderSizes.Changed -= OnFolderSizesRenewed;
        _loadCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---------- Status bar numbers ----------

    public int FolderCount { get; private set; }
    public int FileCount { get; private set; }
    public long TotalSize { get; private set; }

    /// <summary>Free / total bytes of the drive or share (null = unknown, e.g. a server listing).</summary>
    public long? DriveFree { get; private set; }
    public long? DriveTotal { get; private set; }

    /// <summary>Counts, sizes or drive space changed (the status bar redraws).</summary>
    public event Action? TotalsChanged;

    private int _spaceVersion;

    private void UpdateStatus()
    {
        UpdateTotals();
        _ = UpdateDriveSpaceAsync();
    }

    /// <summary>Counts and the total size (files, plus the folders whose size is known).</summary>
    private void UpdateTotals()
    {
        FolderCount = Items.Count(i => i.IsDirectory);
        FileCount = Items.Count - FolderCount;
        TotalSize = Items.Sum(i => i.SortSize ?? 0);
        TotalsChanged?.Invoke();
    }

    // ---------- Folder sizes ----------

    /// <summary>Folder sizes arrived (true: the first ones; a list sorted by size sorts again) or changed (false).</summary>
    public event Action<bool>? FolderSizesChanged;

    private int _sizeVersion;

    private bool ShowsSizeColumn => ColumnLayout is null || ColumnLayout.Any(c => c.Key == nameof(SortColumn.Size));

    /// <summary>
    /// Fills the Size of the folder rows from the drive's file table (see <see cref="FolderSizes"/>):
    /// at once when the drive was read before, else after its first read ("…" meanwhile).
    /// </summary>
    public async Task UpdateFolderSizesAsync()
    {
        string path = CurrentPath;
        int version = ++_sizeVersion;
        FolderSizeResult? result = null;
        if (FolderSizes.Enabled && ShowsSizeColumn && path.Length > 0 && !PathHelper.IsThisPc(path))
        {
            try
            {
                result = await FolderSizes.ForFolderAsync(path, reading: () =>
                {
                    if (version != _sizeVersion)
                        return;
                    foreach (var item in Items)
                    {
                        if (item.IsDirectory && item.FolderSize is null)
                            item.FolderSizePending = true;
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result = null;
            }
            if (version != _sizeVersion || !PathHelper.IsSamePath(path, CurrentPath))
                return;
        }

        bool changed = false, arrived = false;
        foreach (var item in Items)
        {
            if (!item.IsDirectory)
                continue;
            item.FolderSizePending = false;
            long? size = result is not null && result.Sizes.TryGetValue(item.Name, out long bytes) ? bytes : null;
            if (item.FolderSize != size)
            {
                arrived |= item.FolderSize is null;
                item.FolderSize = size;
                changed = true;
            }
        }
        if (changed)
        {
            UpdateTotals();
            FolderSizesChanged?.Invoke(arrived);
        }
    }

    /// <summary>A drive was read again: the visible tab of a folder on it takes the new sizes.</summary>
    private void OnFolderSizesRenewed(string root)
    {
        if (IsSelected && CurrentPath.StartsWith(root[..2], StringComparison.OrdinalIgnoreCase))
            _ = UpdateFolderSizesAsync();
    }

    /// <summary>Free space is read off the UI thread: a network share can take a moment.</summary>
    private async Task UpdateDriveSpaceAsync()
    {
        string path = CurrentPath;
        int version = ++_spaceVersion;
        string spaceFolder = ZipBrowser.TrySplit(path, out string zip, out _) ? Path.GetDirectoryName(zip)! : path;
        var space = PathHelper.IsServerPath(path) || path.Length == 0
            ? null
            : await Task.Run(() => DriveSpace.Get(spaceFolder));
        if (version != _spaceVersion)
            return;
        DriveFree = space?.Free;
        DriveTotal = space?.Total;
        TotalsChanged?.Invoke();
    }

    private static List<FileItem> ReadFolder(string path, CancellationToken ct)
    {
        // This PC has no files: the pane shows its drives view.
        if (PathHelper.IsThisPc(path))
            return [];
        // "\\SERVER": list its shares as folders (opening one goes to "\\SERVER\share").
        if (PathHelper.IsServerPath(path))
        {
            string server = path.TrimEnd('\\');
            return [.. NetworkShares.List(server).Select(share => new FileItem
            {
                Name = share,
                FullPath = $@"{server}\{share}",
                IsDirectory = true,
                IsShare = true,
            })];
        }

        // Inside a ZIP file: its entries, shown like files and folders.
        if (ZipBrowser.TrySplit(path, out string zipFile, out string inner))
        {
            string basePath = path.TrimEnd('\\');
            return [.. ZipBrowser.List(zipFile, inner).Select(z => new FileItem
            {
                Name = z.Name,
                FullPath = $@"{basePath}\{z.Name}",
                IsDirectory = z.IsDirectory,
                Size = z.IsDirectory ? null : z.Size,
                DateModified = z.Modified,
                IsInArchive = true,
            })];
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            // Always everything: hidden and protected system files too.
            AttributesToSkip = 0,
        };

        var list = new List<FileItem>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
            list.Add(new FileItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDir,
                Size = isDir ? null : ((FileInfo)info).Length,
                DateCreated = info.CreationTime,
                DateModified = info.LastWriteTime,
                IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
            });
        }

        // Folders first, then by name (natural sort comes later).
        list.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }
}
