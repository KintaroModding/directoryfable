using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.ViewModels;

/// <summary>
/// The shared folder tree on the left. Top-level groups:
/// Home, Favorites (same folders as the favorites bar), Desktop, This PC
/// (drives). It follows the active pane's folder.
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

        FavoritesNode.IsExpanded = true;
        ThisPc.IsExpanded = true;
    }

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    public FolderNodeViewModel FavoritesNode { get; }

    public FolderNodeViewModel ThisPc { get; }

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

        var starts = Roots.Where(r => !r.IsVirtual).Select(n => (Group: (FolderNodeViewModel?)null, Node: n))
            .Concat(FavoritesNode.Children.Select(n => (Group: (FolderNodeViewModel?)FavoritesNode, Node: n)))
            .Concat(ThisPc.Children.Select(n => (Group: (FolderNodeViewModel?)ThisPc, Node: n)));
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
    /// Back to the overview: Home closed; Favorites, Desktop and
    /// This PC open with only their first level shown; everything deeper closed.
    /// </summary>
    public void CollapseAll()
    {
        _syncVersion++; // A running "follow the pane" must not open the tree again.
        foreach (var root in Roots)
        {
            foreach (var child in root.Children)
                child.CollapseAll();
            root.IsExpanded = root != Roots[0]; // Roots[0] = Home (large; closed).
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
