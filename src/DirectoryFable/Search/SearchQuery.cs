using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace DirectoryFable.Search;

public enum SearchType { All, Files, Folders, Images, Text, Extensions }

public enum DateColumn { Any, Modified, Created, Accessed }

public enum ResultSort { None, Name, Path, Size, Modified, Accessed, Created, Type }

/// <summary>
/// What to look for. Text: words that must all appear in the name (any order, case ignored
/// unless <see cref="MatchCase"/>); "quotes" keep spaces; * and ? are wildcards for the whole
/// name ("*.sav", "save??.dat"). <see cref="UseRegex"/>: the text is one regular expression.
/// Filters: type, size range, and a date (one column or any of the three) from a point in time on.
/// </summary>
public sealed class SearchQuery
{
    public string Text { get; init; } = "";
    public bool MatchCase { get; init; }
    public bool UseRegex { get; init; }
    public SearchType Type { get; init; }
    public HashSet<string>? Extensions { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateColumn DateColumn { get; init; }

    /// <summary>Only entries with a date (of <see cref="DateColumn"/>) at or after this UTC FILETIME.</summary>
    public long? Since { get; init; }

    public static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".avif", ".jxr", ".wdp", ".svg", ".psd", ".raw", ".cr2", ".nef", ".arw", ".dng" };

    public static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".ini", ".cfg", ".conf", ".config", ".json", ".jsonc", ".xml", ".md", ".yml", ".yaml", ".toml", ".csv", ".tsv",
          ".cs", ".xaml", ".py", ".js", ".ts", ".css", ".html", ".htm", ".sql", ".ahk", ".ps1", ".bat", ".cmd", ".lua", ".c", ".cpp", ".h" };

    private delegate bool NameTest(ReadOnlySpan<char> name);

    private NameTest[]? _tests;

    /// <summary>Checks the text (a broken regular expression throws ArgumentException here, once).</summary>
    public void Compile()
    {
        var tests = new List<NameTest>();
        var options = RegexOptions.CultureInvariant | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        if (UseRegex)
        {
            if (Text.Trim().Length > 0)
            {
                var regex = new Regex(Text, options, TimeSpan.FromMilliseconds(200));
                tests.Add(name => regex.IsMatch(name));
            }
        }
        else
        {
            var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (string token in Tokens(Text))
            {
                if (token.IndexOfAny(['*', '?']) >= 0)
                {
                    var regex = new Regex("^" + Regex.Escape(token).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", options);
                    tests.Add(name => regex.IsMatch(name));
                }
                else
                {
                    string word = token;
                    tests.Add(name => name.Contains(word, comparison));
                }
            }
        }
        _tests = [.. tests];
    }

    /// <summary>Words separated by spaces; "quoted text" is one word.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                yield break;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                    end = text.Length;
                if (end > i + 1)
                    yield return text[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;
                yield return text[start..i];
            }
        }
    }

    public bool Matches(DriveIndex index, int i)
    {
        bool folder = index.IsDirectory(i);
        switch (Type)
        {
            case SearchType.Files when folder:
            case SearchType.Folders when !folder:
                return false;
            case SearchType.Images or SearchType.Text or SearchType.Extensions:
            {
                if (folder)
                    return false;
                var set = Type == SearchType.Images ? ImageTypes : Type == SearchType.Text ? TextTypes : Extensions;
                if (set is null || !set.Contains(Path.GetExtension(index.Name(i)).ToString()))
                    return false;
                break;
            }
        }
        if (MinSize is long min && (folder || index.Size[i] < min))
            return false;
        if (MaxSize is long max && (folder || index.Size[i] > max))
            return false;
        if (Since is long since)
        {
            bool recent = DateColumn switch
            {
                DateColumn.Modified => index.Modified[i] >= since,
                DateColumn.Created => index.Created[i] >= since,
                DateColumn.Accessed => index.Accessed[i] >= since,
                _ => index.Modified[i] >= since || index.Created[i] >= since || index.Accessed[i] >= since,
            };
            if (!recent)
                return false;
        }
        return MatchesName(index.Name(i));
    }

    /// <summary>Only the text part (after <see cref="Compile"/>): also the quick filter of the file list.</summary>
    public bool MatchesName(ReadOnlySpan<char> name)
    {
        foreach (var test in _tests!)
        {
            if (!test(name))
                return false;
        }
        return true;
    }
}

/// <summary>The found entries: (index number &lt;&lt; 32 | entry) per item, in display order.</summary>
public sealed class SearchResult(IReadOnlyList<DriveIndex> indexes, long[] items, long searched)
{
    public static readonly SearchResult Empty = new([], [], 0);

    public IReadOnlyList<DriveIndex> Indexes { get; } = indexes;
    public long[] Items { get; } = items;

    /// <summary>How many entries were looked at.</summary>
    public long Searched { get; } = searched;

    public DriveIndex IndexOf(long item) => Indexes[(int)(item >> 32)];

    public static int EntryOf(long item) => (int)(item & 0xFFFF_FFFF);
}

