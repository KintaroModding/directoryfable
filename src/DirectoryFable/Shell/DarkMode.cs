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

    /// <summary>
    /// Takes the white flash away when a window comes up. WPF (with its dark theme) shows a new window before the theme's
    /// colors and the first picture are there, so it shows white for a moment. The window is "cloaked" (DWM keeps it
    /// invisible, but it is laid out and drawn) from the moment its handle is made until its first picture is drawn,
    /// then shown. The clear color and the title bar are dark from the start too.
    /// </summary>
    public static void PrepareWindow(System.Windows.Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (System.Windows.Interop.HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
            {
                target.BackgroundColor = window.Background is System.Windows.Media.SolidColorBrush { Color: var color }
                    ? color
                    : System.Windows.Media.Color.FromRgb(0x19, 0x19, 0x19);
            }
            int on = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaCloak, ref on, sizeof(int));
            // Shown after the first picture, and a short moment more for the theme's backdrop.
            window.ContentRendered += Uncloak;
            var safety = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            safety.Tick += (_, _) => { safety.Stop(); Show(); }; // Never stays hidden.
            safety.Start();

            void Uncloak(object? sender, EventArgs e)
            {
                window.ContentRendered -= Uncloak;
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                later.Tick += (_, _) => { later.Stop(); Show(); };
                later.Start();
            }

            void Show()
            {
                int off = 0;
                _ = DwmSetWindowAttribute(handle, DwmwaCloak, ref off, sizeof(int));
            }
        };
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
