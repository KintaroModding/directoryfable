using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// A place in the search's location list: a drive, a folder the user added (pinned, top level),
/// or a subfolder in their tree. Ticked = searched. Drives and pinned folders show the drive's
/// total size, free space and a "% free" bar; subfolders load when their row is opened.
/// </summary>
public sealed class LocationNode : ObservableObject
{
    private static readonly LocationNode Placeholder = new("", "...", isDrive: false, isTopLevel: false, owner: null);

    private readonly Action<LocationNode>? _checkChanged;
    private bool _isChecked;
    private bool _isExpanded;
    private bool _loaded;

    public LocationNode(string path, string label, bool isDrive, bool isTopLevel, Action<LocationNode>? owner, bool hasChildren = true)
    {
        Path = path;
        Label = label;
        IsDrive = isDrive;
        IsTopLevel = isTopLevel;
        _checkChanged = owner;
        if (hasChildren && path.Length > 0)
            Children.Add(Placeholder);
    }

    public string Path { get; }

    public string Label { get; }

    public bool IsDrive { get; }

    /// <summary>A drive or a pinned folder (the first level of the list).</summary>
    public bool IsTopLevel { get; }

    /// <summary>A pinned folder: can be taken off the list.</summary>
    public bool CanRemove => IsTopLevel && !IsDrive;

    public ObservableCollection<LocationNode> Children { get; } = [];

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
                _checkChanged?.Invoke(this);
        }
    }

    /// <summary>Set by the owner without reporting back (keeps several rows of one path in step).</summary>
    internal void SetCheckedQuietly(bool value)
    {
        if (_isChecked != value)
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value && !_loaded)
                _ = LoadChildrenAsync();
        }
    }

    public ImageSource? Icon => IsTopLevel ? ShellInfo.GetFolderIcon(Path) : ShellInfo.GetIcon(Path, isDirectory: true);

    // Drive numbers (top level only).
    public string TotalText { get; private set; } = "";
    public string FreeText { get; private set; } = "";
    public string PercentText { get; private set; } = "";
    public double FreeBarWidth { get; private set; }

    /// <summary>Reads the drive's size (for drives and pinned folders).</summary>
    public void ReadSpace()
    {
        if (!IsTopLevel)
            return;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(Path) ?? Path);
            if (!drive.IsReady)
                return;
            long total = drive.TotalSize, free = drive.AvailableFreeSpace;
            TotalText = DisplayFormat.Size(total);
            FreeText = DisplayFormat.Size(free);
            double share = total > 0 ? (double)free / total : 0;
            PercentText = $"{share * 100:0} %";
            FreeBarWidth = Math.Round(share * 120);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Network or removed drive: no numbers.
        }
    }

    /// <summary>Called for each row by the owner's lookup of ticked paths.</summary>
    internal Func<string, bool>? IsPathChecked { get; set; }

    /// <summary>The subfolders (natural order), each with an expander only when it has subfolders itself.</summary>
    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        string path = Path;
        var found = await Task.Run(() =>
        {
            var list = new List<(string Path, bool HasChildren)>();
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (string folder in Directory.EnumerateDirectories(path, "*", options))
                {
                    bool more;
                    try
                    {
                        more = Directory.EnumerateDirectories(folder, "*", options).Any();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        more = false;
                    }
                    list.Add((folder, more));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not readable: no subfolders shown.
            }
            list.Sort((a, b) => FileItemComparer.NaturalCompare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path)));
            return list;
        });
        Children.Clear();
        foreach (var (folder, more) in found)
        {
            var child = new LocationNode(folder, System.IO.Path.GetFileName(folder), isDrive: false, isTopLevel: false, _checkChanged, more)
            {
                IsPathChecked = IsPathChecked,
            };
            child.SetCheckedQuietly(IsPathChecked?.Invoke(folder) == true);
            Children.Add(child);
        }
    }

    /// <summary>All rows that are loaded (this one and its opened subfolders).</summary>
    public IEnumerable<LocationNode> LoadedRows()
    {
        yield return this;
        foreach (var child in Children)
        {
            if (child == Placeholder)
                continue;
            foreach (var row in child.LoadedRows())
                yield return row;
        }
    }
}
