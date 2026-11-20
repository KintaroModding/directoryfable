using System.IO;

namespace DirectoryFable.Sync;

/// <summary>
/// The wipe before a FlashSync copy: deletes everything inside one folder for good (not to the Recycle Bin), and
/// keeps the folder itself. Links to other folders (junctions, symbolic links) are removed as links; what they
/// point to is never touched. On a drive root, Windows' own two folders stay.
/// </summary>
public static class SyncWipe
{
    /// <summary>What a wipe did: how many files and folders went, and what could not be deleted (the first ones).</summary>
    public sealed record Result(long Deleted, IReadOnlyList<string> Failed, long FailedCount);

    private const int ListedFailures = 200;

    private static readonly EnumerationOptions Everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Empties <paramref name="folder"/>. <paramref name="progress"/> gets the count so far and the folder at work,
    /// now and then. Throws when the folder is one a wipe must never empty (see <see cref="SyncSafety.WipeProblem"/>).
    /// </summary>
    public static Result Run(string folder, Action<long, string>? progress, CancellationToken token)
    {
        if (SyncSafety.WipeProblem(folder) is { } problem)
            throw new InvalidOperationException(problem);
        var state = new State(progress, token);
        var top = new DirectoryInfo(SyncSafety.Normalize(folder));
        if (!top.Exists)
            return new Result(0, [], 0);
        bool driveRoot = top.Parent is null;
        DeleteContents(top, state, driveRoot);
        return new Result(state.Deleted, state.Failed, state.FailedCount);
    }

    private sealed class State(Action<long, string>? progress, CancellationToken token)
    {
        public long Deleted, FailedCount;
        public readonly List<string> Failed = [];
        private long _reported = Environment.TickCount64;

        public void Check() => token.ThrowIfCancellationRequested();

        public void Done(string folder)
        {
            Deleted++;
            long now = Environment.TickCount64;
            if (now - _reported < 200)
                return;
            _reported = now;
            progress?.Invoke(Deleted, folder);
        }

        public void Fail(string path, Exception ex)
        {
            FailedCount++;
            if (Failed.Count < ListedFailures)
                Failed.Add($"{path} - {ex.Message}");
        }
    }

    private static void DeleteContents(DirectoryInfo folder, State state, bool driveRoot)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos("*", Everything);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.Fail(folder.FullName, ex);
            return;
        }

        using var walk = entries.GetEnumerator();
        while (true)
        {
            state.Check();
            FileSystemInfo entry;
            try
            {
                if (!walk.MoveNext())
                    break;
                entry = walk.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Fail(folder.FullName, ex);
                break;
            }

            if (driveRoot && entry.Name is var name &&
                (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes = attributes & ~FileAttributes.ReadOnly;
                if (entry is DirectoryInfo sub)
                {
                    // A link is removed itself; a real folder is emptied first.
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        DeleteContents(sub, state, driveRoot: false);
                    sub.Delete(false);
                }
                else
                {
                    entry.Delete();
                }
                state.Done(folder.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Fail(entry.FullName, ex);
            }
        }
    }
}
