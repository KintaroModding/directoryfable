using System.IO;
using System.Windows.Threading;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// Folder tree auto-refresh: watches the subfolders of one open tree folder.
/// Only folder names are watched (file changes cost nothing). Reports on the UI thread, at
/// most every 300 ms: <c>childrenChanged</c> when a direct subfolder was added, deleted or
/// renamed; <c>grandchildrenChanged</c> with the subfolders whose own subfolders changed
/// (their expander box may have to appear or disappear).
/// </summary>
public sealed class TreeFolderWatcher : IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(300);

    private readonly string _folder;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _timer;
    private readonly Action _childrenChanged;
    private readonly Action<IReadOnlyCollection<string>> _grandchildrenChanged;
    private readonly HashSet<string> _changedChildren = new(StringComparer.OrdinalIgnoreCase);
    private bool _childrenDirty;

    public TreeFolderWatcher(string folder, Dispatcher dispatcher, Action childrenChanged,
        Action<IReadOnlyCollection<string>> grandchildrenChanged)
    {
        _folder = folder.TrimEnd('\\');
        _childrenChanged = childrenChanged;
        _grandchildrenChanged = grandchildrenChanged;
        _timer = new DispatcherTimer(Throttle, DispatcherPriority.Background, OnTimer, dispatcher);
        _timer.Stop();

        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                // Two levels are needed (children and grandchildren); deeper events are ignored.
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.DirectoryName,
            };
            _watcher.Created += (_, e) => Note(dispatcher, e.FullPath);
            _watcher.Deleted += (_, e) => Note(dispatcher, e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Note(dispatcher, e.OldFullPath);
                Note(dispatcher, e.FullPath);
            };
            // Buffer overflow etc.: read the children again.
            _watcher.Error += (_, _) => dispatcher.BeginInvoke(() => Mark(childrenDirty: true, child: null));
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some locations cannot be watched (certain network shares). F5 in the tree still works.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Runs on a watcher thread: decide the level, then hand over to the UI thread.</summary>
    private void Note(Dispatcher dispatcher, string path)
    {
        string? parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (parent is null)
            return;
        if (string.Equals(parent, _folder, StringComparison.OrdinalIgnoreCase))
        {
            dispatcher.BeginInvoke(() => Mark(childrenDirty: true, child: null));
            return;
        }
        if (string.Equals(Path.GetDirectoryName(parent), _folder, StringComparison.OrdinalIgnoreCase))
            dispatcher.BeginInvoke(() => Mark(childrenDirty: false, child: parent));
    }

    private void Mark(bool childrenDirty, string? child)
    {
        _childrenDirty |= childrenDirty;
        if (child is not null)
            _changedChildren.Add(child);
        // Throttle, not debounce: the first change starts the timer, later ones join it.
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_childrenDirty)
        {
            _childrenDirty = false;
            _changedChildren.Clear(); // Reading the children again also checks their expanders.
            _childrenChanged();
        }
        else if (_changedChildren.Count > 0)
        {
            var changed = _changedChildren.ToList();
            _changedChildren.Clear();
            _grandchildrenChanged(changed);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
    }
}
