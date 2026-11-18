using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DirectoryFable.Shell;

/// <summary>What the progress window shows: a copy, a move, a delete to the Recycle Bin or a permanent delete.</summary>
public enum OperationKind
{
    Copy,
    Move,
    Recycle,
    Delete,
}

/// <summary>
/// DirectoryFable's own progress window for a copy, move or delete, put into the Windows copy engine in place of its
/// white progress dialog (IFileOperation.SetProgressDialog). The engine reports bytes, items and locations
/// here, and asks <see cref="GetOperationStatus"/> whether to go on or cancel. Pause: the engine stops a
/// copy with this window when it is told "paused" (measured), so instead its progress report waits until
/// Resume (or Cancel); the engine reports after every piece it copies. Windows' own questions (too big for the
/// Recycle Bin, file in use, permission) stay with Windows: while one is open the window does not appear, or it
/// goes behind the question (<see cref="OpenQuestion"/>). Calls come from the engine's threads; the window
/// (<see cref="Views.FileProgressWindow"/>) reads <see cref="Snapshot"/> a few times a second on the UI thread.
/// </summary>
[ComVisible(true)]
internal sealed class OperationProgress : OperationProgress.IOperationsProgressDialog
{
    private const int PDOPS_RUNNING = 1;
    private const int PDOPS_CANCELLED = 3;
    private const uint PDM_PREFLIGHT = 0x2;
    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>The window shows only for operations that take longer than this.</summary>
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(600);

    private readonly Lock _lock = new();
    private readonly Dispatcher _ui;
    private readonly OperationKind _kind;
    private readonly Stopwatch _clock = new();   // Runs while copying (Windows pauses it for its questions, we for Pause).
    private readonly Queue<(double Seconds, ulong Bytes, ulong Items)> _samples = new();
    private readonly ManualResetEventSlim _running = new(true);   // Reset while paused.
    private volatile int _status = PDOPS_RUNNING;
    private volatile bool _paused;
    private Views.FileProgressWindow? _window;
    private DispatcherTimer? _showTimer;

    private ulong _sizeDone, _sizeTotal, _itemsDone, _itemsTotal;
    private bool _preflight = true;
    private string _from = "", _fromPath = "", _to = "", _toPath = "", _item = "", _itemPath = "";
    private bool _enginePaused;

    public OperationProgress(Dispatcher ui, OperationKind kind)
    {
        _ui = ui;
        _kind = kind;
    }

    /// <summary>The thread that runs the operation (its question dialogs belong to it); set by the runner.</summary>
    public uint EngineThread { get; set; }

