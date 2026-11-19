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
/// Updates: <c>--after-update=pid</c> (set by the restart after an update) waits for the old copy to
/// end; <c>--update-feed=url</c> reads the release list from another address (tests).
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
    private string[] _args = [];
    private bool _restartAfterExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        var folders = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        bool Flag(string name) => e.Args.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool multi = Flag("--multi");
        string? Value(string name) => e.Args.FirstOrDefault(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase))?[name.Length..].Trim('"');
        _args = e.Args;
        if (Value("--data=") is { } dataFolder)
            AppSettings.DataFolder = dataFolder;
        if (Value("--update-feed=") is { Length: > 0 } feed)
            Updates.UpdateSource.FeedOverride = feed;
        // Started by an update: the old copy is still ending (it holds the single-instance lock until then).
        if (int.TryParse(Value("--after-update="), out int oldProcess))
            WaitForExit(oldProcess);
        CleanUpAfterUpdate();

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
        Viewer.ImageFileTypes.Configure(settings);
        Player.VideoFileTypes.Configure(settings);
        Player.VlcEngine.CleanUpPending(); // A "Remove" of VLC's engine that had to wait for its files.

        _window = new MainWindow(folders.ElementAtOrDefault(0), folders.ElementAtOrDefault(1), trayMode, settings, session);
        MainWindow = _window;
        // The last session decides; without one, "startMaximized".
        bool maximized = session?.Maximized ?? settings.StartMaximized;
        // "Start hidden" (a setting): not when a folder was asked for, and not right after an update.
        bool hidden = settings.StartHidden && folders.Length == 0 && Value("--after-update=") is null;
        if (maximized && !hidden)
            _window.WindowState = WindowState.Maximized;
        if (trayMode)
            _tray = new TrayIcon(leftClick: _window.ToggleFromTray, show: _window.ShowFromTray, exit: ExitApp);
        else
            _window.Closed += (_, _) => Shutdown(); // Taskbar mode: closing the window exits.
        if (hidden)
            _window.ShowHiddenAtStart(maximized);
        else
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

    /// <summary>Waits (at most 30 seconds) until the copy that installed an update has ended.</summary>
    private static void WaitForExit(int processId)
    {
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(processId);
            old.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Deletes the old exe an update left behind (in the background: the old copy may still be ending).</summary>
    private static void CleanUpAfterUpdate()
    {
        if (Environment.ProcessPath is not { } exe || !Updates.UpdateSource.CanUpdateItself)
            return;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5 && !Updates.SelfUpdate.CleanUp(exe); attempt++)
                await Task.Delay(TimeSpan.FromSeconds(2));
        });
    }

    /// <summary>
    /// After an update put the new exe in place: closes the window (the session is saved as on every exit) and
    /// starts the new exe when the app has ended. False when closing was cancelled (unsaved Notepad# files).
    /// </summary>
    internal bool RestartAfterUpdate()
    {
        _restartAfterExit = true;
        ExitApp();
        if (_window is { IsClosed: false })
        {
            _restartAfterExit = false;
            return false;
        }
        return true;
    }

    /// <summary>Starts the (new) exe with this copy's options, not its folders: the session restores the tabs.</summary>
    private void StartNewCopy()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string arg in _args.Where(a => a.StartsWith("--", StringComparison.Ordinal)
                     && !a.StartsWith("--after-update=", StringComparison.OrdinalIgnoreCase)))
            start.ArgumentList.Add(arg);
        start.ArgumentList.Add($"--after-update={Environment.ProcessId}");
        try
        {
            System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The new version is in place; the next start by hand uses it.
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
        if (_restartAfterExit)
            StartNewCopy();
        base.OnExit(e);
    }
}
