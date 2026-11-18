using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable;

/// <summary>
/// Own title bar. The Windows title bar has a fixed height, so the window
/// uses <see cref="System.Windows.Shell.WindowChrome"/> and draws the bar itself. The title is
/// the name of the active pane's folder, so the taskbar button shows it (like Explorer).
/// </summary>
public partial class MainWindow
{
    private const string AppName = "DirectoryFable";

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTMAXBUTTON = 9;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    private bool _maxPressed;

    private void InitTitleBar()
    {
        Title = AppName;
        StateChanged += (_, _) => UpdateMaximizedLayout();
        DpiChanged += (_, _) => UpdateMaximizedLayout();
        UpdateMaximizedLayout();
    }

    /// <summary>
    /// The explorer tab shows the active folder's name; the other tabs show only their view's
    /// name (no file name, no search text). The window title (taskbar button, Alt+Tab) is the
    /// name of what is shown: that folder, Notepad#'s file, the image, or the search.
    /// </summary>
    private void UpdateTitle()
    {
        string? path = ActivePane.CurrentFolder;
        string folder = AppName;
        if (!string.IsNullOrEmpty(path))
        {
            string name = PathHelper.GetName(path);
            // A drive root has no folder name: "C:" reads better as "C:\".
            folder = name.Length == 2 && name[1] == ':' ? name + "\\" : name;
        }
        ExplorerTabText.Text = folder;
        string? file = NotepadPage.Active?.FileName;
        string? image = AstraViewerPage.CurrentPath is { } shown ? System.IO.Path.GetFileName(shown) : null;
        string search = SearchPage.TitleText;
        Title = ShownView switch
        {
            MainView.Notepad => file ?? "Notepad#",
            MainView.Viewer => image ?? "AstraViewer",
            MainView.Search => search.Length == 0 ? "SonnetSearch" : $"SonnetSearch: {search}",
            _ => folder,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Added after WindowChrome's own hook, so this one runs first.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(TitleBarHook);

        // Every clipboard change (also from other apps) updates the faded "cut" rows.
        AddClipboardFormatListener(hwnd);
        FileClipboard.ClipboardChanged();
    }

    /// <summary>
    /// A maximized window is larger than the screen by its resize border on each side.
    /// With WindowChrome that part is client area, so pad it away (else the bar is cut off).
    /// </summary>
    private void UpdateMaximizedLayout()
    {
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            uint dpiValue = (uint)Math.Round(dpi.PixelsPerInchX);
            double px = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpiValue) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpiValue);
            double dip = px / dpi.DpiScaleX;
            RootBorder.Padding = new Thickness(dip);
            MaxButton.Content = "\uE923"; // Restore glyph.
            MaxButton.ToolTip = "Restore Down";
        }
        else
        {
            RootBorder.Padding = new Thickness(0);
            MaxButton.Content = "\uE922";
            MaxButton.ToolTip = "Maximize";
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void Close_Click(object sender, RoutedEventArgs e) => Close(); // Tray mode: Closing hides.

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Windows 11 shows the snap layouts flyout only when the maximize button answers
    /// WM_NCHITTEST with HTMAXBUTTON. Mouse input over it then arrives as non-client
    /// messages, so hover, press and click are handled here.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                // Read the clipboard after this message, not while Windows sends it.
                Dispatcher.BeginInvoke(FileClipboard.ClipboardChanged);
                break;

            case WM_NCHITTEST:
                bool over = IsOverMaxButton(lParam);
                SetMaxButtonState(over, over && _maxPressed);
                if (over)
                {
                    handled = true;
                    return HTMAXBUTTON;
                }
                break;

            case WM_NCMOUSELEAVE:
                _maxPressed = false;
                SetMaxButtonState(false, false);
                break;

            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                handled = true;
                _maxPressed = true;
                SetMaxButtonState(true, true);
                return IntPtr.Zero;

            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                handled = true;
                bool click = _maxPressed;
                _maxPressed = false;
                SetMaxButtonState(true, false);
                if (click)
                    ToggleMaximized();
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible)
            return false;
        long value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var p = MaxButton.PointFromScreen(screen);
        return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
    }

    private void SetMaxButtonState(bool hover, bool pressed)
    {
        var brush = pressed ? (Brush)FindResource("SubtlePressedBrush")
            : hover ? (Brush)FindResource("SubtleHoverBrush")
            : Brushes.Transparent;
        if (!ReferenceEquals(MaxButton.Background, brush))
            MaxButton.Background = brush;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
