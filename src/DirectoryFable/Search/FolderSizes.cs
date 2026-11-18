using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DirectoryFable.Search;

/// <summary>Sizes of the folders directly in one folder (name -> bytes of all files below), from one read of its drive.</summary>
public sealed record FolderSizeResult(Dictionary<string, long> Sizes, long? FolderTotal);

/// <summary>
/// Folder sizes for the Size column of the file panes, from the drive's file table (NTFS, as
/// administrator; see <see cref="MftReader"/>). The first folder on a drive starts one read of the
/// whole drive (a few seconds); only its folders and their totals are kept (<see cref="SizeTree"/>),
/// so after that every folder on the drive has its sizes at once. The values stay fresh in the
/// background: a change seen in a shown folder reads the drive again a moment later (at most once a
/// minute), a visit after ten minutes does the same, F5 reads it again at once, and a new read by
/// SonnetSearch or the Space view is used too. Other places (network, FAT, no administrator rights,
/// inside archives) get no folder sizes.
/// </summary>
public static class FolderSizes
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(2);

    private static readonly Dictionary<string, bool> TableRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<SizeTree>> Trees = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DispatcherTimer> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Stale = new(StringComparer.OrdinalIgnoreCase);
    private static bool _hooked;

    private static bool _enabled = true;

    /// <summary>Settings: folder sizes in the Size column (default on). Off: nothing is read for them and the kept sizes are let go.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
                return;
            foreach (var timer in Pending.Values)
                timer.Stop();
            Pending.Clear();
            Trees.Clear();
            Stale.Clear();
        }
    }

    /// <summary>New sizes for a drive ("C:\"): the panes showing a folder on it take them.</summary>
    public static event Action<string>? Changed;

    /// <summary>"C:\" for a path on a local drive ("C:\..."), else null.</summary>
    private static string? DriveRoot(string folder) =>
        folder.Length >= 3 && folder[1] == ':' && folder[2] == '\\' && char.IsAsciiLetter(folder[0]) &&
        !Shell.ZipBrowser.IsInArchive(folder)
            ? char.ToUpperInvariant(folder[0]) + @":\"
            : null;

    /// <summary>The drive root when this folder's sizes come from its drive's file table (checked once per drive).</summary>
    public static async Task<string?> TableRootAsync(string folder)
    {
        if (DriveRoot(folder) is not { } root)
            return null;
        if (!TableRoots.TryGetValue(root, out bool can))
        {
            can = await Task.Run(() => IsLocal(root) && MftReader.CanRead(root));
            TableRoots[root] = can;
        }
        return can ? root : null;
    }

    /// <summary>Like <see cref="TableRootAsync"/>, without a check: null for a drive not checked yet.</summary>
    public static string? KnownTableRoot(string folder) =>
        DriveRoot(folder) is { } root && TableRoots.TryGetValue(root, out bool can) && can ? root : null;

    private static bool IsLocal(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sizes of the folders directly in <paramref name="folder"/>. Null: no folder sizes for this place.
    /// The first call for a drive waits for its read (<paramref name="reading"/> is called before it
    /// starts); later calls answer from the last read.
    /// </summary>
    public static async Task<FolderSizeResult?> ForFolderAsync(string folder, Action? reading)
    {
        if (!Enabled || await TableRootAsync(folder) is not { } root)
            return null;
        Hook();
        SizeTree tree;
        try
        {
            if (Trees.TryGetValue(root, out var known) && !known.IsFaulted && !known.IsCanceled)
            {
                tree = await known;
                if (Stale.Contains(root) || DateTime.Now - tree.ReadAt > MaxAge)
                    Schedule(root, TimeSpan.Zero);
            }
            else
            {
                reading?.Invoke();
                var index = await DriveIndexCache.Load(root, reread: false, walkFolder: false, owner: null);
                tree = await TreeFor(index);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            TableRoots[root] = false; // This drive cannot be read: no more tries this session.
            return null;
        }
        return Collect(tree, folder);
    }

    private static FolderSizeResult Collect(SizeTree tree, string folder)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (tree.Find(folder) is not int entry)
            return new FolderSizeResult(sizes, null);
        foreach (var (name, total) in tree.Subfolders(entry))
            sizes.TryAdd(name, total);
        return new FolderSizeResult(sizes, tree.Total(entry));
    }

    /// <summary>The size tree of a drive read: the one being built for it, or a new one.</summary>
    private static Task<SizeTree> TreeFor(DriveIndex index)
    {
        string root = index.Root;
        if (Trees.TryGetValue(root, out var known) && !known.IsFaulted &&
            (!known.IsCompleted || known.Result.ReadAt >= index.ReadAt))
            return known;
        var task = Task.Run(() => SizeTree.Build(index));
        Trees[root] = task;
        task.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully)
                Changed?.Invoke(root);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        return task;
    }

    /// <summary>Every new file table read of a drive with folder sizes (also by SonnetSearch or the Space view) renews them.</summary>
    private static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        DriveIndexCache.Loaded += index =>
        {
            if (Enabled && index.Method == "MFT" && Trees.ContainsKey(index.Root))
                _ = TreeFor(index);
        };
    }

    private static DateTime? ReadAt(string root) =>
        Trees.TryGetValue(root, out var task) && task.IsCompletedSuccessfully ? task.Result.ReadAt : null;

    /// <summary>A shown folder changed (folder watcher): read its drive again soon (at most once a minute).</summary>
    public static void NoteChange(string folder)
    {
        if (!Enabled || KnownTableRoot(folder) is not { } root || ReadAt(root) is null)
            return;
        if (DriveIndexCache.IsLoading(root))
            Stale.Add(root); // The running read may have missed it.
        else
            Schedule(root, ChangeDelay);
    }

    /// <summary>F5 in a pane: read the folder's drive again now (not when the last read is only seconds old).</summary>
    public static void Reread(string folder)
    {
        if (!Enabled || KnownTableRoot(folder) is not { } root || DriveIndexCache.IsLoading(root) ||
            ReadAt(root) is not { } readAt || DateTime.Now - readAt < TimeSpan.FromSeconds(3))
            return;
        if (Pending.Remove(root, out var timer))
            timer.Stop();
        StartRead(root);
    }

    private static void Schedule(string root, TimeSpan delay)
    {
        if (Pending.ContainsKey(root) || DriveIndexCache.IsLoading(root))
            return;
        if (ReadAt(root) is { } readAt && readAt + MinGap - DateTime.Now is var wait && wait > delay)
            delay = wait;
        var timer = new DispatcherTimer { Interval = delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Pending.Remove(root);
            // Hidden or minimized: no reading in the background; the next visit catches up.
            if (Application.Current?.MainWindow is not { IsVisible: true, WindowState: not WindowState.Minimized })
                Stale.Add(root);
            else
                StartRead(root);
        };
        Pending[root] = timer;
        timer.Start();
    }

    private static void StartRead(string root)
    {
        Stale.Remove(root);
        _ = ReadAgainAsync(root);
    }

    private static async Task ReadAgainAsync(string root)
    {
        try
        {
            // Not kept: the Loaded handler builds the new size tree from it.
            await DriveIndexCache.Load(root, reread: true, walkFolder: false, owner: null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            Stale.Add(root); // The old values stay; the next visit tries again.
        }
    }
}
