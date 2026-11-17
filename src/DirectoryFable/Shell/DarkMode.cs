using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>
/// Dark Win32 popup menus (the native shell context menu), like Explorer
/// in dark mode. Uses the undocumented uxtheme exports that Explorer, Notepad++ and others
/// use (ordinal 135 SetPreferredAppMode, 136 FlushMenuThemes). If they are missing the
/// menus simply stay light.
/// </summary>
public static class DarkMode
{
    private const int ForceDark = 2;

    public static void EnableForMenus()
    {
        try
        {
            SetPreferredAppMode(ForceDark);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older or changed Windows build: keep the default (light) menus.
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
