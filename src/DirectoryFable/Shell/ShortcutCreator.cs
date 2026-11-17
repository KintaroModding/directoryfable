using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DirectoryFable.Shell;

/// <summary>Creates Windows shortcuts (.lnk) like Explorer's "Create shortcuts here".</summary>
public static class ShortcutCreator
{
    /// <summary>
    /// Creates "<name> - Shortcut.lnk" in <paramref name="folder"/> for each target
    /// (numbered if the name exists). Returns the created paths. Runs on an STA thread.
    /// </summary>
    public static Task<IReadOnlyList<string>> CreateAsync(IReadOnlyList<string> targets, string folder)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var created = new List<string>();
                foreach (string target in targets)
                {
                    string path = UniquePath(folder, Path.GetFileName(target.TrimEnd('\\')) + " - Shortcut");
                    var link = (IShellLinkW)new ShellLink();
                    try
                    {
                        link.SetPath(target);
                        if (Path.GetDirectoryName(target) is { } dir)
                            link.SetWorkingDirectory(dir);
                        ((IPersistFile)link).Save(path, true);
                        created.Add(path);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(link);
                    }
                }

                tcs.SetResult(created);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "CreateShortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string UniquePath(string folder, string baseName)
    {
        string path = Path.Combine(folder, baseName + ".lnk");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{baseName} ({n}).lnk");
        return path;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    // Method order must match ShObjIdl_core.h exactly; only SetPath/SetWorkingDirectory are used.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
