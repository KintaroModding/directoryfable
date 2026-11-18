namespace DirectoryFable.Infrastructure;

/// <summary>One folder of the recent folders list (session.json).</summary>
public sealed class RecentFolderState
{
    public string Path { get; set; } = "";

    /// <summary>When it was opened last (local time).</summary>
    public DateTime Opened { get; set; }
}

/// <summary>
/// The folders opened last in any pane or tab, the newest first: This PC shows them as "Recent folders". Kept in
/// the session; how many is a setting (<see cref="Max"/>, 0 = none are kept). This PC itself, folders inside
/// archives and a server's share list are not added. Used on the UI thread.
/// </summary>
public static class RecentFolders
{
    private static readonly List<RecentFolderState> Items = [];
    private static int _max = 10;

    /// <summary>The list changed (This PC shows it again).</summary>
    public static event Action? Changed;

    /// <summary>True while the session's tabs are opened at the start: those are not "opened" by the user.</summary>
    public static bool Paused { get; set; }

    /// <summary>How many are kept (0 to 100); a smaller number drops the oldest at once.</summary>
    public static int Max
    {
        get => _max;
        set
        {
            _max = Math.Clamp(value, 0, 100);
            if (Trim())
                Changed?.Invoke();
        }
    }

    public static IReadOnlyList<RecentFolderState> List => Items;

    /// <summary>A folder was opened: it goes to the top (once).</summary>
    public static void Note(string path)
    {
        if (Paused || _max == 0 || path.Length == 0 || PathHelper.IsThisPc(path) || PathHelper.IsServerPath(path) ||
            Shell.ZipBrowser.IsInArchive(path))
            return;
        Items.RemoveAll(i => PathHelper.IsSamePath(i.Path, path));
        Items.Insert(0, new RecentFolderState { Path = path, Opened = DateTime.Now });
        Trim();
        Changed?.Invoke();
    }

    public static void Remove(string path)
    {
        if (Items.RemoveAll(i => PathHelper.IsSamePath(i.Path, path)) > 0)
            Changed?.Invoke();
    }

    public static void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }

    /// <summary>The saved list (session start).</summary>
    public static void Restore(IEnumerable<RecentFolderState>? saved)
    {
        Items.Clear();
        foreach (var item in saved ?? [])
        {
            if (!string.IsNullOrWhiteSpace(item.Path) && !Items.Any(i => PathHelper.IsSamePath(i.Path, item.Path)))
                Items.Add(new RecentFolderState { Path = item.Path, Opened = item.Opened });
        }
        Trim();
        Changed?.Invoke();
    }

    public static List<RecentFolderState> Capture() => [.. Items.Select(i => new RecentFolderState { Path = i.Path, Opened = i.Opened })];

    private static bool Trim()
    {
        if (Items.Count <= _max)
            return false;
        Items.RemoveRange(_max, Items.Count - _max);
        return true;
    }
}
