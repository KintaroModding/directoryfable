using System.IO;

namespace DirectoryFable.Search;

/// <summary>
/// Only the folders of one drive read, each with the size of all files below it: what the folder
/// sizes of the file list need, at a fraction of the memory of the whole <see cref="DriveIndex"/>
/// (no files, no dates), so it can be kept for every drive. Built in one pass; read-only after that.
/// </summary>
public sealed class SizeTree
{
    private readonly int[] _firstChild; // -1 = no subfolders.
    private readonly int[] _next;       // Next folder in the same folder, -1 = last.
    private readonly int[] _nameStart;
    private readonly ushort[] _nameLength;
    private readonly char[] _names;
    private readonly long[] _total;
    private readonly int _rootFirst;

    private SizeTree(string root, DateTime readAt, int[] firstChild, int[] next, int[] nameStart, ushort[] nameLength,
        char[] names, long[] total, int rootFirst, long rootTotal)
    {
        Root = root;
        ReadAt = readAt;
        _firstChild = firstChild;
        _next = next;
        _nameStart = nameStart;
        _nameLength = nameLength;
        _names = names;
        _total = total;
        _rootFirst = rootFirst;
        RootTotal = rootTotal;
    }

    /// <summary>"C:\".</summary>
    public string Root { get; }

    /// <summary>When the drive was read.</summary>
    public DateTime ReadAt { get; }

    public long RootTotal { get; }

    public int FolderCount => _total.Length;

    private ReadOnlySpan<char> Name(int folder) => _names.AsSpan(_nameStart[folder], _nameLength[folder]);

    /// <summary>The folder's number: -1 = the root, null = not in this read (another drive, or newer).</summary>
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
                if (Name(c).Equals(part, StringComparison.OrdinalIgnoreCase))
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
            yield return (Name(c).ToString(), _total[c]);
    }

    public static SizeTree Build(DriveIndex index)
    {
        int n = index.Count;
        // Folder numbers 0 .. f - 1 for the folder entries; files get -1.
        var map = new int[n];
        int f = 0;
        long nameChars = 0;
        for (int i = 0; i < n; i++)
        {
            if (index.IsDirectory(i))
            {
                map[i] = f++;
                nameChars += index.NameLength[i];
            }
            else
            {
                map[i] = -1;
            }
        }

        var parent = new int[f];
        var nameStart = new int[f];
        var nameLength = new ushort[f];
        var names = new char[Math.Max(1, nameChars)];
        var total = new long[f]; // First the files directly in each folder, then everything below.
        long rootTotal = 0;
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            int p = index.Parent[i];
            int parentFolder = p >= 0 && p < n ? map[p] : -1;
            int k = map[i];
            if (k >= 0)
            {
                parent[k] = parentFolder;
                var name = index.Name(i);
                name.CopyTo(names.AsSpan(used));
                nameStart[k] = used;
                nameLength[k] = (ushort)name.Length;
                used += name.Length;
            }
            else
            {
                long size = Math.Max(0, index.Size[i]);
                if (parentFolder >= 0)
                    total[parentFolder] += size;
                else
                    rootTotal += size;
            }
        }

        // Subfolder lists (backwards, so they keep the index order).
        var firstChild = new int[f];
        var next = new int[f];
        Array.Fill(firstChild, -1);
        int rootFirst = -1;
        for (int k = f - 1; k >= 0; k--)
        {
            if (parent[k] < 0)
            {
                next[k] = rootFirst;
                rootFirst = k;
            }
            else
            {
                next[k] = firstChild[parent[k]];
                firstChild[parent[k]] = k;
            }
        }

        // Breadth-first order (a folder before its subfolders), then backwards: each adds itself to its parent.
        var order = new int[f];
        int head = 0, tail = 0;
        for (int c = rootFirst; c >= 0 && tail < f; c = next[c])
            order[tail++] = c;
        while (head < tail)
        {
            int folder = order[head++];
            for (int c = firstChild[folder]; c >= 0 && tail < f; c = next[c])
                order[tail++] = c;
        }
        for (int j = tail - 1; j >= 0; j--)
        {
            int k = order[j];
            if (parent[k] >= 0)
                total[parent[k]] += total[k];
            else
                rootTotal += total[k];
        }
        return new SizeTree(index.Root, index.ReadAt, firstChild, next, nameStart, nameLength, names, total, rootFirst, rootTotal);
    }
}
