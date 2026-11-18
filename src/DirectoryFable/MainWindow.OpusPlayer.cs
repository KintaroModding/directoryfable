using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DirectoryFable.Infrastructure;
using DirectoryFable.Player;
using DirectoryFable.Views;

namespace DirectoryFable;

/// <summary>
/// OpusHC, the fifth title bar view: videos opened from DirectoryFable (double-click on the chosen
/// types, "Open in OpusHC"). Until VLC's engine is downloaded the view shows what it is, where it comes from and
/// where it goes, with Download. Full screen: the title bar goes and the window covers its whole monitor.
/// </summary>
public partial class MainWindow
{
    private bool PlayerShown => PlayerPage.Visibility == Visibility.Visible;

    private readonly System.Windows.Threading.DispatcherTimer _playerSessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The window before full screen (state, normal bounds, resize mode, caption height, glass frame).</summary>
    private (WindowState State, Rect Bounds, ResizeMode Resize, double Caption, Thickness Glass)? _beforeFullScreen;

    private void InitOpusPlayer()
    {
        PlayerPage.ActiveChanged += UpdateTitle;
        PlayerPage.ShowInExplorerRequested += path => _ = ShowInExplorerAsync(path);
        PlayerPage.FullScreenRequested += SetFullScreen;
        // Volume, the last video and the resume list are part of the session (saved a moment after a pause or a switch).
        _playerSessionTimer.Tick += (_, _) =>
        {
            _playerSessionTimer.Stop();
            SaveSession();
        };
        PlayerPage.StateChanged += () =>
        {
            _playerSessionTimer.Stop();
            _playerSessionTimer.Start();
        };
        // Explorer: double-click / Enter on a video type, or "Open in OpusHC".
        FilePane.OpenInPlayer = async path =>
        {
            ShowView(MainView.Player);
            await PlayerPage.OpenAsync(path);
        };
    }

    private void PlayerTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Player);

    /// <summary>The view was just shown: a video of the last session is loaded (paused).</summary>
    private void PlayerShownFirstTime() => _ = PlayerPage.OnShownAsync();

    /// <summary>Full screen: no title bar, the whole monitor, on top; back: the window as it was.</summary>
    private void SetFullScreen(bool on)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (on)
        {
            if (_beforeFullScreen is not null)
                return;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _beforeFullScreen = (WindowState, bounds, ResizeMode, chrome?.CaptionHeight ?? 0, chrome?.GlassFrameThickness ?? default);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var monitor = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            TitleBar.Visibility = Visibility.Collapsed;
            if (chrome is not null)
            {
                chrome.CaptionHeight = 0; // The top of the picture is no window caption to drag.
                chrome.GlassFrameThickness = new Thickness(0);
            }
            // Windows 11 draws a 1 px border and round corners around every window that is not maximized: not here.
            SetBorderless(hwnd, true);
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            RootBorder.Padding = new Thickness(0);
            Left = monitor.Left / dpi.DpiScaleX;
            Top = monitor.Top / dpi.DpiScaleY;
            Width = monitor.Width / dpi.DpiScaleX;
            Height = monitor.Height / dpi.DpiScaleY;
            Topmost = true;
        }
        else
        {
            if (_beforeFullScreen is not { } before)
                return;
            _beforeFullScreen = null;
            Topmost = false;
            TitleBar.Visibility = Visibility.Visible;
            if (chrome is not null)
            {
                chrome.CaptionHeight = before.Caption;
                chrome.GlassFrameThickness = before.Glass;
            }
            SetBorderless(new WindowInteropHelper(this).Handle, false);
            ResizeMode = before.Resize;
            Left = before.Bounds.Left;
            Top = before.Bounds.Top;
            Width = before.Bounds.Width;
            Height = before.Bounds.Height;
            // Maximized again once Windows has the resizable window style back (in the same step it stays normal).
            if (before.State == WindowState.Maximized)
                Dispatcher.BeginInvoke(() => WindowState = WindowState.Maximized, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Windows 11's window border and round corners off (full screen) or back to Windows' default.</summary>
    private static void SetBorderless(IntPtr hwnd, bool borderless)
    {
        int color = borderless ? DwmColorNone : DwmColorDefault;
        int corner = borderless ? DwmCornerDoNotRound : DwmCornerDefault;
        // Older Windows (10) does not know these attributes: the call fails and nothing changes.
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmCornerDefault = 0;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
