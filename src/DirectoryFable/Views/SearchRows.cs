using System.Collections;
using System.Globalization;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Search;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>One row of the search results (made only when the list shows it).</summary>
public sealed class SearchRow
{
    internal SearchRow(SearchResult result, int position)
    {
        Position = position;
        Group = result.Groups?[position] ?? 0;
        long item = result.Items[position];
        var index = result.IndexOf(item);
        int i = SearchResult.EntryOf(item);
        Name = index.Name(i).ToString();
        Folder = index.FolderPath(i);
        IsDirectory = index.IsDirectory(i);
        SizeValue = result.SizeOf(item); // A folder: all files below it (folder sizes on), else -1.
        Modified = Date(index.Modified[i]);
        Accessed = Date(index.Accessed[i]);
        Created = Date(index.Created[i]);
    }

    internal int Position { get; }

    /// <summary>"Duplicates": the number of the row's group (1, 2, ...); 0 in a normal search.</summary>
    public int Group { get; }

    /// <summary>Every second group of duplicates has the darker background (the groups stand apart).</summary>
    public bool Band => Group % 2 == 0 && Group > 0;

    /// <summary>The row number in the shown order (1 = first row); "Duplicates": the group number.</summary>
    public string Number => (Group > 0 ? Group : Position + 1).ToString("N0", CultureInfo.CurrentCulture);

    public string Name { get; }

    /// <summary>"Containing Path", with a trailing backslash.</summary>
    public string Folder { get; }

    public string FullPath => Folder + Name;

    public bool IsDirectory { get; }

    public long SizeValue { get; }

    public string SizeText => SizeValue < 0 ? "" : DisplayFormat.Size(SizeValue);

    public string Modified { get; }
    public string Accessed { get; }
    public string Created { get; }

    public string TypeName => ShellInfo.GetTypeName(Name, IsDirectory);

    public ImageSource? Icon => ShellInfo.GetIcon(Name, IsDirectory);

    // The explorer's row style also looks at these (drop target, cut).
    public bool IsDropTarget => false;
    public bool IsCut => false;

    /// <summary>Full date and time with seconds: changes are easy to compare.</summary>
    private static string Date(long fileTime) =>
        fileTime <= 0 ? "" : DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
}

/// <summary>
/// The result rows for the list: a read-only list over the found entries that makes a row only
/// when the list asks for it (millions of results stay cheap). The same row object comes back
/// for the same position, so the selection stays.
/// </summary>
public sealed class SearchRowList(SearchResult result) : IList
{
    private readonly SearchRow?[] _rows = new SearchRow?[result.Items.Length];

    public SearchResult Result { get; } = result;

    public int Count => _rows.Length;

    public object? this[int index]
    {
        get => _rows[index] ??= new SearchRow(Result, index);
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public int IndexOf(object? value) => value is SearchRow row && row.Position < _rows.Length && _rows[row.Position] == row ? row.Position : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _rows.Length; i++)
            yield return this[i];
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _rows.Length; i++)
            array.SetValue(this[i], index + i);
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
