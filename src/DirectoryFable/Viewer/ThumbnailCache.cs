using System.Windows.Media.Imaging;

namespace DirectoryFable.Viewer;

/// <summary>
/// Thumbnails for AstraViewer's strip and history: decoded small (128 device pixels high), at
/// most two at a time, the last 400 kept in memory. Used from the UI thread only. A thumbnail
/// whose row scrolled out of view is cancelled before it is decoded, so fast scrolling through
/// thousands of images does not queue them all.
/// </summary>
public static class ThumbnailCache
{
    private const int Height = 128;
    private const int MaxEntries = 400;

    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Node)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new(); // Most recently used first.

    /// <summary>The thumbnail, or null when the file cannot be decoded. Throws OperationCanceledException when cancelled.</summary>
    public static async Task<BitmapSource?> GetAsync(string path, CancellationToken token)
    {
        if (Cache.TryGetValue(path, out var hit))
        {
            Order.Remove(hit.Node);
            Order.AddFirst(hit.Node);
            return hit.Image;
        }

        await Gate.WaitAsync(token);
        BitmapSource? image;
        try
        {
            token.ThrowIfCancellationRequested();
            image = await Task.Run(() =>
            {
                try
                {
                    return ImageLoader.LoadThumbnail(path, Height);
                }
                catch (Exception)
                {
                    return null; // Missing, locked or broken: no thumbnail (the row shows a placeholder).
                }
            }, token);
        }
        finally
        {
            Gate.Release();
        }

        if (image is not null && !Cache.ContainsKey(path))
        {
            Cache[path] = (image, Order.AddFirst(path));
            while (Order.Count > MaxEntries && Order.Last is { } oldest)
            {
                Cache.Remove(oldest.Value);
                Order.RemoveLast();
            }
        }
        return image;
    }

    /// <summary>F5 in AstraViewer: changed files get new thumbnails.</summary>
    public static void Clear()
    {
        Cache.Clear();
        Order.Clear();
    }
}
