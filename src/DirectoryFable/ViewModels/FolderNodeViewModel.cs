using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.ViewModels;

/// <summary>
/// One folder in the folder tree. Subfolders load the first time the node is expanded
/// (a placeholder child makes the expander visible until then). While a folder node is open
/// it watches its subfolders and merges changes in.
/// </summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private readonly Func<Task<List<FolderNodeViewModel>>>? _childFactory;
    private Task? _loading;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isDropTarget;
    private TreeFolderWatcher? _watcher;
    private int _refreshVersion;

    /// <param name="path">Folder path; empty for a virtual node (This PC).</param>
    /// <param name="childFactory">Custom children (This PC lists drives); null = read subfolders.</param>
    /// <param name="hasChildren">False: the folder has no subfolders, so no expander box is shown.</param>
    public FolderNodeViewModel(string name, string path, int depth, ImageSource? icon,
        Func<Task<List<FolderNodeViewModel>>>? childFactory = null, bool isPlaceholder = false, bool hasChildren = true)
    {
        Name = name;
        Path = path;
        Depth = depth;
        Icon = icon;
        IsPlaceholder = isPlaceholder;
        _childFactory = childFactory;
        if (!isPlaceholder && hasChildren)
            Children.Add(NewPlaceholder(depth + 1));
    }

    public string Name { get; }
    public string Path { get; }
    public int Depth { get; }
    public ImageSource? Icon { get; }
    public bool IsPlaceholder { get; }

    /// <summary>A node without a real folder (This PC).</summary>
    public bool IsVirtual => Path.Length == 0;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value))
                return;
            if (value)
            {
                // Opened again: it watched nothing while closed, so read the subfolders again.
                _ = _loading is { IsCompleted: true } ? RefreshChildrenAsync() : LoadChildrenAsync();
                StartWatching();
            }
            else
            {
                StopWatching();
            }
        }
    }

    // ---------- Auto-refresh ----------

    /// <summary>Real folders only: not This PC, the Favorites group, a server or a placeholder.</summary>
    private bool CanWatch => !IsVirtual && !IsPlaceholder && _childFactory is null && !PathHelper.IsServerPath(Path);

    private void StartWatching()
    {
        if (_watcher is not null || !CanWatch)
            return;
        _watcher = new TreeFolderWatcher(Path, Dispatcher.CurrentDispatcher,
            () => _ = RefreshChildrenAsync(), OnGrandchildrenChanged);
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>A removed branch: stop every watcher in it.</summary>
    private void StopWatchingTree()
    {
        StopWatching();
        foreach (var child in Children)
            child.StopWatchingTree();
    }

    /// <summary>
    /// Reads the subfolders again and merges them in: existing rows stay (with their open
    /// branches and selection), gone ones are removed, new ones inserted in name order.
    /// </summary>
    private async Task RefreshChildrenAsync()
    {
        if (_childFactory is not null || IsVirtual || IsPlaceholder)
            return;
        if (_loading is null)
        {
            await LoadChildrenAsync();
            return;
        }
        await _loading;

        int version = ++_refreshVersion;
        var folders = await ReadChildrenAsync();
        if (version != _refreshVersion)
            return; // A newer read is on its way.

        var wanted = new HashSet<string>(folders.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Children.Where(c => c.IsPlaceholder || !wanted.Contains(c.Path)).ToList())
        {
            gone.StopWatchingTree();
            Children.Remove(gone);
        }

        for (int i = 0; i < folders.Count; i++)
        {
            var (path, hasSubfolders) = folders[i];
            int at = -1;
            for (int j = i; j < Children.Count; j++)
            {
                if (string.Equals(Children[j].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                Children.Insert(i, new FolderNodeViewModel(PathHelper.GetName(path), path, Depth + 1,
                    ShellInfo.GetFolderIconFast(path), hasChildren: hasSubfolders));
                continue;
            }
            if (at != i)
                Children.Move(at, i);
            Children[i].SetHasSubfolders(hasSubfolders);
        }
    }

    /// <summary>Subfolders of some children changed: their expander box may appear or disappear.</summary>
    private async void OnGrandchildrenChanged(IReadOnlyCollection<string> paths)
    {
        var results = await Task.Run(() => paths.Select(p => (Path: p, Has: HasSubfolders(p))).ToList());
        foreach (var (path, has) in results)
        {
            Children.FirstOrDefault(c => !c.IsPlaceholder &&
                string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))?.SetHasSubfolders(has);
        }
    }

    /// <summary>
    /// Keeps the expander box right for a closed node. A closed node that was loaded before
    /// is reset when it changes, so it reads its subfolders again when opened.
    /// </summary>
    private void SetHasSubfolders(bool has)
    {
        if (IsExpanded || has == Children.Count > 0)
            return; // An open node watches itself; the box is already right.
        foreach (var child in Children)
            child.StopWatchingTree();
        Children.Clear();
        _loading = null;
        if (has)
            Children.Add(NewPlaceholder(Depth + 1));
    }

    /// <summary>True when <paramref name="node"/> is somewhere below this node (loaded children only).</summary>
    public bool ContainsLoaded(FolderNodeViewModel node) =>
        Children.Any(c => c == node || c.ContainsLoaded(node));

    /// <summary>Closes this node and every open node below it (deepest first).</summary>
    public void CollapseAll()
    {
        foreach (var child in Children)
            child.CollapseAll();
        IsExpanded = false;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>True while files are dragged over this node.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetField(ref _isDropTarget, value);
    }

    /// <summary>Loads the subfolders once; later calls wait for the same load.</summary>
    public Task LoadChildrenAsync() => _loading ??= LoadCoreAsync();

    /// <summary>
    /// Reads the subfolders again (F5 in the tree; the Favorites group when the bar changed).
    /// Real folders merge the changes in, so open branches stay open.
    /// </summary>
    public Task ReloadAsync()
    {
        if (_childFactory is null && _loading is { IsCompleted: true })
            return RefreshChildrenAsync();
        _loading = null;
        return LoadChildrenAsync();
    }

    private async Task LoadCoreAsync()
    {
        List<FolderNodeViewModel> children;
        if (_childFactory is not null)
        {
            children = await _childFactory();
        }
        else
        {
            var folders = await ReadChildrenAsync();
            children = [.. folders.Select(f => new FolderNodeViewModel(
                PathHelper.GetName(f.Path), f.Path, Depth + 1, ShellInfo.GetFolderIconFast(f.Path),
                hasChildren: f.HasSubfolders))];
        }

        foreach (var old in Children)
            old.StopWatchingTree();
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    // A server's hidden shares ("C$") that the pane went to: a read of the server never lists them, so they are
    // kept here and stay in the tree over refreshes (else the selected folder below would vanish from it).
    private HashSet<string>? _unlisted;

    /// <summary>The subfolders as read (off the UI thread), plus the unlisted ones, in name order.</summary>
    private Task<List<(string Path, bool HasSubfolders)>> ReadChildrenAsync()
    {
        string path = Path;
        var unlisted = _unlisted?.ToList();
        return Task.Run(() =>
        {
            var folders = ReadSubfolders(path);
            if (unlisted is null)
                return folders;
            foreach (string extra in unlisted)
            {
                if (!folders.Any(f => string.Equals(f.Path, extra, StringComparison.OrdinalIgnoreCase)))
                    folders.Add((extra, true));
            }
            folders.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(PathHelper.GetName(a.Path), PathHelper.GetName(b.Path)));
            return folders;
        });
    }

    private static readonly EnumerationOptions FolderOptions = new()
    {
        IgnoreInaccessible = true,
        // Always everything, hidden and protected system folders too.
        AttributesToSkip = 0,
    };

    /// <summary>Subfolders, sorted, each with "has subfolders itself" (decides the expander box).</summary>
    private static List<(string Path, bool HasSubfolders)> ReadSubfolders(string path)
    {
        try
        {
            // "\\SERVER": its shares (always with an expander: checking each share is slow on a network).
            if (PathHelper.IsServerPath(path))
                return [.. NetworkShares.List(path).Select(s => ($@"{path.TrimEnd('\\')}\{s}", true))];

            var list = Directory.EnumerateDirectories(path, "*", FolderOptions).ToList();
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            return [.. list.Select(p => (p, HasSubfolders(p)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool HasSubfolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", FolderOptions).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds one subfolder that is not in the loaded list yet (created since the last load) when
    /// the pane shows a folder inside it, so the tree can still open the path. Kept in name order.
    /// </summary>
    public FolderNodeViewModel AddHiddenChild(string childPath)
    {
        if (PathHelper.IsServerPath(Path))
            (_unlisted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(childPath);
        var node = new FolderNodeViewModel(PathHelper.GetName(childPath), childPath, Depth + 1,
            ShellInfo.GetFolderIconFast(childPath));
        int index = 0;
        while (index < Children.Count &&
               StringComparer.CurrentCultureIgnoreCase.Compare(Children[index].Name, node.Name) < 0)
            index++;
        Children.Insert(index, node);
        return node;
    }


    private static FolderNodeViewModel NewPlaceholder(int depth) =>
        new("", "", depth, null, isPlaceholder: true);

    /// <summary>The folder name: what screen readers and UI Automation see for a tree row.</summary>
    public override string ToString() => Name;

    /// <summary>True when <paramref name="path"/> is this folder or inside it.</summary>
    public bool Contains(string path)
    {
        if (IsVirtual || IsPlaceholder)
            return false;
        string mine = Path.TrimEnd('\\');
        return string.Equals(mine, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(mine + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
