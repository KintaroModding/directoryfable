using System.IO;

namespace DirectoryFable.Shell;

/// <summary>What to do with an item whose name is already taken in the target folder.</summary>
public enum ConflictChoice
{
    Replace,
    Skip,
    KeepBoth,
    /// <summary>Replace when the new one is newer (<see cref="ConflictPlanner.IsNewer"/>), else skip; decided per item.</summary>
    ReplaceIfNewer,
}

/// <summary>An item whose name is taken in the target folder (also inside a folder that is merged into one).</summary>
/// <param name="TypeMismatch">A file meets a folder of the same name (or the other way round): it cannot replace it.</param>
public sealed record Conflict(string Source, string Target, bool TypeMismatch);

/// <summary>One item for the copy engine: copied / moved into <paramref name="TargetFolder"/>, under a new name or its own.</summary>
public sealed record PlannedItem(string Source, string TargetFolder, string? NewName);

/// <summary>
/// Finds the name conflicts of a copy or move before it starts, so DirectoryFable can ask in its own dialog and
/// the Windows copy engine never has to ask: skipped items are left out, "keep both" items get a free name
/// ("name (2).ext"), and the rest replace (the engine runs with "yes to all"). A folder that goes into an existing
/// folder is merged; only where a conflict inside it is not "replace" is it split into its children.
/// </summary>
public static class ConflictPlanner
{
    /// <summary>The conflicts, in the order the items come (folders: depth first).</summary>
    public static List<Conflict> Find(IReadOnlyList<string> sources, string targetFolder)
    {
        var conflicts = new List<Conflict>();
        foreach (string source in sources)
            Check(Trim(source), targetFolder, conflicts);
        return conflicts;
    }

    private static void Check(string source, string targetFolder, List<Conflict> conflicts)
    {
        string target = Path.Combine(targetFolder, Path.GetFileName(source));
        bool sourceIsFolder = Directory.Exists(source);
        bool targetIsFolder = Directory.Exists(target);
        bool targetIsFile = !targetIsFolder && File.Exists(target);
        if (string.Equals(Trim(source), Trim(target), StringComparison.OrdinalIgnoreCase))
            return; // Onto itself: the caller handles copies into their own folder.
        if (sourceIsFolder && targetIsFolder)
        {
            // Merged: the conflicts are inside. Reparse points (links) are not followed.
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                return;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(source).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // Unreadable: the engine reports it.
            }
            foreach (string child in children)
                Check(child, target, conflicts);
        }
        else if (targetIsFolder || targetIsFile)
        {
            conflicts.Add(new Conflict(source, target, TypeMismatch: sourceIsFolder != targetIsFolder));
        }
    }

    /// <summary>
    /// The items to give the engine for these choices (key: the conflict's source), and the folders that were
    /// split (for a move they may be left empty).
    /// </summary>
    public static (List<PlannedItem> Items, List<string> SplitFolders) Plan(
        IReadOnlyList<string> sources, string targetFolder, IReadOnlyDictionary<string, ConflictChoice> choices)
    {
        var items = new List<PlannedItem>();
        var split = new List<string>();
        // Folders that hold a conflict that is not "replace": only those are split.
        var mustSplit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, choice) in choices)
        {
            if (choice == ConflictChoice.Replace)
                continue;
            for (string? parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
                if (!mustSplit.Add(parent))
                    break;
        }
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // New names already given in this plan.

        void Add(string source, string folder)
        {
            string name = Path.GetFileName(source);
            if (choices.TryGetValue(source, out var choice))
            {
                if (choice == ConflictChoice.Skip)
                    return;
                items.Add(new PlannedItem(source, folder, choice == ConflictChoice.KeepBoth ? FreeName(folder, name, taken) : null));
                return;
            }
            string target = Path.Combine(folder, name);
            if (mustSplit.Contains(source) && Directory.Exists(source) && Directory.Exists(target))
            {
                split.Add(source);
                foreach (string child in Directory.EnumerateFileSystemEntries(source).ToList())
                    Add(child, target);
                return;
            }
            items.Add(new PlannedItem(source, folder, null));
        }

        foreach (string source in sources)
            Add(Trim(source), targetFolder);
        return (items, split);
    }

    /// <summary>
    /// True when <paramref name="source"/> was modified at least 2 seconds after <paramref name="target"/> (FAT drives
    /// keep times in 2-second steps, so a smaller difference is "the same").
    /// </summary>
    public static bool IsNewer(string source, string target)
    {
        try
        {
            return File.GetLastWriteTimeUtc(source) - File.GetLastWriteTimeUtc(target) >= TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"name (2).ext", "name (3).ext" ...: the first that is free in the folder and not given yet.</summary>
    public static string FreeName(string folder, string name, HashSet<string>? taken = null)
    {
        bool isFolderName = Directory.Exists(Path.Combine(folder, name));
        string stem = isFolderName ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isFolderName ? "" : Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){extension}";
            string path = Path.Combine(folder, candidate);
            if (!File.Exists(path) && !Directory.Exists(path) && (taken is null || taken.Add(path)))
                return candidate;
        }
    }

    private static string Trim(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;
}
