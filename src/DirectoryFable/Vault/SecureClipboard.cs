using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DirectoryFable.Vault;

/// <summary>
/// KeeSeek's copies of user names and passwords: put on the clipboard with Windows' privacy marks, so they stay
/// out of the clipboard history (Win+V), out of cloud clipboard sync and out of clipboard monitor apps; cleared
/// after the set time - but only while the clipboard still holds this very text (something copied later stays).
/// Also cleared when KeeSeek locks or DirectoryFable ends.
/// </summary>
public static class SecureClipboard
{
    private static DispatcherTimer? _timer;
    private static string? _value;
    private static DateTime _clearAt;

    /// <summary>Seconds left before the clipboard is cleared; 0 = cleared (or replaced by something else).</summary>
    public static event Action<int>? Countdown;

    public static bool Copy(string text, int seconds)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (COMException)
        {
            return false; // Another app holds the clipboard.
        }
        _value = text;
        _clearAt = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
        Countdown?.Invoke(SecondsLeft());
        return true;
    }

    private static int SecondsLeft() => Math.Max(0, (int)Math.Ceiling((_clearAt - DateTime.UtcNow).TotalSeconds));

    private static void Tick()
    {
        if (_value is null)
        {
            _timer?.Stop();
            return;
        }
        int left = SecondsLeft();
        if (left <= 0 || !StillOurs())
        {
            ClearIfOurs();
            return;
        }
        Countdown?.Invoke(left);
    }

    private static bool StillOurs()
    {
        try
        {
            return Clipboard.ContainsText() && Clipboard.GetText() == _value;
        }
        catch (COMException)
        {
            return true; // Busy: try again next second.
        }
    }

    /// <summary>Clears the clipboard if it still holds KeeSeek's copy.</summary>
    public static void ClearIfOurs()
    {
        if (_value is null)
            return;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _value)
                Clipboard.Clear();
        }
        catch (COMException)
        {
            // Another app holds the clipboard right now.
        }
        _value = null;
        _timer?.Stop();
        Countdown?.Invoke(0);
    }
}
