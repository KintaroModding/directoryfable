using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DirectoryFable.Models;
using DirectoryFable.Player;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// The picture of a tile in the file list's tile view: Windows' thumbnail of the item, filling the picture box,
/// or its icon (at most 96 DIP, in the middle). Loads when the tile appears or its item or size changes, and
/// stops when the tile goes (scrolled away). <see cref="Duration"/> holds a video's length for the badge.
/// </summary>
public sealed class ThumbImage : Image
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(FileItem),
        typeof(ThumbImage), new PropertyMetadata(null, (d, _) => ((ThumbImage)d).Reload()));

    /// <summary>The width of the picture box in DIP (the thumbnail is asked for in a matching size).</summary>
    public static readonly DependencyProperty BoxWidthProperty = DependencyProperty.Register(nameof(BoxWidth), typeof(double),
        typeof(ThumbImage), new PropertyMetadata(256.0, (d, _) => ((ThumbImage)d).Reload()));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(string),
        typeof(ThumbImage), new PropertyMetadata(null));

    public FileItem? Item
    {
        get => (FileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public double BoxWidth
    {
        get => (double)GetValue(BoxWidthProperty);
        set => SetValue(BoxWidthProperty, value);
    }

    public string? Duration
    {
        get => (string?)GetValue(DurationProperty);
        private set => SetValue(DurationProperty, value);
    }

    /// <summary>Thumbnail sizes asked from Windows (device pixels): few steps, so the size slider does not ask for every pixel.</summary>
    private static readonly int[] Steps = [128, 256, 384, 512, 768, 1024];

    private CancellationTokenSource? _cancel;
    private (FileItem Item, int Size)? _shown;

    public ThumbImage()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) =>
        {
            _cancel?.Cancel();
            _cancel = null;
        };
    }

    private async void Reload()
    {
        if (!IsLoaded || Item is not { } item)
        {
            if (Item is null)
            {
                _cancel?.Cancel();
                Source = null;
                Duration = null;
                _shown = null;
            }
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int needed = (int)Math.Ceiling(BoxWidth * scale);
        int size = Steps.FirstOrDefault(s => s >= needed, Steps[^1]);
        if (_shown is { } shown && shown.Item == item && shown.Size == size)
            return;

        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        if (_shown?.Item != item)
        {
            Source = null;
            Duration = null;
        }
        try
        {
            bool video = !item.IsDirectory && VideoFileTypes.IsVideo(item.FullPath);
            var result = await ShellThumbnails.GetAsync(item.FullPath, item.DateModified, size, (int)Math.Ceiling(96 * scale), video, cancel.Token);
            if (cancel.IsCancellationRequested || Item != item)
                return;
            _shown = (item, size);
            Source = result?.Image;
            Duration = result?.Duration;
            // Icons stay icon-sized in the middle of the box; thumbnails fill it.
            MaxWidth = MaxHeight = result is { IsThumbnail: false } ? 96 : double.PositiveInfinity;
        }
        catch (OperationCanceledException)
        {
            // The tile went or shows another item.
        }
    }
}
