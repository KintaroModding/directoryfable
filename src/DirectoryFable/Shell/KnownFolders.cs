using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>
/// Real locations of Windows known folders. Downloads has no Environment.SpecialFolder value,
/// and users often move it to another drive, so "%USERPROFILE%\Downloads" is wrong.
/// </summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads => Get(DownloadsId)
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string? Get(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr path) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
