using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.ViewModels;

/// <summary>
/// The shared folder tree on the left. Top-level groups:
/// Home, Favorites (same folders as the favorites bar), Desktop, This PC
/// (drives) and, with the setting on, Network (computers, then their shares).
/// It follows the active pane's folder.
/// </summary>
public sealed class FolderTreeViewModel : ObservableObject
{
    private readonly FavoritesViewModel _favorites;
    private int _syncVersion;

    public FolderTreeViewModel(FavoritesViewModel favorites)
    {
        _favorites = favorites;

        Roots.Add(UserFolder("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        FavoritesNode = new FolderNodeViewModel("Favorites", "", 0, ShellInfo.GetIcon("favorites", isDirectory: true), LoadFavoritesAsync);
        Roots.Add(FavoritesNode);
        // The bar changed (add, remove, rename, reorder): rebuild this group.
        _favorites.Items.CollectionChanged += (_, _) => _ = FavoritesNode.ReloadAsync();

        Roots.Add(UserFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));

        ThisPc = new FolderNodeViewModel("This PC", "", 0, ShellInfo.GetThisPcIcon(), LoadDrivesAsync);
        Roots.Add(ThisPc);

        // Shown by ShowNetwork (the setting); it reads the network only when opened.
        Network = new FolderNodeViewModel("Network", "", 0, ShellInfo.GetNetworkFolderIcon(), LoadNetworkAsync);

        FavoritesNode.IsExpanded = true;
        ThisPc.IsExpanded = true;
    }

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    public FolderNodeViewModel FavoritesNode { get; }

    public FolderNodeViewModel ThisPc { get; }

    /// <summary>The Network group: the computers on the network, below them their shares.</summary>
    public FolderNodeViewModel Network { get; }

    public bool ShowsNetwork => Roots.Contains(Network);

    /// <summary>Adds the Network group after This PC, or removes it (setting "Network in the folder tree").</summary>
    public void ShowNetwork(bool show)
    {
        if (show == ShowsNetwork)
            return;
        if (show)
        {
            Roots.Insert(Roots.IndexOf(ThisPc) + 1, Network);
            return;
        }
        if (Selected is not null && (Selected == Network || Network.ContainsLoaded(Selected)))
        {
            Selected.IsSelected = false;
            Selected = null;
        }
        Network.CollapseAll(); // Stops the folder watchers of open shares.
        Roots.Remove(Network);
    }

    public FolderNodeViewModel? Selected { get; private set; }

    private bool _followsPane = true;

    /// <summary>
    /// Header toggle: true = the tree expands to and selects the active pane's folder while
    /// you browse; false = the tree stays as it is. Saved in the session.
    /// </summary>
    public bool FollowsPane
    {
        get => _followsPane;
        set
        {
            if (!SetField(ref _followsPane, value))
                return;
            if (!value)
                _syncVersion++; // Stop a sync that is still running.
            FollowsPaneChanged?.Invoke();
        }
    }

    public event Action? FollowsPaneChanged;

    /// <summary>True while the tree selects a folder by itself (following the pane): no navigation then.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>Raised after <see cref="SelectPathAsync"/> with the chain root → selected node.</summary>
    public event Action<IReadOnlyList<FolderNodeViewModel>>? Synced;

    /// <summary>
    /// Expands the tree down to <paramref name="path"/> and selects it. The deepest matching
    /// start node wins: a folder inside a favorite shows under Favorites, inside the user
    /// profile under Home, everything else under its drive in This PC.
    /// </summary>
    public async Task SelectPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        int version = ++_syncVersion;

        // This PC: its own node.
        if (PathHelper.IsThisPc(path))
        {
            IsSyncing = true;
            try
            {
                if (Selected is not null && Selected != ThisPc)
                    Selected.IsSelected = false;
                ThisPc.IsSelected = true;
                Selected = ThisPc;
            }
            finally
            {
                IsSyncing = false;
            }
            Synced?.Invoke([ThisPc]);
            return;
        }

        await Task.WhenAll(ThisPc.LoadChildrenAsync(), FavoritesNode.LoadChildrenAsync());
        if (version != _syncVersion)
            return;

        // A network path shows under Network (when it is in the tree), also on a computer that the discovery missed.
        string? server = ShowsNetwork ? NetworkComputers.ServerOf(path) : null;
        if (server is not null)
        {
            await Network.LoadChildrenAsync(); // The first time: the network is read (can take seconds).
            if (version != _syncVersion)
                return;
            if (!Network.Children.Any(c => c.Contains(path)))
                AddComputer(server);
        }

        var computers = server is not null ? Network.Children : Enumerable.Empty<FolderNodeViewModel>();
        var starts = Roots.Where(r => !r.IsVirtual).Select(n => (Group: (FolderNodeViewModel?)null, Node: n))
            .Concat(FavoritesNode.Children.Select(n => (Group: (FolderNodeViewModel?)FavoritesNode, Node: n)))
            .Concat(ThisPc.Children.Select(n => (Group: (FolderNodeViewModel?)ThisPc, Node: n)))
            .Concat(computers.Select(n => (Group: (FolderNodeViewModel?)Network, Node: n)));
        var start = starts.Where(s => s.Node.Contains(path)).MaxBy(s => s.Node.Path.Length);
        if (start.Node is null)
            return;

        var chain = new List<FolderNodeViewModel>();
        if (start.Group is not null)
            chain.Add(start.Group);
        var node = start.Node;
        chain.Add(node);

        while (!PathHelper.IsSamePath(node.Path, path))
        {
            await node.LoadChildrenAsync();
            if (version != _syncVersion)
                return; // A newer request (fast navigation) took over.
            var next = node.Children.FirstOrDefault(c => c.Contains(path));
            if (next is null)
            {
                // The path goes through a folder that is not in the loaded list (created since
                // the list was read): add just that one folder, if it exists.
                string rest = path[node.Path.TrimEnd('\\').Length..].TrimStart('\\');
                string childPath = Path.Combine(node.Path, rest.Split('\\')[0]);
                if (rest.Length == 0 || !Directory.Exists(childPath))
                    break; // Select the deepest known parent.
                next = node.AddHiddenChild(childPath);
            }
            node = next;
            chain.Add(node);
        }

        foreach (var n in chain.Take(chain.Count - 1))
            n.IsExpanded = true;

        IsSyncing = true;
        try
        {
            if (Selected is not null && Selected != node)
                Selected.IsSelected = false;
            node.IsSelected = true;
            Selected = node;
        }
        finally
        {
            IsSyncing = false;
        }
        Synced?.Invoke(chain);
    }

