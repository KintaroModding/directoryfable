using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>Free and total space of the drive or network share that holds a folder.</summary>
public static class DriveSpace
{
    /// <summary>Null when Windows cannot tell (offline share, no access). Call off the UI thread.</summary>
    public static (long Free, long Total)? Get(string folder)
    {
        string dir = folder.EndsWith('\\') ? folder : folder + "\\";
        return GetDiskFreeSpaceEx(dir, out ulong freeForUser, out ulong total, out _)
            ? ((long)freeForUser, (long)total)
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}
