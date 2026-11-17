using System.IO;
using System.Windows.Media.Imaging;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Viewer;

/// <summary>An image in AstraViewer's thumbnail strip or history list.</summary>
public sealed class ViewerItem(string path) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private bool _isMissing;
    private CancellationTokenSource? _loading;

    public string FullPath { get; } = path;

    public string Name => Path.GetFileName(FullPath);

    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetField(ref _thumbnail, value);
    }

    /// <summary>The file is gone (history): shown red.</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set => SetField(ref _isMissing, value);
    }

    /// <summary>The row came into view: load its thumbnail (once).</summary>
    public async void RequestThumbnail()
    {
        if (Thumbnail is not null || _loading is not null)
            return;
        var loading = new CancellationTokenSource();
        _loading = loading;
        try
        {
            Thumbnail = await ThumbnailCache.GetAsync(FullPath, loading.Token);
        }
        catch (OperationCanceledException)
        {
            // Scrolled out of view before it was decoded.
        }
        finally
        {
            if (_loading == loading)
                _loading = null;
            loading.Dispose();
        }
    }

    /// <summary>The row left the view: a thumbnail that is not decoded yet is not needed now.</summary>
    public void CancelThumbnail()
    {
        _loading?.Cancel();
        _loading = null;
    }

    /// <summary>F5: load the thumbnail again when it is shown next.</summary>
    public void ResetThumbnail()
    {
        CancelThumbnail();
        Thumbnail = null;
    }
}
