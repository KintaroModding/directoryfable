using System.IO;

namespace DirectoryFable.Search;

/// <summary>
/// Only the folders of one drive, each with the size of the files directly in it and of everything below it:
/// what the folder sizes of the file list need, at a fraction of the memory of the whole <see cref="DriveIndex"/>
/// (no files, no dates), so it is kept for every drive. Built from a file table read; after that it follows the
/// drive's change journal (<see cref="UsnJournal"/>): folders are added, removed, moved and renamed by their MFT
/// record number, and a folder whose files changed gets its own size again (<see cref="SetDirect"/>), which
/// changes every folder above it. Changed on the UI thread only.
/// </summary>
public sealed class SizeTree
{
    private const long RootRecord = 5; // The drive's root folder in the MFT.

    private readonly List<int> _parent = [];      // -1 = directly in the root.
    private readonly List<int> _firstChild = [];  // -1 = no subfolders.
    private readonly List<int> _next = [];        // Next folder in the same folder, -1 = last.
    private readonly List<string?> _name = [];    // null = a free slot.
    private readonly List<long> _direct = [];     // Files directly in the folder.
    private readonly List<long> _total = [];      // Everything below.
    private readonly List<long> _record = [];
    private readonly Dictionary<long, int> _byRecord = [];
    private readonly Stack<int> _free = new();
    private int _rootFirst = -1;
    private long _rootDirect;

    private SizeTree(string root, DateTime readAt)
    {
        Root = root;
        ReadAt = readAt;
    }

    /// <summary>"C:\".</summary>
    public string Root { get; }

    /// <summary>When the drive was read (the journal brings the changes since then).</summary>
    public DateTime ReadAt { get; }

    public bool HasJournal { get; private init; }
    public ulong JournalId { get; private init; }

    /// <summary>The journal position to read from next.</summary>
    public long NextUsn { get; set; }

    public long RootTotal { get; private set; }

    public int FolderCount => _byRecord.Count;

    /// <summary>The folder of an MFT record: -1 = the root, null = not a known folder.</summary>
    public int? NodeOf(long record) => record == RootRecord ? -1 : _byRecord.TryGetValue(record, out int node) ? node : null;

    public bool IsAlive(int node) => node < 0 || (node < _name.Count && _name[node] is not null);

