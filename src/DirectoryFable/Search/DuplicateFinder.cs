using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DirectoryFable.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace DirectoryFable.Search;

/// <summary>
/// "Duplicates" in SonnetSearch: the files of a search result whose content another file of the result has too.
/// Step by step, so that few files are read whole: the same size (from the index, no file read), then the same
/// first 64 KB, then the same SHA-256 of the whole file. Empty files are left out. Hard links (one file under two
/// names) count once: only the first name shows. What a file gave is kept (its length and change time on disk tell
/// when to read it again), so a second run with other filters reads only new and changed files.
/// </summary>
public sealed class DuplicateFinder
{
    private const int HeadBytes = 64 * 1024;
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>What one file gave. Length and Modified: as on disk when it was read (they decide reuse).</summary>
    private sealed class FileFacts
    {
        public long Length;
        public long Modified;
        public uint Volume;    // Volume serial and file id: the same pair = the same file (a hard link).
        public ulong FileId;
        public string? Head;   // SHA-256 of the first 64 KB (the whole file when it is smaller).
        public string? Full;   // SHA-256 of the whole file.
    }

    private readonly ConcurrentDictionary<string, FileFacts> _known = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _wholeFiles;
    private int _filesDone, _filesTotal;
    private long _bytesDone, _bytesTotal;

    /// <summary>What is being done now, for the status line.</summary>
    public string ProgressText => _wholeFiles
        ? $"Duplicates: comparing whole files, {DisplayFormat.Size(Interlocked.Read(ref _bytesDone))} of {DisplayFormat.Size(Interlocked.Read(ref _bytesTotal))}"
        : $"Duplicates: comparing the start of {Volatile.Read(ref _filesTotal):N0} files of the same size, {Volatile.Read(ref _filesDone):N0} done";

    /// <summary>
    /// The duplicates among <paramref name="result"/>: groups of files with the same content, the largest files first,
    /// each group's files in path order. <see cref="SearchResult.Groups"/> numbers the groups (1, 2, ...).
    /// Files that cannot be read (gone, locked, no access) are left out.
    /// </summary>
    public SearchResult Find(SearchResult result, CancellationToken token)
    {
        _wholeFiles = false;
        _filesDone = _filesTotal = 0;
        _bytesDone = _bytesTotal = 0;

        // 1. The same size: from the index, nothing is read.
        var bySize = new Dictionary<long, List<long>>();
        foreach (long item in result.Items)
        {
            var index = result.IndexOf(item);
            int i = SearchResult.EntryOf(item);
            long size = index.Size[i];
            if (index.IsDirectory(i) || size <= 0)
                continue;
            if (!bySize.TryGetValue(size, out var list))
                bySize[size] = list = [];
            list.Add(item);
        }
        long[] candidates = [.. bySize.Values.Where(l => l.Count > 1).SelectMany(l => l)];
        bySize.Clear();
        token.ThrowIfCancellationRequested();

        // 2. The same start: one read of up to 64 KB per file.
        var paths = new string[candidates.Length];
        var facts = new FileFacts?[candidates.Length];
        _filesTotal = candidates.Length;
        Parallel.For(0, candidates.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, k =>
        {
            var index = result.IndexOf(candidates[k]);
            int i = SearchResult.EntryOf(candidates[k]);
            paths[k] = index.FullPath(i);
            facts[k] = ReadStart(paths[k]);
            Interlocked.Increment(ref _filesDone);
        });

        var sameStart = Group(Enumerable.Range(0, candidates.Length).Where(k => facts[k]?.Head is not null),
            k => (facts[k]!.Length, facts[k]!.Head!), facts);

        // 3. The same whole content: only where the start is not the whole file.
        int[] wholes = [.. sameStart.Where(g => facts[g[0]]!.Length > HeadBytes).SelectMany(g => g).Where(k => facts[k]!.Full is null)];
        _bytesTotal = wholes.Sum(k => facts[k]!.Length);
        _wholeFiles = true;
        Parallel.For(0, wholes.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, j =>
        {
            int k = wholes[j];
            if (!ReadWhole(paths[k], facts[k]!, token))
                facts[k] = null;
        });
        token.ThrowIfCancellationRequested();

        var groups = Group(sameStart.SelectMany(g => g).Where(k => facts[k]?.Full is not null),
            k => (facts[k]!.Length, facts[k]!.Full!), facts);

        // Largest files first (they free the most space); inside a group by path.
        foreach (var group in groups)
            group.Sort((a, b) => string.Compare(paths[a], paths[b], StringComparison.OrdinalIgnoreCase));
        groups.Sort((a, b) =>
        {
            int c = facts[b[0]]!.Length.CompareTo(facts[a[0]]!.Length);
            return c != 0 ? c : string.Compare(paths[a[0]], paths[b[0]], StringComparison.OrdinalIgnoreCase);
        });

        var items = new List<long>();
        var numbers = new List<int>();
        for (int g = 0; g < groups.Count; g++)
        {
            foreach (int k in groups[g])
            {
                items.Add(candidates[k]);
                numbers.Add(g + 1);
            }
        }
        return new SearchResult(result.Indexes, [.. items], result.Searched, result.Totals, [.. numbers]);
    }

