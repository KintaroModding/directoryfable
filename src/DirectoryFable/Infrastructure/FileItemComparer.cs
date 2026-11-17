using System.Collections;
using System.Runtime.InteropServices;
using DirectoryFable.Models;

namespace DirectoryFable.Infrastructure;

/// <summary>Columns the file list can be sorted by.</summary>
public enum SortColumn
{
    Name,
    Type,
    Size,
    Created,
    Modified,
    Extension,
}

/// <summary>
/// Sort order of the file list:
/// with grouping, groups by type A–Z; without grouping, folders first;
/// then the chosen column; ties by name. Names compare "naturally" like Explorer
/// ("file2" before "file10").
/// </summary>
public sealed class FileItemComparer(SortColumn column, bool descending, bool groupByType) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not FileItem a || y is not FileItem b)
            return 0;

        // With groups, the groups are simply A–Z by type name
        // ("File (1)" comes before "File Folder (5)"). Without groups, folders come first.
        if (groupByType)
        {
            int group = string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase);
            if (group != 0)
                return group;
        }
        else if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        int result = column switch
        {
            SortColumn.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            SortColumn.Size => Nullable.Compare(a.Size, b.Size),
            SortColumn.Created => a.DateCreated.CompareTo(b.DateCreated),
            SortColumn.Modified => a.DateModified.CompareTo(b.DateModified),
            SortColumn.Extension => string.Compare(a.Extension, b.Extension, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (result == 0)
            result = NaturalCompare(a.Name, b.Name);
        return descending ? -result : result;
    }

    /// <summary>Explorer's own name order (numbers inside names compare as numbers).</summary>
    public static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