    /// <summary>The folder's number: -1 = the root, null = not in this tree (another drive, or not known).</summary>
    public int? Find(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
            return -1;
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            return null;
        int folder = -1;
        foreach (string part in full[Root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int c = folder < 0 ? _rootFirst : _firstChild[folder]; c >= 0; c = _next[c])
            {
                if (string.Equals(_name[c], part, StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }
            if (found < 0)
                return null;
            folder = found;
        }
        return folder;
    }

    /// <summary>Total bytes below a folder (-1 = the root).</summary>
    public long Total(int folder) => folder < 0 ? RootTotal : _total[folder];

    /// <summary>The subfolders of a folder with their totals (-1 = the root).</summary>
    public IEnumerable<(string Name, long Total)> Subfolders(int folder)
    {
        for (int c = folder < 0 ? _rootFirst : _firstChild[folder]; c >= 0; c = _next[c])
            yield return (_name[c]!, _total[c]);
    }

    /// <summary>"D:\Games\Steam" for a folder (-1 = the root).</summary>
    public string PathOf(int folder)
    {
        if (folder < 0)
            return Root;
        var parts = new List<string>();
        for (int f = folder; f >= 0 && parts.Count < 4096; f = _parent[f])
            parts.Add(_name[f]!);
        parts.Reverse();
        return Root + string.Join('\\', parts);
    }

    // ---------- Changes from the journal ----------

    /// <summary>
    /// A folder change from the journal: created, deleted, or renamed / moved. Returns true when the tree changed.
    /// Changes the read already holds (the journal is read from the read's start) change nothing.
    /// </summary>
    internal bool Apply(UsnJournal.Change change)
    {
        if (!change.IsFolder || change.Record == RootRecord)
            return false;
        int? node = NodeOf(change.Record);
        if ((change.Reason & UsnJournal.FileDelete) != 0)
        {
            if (node is not int gone)
                return false;
            Remove(gone);
            return true;
        }
        if ((change.Reason & (UsnJournal.FileCreate | UsnJournal.RenameNewName)) == 0 || change.Name.Length == 0)
            return false;
        if (NodeOf(change.ParentRecord) is not int parent)
        {
            // Moved into a folder this tree does not know (or created there): gone from here.
            if (node is int known)
            {
                Remove(known);
                return true;
            }
            return false;
        }
        if (node is int existing)
        {
            if (_parent[existing] == parent && _name[existing] == change.Name)
                return false;
            Move(existing, parent, change.Name);
            return true;
        }
        Add(change.Record, parent, change.Name, 0);
        return true;
    }

    /// <summary>The files directly in a folder have a new size: the folder and all folders above it change.</summary>
    public bool SetDirect(int folder, long size)
    {
        long old = folder < 0 ? _rootDirect : _direct[folder];
        if (old == size || !IsAlive(folder))
            return false;
        if (folder < 0)
            _rootDirect = size;
        else
            _direct[folder] = size;
        AddUp(folder, size - old);
        return true;
    }

    /// <summary>Adds <paramref name="delta"/> to a folder and every folder above it.</summary>
    private void AddUp(int folder, long delta)
    {
        for (int f = folder; f >= 0; f = _parent[f])
            _total[f] += delta;
        RootTotal += delta;
    }

    private int Add(long record, int parent, string name, long direct)
    {
        int node;
        if (_free.Count > 0)
        {
            node = _free.Pop();
            _parent[node] = parent;
            _firstChild[node] = -1;
            _name[node] = name;
            _direct[node] = direct;
            _total[node] = 0;
            _record[node] = record;
        }
        else
        {
            node = _name.Count;
            _parent.Add(parent);
            _firstChild.Add(-1);
            _next.Add(-1);
            _name.Add(name);
            _direct.Add(direct);
            _total.Add(0);
            _record.Add(record);
        }
        _byRecord[record] = node;
        Link(node, parent);
        return node;
    }

    private void Link(int node, int parent)
    {
        _parent[node] = parent;
        if (parent < 0)
        {
            _next[node] = _rootFirst;
            _rootFirst = node;
        }
        else
        {
            _next[node] = _firstChild[parent];
            _firstChild[parent] = node;
        }
    }

    private void Unlink(int node)
    {
        int parent = _parent[node];
        int first = parent < 0 ? _rootFirst : _firstChild[parent];
        if (first == node)
        {
            if (parent < 0)
                _rootFirst = _next[node];
            else
                _firstChild[parent] = _next[node];
        }
        else
        {
            for (int c = first; c >= 0; c = _next[c])
            {
                if (_next[c] == node)
                {
                    _next[c] = _next[node];
                    break;
                }
            }
        }
        _next[node] = -1;
    }

    private void Move(int node, int parent, string name)
    {
        long total = _total[node];
        AddUp(_parent[node], -total);
        Unlink(node);
        _name[node] = name;
        Link(node, parent);
        AddUp(parent, total);
    }

    /// <summary>A folder and everything in it leave the tree.</summary>
    private void Remove(int node)
    {
        AddUp(_parent[node], -_total[node]);
        Unlink(node);
        var pending = new Stack<int>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            int f = pending.Pop();
            for (int c = _firstChild[f]; c >= 0; c = _next[c])
                pending.Push(c);
            _byRecord.Remove(_record[f]);
            _name[f] = null;
            _firstChild[f] = -1;
            _free.Push(f);
        }
    }

    // ---------- Building ----------

    public static SizeTree Build(DriveIndex index)
    {
        var tree = new SizeTree(index.Root, index.ReadAt)
        {
            HasJournal = index.HasJournal && index.Record.Length == index.Count,
            JournalId = index.JournalId,
            NextUsn = index.StartUsn,
        };
        int n = index.Count;
        var map = new int[n]; // Entry -> folder number; files: -1.
        int f = 0;
        for (int i = 0; i < n; i++)
            map[i] = index.IsDirectory(i) ? f++ : -1;

        var parent = new int[f];
        var direct = new long[f];
        tree._name.Capacity = tree._parent.Capacity = tree._direct.Capacity = tree._total.Capacity = tree._record.Capacity = f;
        tree._firstChild.Capacity = tree._next.Capacity = f;
        for (int i = 0; i < n; i++)
        {
            int p = index.Parent[i];
            int parentFolder = p >= 0 && p < n ? map[p] : -1;
            int k = map[i];
            if (k >= 0)
            {
                parent[k] = parentFolder;
                tree._name.Add(index.Name(i).ToString());
                tree._record.Add(tree.HasJournal ? index.Record[i] : -1 - k);
            }
            else
            {
                long size = Math.Max(0, index.Size[i]);
                if (parentFolder >= 0)
                    direct[parentFolder] += size;
                else
                    tree._rootDirect += size;
            }
        }
        for (int k = 0; k < f; k++)
        {
            tree._parent.Add(parent[k]);
            tree._firstChild.Add(-1);
            tree._next.Add(-1);
            tree._direct.Add(direct[k]);
            tree._total.Add(direct[k]);
            tree._byRecord[tree._record[k]] = k;
        }
        // Subfolder lists (backwards, so they keep the index order).
        for (int k = f - 1; k >= 0; k--)
            tree.Link(k, parent[k]);

        // Breadth-first order (a folder before its subfolders), then backwards: each adds itself to its parent.
        var order = new int[f];
        int head = 0, tail = 0;
        for (int c = tree._rootFirst; c >= 0 && tail < f; c = tree._next[c])
            order[tail++] = c;
        while (head < tail)
        {
            int folder = order[head++];
            for (int c = tree._firstChild[folder]; c >= 0 && tail < f; c = tree._next[c])
                order[tail++] = c;
        }
        long rootTotal = tree._rootDirect;
        for (int j = tail - 1; j >= 0; j--)
        {
            int k = order[j];
            if (parent[k] >= 0)
                tree._total[parent[k]] += tree._total[k];
            else
                rootTotal += tree._total[k];
        }
        tree.RootTotal = rootTotal;
        return tree;
    }
}