    /// <summary>Files with the same key; one name per file (hard links); only groups of two and more.</summary>
    private static List<List<int>> Group(IEnumerable<int> files, Func<int, (long, string)> key, FileFacts?[] facts)
    {
        var groups = new Dictionary<(long, string), (List<int> Files, HashSet<(uint, ulong)> Ids)>();
        foreach (int k in files)
        {
            var id = key(k);
            if (!groups.TryGetValue(id, out var group))
                groups[id] = group = ([], []);
            var f = facts[k]!;
            if (f.FileId != 0 && !group.Ids.Add((f.Volume, f.FileId)))
                continue; // Another name of a file that is in the group already.
            group.Files.Add(k);
        }
        return [.. groups.Values.Select(g => g.Files).Where(g => g.Count > 1)];
    }

    /// <summary>
    /// The file's id and the hash of its first 64 KB. Kept from an earlier run only while the file on disk has the same
    /// length and change time (the index can be older than the file: a changed file must not count as a copy).
    /// </summary>
    private FileFacts? ReadStart(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0)
                return null; // Deleted (or emptied) since the drive was read.
            long modified = file.LastWriteTimeUtc.ToFileTimeUtc();
            if (_known.TryGetValue(path, out var known) && known.Length == file.Length && known.Modified == modified && known.Head is not null)
                return known;

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var facts = new FileFacts { Modified = modified, Length = RandomAccess.GetLength(handle) };
            if (facts.Length <= 0)
                return null;
            if (GetFileInformationByHandle(handle, out var info))
            {
                facts.Volume = info.VolumeSerialNumber;
                facts.FileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            }
            int length = (int)Math.Min(facts.Length, HeadBytes);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (ReadAt(handle, buffer.AsSpan(0, length), 0) != length)
                    return null;
                facts.Head = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (facts.Length <= HeadBytes)
                facts.Full = facts.Head;
            _known[path] = facts;
            return facts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The hash of the whole file into <paramref name="facts"/>; false when it cannot be read or changed its length.</summary>
    private bool ReadWhole(string path, FileFacts facts, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                FileOptions.SequentialScan);
            if (RandomAccess.GetLength(handle) != facts.Length)
                return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < facts.Length)
            {
                if (token.IsCancellationRequested)
                    return false; // Find stops after the loop.
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, ChunkBytes), offset);
                if (read <= 0)
                    return false;
                hash.AppendData(buffer, 0, read);
                offset += read;
                Interlocked.Add(ref _bytesDone, read);
            }
            facts.Full = Convert.ToHexString(hash.GetHashAndReset());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    /// <summary>Groups, files and the space that deleting all but one file of each group frees.</summary>
    public static (int Groups, int Files, long Freeable) Summarize(SearchResult result)
    {
        if (result.Groups is not { } numbers)
            return default;
        int groups = 0;
        long freeable = 0;
        for (int p = 0; p < numbers.Length; p++)
        {
            if (p == 0 || numbers[p] != numbers[p - 1])
                groups++;
            else
                freeable += result.SizeOf(result.Items[p]);
        }
        return (groups, numbers.Length, freeable);
    }

    // ---------- Interop ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}
