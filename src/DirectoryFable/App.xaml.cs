using System.Windows;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable;

/// <summary>
/// Startup: settings, single instance, tray icon (tray mode), main window.
/// Command line: <c>DirectoryFable.exe [leftFolder] [rightFolder] [--multi] [--taskbar|--tray] [--data=folder]</c>.
/// <c>--multi</c> skips the single-instance check, <c>--taskbar</c>/<c>--tray</c> override the
/// window mode, <c>--data=</c> uses another folder for settings/favorites. Automated UI tests use
/// all three so they never touch the user's copy, tray icon or files.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\DirectoryFable.SingleInstance";
    private const string ShowEventName = @"Local\DirectoryFable.Show";

    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        var folders = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        bool Flag(string name) => e.Args.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool multi = Flag("--multi");
        string? dataArg = e.Args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase));
        if (dataArg is not null)
            AppSettings.DataFolder = dataArg["--data=".Length..].Trim('"');

        if (!multi && !TryBecomeSingleInstance())
        {
            // Another copy runs: ask it to show its window, then quit.
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }

        DarkMode.EnableForMenus();
        ZipBrowser.CleanStaleTemp(); // Files unpacked from ZIPs by copies that crashed.
        base.OnStartup(e);

        var settings = AppSettings.Load();
        bool trayMode = Flag("--taskbar") ? false : Flag("--tray") || settings.IsTrayMode;
        var session = settings.RestoreSession ? SessionState.Load() : null;
        ApplyColors(settings);
        SevenZip.Configure(settings.SevenZipFolder);
        Editor.EditorFileTypes.Configure(settings);

        _window = new MainWindow(folders.ElementAtOrDefault(0), folders.ElementAtOrDefault(1), trayMode, settings, session);
        MainWindow = _window;
        // The last session decides; without one, "startMaximized".
        if (session?.Maximized ?? settings.StartMaximized)
            _window.WindowState = WindowState.Maximized;
        if (trayMode)
            _tray = new TrayIcon(leftClick: _window.ToggleFromTray, show: _window.ShowFromTray, exit: ExitApp);
        else
            _window.Closed += (_, _) => Shutdown(); // Taskbar mode: closing the window exits.
        _window.Show();

        if (_showSignal is not null)
            StartShowListener(_showSignal);

        // Windows logoff/shutdown must not be blocked by "close = hide to tray".
        SessionEnding += (_, _) =>
        {
            _window.SaveSession();
            _window.SkipUnsavedPrompt = true;
            _window.AllowClose = true;
        };
    }

    /// <summary>
    /// Colors from settings.json override the theme (top-level resources win over merged ones).
    /// Also called by the settings page, so a new color shows at once.
    /// </summary>
    internal static void ApplyColors(AppSettings settings)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.ArchiveBackground);
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            Current.Resources["ArchiveListBackgroundBrush"] = brush;
        }
        catch (FormatException)
        {
            // Not a valid color: keep the theme's default.
        }
    }

    private bool TryBecomeSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
            return false; // Only the first copy owns the mutex; a second copy must not release it.
        _ownsSingleInstance = true;
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return true;
    }

    /// <summary>A second start of the exe sets the event; this shows the window.</summary>
    private void StartShowListener(EventWaitHandle signal)
    {
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        thread.Start();
    }

    private void ExitApp()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
            if (!_window.IsClosed)
            {
                // Cancelled (unsaved Notepad# files): keep running.
                _window.AllowClose = !_window.TrayMode;
                return;
            }
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        ZipBrowser.CleanOwnTemp();
        if (_ownsSingleInstance)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
