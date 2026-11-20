using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Sync;

/// <summary>
/// The checks before a FlashSync job is saved or run. The wipe option deletes a whole folder, so the destination
/// is checked on its own: never Windows' drive itself, a Windows or program folder, a user's folder, or the app's
/// own folder; never a folder that holds a source or lies inside one.
/// </summary>
public static class SyncSafety
{
    /// <summary>The file FlashSync puts into a job's destination folder; it names the job the folder belongs to.</summary>
    public const string MarkerName = "FlashSync job.txt";

    /// <summary>Why the job cannot be saved or run, in words for the user; null = it is fine.</summary>
    public static string? Problem(SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
            return "Give the job a name.";
        if (job.Sources.Count == 0)
            return "Tick at least one folder in the tree.";
        if (string.IsNullOrWhiteSpace(job.Destination) || !IsFullPath(job.Destination))
            return "Choose a destination folder (a full path like Q:\\Backup).";
        foreach (string source in job.Sources)
        {
            if (!IsFullPath(source))
                return $"\"{source}\" is not a full folder path.";
        }
        foreach (string name in job.ExcludedNames)
        {
            if (!IsNamePattern(name))
                return $"\"{name}\" cannot be an excluded name. Use a file or folder name, with * and ? if needed, and no \\ / : \" < > |.";
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in job.Sources)
        {
            string name = SyncJob.TargetName(source);
            if (names.TryGetValue(name, out string? other))
                return $"\"{other}\" and \"{source}\" would both go into the folder \"{name}\" of the destination. Put them into two jobs.";
            names[name] = source;
        }

        foreach (string source in job.Sources)
        {
            if (IsSameOrInside(job.Destination, source))
                return $"The destination is inside the source \"{source}\". The copy would copy itself.";
            if (IsSameOrInside(source, job.Destination))
                return $"The source \"{source}\" is inside the destination folder.";
        }
        return job.Wipe ? WipeProblem(job.Destination) : null;
    }

    /// <summary>Why this folder must never be emptied by a wipe; null = a wipe is allowed there.</summary>
    public static string? WipeProblem(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !IsFullPath(destination))
            return "The wipe option needs a full destination path.";
        string folder = Normalize(destination);
        string? windows = FolderOrNull(Environment.SpecialFolder.Windows);
        if (windows is not null && string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(windows), StringComparison.OrdinalIgnoreCase)
            && Path.GetPathRoot(folder) is { } root && string.Equals(Normalize(root), folder, StringComparison.OrdinalIgnoreCase))
            return "The wipe option cannot empty the Windows drive itself. Choose a folder on it, or another drive.";
        if (windows is not null && IsSameOrInside(folder, windows))
            return "The wipe option cannot be used inside the Windows folder.";

        string? profile = FolderOrNull(Environment.SpecialFolder.UserProfile);
        var kept = new List<(string? Path, string What)>
        {
            (windows, "the Windows folder"),
            (FolderOrNull(Environment.SpecialFolder.ProgramFiles), "the Program Files folder"),
            (FolderOrNull(Environment.SpecialFolder.ProgramFilesX86), "the Program Files (x86) folder"),
            (FolderOrNull(Environment.SpecialFolder.CommonApplicationData), "the ProgramData folder"),
            (profile, "your user folder"),
            (profile is null ? null : Path.GetDirectoryName(profile), "the Users folder"),
            (AppContext.BaseDirectory, "the folder of DirectoryFable"),
            (AppSettings.DataFolder, "the data folder of DirectoryFable"),
        };
        foreach (var (path, what) in kept)
        {
            // The destination is that folder, or a folder above it: the wipe would delete it.
            if (path is not null && IsSameOrInside(path, folder))
                return $"The wipe option cannot be used here: it would delete {what}.";
        }
        return null;
    }

    private static string? FolderOrNull(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        return path.Length > 0 ? path : null;
    }

    /// <summary>A path with a drive letter ("X:\...") or a network path ("\\server\share...").</summary>
    public static bool IsFullPath(string path)
    {
        path = path.Trim();
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return path.Length > 4 && path.IndexOf('\\', 2) > 2;
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
    }

    /// <summary>
    /// A name or pattern that may be handed to the copy tool as an exclusion: a file or folder name with wildcards,
    /// never something with a path separator and never something the tool could read as one of its options.
    /// </summary>
    public static bool IsNamePattern(string text)
    {
        text = text.Trim();
        return text.Length is > 0 and <= 200 && text.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|']) < 0 && !text.Any(char.IsControl);
    }

    /// <summary>The full path without a "\" at the end (a drive root keeps it: "X:\").</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or lies below it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        try
        {
            string a = Normalize(path).TrimEnd('\\'), b = Normalize(folder).TrimEnd('\\');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The serial number ("1A2B-3C4D") and the label of the drive a path is on; null when it cannot be read.</summary>
    public static (string Serial, string Label)? Volume(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;
            if (!root.EndsWith('\\'))
                root += "\\";
            var label = new StringBuilder(261);
            if (!GetVolumeInformation(root, label, label.Capacity, out uint serial, out _, out _, null, 0))
                return null;
            return ($"{serial >> 16:X4}-{serial & 0xFFFF:X4}", label.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The id of the job whose marker file lies in a folder; null = no marker (or it cannot be read).</summary>
    public static string? MarkerJobId(string destination)
    {
        try
        {
            string file = Path.Combine(destination, MarkerName);
            if (!File.Exists(file))
                return null;
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith("Job id:", StringComparison.OrdinalIgnoreCase))
                    return line["Job id:".Length..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Writes the marker file that names the job a destination folder belongs to.</summary>
    public static void WriteMarker(SyncJob job)
    {
        string file = Path.Combine(job.Destination, MarkerName);
        string text = $"This folder is the destination of the FlashSync job \"{job.Name}\" (DirectoryFable).\r\n" +
                      $"Job id: {job.Id}\r\n" +
                      (job.Wipe ? "This job has the wipe option: each run deletes everything in this folder first.\r\n" : "") +
                      $"Last run started: {DateTime.Now:yyyy-MM-dd HH:mm}\r\n";
        if (File.Exists(file))
            File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllText(file, text);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, StringBuilder label, int labelSize, out uint serial,
        out uint maxComponent, out uint flags, StringBuilder? fileSystem, int fileSystemSize);
}
