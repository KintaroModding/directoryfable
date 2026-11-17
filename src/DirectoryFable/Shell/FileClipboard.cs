using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace DirectoryFable.Shell;

/// <summary>
/// Files on the Windows clipboard, in the same format as Explorer
/// (CF_HDROP + "Preferred DropEffect"), so copy/paste works both ways.
/// </summary>
public static class FileClipboard
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int DropEffectLink = 4;

    public static void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange([.. paths]);

        var data = new DataObject();
        data.SetFileDropList(list);
        int effect = cut ? DropEffectMove : DropEffectCopy | DropEffectLink; // Explorer uses 5 for copy.
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(effect)));

        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    /// <summary>Files on the clipboard and whether they were cut (move) or copied. Null: no files.</summary>
    public static (string[] Paths, bool Cut)? GetFiles()
    {
        IDataObject? data = null;
        Retry(() => data = Clipboard.GetDataObject());
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            return null;

        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return null;

        bool cut = false;
        if (data.GetData(PreferredDropEffect) is MemoryStream stream && stream.Length >= 4)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes);
            cut = (BitConverter.ToInt32(bytes) & DropEffectMove) != 0;
        }

        return (paths, cut);
    }

    public static void Clear() => Retry(Clipboard.Clear);

    // ---------- Cut files (shown faded, like Explorer) ----------

    private static HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the set of cut files changed (see <see cref="ClipboardChanged"/>).</summary>
    public static event Action? CutChanged;

    /// <summary>True when the file is on the clipboard as "cut" (by this app or by Explorer).</summary>
    public static bool IsCut(string path) => _cutPaths.Count > 0 && _cutPaths.Contains(path);

    /// <summary>
    /// Reads the clipboard again. Called by the main window on every clipboard change
    /// (WM_CLIPBOARDUPDATE), so a paste, a new copy or another app's copy ends the "cut" look.
    /// </summary>
    public static void ClipboardChanged()
    {
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetFiles() is { Cut: true } files)
                now.UnionWith(files.Paths.Select(p => p.TrimEnd('\\')));
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException)
        {
            // Clipboard locked by another app: keep the last state.
            return;
        }
        if (now.SetEquals(_cutPaths))
            return;
        _cutPaths = now;
        CutChanged?.Invoke();
    }

    /// <summary>The clipboard can be locked by another app for a moment; try a few times.</summary>
    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
