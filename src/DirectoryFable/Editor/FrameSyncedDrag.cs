using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace DirectoryFable.Editor;

/// <summary>
/// Frame-synced dragging for the Notepad# minimap and scroll bar and the SonnetSearch scroll rail.
/// WPF handles each mouse move whenever it arrives, so during a very fast drag some frames see no
/// new mouse position (the text stands still for a frame) and the next frame jumps twice as far:
/// visible stutter. Like a browser, the drag instead reads the cursor once per frame, just before
/// WPF draws (CompositionTarget.Rendering), so every frame moves by the distance the mouse moved.
/// Measured with a 150 ms top-to-bottom drag: frames without movement 36 -> ~0 (minimap) and
/// 47 -> 0-2 (scroll bar). The callers must still change something on each mouse event (the
/// scroll bar's thumb does, the minimap scrolls), or WPF draws only every 8-10 ms.
/// </summary>
internal sealed class FrameSyncedDrag
{
    private readonly Visual _relativeTo;
    private readonly Action<double> _onFrame;
    private bool _running;
    private double _lastY = double.NaN;

    /// <param name="relativeTo">The cursor Y passed to <paramref name="onFrame"/> is relative to this element (DIP).</param>
    /// <param name="onFrame">Called once per frame while running, when the cursor Y changed.</param>
    public FrameSyncedDrag(Visual relativeTo, Action<double> onFrame)
    {
        _relativeTo = relativeTo;
        _onFrame = onFrame;
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _lastY = double.NaN;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Stops; applies the last cursor position once more, so the drag ends exactly there.</summary>
    public void Stop()
    {
        if (!_running)
            return;
        OnRendering(null, EventArgs.Empty);
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (CursorY() is not double y || y == _lastY)
            return;
        _lastY = y;
        _onFrame(y);
    }

    /// <summary>The cursor's current Y (fresh from Windows, not WPF's last mouse event), or null.</summary>
    public double? CursorY()
    {
        if (!GetCursorPos(out var point) || PresentationSource.FromVisual(_relativeTo) is null)
            return null;
        return _relativeTo.PointFromScreen(new Point(point.X, point.Y)).Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
