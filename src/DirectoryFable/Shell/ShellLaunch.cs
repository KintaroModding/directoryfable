using System.Diagnostics;
using System.IO;

namespace DirectoryFable.Shell;

/// <summary>Opens files with their Windows program, as a double-click in Windows Explorer does.</summary>
internal static class ShellLaunch
{
    /// <summary>
    /// The start info for a file: its program, started in the file's folder. Without a working folder the
    /// program would inherit DirectoryFable's own (the folder of DirectoryFable.exe), so an exe that reads
    /// files next to itself would not find them.
    /// </summary>
    public static ProcessStartInfo For(string path) => new(path)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(path) ?? "",
    };
}