    /// <summary>A question dialog of the engine that is open now (a "#32770" window of its thread), or zero.</summary>
    public IntPtr OpenQuestion()
    {
        if (EngineThread == 0)
            return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(EngineThread, (hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(32);
            if (IsWindowVisible(hwnd) && GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "#32770")
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);

    /// <summary>What the window shows, read on the UI thread.</summary>
    public sealed record State(OperationKind Kind, bool Counting, bool Paused, ulong SizeDone, ulong SizeTotal, ulong ItemsDone, ulong ItemsTotal,
        string From, string FromPath, string To, string ToPath, string Item, string ItemPath, TimeSpan Elapsed, double CurrentSpeed, double AverageSpeed,
        double ItemsPerSecond, double AverageItemsPerSecond);

    public State Snapshot()
    {
        lock (_lock)
        {
            double seconds = _clock.Elapsed.TotalSeconds;
            double average = seconds > 0.5 ? _sizeDone / seconds : 0;
            // Current speed: the bytes (and items) of the last few seconds (sampled by the window's timer).
            _samples.Enqueue((seconds, _sizeDone, _itemsDone));
            while (_samples.Count > 2 && seconds - _samples.Peek().Seconds > 3)
                _samples.Dequeue();
            var first = _samples.Peek();
            double span = seconds - first.Seconds;
            double current = span > 0.4 ? (_sizeDone - first.Bytes) / span : average;
            double items = span > 0.4 ? (_itemsDone - first.Items) / span : seconds > 0.5 ? _itemsDone / seconds : 0;
            double averageItems = seconds > 0.5 ? _itemsDone / seconds : 0;
            return new State(_kind, _preflight, _paused, _sizeDone, _sizeTotal, _itemsDone, _itemsTotal,
                _from, _fromPath, _to, _toPath, _item, _itemPath, _clock.Elapsed, current, average, items, averageItems);
        }
    }

    /// <summary>Pause button: the engine's next progress report waits until Resume.</summary>
    public void TogglePause()
    {
        lock (_lock)
        {
            if (_status == PDOPS_CANCELLED)
                return;
            _paused = !_paused;
            if (_paused)
            {
                _running.Reset();
                _clock.Stop();
            }
            else
            {
                _running.Set();
                if (!_enginePaused)
                    _clock.Start();
            }
        }
    }

    /// <summary>Cancel button (or closing the window): the engine stops; what was copied stays.</summary>
    public void Cancel()
    {
        _status = PDOPS_CANCELLED;
        _running.Set(); // A paused engine goes on to its next check, which says "cancelled".
    }

    /// <summary>While paused, the engine's thread waits here (it is not the UI thread).</summary>
    private void WaitWhilePaused()
    {
        while (!_running.Wait(250) && _status != PDOPS_CANCELLED)
        {
        }
    }

    // ---------- IOperationsProgressDialog (called by the copy engine) ----------

    public void StartProgressDialog(IntPtr hwndOwner, uint flags)
    {
        lock (_lock)
            _clock.Restart();
        _ui.BeginInvoke(() =>
        {
            _showTimer = new DispatcherTimer { Interval = ShowDelay };
            _showTimer.Tick += (_, _) =>
            {
                // A question of Windows is open: wait, so the window does not cover it.
                if (OpenQuestion() != IntPtr.Zero)
                {
                    _showTimer.Interval = TimeSpan.FromMilliseconds(250);
                    return;
                }
                _showTimer.Stop();
                if (_status != PDOPS_CANCELLED && _window is null)
                {
                    _window = new Views.FileProgressWindow(this);
                    _window.Show();
                }
            };
            _showTimer.Start();
        });
    }

    public void StopProgressDialog()
    {
        lock (_lock)
            _clock.Stop();
        _running.Set();
        _ui.BeginInvoke(() =>
        {
            _showTimer?.Stop();
            _window?.Finish();
            _window = null;
        });
    }

    public void SetOperation(uint action)
    {
    }

    public void SetMode(uint mode)
    {
        lock (_lock)
            _preflight = (mode & PDM_PREFLIGHT) != 0;
    }

    public void UpdateProgress(ulong pointsCurrent, ulong pointsTotal, ulong sizeCurrent, ulong sizeTotal, ulong itemsCurrent, ulong itemsTotal)
    {
        lock (_lock)
        {
            _sizeDone = sizeCurrent;
            _sizeTotal = sizeTotal;
            _itemsDone = itemsCurrent;
            _itemsTotal = itemsTotal;
        }
        WaitWhilePaused();
    }

    public void UpdateLocations(FileOperations.IShellItem? source, FileOperations.IShellItem? target, FileOperations.IShellItem? item)
    {
        string from = Name(source, SIGDN_NORMALDISPLAY), fromPath = Name(source, SIGDN_FILESYSPATH);
        string to = Name(target, SIGDN_NORMALDISPLAY), toPath = Name(target, SIGDN_FILESYSPATH);
        string current = Name(item, SIGDN_NORMALDISPLAY), currentPath = Name(item, SIGDN_FILESYSPATH);
        lock (_lock)
        {
            if (from.Length > 0)
                (_from, _fromPath) = (from, fromPath);
            if (to.Length > 0)
                (_to, _toPath) = (to, toPath);
            if (current.Length > 0)
                (_item, _itemPath) = (current, currentPath);
        }
        WaitWhilePaused();
    }

    public void ResetTimer()
    {
        lock (_lock)
        {
            _clock.Restart();
            _samples.Clear();
        }
    }

    /// <summary>Windows pauses the timer while it asks a question (replace or skip, elevation).</summary>
    public void PauseTimer()
    {
        lock (_lock)
        {
            _enginePaused = true;
            _clock.Stop();
        }
    }

    public void ResumeTimer()
    {
        lock (_lock)
        {
            _enginePaused = false;
            if (_status == PDOPS_RUNNING)
                _clock.Start();
        }
    }

    public void GetMilliseconds(out ulong elapsed, out ulong remaining)
    {
        var state = Snapshot();
        elapsed = (ulong)state.Elapsed.TotalMilliseconds;
        double speed = state.AverageSpeed;
        remaining = speed > 0 && state.SizeTotal > state.SizeDone ? (ulong)((state.SizeTotal - state.SizeDone) / speed * 1000) : 0;
    }

    public void GetOperationStatus(out int status) => status = _status;

    private static string Name(FileOperations.IShellItem? item, uint sigdn)
    {
        if (item is null)
            return "";
        try
        {
            item.GetDisplayName(sigdn, out IntPtr name);
            try
            {
                return Marshal.PtrToStringUni(name) ?? "";
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        catch (COMException)
        {
            return ""; // No file system path (a virtual folder).
        }
    }

    // Method order must match shobjidl_core.h exactly.
    [ComImport, Guid("0C9FB851-E5C9-43EB-A370-F0677B13874C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    public interface IOperationsProgressDialog
    {
        void StartProgressDialog(IntPtr hwndOwner, uint flags);
        void StopProgressDialog();
        void SetOperation(uint action);
        void SetMode(uint mode);
        void UpdateProgress(ulong ullPointsCurrent, ulong ullPointsTotal, ulong ullSizeCurrent, ulong ullSizeTotal,
            ulong ullItemsCurrent, ulong ullItemsTotal);
        void UpdateLocations(FileOperations.IShellItem? psiSource, FileOperations.IShellItem? psiTarget, FileOperations.IShellItem? psiItem);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
        void GetMilliseconds(out ulong pullElapsed, out ulong pullRemaining);
        void GetOperationStatus(out int popstatus);
    }
}