    /// <summary>
    /// Back to the overview: Home and Network closed; Favorites, Desktop and
    /// This PC open with only their first level shown; everything deeper closed.
    /// </summary>
    public void CollapseAll()
    {
        _syncVersion++; // A running "follow the pane" must not open the tree again.
        foreach (var root in Roots)
        {
            foreach (var child in root.Children)
                child.CollapseAll();
            root.IsExpanded = root != Roots[0] && root != Network; // Roots[0] = Home (large; closed).
        }
    }

    /// <summary>The user selected a node (click, keyboard).</summary>
    public void SetSelected(FolderNodeViewModel? node) => Selected = node;

    private static FolderNodeViewModel UserFolder(string name, string path) =>
        new(name, path, 0, ShellInfo.GetFolderIcon(path));

    /// <summary>The favorites bar folders. Network favorites always get an expander (no blocking check).</summary>
    private async Task<List<FolderNodeViewModel>> LoadFavoritesAsync()
    {
        var favorites = _favorites.Items.Select(f => (f.Name, Path: f.FullPath, f.Icon)).ToList();
        var hasSubfolders = await Task.Run(() => favorites
            .Select(f => f.Path.StartsWith(@"\\", StringComparison.Ordinal) || FolderNodeViewModel.HasSubfolders(f.Path))
            .ToList());
        return [.. favorites.Select((f, i) => new FolderNodeViewModel(f.Name, f.Path, 1, f.Icon, hasChildren: hasSubfolders[i]))];
    }

    private CancellationTokenSource? _discovery;

    /// <summary>
    /// Network computers, each opens to its shares. At once: this computer and the servers of mapped drives,
    /// favorites and recent folders. Then, as Windows' network discovery finds them (over some seconds, and only
    /// with network discovery on): the other computers, added in name order.
    /// </summary>
    private async Task<List<FolderNodeViewModel>> LoadNetworkAsync()
    {
        var known = _favorites.Items.Select(f => f.FullPath).Concat(RecentFolders.List.Select(r => r.Path)).ToList();
        var servers = await Task.Run(() => NetworkComputers.Known(known));
        _ = AddDiscoveredAsync();
        return [.. servers.Select(ComputerNode)];
    }

    private async Task AddDiscoveredAsync()
    {
        _discovery?.Cancel(); // F5 on the group: a new search replaces the running one.
        var cancel = _discovery = new CancellationTokenSource();
        // First the known computers must be in the list (this runs while the group is still loading them).
        await Task.Yield();
        await Network.LoadChildrenAsync();
        var found = new Progress<string>(server =>
        {
            if (!cancel.IsCancellationRequested && !Network.Children.Any(c => PathHelper.IsSamePath(c.Path, server)))
                AddComputer(server);
        });
        await NetworkComputers.DiscoverAsync(found, cancel.Token);
    }

    private static FolderNodeViewModel ComputerNode(string server) =>
        new(server.TrimStart('\\'), server, 1, ShellInfo.GetNetworkIcon(server));

    /// <summary>A computer that is not in the list yet (the pane shows a path on it), in name order.</summary>
    private void AddComputer(string server)
    {
        var node = ComputerNode(server);
        int index = 0;
        while (index < Network.Children.Count &&
               StringComparer.CurrentCultureIgnoreCase.Compare(Network.Children[index].Name, node.Name) < 0)
            index++;
        Network.Children.Insert(index, node);
    }

    /// <summary>Drives as "C: (System)". Labels are read off the UI thread; network drives are not touched.</summary>
    private static async Task<List<FolderNodeViewModel>> LoadDrivesAsync()
    {
        var drives = await Task.Run(() => DriveInfo.GetDrives().Select(d =>
        {
            string letter = d.Name.TrimEnd('\\');
            string label = "";
            bool ready = false;
            if (d.DriveType != DriveType.Network)
            {
                try
                {
                    ready = d.IsReady;
                    label = ready ? d.VolumeLabel : "";
                }
                catch (IOException)
                {
                }
            }
            return (Root: d.Name, Text: label.Length > 0 ? $"{letter} ({label})" : letter, Ready: ready);
        }).ToList());

        return [.. drives.Select(d => new FolderNodeViewModel(
            d.Text, d.Root, 1,
            d.Ready ? ShellInfo.GetFolderIcon(d.Root) : ShellInfo.GetIcon(d.Root, isDirectory: true)))];
    }
}
