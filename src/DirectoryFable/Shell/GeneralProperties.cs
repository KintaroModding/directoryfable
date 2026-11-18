using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Shell;

/// <summary>
/// The "General" section of the properties window, as Windows' General tab shows it: type, location, size and
/// size on disk (with the exact bytes), what a folder contains, the dates and the attributes. Windows' property
/// list (the Details tab) has almost none of this for folders, so it is computed here. A folder's content is
/// counted in the background (<see cref="CountAsync"/>); the window shows the numbers while they grow.
/// </summary>
public static class GeneralProperties
{
    /// <summary>What a folder holds, filled in by <see cref="CountAsync"/> (read while it runs).</summary>
    public sealed class FolderTotals
    {
        public long Files;
        public long Folders;
        public long Bytes;
        public long OnDisk;
        public volatile bool Done;
    }

    public static PropertyGroup For(string path, FolderTotals? totals)
    {
        if (IsDriveRoot(path))
            return Drive(path);
        bool folder = Directory.Exists(path);
        var info = folder ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var lines = new List<PropertyLine>
        {
            new("Type", ShellInfo.GetTypeName(path, folder)),
            new("Location", Path.GetDirectoryName(path.TrimEnd('\\')) ?? ""),
        };
        if (folder)
        {
            string counting = totals is { Done: false } ? "   (counting...)" : "";
            long bytes = totals is null ? 0 : Interlocked.Read(ref totals.Bytes);
            long onDisk = totals is null ? 0 : Interlocked.Read(ref totals.OnDisk);
            lines.Add(new("Size", SizeText(bytes) + counting));
            lines.Add(new("Size on disk", SizeText(onDisk) + counting));
            long files = totals is null ? 0 : Interlocked.Read(ref totals.Files);
            long folders = totals is null ? 0 : Interlocked.Read(ref totals.Folders);
            lines.Add(new("Contains", $"{files:N0} {(files == 1 ? "File" : "Files")}, {folders:N0} {(folders == 1 ? "Folder" : "Folders")}{counting}"));
        }
        else if (info is FileInfo file)
        {
            lines.Add(new("Size", SizeText(file.Length)));
            lines.Add(new("Size on disk", SizeText(OnDisk(file.FullName, file.Length, file.Attributes, ClusterSize(path)))));
        }
        lines.Add(new("Created", DateText(info.CreationTime)));
        if (!folder)
        {
            lines.Add(new("Modified", DateText(info.LastWriteTime)));
            lines.Add(new("Accessed", DateText(info.LastAccessTime)));
        }
        lines.Add(new("Attributes", AttributesText(info.Attributes, folder)));
        return new PropertyGroup("General", lines);
    }

    /// <summary>
    /// Counts the files and folders below <paramref name="folder"/> with their size and size on disk. Links
    /// (junctions, symbolic links) count as folders but are not entered, as in Windows.
    /// </summary>
    public static Task CountAsync(string folder, FolderTotals totals, CancellationToken token) => Task.Run(() =>
    {
        try
        {
            long cluster = ClusterSize(folder);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // Hidden and system items count too.
                ReturnSpecialDirectories = false,
            };
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = pending.Pop();
                var entries = new FileSystemEnumerable<(string Path, bool Folder, long Length, FileAttributes Attributes)>(current,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.Length, e.Attributes), options);
                try
                {
                    foreach (var (path, isFolder, length, attributes) in entries)
                    {
                        if (isFolder)
                        {
                            Interlocked.Increment(ref totals.Folders);
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                                pending.Push(path);
                            continue;
                        }
                        Interlocked.Increment(ref totals.Files);
                        Interlocked.Add(ref totals.Bytes, length);
                        Interlocked.Add(ref totals.OnDisk, OnDisk(path, length, attributes, cluster));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A folder that cannot be read is left out, as in Windows.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window was closed.
        }
        finally
        {
            totals.Done = true;
        }
    }, token);

    private static PropertyGroup Drive(string root)
    {
        var drive = new DriveInfo(root);
        var lines = new List<PropertyLine>
        {
            new("Type", ShellInfo.GetTypeName(root, true)),
        };
        if (drive.IsReady)
        {
            lines.Add(new("Name", drive.VolumeLabel));
            lines.Add(new("File system", drive.DriveFormat));
            lines.Add(new("Used space", SizeText(drive.TotalSize - drive.TotalFreeSpace)));
            lines.Add(new("Free space", SizeText(drive.TotalFreeSpace)));
            lines.Add(new("Capacity", SizeText(drive.TotalSize)));
        }
        else
        {
            lines.Add(new("State", "Not ready"));
        }
        return new PropertyGroup("General", lines);
    }

    /// <summary>
    /// The space a file takes on the drive: its size rounded up to whole clusters; compressed and sparse files
    /// use what is really stored (GetCompressedFileSize).
    /// </summary>
    private static long OnDisk(string path, long length, FileAttributes attributes, long cluster)
    {
        if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            uint low = GetCompressedFileSizeW(path, out uint high);
            if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0)
                length = ((long)high << 32) | low;
        }
        return cluster > 0 ? (length + cluster - 1) / cluster * cluster : length;
    }

    private static long ClusterSize(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null)
            return 0;
        if (!root.EndsWith('\\'))
            root += "\\";
        return GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 0;
    }

    private static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(path) is { } root && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && root.Length <= 3;

    /// <summary>"126 GB (135.814.438.021 bytes)", as Windows writes it.</summary>
    private static string SizeText(long bytes) => $"{DisplayFormat.Size(bytes)} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes)";

    /// <summary>The long date with the weekday and the time with seconds ("Freitag, 6. Februar 2026 06:01:13").</summary>
    private static string DateText(DateTime date) => date.ToString("F", CultureInfo.CurrentCulture);

    private static string AttributesText(FileAttributes attributes, bool folder)
    {
        var names = new List<string>();
        void Add(FileAttributes flag, string name)
        {
            if ((attributes & flag) != 0)
                names.Add(name);
        }
        Add(FileAttributes.ReadOnly, "Read-only");
        Add(FileAttributes.Hidden, "Hidden");
        Add(FileAttributes.System, "System");
        if (!folder)
            Add(FileAttributes.Archive, "Archive");
        Add(FileAttributes.Compressed, "Compressed");
        Add(FileAttributes.Encrypted, "Encrypted");
        Add(FileAttributes.SparseFile, "Sparse");
        Add(FileAttributes.Offline, "Offline");
        Add(FileAttributes.NotContentIndexed, "Not indexed");
        Add(FileAttributes.ReparsePoint, "Link (reparse point)");
        return names.Count > 0 ? string.Join(", ", names) : "None";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
