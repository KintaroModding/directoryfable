using System.IO;
using System.Windows.Threading;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// Watches one folder (not its subfolders) and calls <c>changed</c> on the UI thread,
/// at most every 250 ms while changes keep coming (a big copy does not cause hundreds
/// of reloads, but the list still moves along).
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _timer;
    private readonly Action _changed;

    /// <param name="fileFilter">Only this file name (a ZIP shown as a folder); null = everything.</param>
    public FolderWatcher(string folder, Dispatcher dispatcher, Action changed, string? fileFilter = null)
    {
        _changed = changed;
        _timer = new DispatcherTimer(Throttle, DispatcherPriority.Background, OnTimer, dispatcher);
        _timer.Stop();

        try
        {
            _watcher = new FileSystemWatcher(folder, fileFilter ?? "*")
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.CreationTime,
            };
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Changed += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => Schedule(dispatcher); // Buffer overflow etc.: just reload.
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some locations cannot be watched (certain network shares, devices). F5 still works there.
            _watcher?.Dispose();
            _watcher = null;
        }

        void OnEvent(object? sender, FileSystemEventArgs e) => Schedule(dispatcher);
    }

    private void Schedule(Dispatcher dispatcher) =>
        dispatcher.BeginInvoke(() =>
        {
            // Throttle, not debounce: the first change starts the timer, later ones join it.
            if (!_timer.IsEnabled)
                _timer.Start();
        });

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer.Stop();
        _changed();
    }

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
    }
}