/// <summary>Runs a query over loaded indexes (in parallel) and sorts the result.</summary>
public static class SearchRunner
{
    /// <param name="scopeFolder">Per index: only entries below this folder entry (null = all; -1 = root).</param>
    public static SearchResult Run(IReadOnlyList<DriveIndex> indexes, IReadOnlyList<int?> scopeFolder, SearchQuery query,
        ResultSort sort, bool descending, CancellationToken token)
    {
        query.Compile();
        var found = new ConcurrentBag<long[]>();
        long searched = 0;
        for (int d = 0; d < indexes.Count; d++)
        {
            var index = indexes[d];
            int? scope = scopeFolder[d];
            sbyte[]? memo = scope is int s && s >= 0 ? new sbyte[index.Count] : null;
            long high = (long)d << 32;
            var options = new ParallelOptions { CancellationToken = token };
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, index.Count, 1 << 15), options, range =>
            {
                var local = new List<long>();
                long looked = 0;
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (scope is int s2 && (s2 < 0 ? false : !InScope(index, i, s2, memo!)))
                        continue;
                    looked++;
                    if (query.Matches(index, i))
                        local.Add(high | (uint)i);
                }
                Interlocked.Add(ref searched, looked);
                if (local.Count > 0)
                    found.Add([.. local]);
            });
        }
        token.ThrowIfCancellationRequested();
        var items = found.SelectMany(x => x).ToArray();
        Sort(items, indexes, sort, descending, token);
        return new SearchResult(indexes, items, searched);
    }

    /// <summary>Is entry <paramref name="i"/> inside folder <paramref name="scope"/> (any depth)? Remembered per folder.</summary>
    private static bool InScope(DriveIndex index, int i, int scope, sbyte[] memo)
    {
        int f = index.Parent[i];
        int steps = 0;
        bool result;
        while (true)
        {
            if (f == scope)
            {
                result = true;
                break;
            }
            if (f < 0 || steps++ > 4096)
            {
                result = false;
                break;
            }
            if (memo[f] != 0)
            {
                result = memo[f] == 1;
                break;
            }
            f = index.Parent[f];
        }
        // Remember for the folders on the way (writes of the same value from other threads are harmless).
        for (int g = index.Parent[i], k = 0; g >= 0 && g != scope && memo[g] == 0 && k < 4096; g = index.Parent[g], k++)
            memo[g] = result ? (sbyte)1 : (sbyte)2;
        return result;
    }

    public static void Sort(long[] items, IReadOnlyList<DriveIndex> indexes, ResultSort sort, bool descending, CancellationToken token)
    {
        if (sort == ResultSort.None || items.Length < 2)
            return;
        DriveIndex Of(long item) => indexes[(int)(item >> 32)];
        static int Entry(long item) => (int)(item & 0xFFFF_FFFF);

        if (sort is ResultSort.Size or ResultSort.Modified or ResultSort.Accessed or ResultSort.Created)
        {
            var keys = new long[items.Length];
            for (int k = 0; k < items.Length; k++)
            {
                var index = Of(items[k]);
                int i = Entry(items[k]);
                keys[k] = sort switch
                {
                    ResultSort.Size => index.IsDirectory(i) ? -1 : index.Size[i],
                    ResultSort.Modified => index.Modified[i],
                    ResultSort.Accessed => index.Accessed[i],
                    _ => index.Created[i],
                };
            }
            token.ThrowIfCancellationRequested();
            Array.Sort(keys, items);
            if (descending)
                Array.Reverse(items);
            return;
        }

        Comparison<long> byName = (x, y) => Of(x).Name(Entry(x)).CompareTo(Of(y).Name(Entry(y)), StringComparison.OrdinalIgnoreCase);
        Comparison<long> comparison = sort switch
        {
            ResultSort.Type => (x, y) =>
            {
                var a = Of(x); var b = Of(y);
                int ia = Entry(x), ib = Entry(y);
                int c = a.IsDirectory(ia).CompareTo(b.IsDirectory(ib)) * -1; // Folders first.
                if (c == 0)
                    c = Path.GetExtension(a.Name(ia)).CompareTo(Path.GetExtension(b.Name(ib)), StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : byName(x, y);
            },
            ResultSort.Path => (x, y) =>
            {
                int c = string.Compare(Of(x).FolderPath(Entry(x)), Of(y).FolderPath(Entry(y)), StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : byName(x, y);
            },
            _ => byName,
        };
        if (sort == ResultSort.Path)
        {
            // Folder paths first (cached per folder), then one sort over strings.
            var keys = new string[items.Length];
            for (int k = 0; k < items.Length; k++)
                keys[k] = Of(items[k]).FolderPath(Entry(items[k]));
            var order = Enumerable.Range(0, items.Length).ToArray();
            Array.Sort(order, (p, q) =>
            {
                int c = string.Compare(keys[p], keys[q], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : byName(items[p], items[q]);
            });
            var sorted = new long[items.Length];
            for (int k = 0; k < order.Length; k++)
                sorted[k] = items[order[k]];
            sorted.CopyTo(items, 0);
        }
        else
        {
            Array.Sort(items, comparison);
        }
        if (descending)
            Array.Reverse(items);
    }
}
