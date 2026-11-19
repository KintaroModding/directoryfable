using System.IO;

namespace DirectoryFable.Search;

/// <summary>
/// The folders of a <see cref="DriveIndex"/> as a tree with totals: for each folder the size of
/// all files below it and how many files and folders it holds (all levels). Built once per index
/// in a single pass (children lists, then the totals from the deepest level up); read-only after
/// that. Used for the folder sizes in the file list and for the Space view.
/// </summary>
public sealed class FolderTotals
{
    private readonly int[] _firstChild; // -1 = no children.
    private readonly int[] _next;       // Next entry in the same folder, -1 = last.
    private readonly long[] _total;     // Folder: all files below; file: its own size.
    private readonly int[] _files;      // Folder: files below (all levels).
    private readonly int[] _folders;    // Folder: folders below (all levels).
    private readonly int _rootFirst;

    private FolderTotals(DriveIndex index, int[] firstChild, int[] next, long[] total, int[] files, int[] folders, int rootFirst,
        long rootTotal, int rootFiles, int rootFolders)
    {
        Index = index;
        _firstChild = firstChild;
        _next = next;
        _total = total;
        _files = files;
        _folders = folders;
        _rootFirst = rootFirst;
        RootTotal = rootTotal;
        RootFiles = rootFiles;
        RootFolders = rootFolders;
    }

    public DriveIndex Index { get; }

    /// <summary>All files of the index (its root folder and below).</summary>
    public long RootTotal { get; }
    public int RootFiles { get; }
    public int RootFolders { get; }

    /// <summary>Total bytes below a folder, or a file's size. -1 = the root.</summary>
    public long Total(int entry) => entry < 0 ? RootTotal : (uint)entry < (uint)_total.Length ? _total[entry] : 0;

    /// <summary>Files below a folder (all levels); 0 for a file. -1 = the root.</summary>
    public int Files(int entry) => entry < 0 ? RootFiles : _files[entry];

    /// <summary>Folders below a folder (all levels); 0 for a file. -1 = the root.</summary>
    public int Folders(int entry) => entry < 0 ? RootFolders : _folders[entry];

    /// <summary>The entries directly in a folder (-1 = the root).</summary>
    public IEnumerable<int> Children(int folder)
    {
        for (int c = folder < 0 ? _rootFirst : _firstChild[folder]; c >= 0; c = _next[c])
            yield return c;
    }

    /// <summary>
    /// The entry of a folder: -1 for the root itself, null when the path is not in this index
    /// (another drive, or a folder made after the read).
    /// </summary>
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
        string root = Index.Root;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return -1;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        int folder = -1;
        foreach (string part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int c = folder < 0 ? _rootFirst : _firstChild[folder]; c >= 0; c = _next[c])
            {
                if (Index.IsDirectory(c) && Index.Name(c).Equals(part, StringComparison.OrdinalIgnoreCase))
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

    public static FolderTotals Build(DriveIndex index)
    {
        int n = index.Count;
        var firstChild = new int[n];
        var next = new int[n];
        Array.Fill(firstChild, -1);
        int rootFirst = -1;
        // Backwards, so each folder's list keeps the index order.
        for (int i = n - 1; i >= 0; i--)
        {
            int parent = index.Parent[i];
            if (parent < 0 || parent >= n)
            {
                next[i] = rootFirst;
                rootFirst = i;
            }
            else
            {
                next[i] = firstChild[parent];
                firstChild[parent] = i;
            }
        }

        // Breadth-first order from the root: every folder comes before its contents.
        var order = new int[n];
        int head = 0, tail = 0;
        for (int c = rootFirst; c >= 0 && tail < n; c = next[c])
            order[tail++] = c;
        while (head < tail)
        {
            int folder = order[head++];
            for (int c = firstChild[folder]; c >= 0 && tail < n; c = next[c])
                order[tail++] = c;
        }

        // Then backwards: each entry adds itself to its folder.
        var total = new long[n];
        var files = new int[n];
        var folders = new int[n];
        long rootTotal = 0;
        int rootFiles = 0, rootFolders = 0;
        for (int k = tail - 1; k >= 0; k--)
        {
            int i = order[k];
            bool isFolder = index.IsDirectory(i);
            if (!isFolder)
                total[i] = Math.Max(0, index.Size[i]);
            int parent = index.Parent[i];
            if (parent >= 0 && parent < n)
            {
                total[parent] += total[i];
                files[parent] += isFolder ? files[i] : 1;
                folders[parent] += isFolder ? folders[i] + 1 : 0;
            }
            else
            {
                rootTotal += total[i];
                rootFiles += isFolder ? files[i] : 1;
                rootFolders += isFolder ? folders[i] + 1 : 0;
            }
        }
        return new FolderTotals(index, firstChild, next, total, files, folders, rootFirst, rootTotal, rootFiles, rootFolders);
    }
}
