using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DirectoryFable.Search;

/// <summary>
/// The drives and folder trees read so far, shared by SonnetSearch, the folder sizes of the file
/// panes and the Space view. A request while a target ("C:\" or a walked folder "D:\Games\") is read
/// waits for the same read. A finished read is kept while an owner holds it (SonnetSearch: until it
/// reads again; a Space view: while it is shown); a read without an owner (the folder sizes keep
/// their own small <see cref="SizeTree"/>) is handed out and then let go. Called on the UI thread;
/// the reading itself runs in the background.
/// </summary>
public static class DriveIndexCache
{
    private static readonly Dictionary<string, Task<DriveIndex>> Loads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<object>> Owners = new(StringComparer.OrdinalIgnoreCase);
    private static CancellationTokenSource _cancel = new();
    private static bool _releasePending;

    /// <summary>Reads in progress: target -> 0..1 (file table) or -objects (folder walk).</summary>
    public static ConcurrentDictionary<string, double> Progress { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A read finished (on the UI thread). Folder sizes and the Space view take the new values.</summary>
    public static event Action<DriveIndex>? Loaded;

    public static bool IsReading => !Progress.IsEmpty;

    /// <summary>True after <see cref="CancelReads"/> stopped the reads (until the next read starts).</summary>
    public static bool WasCancelled => _cancel.IsCancellationRequested;

    /// <summary>
    /// A drive (its file table when possible, else a folder walk) or a folder (<paramref name="walkFolder"/>:
    /// always a walk). <paramref name="reread"/> reads a target again that was read before; a read that
    /// is still running is never started twice. <paramref name="owner"/> keeps the result until it calls
    /// <see cref="Release"/>; null: the result is handed out (<see cref="Loaded"/>, the returned task) and
    /// then let go, unless another caller owns it.
    /// </summary>
    public static Task<DriveIndex> Load(string target, bool reread, bool walkFolder, object? owner)
    {
        if (owner is not null)
        {
            if (!Owners.TryGetValue(target, out var owners))
                Owners[target] = owners = [];
            owners.Add(owner);
        }
        if (Loads.TryGetValue(target, out var known) && !known.IsFaulted && !known.IsCanceled && (!reread || !known.IsCompleted))
            return known;
        if (_cancel.IsCancellationRequested)
            _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Progress[target] = 0;
        var task = Task.Run(() =>
        {
            try
            {
                if (!walkFolder && MftReader.CanRead(target))
                    return MftReader.Read(target, p => Progress[target] = p, token);
                return FolderWalker.Walk(target, n => Progress[target] = -n, token);
            }
            finally
            {
                Progress.TryRemove(target, out _);
            }
        }, token);
        Loads[target] = task;
        task.ContinueWith(done =>
        {
            // Only the newest read of a target counts (an older one may end later).
            if (done.IsCompletedSuccessfully && Loads.TryGetValue(target, out var current) && current == done)
            {
                Loaded?.Invoke(done.Result);
                if (!IsOwned(target))
                {
                    Loads.Remove(target);
                    ReleaseMemorySoon();
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        return task;
    }

    private static bool IsOwned(string target) => Owners.TryGetValue(target, out var owners) && owners.Count > 0;

    /// <summary>The owner no longer needs its reads (a Space view was closed): reads nobody owns are let go.</summary>
    public static void Release(object owner)
    {
        bool freed = false;
        foreach (var (target, owners) in Owners.ToList())
        {
            if (!owners.Remove(owner) || owners.Count > 0)
                continue;
            Owners.Remove(target);
            if (Loads.TryGetValue(target, out var task) && task.IsCompleted)
            {
                Loads.Remove(target);
                freed = true;
            }
        }
        if (freed)
            ReleaseMemorySoon();
    }

    /// <summary>
    /// A whole drive read (hundreds of MB for a big drive) was let go: give that memory back now instead
    /// of at some later collection. Once the UI is idle, so the readers have let go of it too.
    /// </summary>
    private static void ReleaseMemorySoon()
    {
        if (_releasePending || Application.Current is null)
            return;
        _releasePending = true;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _releasePending = false;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>The finished index of a target, or null (not read, still reading, failed, let go).</summary>
    public static DriveIndex? Done(string target) =>
        Loads.TryGetValue(target, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    /// <summary>True while the target is read (the first time or again).</summary>
    public static bool IsLoading(string target) => Loads.TryGetValue(target, out var task) && !task.IsCompleted;

    /// <summary>
    /// A finished index whose tree holds <paramref name="path"/>; with several (a drive and a walked
    /// folder on it), the one whose root is closest to the path. Null when none.
    /// </summary>
    public static DriveIndex? Covering(string path)
    {
        string wanted = Path.TrimEndingDirectorySeparator(path) + "\\";
        DriveIndex? best = null;
        foreach (var task in Loads.Values)
        {
            if (!task.IsCompletedSuccessfully)
                continue;
            var index = task.Result;
            if (wanted.StartsWith(index.Root, StringComparison.OrdinalIgnoreCase) &&
                (best is null || index.Root.Length > best.Root.Length))
                best = index;
        }
        return best;
    }

    /// <summary>Esc in SonnetSearch: stops all reads.</summary>
    public static void CancelReads() => _cancel.Cancel();
}
