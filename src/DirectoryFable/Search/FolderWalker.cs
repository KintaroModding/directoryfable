using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DirectoryFable.Search;

/// <summary>
/// The fallback when the Master File Table cannot be read (no administrator rights, not NTFS,
/// network drives): a normal folder walk with FindFirstFileEx (large fetch, no short names).
/// Slower for whole drives, fine for a folder tree. Junctions and symbolic links are listed but
/// not followed (no loops, no double counting). Folders that cannot be opened are skipped.
/// </summary>
public static class FolderWalker
{
    public static DriveIndex Walk(string root, Action<int>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var index = new DriveIndex(root, "folder walk");
        var pending = new Stack<(int Entry, string Path)>();
        pending.Push((-1, index.Root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (parent, path) = pending.Pop();
            string pattern = (path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path) + "*";
            using var find = FindFirstFileExW(pattern, 1 /* FindExInfoBasic */, out var data, 0, IntPtr.Zero, 2 /* LARGE_FETCH */);
            if (find.IsInvalid)
                continue;
            do
            {
                string name = data.cFileName;
                if (name is "." or "..")
                    continue;
                long size = data.nFileSizeHigh * 0x1_0000_0000L + data.nFileSizeLow;
                bool folder = (data.dwFileAttributes & 0x10) != 0;
                int entry = index.Add(parent, name, folder ? 0 : size, data.ftCreationTime, data.ftLastWriteTime,
                    data.ftLastAccessTime, data.dwFileAttributes);
                // Do not follow junctions / symbolic links (0x400 = reparse point).
                if (folder && (data.dwFileAttributes & 0x400) == 0)
                    pending.Push((entry, path + name + "\\"));
                if ((index.Count & 0x3FFF) == 0)
                    progress?.Invoke(index.Count);
            }
            while (FindNextFileW(find, out data));
        }
        index.ReadTime = watch.Elapsed;
        index.ReadAt = DateTime.Now;
        return index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private sealed class FindHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public FindHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data,
        int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(FindHandle find, out Win32FindData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr find);
}
