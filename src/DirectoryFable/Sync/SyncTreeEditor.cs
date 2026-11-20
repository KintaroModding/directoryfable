using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Sync;

/// <summary>
/// The folder tree of FlashSync's job editor and what is ticked in it. A tick on a folder makes it a source. A tick
/// taken away below a source leaves that folder out. Nothing below a left-out folder can be ticked again (the copy
/// tool cannot take a part of a left-out folder).
/// </summary>
public sealed class SyncTreeEditor
{
    private readonly List<string> _sources = [];
    private readonly List<string> _excluded = [];

    /// <summary>The drives, each with its folders below (read when a node is opened).</summary>
    public ObservableCollection<SyncTreeNode> Roots { get; } = [];

    /// <summary>The ticked folders, sorted.</summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>The folders left out below a source, sorted.</summary>
    public IReadOnlyList<string> Excluded => _excluded;

    /// <summary>The ticks changed (the lists of the job pane follow).</summary>
    public event Action? Changed;

    public SyncTreeEditor()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            string label = "";
            try
            {
                ready = drive.IsReady;
                if (ready)
                    label = drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }
            if (!ready)
                continue;
            string path = drive.Name; // "C:\"
            string name = label.Length > 0 ? $"{path.TrimEnd('\\')}  {label}" : path.TrimEnd('\\');
            Roots.Add(new SyncTreeNode(this, path, name, 0, ShellInfo.GetFolderIcon(path), hasChildren: true));
        }
    }

    /// <summary>Takes a job's sources and left-out folders, and opens the tree down to each source.</summary>
    public async Task LoadAsync(SyncJob job)
    {
        _sources.Clear();
        _excluded.Clear();
        _sources.AddRange(job.Sources.Where(SyncSafety.IsFullPath).Select(Tidy));
        _excluded.AddRange(job.ExcludedFolders.Where(SyncSafety.IsFullPath).Select(Tidy)
            .Where(e => _sources.Any(s => Inside(e, s) && !Same(e, s))));
        Sort();
        Refresh();
        foreach (string source in _sources.ToList())
            await RevealAsync(source);
    }

    /// <summary>The path as the tree writes it: no "\" at the end, except a drive ("C:\").</summary>
    private static string Tidy(string path)
    {
        path = path.Trim().Replace('/', '\\');
        string trimmed = path.TrimEnd('\\');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies below it.</summary>
    private static bool Inside(string path, string folder)
    {
        string a = path.TrimEnd('\\'), b = folder.TrimEnd('\\');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>true = copied whole; null = copied in part (something below is left out, or only folders below are ticked); false = not copied.</summary>
    public bool? StateOf(string path)
    {
        if (_sources.Any(s => Inside(path, s)))
        {
            if (_excluded.Any(e => Inside(path, e)))
                return false;
            return _excluded.Any(e => Inside(e, path)) ? null : true;
        }
        return _sources.Any(s => Inside(s, path)) ? null : false;
    }

    /// <summary>The folder lies below a left-out folder (its own tick cannot change).</summary>
    public bool IsLocked(string path) => _excluded.Any(e => Inside(path, e) && !Same(path, e));

    /// <summary>The folder is left out of a source (itself or through a folder above).</summary>
    public bool IsLeftOut(string path) => _excluded.Any(e => Inside(path, e));

    /// <summary>
    /// A click on a folder's box. Returns a text when nothing could change (the folder lies below a left-out one).
    /// </summary>
    public string? Toggle(string path)
    {
        path = Tidy(path);
        string? source = _sources.FirstOrDefault(s => Inside(path, s));
        if (source is null)
        {
            // Not covered yet: it becomes a source. Sources and left-out folders below it are covered by it now.
            _sources.RemoveAll(s => Inside(s, path));
            _excluded.RemoveAll(e => Inside(e, path));
            _sources.Add(path);
        }
        else if (Same(source, path))
        {
            _sources.Remove(source);
            _excluded.RemoveAll(e => Inside(e, path));
        }
        else
        {
            string? above = _excluded.FirstOrDefault(e => Inside(path, e));
            if (above is null)
            {
                _excluded.RemoveAll(e => Inside(e, path));
                _excluded.Add(path);
            }
            else if (Same(above, path))
            {
                _excluded.Remove(above);
            }
            else
            {
                return $"\"{above}\" is left out, with everything in it. Tick that folder first.";
            }
        }
        Sort();
        Refresh();
        return null;
    }

    public void RemoveSource(string path)
    {
        _sources.RemoveAll(s => Same(s, path));
        _excluded.RemoveAll(e => !_sources.Any(s => Inside(e, s)));
        Refresh();
    }

    public void RemoveExclusion(string path)
    {
        _excluded.RemoveAll(e => Same(e, path));
        Refresh();
    }

    private void Sort()
    {
        _sources.Sort(StringComparer.OrdinalIgnoreCase);
        _excluded.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        foreach (var root in Roots)
            root.Refresh();
        Changed?.Invoke();
    }

    /// <summary>Opens the tree down to a folder's parent, so the folder shows (a folder that is gone shows nothing).</summary>
    public async Task RevealAsync(string path)
    {
        var node = Roots.FirstOrDefault(r => Inside(path, r.Path));
        while (node is not null && !Same(node.Path, path))
        {
            await node.LoadChildrenAsync();
            node.IsExpanded = true;
            node = node.Children.FirstOrDefault(c => !c.IsPlaceholder && Inside(path, c.Path));
        }
    }
}

/// <summary>One folder in the job editor's tree.</summary>
public sealed class SyncTreeNode : ObservableObject
{
    private readonly SyncTreeEditor _editor;
    private bool _expanded, _selected;
    private Task? _loading;

    public SyncTreeNode(SyncTreeEditor editor, string path, string name, int depth, ImageSource? icon, bool hasChildren, bool placeholder = false)
    {
        _editor = editor;
        Path = path;
        Name = name;
        Depth = depth;
        Icon = icon;
        IsPlaceholder = placeholder;
        // A stand-in child gives the node its expander box before its folders are read.
        if (hasChildren && !placeholder)
            Children.Add(new SyncTreeNode(editor, "", "", depth + 1, null, hasChildren: false, placeholder: true));
    }

    public string Path { get; }
    public string Name { get; }
    public int Depth { get; }
    public ImageSource? Icon { get; }
    public bool IsPlaceholder { get; }

    public ObservableCollection<SyncTreeNode> Children { get; } = [];

    /// <summary>The box: ticked, empty, or filled in part (null).</summary>
    public bool? IsChecked => IsPlaceholder ? false : _editor.StateOf(Path);

    /// <summary>Below a left-out folder: the box cannot be changed.</summary>
    public bool IsLocked => !IsPlaceholder && _editor.IsLocked(Path);

    /// <summary>Left out of a source: the name shows dim.</summary>
    public bool IsLeftOut => !IsPlaceholder && _editor.IsLeftOut(Path);

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (SetField(ref _expanded, value) && value)
                _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    /// <summary>The ticks changed: this node and the folders read below it show their new state.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsLeftOut));
        foreach (var child in Children)
        {
            if (!child.IsPlaceholder)
                child.Refresh();
        }
    }

    /// <summary>Reads the folders below this one, once.</summary>
    public Task LoadChildrenAsync() => _loading ??= LoadAsync();

    private static readonly EnumerationOptions AllFolders = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    private async Task LoadAsync()
    {
        if (IsPlaceholder || Children.Count == 0 || !Children[0].IsPlaceholder)
            return;
        string path = Path;
        var folders = await Task.Run(() =>
        {
            try
            {
                var list = Directory.EnumerateDirectories(path, "*", AllFolders).ToList();
                list.Sort(StringComparer.CurrentCultureIgnoreCase);
                return list.Select(folder => (Path: folder, HasChildren: FolderNodeViewModel.HasSubfolders(folder))).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        });
        Children.Clear();
        foreach (var (folder, hasChildren) in folders)
            Children.Add(new SyncTreeNode(_editor, folder, System.IO.Path.GetFileName(folder), Depth + 1, ShellInfo.GetFolderIconFast(folder), hasChildren));
    }

    public override string ToString() => Name;
}
