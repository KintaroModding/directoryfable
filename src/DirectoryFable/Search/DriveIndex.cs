using System.IO;

namespace DirectoryFable.Search;

/// <summary>
/// All files and folders of one drive (or one folder tree) for the search, in compact arrays
/// (one slot per entry; names in one shared char pool). Built by <see cref="MftReader"/>
/// (NTFS + administrator: the drive's Master File Table, a few seconds for a whole drive) or by
/// <see cref="FolderWalker"/> (everything else: a normal folder walk). Dates are UTC FILETIME
/// values (0 = unknown). Read-only once built, so queries can run in parallel.
/// </summary>
public sealed class DriveIndex
{
    internal int[] Parent = [];        // Entry index of the containing folder; -1 = directly in Root.
    internal int[] NameStart = [];
    internal ushort[] NameLength = [];
    internal long[] Size = [];
    internal long[] Created = [];
    internal long[] Modified = [];
    internal long[] Accessed = [];
    internal uint[] Attributes = [];
    internal char[] Names = new char[1 << 16];
    internal int NamesUsed;
    internal int[] Record = [];        // MFT record number per entry (file table reads only).

    /// <summary>File table reads: the change journal's id and its position when the read began (see UsnJournal).</summary>
    public bool HasJournal { get; set; }
    public ulong JournalId { get; set; }
    public long StartUsn { get; set; }
    private int _count;

    private readonly Dictionary<int, string> _folderPaths = [];
    private readonly Lock _pathLock = new();

    public DriveIndex(string root, string method)
    {
        Root = root.EndsWith('\\') ? root : root + "\\";
        Method = method;
    }

    /// <summary>"C:\" for a drive, or the folder a folder walk started in (with a trailing backslash).</summary>
    public string Root { get; }

    /// <summary>"MFT" or "folder walk" (shown in the status line).</summary>
    public string Method { get; }

    public TimeSpan ReadTime { get; set; }

    /// <summary>When the reading ended (the results show what was on the drive then).</summary>
    public DateTime ReadAt { get; set; } = DateTime.Now;

    /// <summary>Number of entries (files and folders); entry numbers are 0 .. Count - 1.</summary>
    public int Count => _count;

    /// <summary>Number of real entries (files and folders).</summary>
    public int EntryCount { get; internal set; }

    private Task<FolderTotals>? _tree;

    /// <summary>The folder tree with totals (folder sizes); built in the background on first use, then kept.</summary>
    public Task<FolderTotals> TotalsAsync()
    {
        lock (_pathLock)
            return _tree ??= Task.Run(() => FolderTotals.Build(this));
    }

    public bool IsEntry(int i) => i >= 0 && i < _count;

    public bool IsDirectory(int i) => (Attributes[i] & 0x10) != 0;

    public ReadOnlySpan<char> Name(int i) => Names.AsSpan(NameStart[i], NameLength[i]);

    /// <summary>The folder that contains entry <paramref name="i"/>, with a trailing backslash ("D:\Work\").</summary>
    public string FolderPath(int i) => DirectoryPath(Parent[i]);

    public string FullPath(int i) => FolderPath(i) + Name(i).ToString();

    /// <summary>Path of a folder entry with a trailing backslash (-1 = <see cref="Root"/>); cached.</summary>
    private string DirectoryPath(int folder)
    {
        if (folder < 0)
            return Root;
        lock (_pathLock)
        {
            if (_folderPaths.TryGetValue(folder, out string? known))
                return known;
        }
        // Up to the first known folder, then down again.
        var chain = new List<int>();
        string start = Root;
        for (int f = folder; f >= 0 && chain.Count < 1024; f = Parent[f])
        {
            lock (_pathLock)
            {
                if (_folderPaths.TryGetValue(f, out string? cached))
                {
                    start = cached;
                    break;
                }
            }
            chain.Add(f);
        }
        string path = start;
        for (int k = chain.Count - 1; k >= 0; k--)
        {
            path = path + Name(chain[k]).ToString() + "\\";
            lock (_pathLock)
                _folderPaths[chain[k]] = path;
        }
        return path;
    }

    /// <summary>
    /// The entry of a folder inside this index (for "search in this folder"), or -1 when the path
    /// is the root itself, or null when it is not found.
    /// </summary>
    public int? FindFolder(string path)
    {
        string wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + "\\";
        if (string.Equals(wanted, Root, StringComparison.OrdinalIgnoreCase))
            return -1;
        if (!wanted.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            return null;
        string name = Path.GetFileName(wanted.TrimEnd('\\'));
        for (int i = 0; i < _count; i++)
        {
            if (IsEntry(i) && IsDirectory(i) && Name(i).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(DirectoryPath(i), wanted, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    // ---------- Building ----------

    /// <summary>Room for <paramref name="slots"/> entries (MFT: one slot per record).</summary>
    internal void Allocate(int slots)
    {
        Parent = new int[slots];
        NameStart = new int[slots];
        NameLength = new ushort[slots];
        Size = new long[slots];
        Created = new long[slots];
        Modified = new long[slots];
        Accessed = new long[slots];
        Attributes = new uint[slots];
        _count = slots;
    }

    internal int AddName(ReadOnlySpan<char> name)
    {
        if (NamesUsed + name.Length > Names.Length)
            Array.Resize(ref Names, Math.Max(Names.Length * 2, NamesUsed + name.Length));
        name.CopyTo(Names.AsSpan(NamesUsed));
        int start = NamesUsed;
        NamesUsed += name.Length;
        return start;
    }

    /// <summary>Folder walk: appends one entry and returns its index.</summary>
    internal int Add(int parent, ReadOnlySpan<char> name, long size, long created, long modified, long accessed, uint attributes)
    {
        if (_count == Parent.Length)
        {
            int grow = Math.Max(1024, _count * 2);
            Array.Resize(ref Parent, grow);
            Array.Resize(ref NameStart, grow);
            Array.Resize(ref NameLength, grow);
            Array.Resize(ref Size, grow);
            Array.Resize(ref Created, grow);
            Array.Resize(ref Modified, grow);
            Array.Resize(ref Accessed, grow);
            Array.Resize(ref Attributes, grow);
        }
        int i = _count++;
        Parent[i] = parent;
        NameStart[i] = AddName(name);
        NameLength[i] = (ushort)Math.Min(name.Length, ushort.MaxValue);
        Size[i] = size;
        Created[i] = created;
        Modified[i] = modified;
        Accessed[i] = accessed;
        Attributes[i] = attributes;
        EntryCount = _count;
        return i;
    }
}
