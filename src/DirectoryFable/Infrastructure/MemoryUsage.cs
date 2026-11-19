using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// DirectoryFable's own memory use for the status bar: the private working set (the RAM only this process uses),
/// the same number as Task Manager's "Memory" column. Read every 2 seconds while someone listens.
/// </summary>
public static class MemoryUsage
{
    private static DispatcherTimer? _timer;
    private static event Action<long>? ChangedCore;

    /// <summary>Raised on the UI thread with the bytes in use (at once for a new listener, then every 2 s).</summary>
    public static event Action<long>? Changed
    {
        add
        {
            ChangedCore += value;
            value?.Invoke(Current());
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) => ChangedCore?.Invoke(Current());
                _timer.Start();
            }
        }
        remove => ChangedCore -= value;
    }

    /// <summary>The private working set in bytes; 0 when Windows does not tell.</summary>
    public static long Current()
    {
        var counters = new ProcessMemoryCountersEx2 { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        return K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb) ? (long)counters.PrivateWorkingSetSize : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);
}
