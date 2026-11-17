using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Models;

namespace DirectoryFable.Shell;

/// <summary>
/// Per-file icons for file types whose icon is stored in the file itself (.exe, .lnk, .ico,
/// ...). The list first shows the fast icon by extension; this replaces it in the background,
/// so a folder full of programs (or a slow network folder) never blocks the UI.
/// </summary>
public static partial class ShellInfo
{
    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".url", ".scr", ".msc", ".cpl", ".appref-ms",
    };

    private static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".appref-ms",
    };

    private const int OwnIconBatchSize = 32;
    private const int OwnIconCacheLimit = 4000;

    /// <summary>Only used on the worker thread. Key: path + last write time (a rebuilt exe gets a new icon).</summary>
    private static readonly Dictionary<string, ImageSource?> OwnIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly BlockingCollection<Action> OwnIconQueue = StartOwnIconWorker();

    /// <summary>True when the row's real icon can differ from the icon of its extension.</summary>
    public static bool NeedsOwnIcon(FileItem item) =>
        !item.IsDirectory && !item.IsInArchive && !item.IsShare &&
        OwnIconExtensions.Contains(Path.GetExtension(item.Name));

    /// <summary>
    /// Loads the file's own icon for every item that needs one and sets <see cref="FileItem.Icon"/>.
    /// Call on the UI thread; items are updated there, in batches, until <paramref name="token"/> is cancelled.
    /// </summary>
    public static async Task LoadOwnIconsAsync(IEnumerable<FileItem> items, CancellationToken token)
    {
        var todo = items.Where(NeedsOwnIcon).ToList();
        for (int start = 0; start < todo.Count && !token.IsCancellationRequested; start += OwnIconBatchSize)
        {
            var batch = todo.Skip(start).Take(OwnIconBatchSize).Select(i => (i.FullPath, i.DateModified)).ToList();
            ImageSource?[] icons;
            try
            {
                icons = await RunOnIconWorker(() => batch.Select(b => token.IsCancellationRequested ? null : GetOwnIcon(b.FullPath, b.DateModified)).ToArray());
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (token.IsCancellationRequested)
                return;
            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i] is { } icon)
                    todo[start + i].Icon = icon;
            }
        }
    }

    /// <summary>Runs on the worker thread (STA: shell icon handlers expect it).</summary>
    private static ImageSource? GetOwnIcon(string path, DateTime modified)
    {
        string key = path + "|" + modified.Ticks;
        if (OwnIconCache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? icon = null;
        var info = new SHFILEINFO();
        // No SHGFI_USEFILEATTRIBUTES: read the file. LINKOVERLAY puts the arrow on any icon,
        // so only shortcut types get it (as in Explorer).
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        if (ShortcutExtensions.Contains(Path.GetExtension(path)))
            flags |= SHGFI_LINKOVERLAY;
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero &&
            info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze(); // Frozen: usable on the UI thread.
                icon = bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        if (OwnIconCache.Count >= OwnIconCacheLimit)
            OwnIconCache.Clear();
        OwnIconCache[key] = icon;
        return icon;
    }

    private static Task<T> RunOnIconWorker<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OwnIconQueue.Add(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private static BlockingCollection<Action> StartOwnIconWorker()
    {
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "OwnIconLoader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return queue;
    }

    private const uint SHGFI_LINKOVERLAY = 0x000008000;
}
