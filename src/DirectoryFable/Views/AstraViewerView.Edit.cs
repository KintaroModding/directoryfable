using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// AstraViewer's simple picture edits: crop (a frame with handles and an aspect ratio), resize, the view's rotation
/// written into the file, Save (the original goes to the Recycle Bin first) and Save as (JPEG, PNG, WebP, BMP, TIFF, GIF).
/// Edits work on the shown pixels; Ctrl+Z goes back one edit. Not for PDFs, animated or multi-size images, or images
/// too large to be shown at full size.
/// </summary>
public partial class AstraViewerView
{
    private const int JpegQuality = 92;

    private BitmapSource? _edited; // The pixels after crop / resize; null = as loaded.
    private readonly Stack<(BitmapSource? Edited, int Rotation)> _undo = new();

    // Crop: the frame in pixels of the shown picture (rotation is written into the pixels first).
    private bool _cropping;
    private Rect _crop;
    private (BitmapSource? Edited, int Rotation) _beforeCrop;
    private CropDrag? _cropDrag;

    private sealed record CropDrag(bool Left, bool Top, bool Right, bool Bottom, bool Move, Point StartPixel, Rect StartRect);

    /// <summary>The picture as shown now (edits applied, rotation not).</summary>
    private BitmapSource? ShownPixels => _edited ?? _image?.Image;

    /// <summary>Crop / resize done and not saved (a rotation alone stays a view change unless saved).</summary>
    private bool IsEdited => _edited is not null;

    private bool HasChanges => _edited is not null || _rotation != 0;

    private bool CanEdit => _image is { Reduced: false } image && image.Details.Frames <= 1 && !IsPdfShown && !_animatedWebP;

    /// <summary>The shown file is an animated WebP (Windows shows its first frame; saving would lose the animation).</summary>
    private bool _animatedWebP;

    /// <summary>A new picture (or none): no edits, no crop frame.</summary>
    private void ResetEdits()
    {
        _edited = null;
        _undo.Clear();
        EndCrop();
        UpdateEditButtons();
    }

    private void UpdateEditButtons()
    {
        EditBar.Visibility = _image is not null && !IsPdfShown ? Visibility.Visible : Visibility.Collapsed;
        bool can = CanEdit && !_cropping;
        CropButton.IsEnabled = ResizeButton.IsEnabled = SaveAsButton.IsEnabled = can;
        bool encoder = CurrentPath is { } path && CanWrite(path);
        SaveButton.IsEnabled = can && HasChanges && encoder;
        ToolTipService.SetShowOnDisabled(SaveButton, true);
        SaveButton.ToolTip = CurrentPath is { } shown && !encoder
            ? $"Windows cannot write {Path.GetExtension(shown).TrimStart('.').ToUpperInvariant()} files: use Save as"
            : "Save the changes into the file (Ctrl+S); the original goes to the Recycle Bin first";
        ToolTipService.SetShowOnDisabled(CropButton, true);
        CropButton.ToolTip = _image is not null && !CanEdit && !IsPdfShown
            ? "Not for animated or multi-size images, or images too large to show at full size"
            : "Crop: drag a frame on the picture (or its handles), Enter keeps the frame, Esc cancels";
    }

    /// <summary>The pixel size the picture has with its edits and rotation (what Save writes).</summary>
    private (int Width, int Height) OutputSize()
    {
        if (ShownPixels is not { } pixels || _image is null)
            return (0, 0);
        (int width, int height) = _edited is null ? (_image.Details.PixelWidth, _image.Details.PixelHeight) : (pixels.PixelWidth, pixels.PixelHeight);
        return _rotation % 180 != 0 ? (height, width) : (width, height);
    }

    private void PushUndo()
    {
        _undo.Push((_edited, _rotation));
    }

    /// <summary>The rotation goes into the pixels (before crop and resize, which work on what is shown).</summary>
    private void BakeRotation()
    {
        if (_rotation == 0 || ShownPixels is not { } pixels)
            return;
        _edited = Rotated(pixels, _rotation);
        _rotation = 0;
    }

    private void AfterEdit()
    {
        _fit = true;
        _pan = default;
        if (ShownPixels is { } pixels)
            Picture.Source = pixels;
        UpdateView();
        UpdateDetails();
        UpdateEditButtons();
    }

    private void UndoEdit()
    {
        if (_undo.Count == 0)
            return;
        (_edited, _rotation) = _undo.Pop();
        AfterEdit();
    }

    // ---------- Crop ----------

    private void Crop_Click(object sender, RoutedEventArgs e) => BeginCrop();

    private void BeginCrop()
    {
        if (!CanEdit || _cropping)
            return;
        _beforeCrop = (_edited, _rotation);
        BakeRotation();
        if (ShownPixels is not { } pixels)
            return;
        _cropping = true;
        _crop = new Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight);
        if (CropAspectBox.SelectedIndex < 0)
            CropAspectBox.SelectedIndex = 0;
        ApplyCropAspect();
        CropLayer.Visibility = CropBar.Visibility = Visibility.Visible;
        AfterEdit();
        Focus();
    }

    /// <summary>Leaves crop mode; <paramref name="restore"/> puts back the rotation that crop wrote into the pixels.</summary>
    private void EndCrop(bool restore = false)
    {
        if (!_cropping)
            return;
        _cropping = false;
        _cropDrag = null;
        if (restore)
            (_edited, _rotation) = _beforeCrop;
        CropLayer.Visibility = CropBar.Visibility = Visibility.Collapsed;
        Stage.Cursor = null;
    }

    private void ApplyCrop()
    {
        if (!_cropping || ShownPixels is not { } pixels)
            return;
        var rect = PixelRect(_crop, pixels);
        var before = _beforeCrop;
        if (rect.Width == pixels.PixelWidth && rect.Height == pixels.PixelHeight)
        {
            EndCrop(restore: true); // The whole picture: nothing to cut, everything as before.
            AfterEdit();
            return;
        }
        EndCrop();
        _undo.Push(before);
        var cropped = new CroppedBitmap(pixels, rect);
        cropped.Freeze();
        _edited = cropped;
        AfterEdit();
    }

    private void CropApply_Click(object sender, RoutedEventArgs e) => ApplyCrop();

    private void CropCancel_Click(object sender, RoutedEventArgs e)
    {
        EndCrop(restore: true);
        AfterEdit();
    }

    /// <summary>The crop bar's own background must not start a frame on the picture below it.</summary>
    private void CropBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CropAspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_cropping)
        {
            ApplyCropAspect();
            UpdateCropOverlay();
        }
        Focus();
    }

    /// <summary>Width / height of the chosen aspect (0 = free).</summary>
    private double CropAspect()
    {
        if (CropAspectBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
            return 0;
        if (ratio < 0 && ShownPixels is { } pixels)
            return (double)pixels.PixelWidth / pixels.PixelHeight;
        return ratio;
    }

    /// <summary>A new aspect: the largest frame of that shape inside the current one, around its middle.</summary>
    private void ApplyCropAspect()
    {
        double ratio = CropAspect();
        if (ratio <= 0)
            return;
        double width = _crop.Width, height = _crop.Height;
        if (width / height > ratio)
            width = height * ratio;
        else
            height = width / ratio;
        _crop = new Rect(_crop.X + (_crop.Width - width) / 2, _crop.Y + (_crop.Height - height) / 2, width, height);
    }

    private Rect Bounds() => ShownPixels is { } pixels ? new Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight) : Rect.Empty;

    private static Int32Rect PixelRect(Rect crop, BitmapSource pixels)
    {
        int x = Math.Clamp((int)Math.Round(crop.Left), 0, pixels.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(crop.Top), 0, pixels.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Round(crop.Right), x + 1, pixels.PixelWidth);
        int bottom = Math.Clamp((int)Math.Round(crop.Bottom), y + 1, pixels.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    // Picture pixels <-> stage points, through the picture's own transform (rotation is 0 while cropping).
    private Point PixelToStage(Point pixel)
    {
        double scale = DpiScale;
        return (Picture.RenderTransform?.Value ?? Matrix.Identity).Transform(new Point(pixel.X / scale, pixel.Y / scale));
    }

    private Point StageToPixel(Point stage)
    {
        var matrix = Picture.RenderTransform?.Value ?? Matrix.Identity;
        if (!matrix.HasInverse)
            return default;
        matrix.Invert();
        var local = matrix.Transform(stage);
        double scale = DpiScale;
        return new Point(local.X * scale, local.Y * scale);
    }

    private Rect CropOnStage() => new(PixelToStage(_crop.TopLeft), PixelToStage(_crop.BottomRight));

    /// <summary>Which edges of the frame are under the mouse (8 DIP), or inside / outside.</summary>
    private (bool Left, bool Top, bool Right, bool Bottom, bool Inside) CropHit(Point stage)
    {
        var frame = CropOnStage();
        const double Grip = 8;
        bool nearX = stage.X >= frame.Left - Grip && stage.X <= frame.Right + Grip;
        bool nearY = stage.Y >= frame.Top - Grip && stage.Y <= frame.Bottom + Grip;
        bool left = nearY && Math.Abs(stage.X - frame.Left) <= Grip;
        bool right = nearY && !left && Math.Abs(stage.X - frame.Right) <= Grip;
        bool top = nearX && Math.Abs(stage.Y - frame.Top) <= Grip;
        bool bottom = nearX && !top && Math.Abs(stage.Y - frame.Bottom) <= Grip;
        return (left, top, right, bottom, frame.Contains(stage));
    }

    private void CropMouseDown(MouseButtonEventArgs e)
    {
        var stage = e.GetPosition(Stage);
        var hit = CropHit(stage);
        if (e.ClickCount == 2 && hit.Inside)
        {
            ApplyCrop();
            return;
        }
        var pixel = StageToPixel(stage);
        bool edge = hit.Left || hit.Top || hit.Right || hit.Bottom;
        bool whole = _crop.Size == Bounds().Size; // The first frame is the whole picture: a drag inside draws a new one.
        if ((!edge && !hit.Inside) || (whole && !edge))
        {
            // Outside the frame (or inside the whole-picture frame): a new frame from here.
            pixel = Clamp(pixel, Bounds());
            _crop = new Rect(pixel, pixel);
            _cropDrag = new CropDrag(false, false, true, true, false, pixel, _crop);
        }
        else
        {
            _cropDrag = new CropDrag(hit.Left, hit.Top, hit.Right, hit.Bottom, !edge, pixel, _crop);
        }
        Stage.CaptureMouse();
        UpdateCropOverlay();
    }

    private void CropMouseMove(MouseEventArgs e)
    {
        var stage = e.GetPosition(Stage);
        if (_cropDrag is not { } drag)
        {
            var hit = CropHit(stage);
            Stage.Cursor = (hit.Left && hit.Top) || (hit.Right && hit.Bottom) ? Cursors.SizeNWSE
                : (hit.Right && hit.Top) || (hit.Left && hit.Bottom) ? Cursors.SizeNESW
                : hit.Left || hit.Right ? Cursors.SizeWE
                : hit.Top || hit.Bottom ? Cursors.SizeNS
                : hit.Inside ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        var bounds = Bounds();
        var pixel = StageToPixel(stage);
        var start = drag.StartRect;
        double ratio = CropAspect();
        if (drag.Move)
        {
            double x = Math.Clamp(start.X + pixel.X - drag.StartPixel.X, 0, bounds.Width - start.Width);
            double y = Math.Clamp(start.Y + pixel.Y - drag.StartPixel.Y, 0, bounds.Height - start.Height);
            _crop = new Rect(x, y, start.Width, start.Height);
        }
        else if ((drag.Left || drag.Right) && (drag.Top || drag.Bottom))
        {
            // A corner (or a new frame): the opposite corner stays.
            var anchor = new Point(drag.Left ? start.Right : start.Left, drag.Top ? start.Bottom : start.Top);
            _crop = FromAnchor(anchor, pixel, ratio, bounds);
        }
        else if (drag.Left || drag.Right)
        {
            double edge = Math.Clamp(pixel.X, bounds.Left, bounds.Right);
            double other = drag.Left ? start.Right : start.Left;
            double width = Math.Max(1, Math.Abs(edge - other));
            double height = start.Height;
            if (ratio > 0)
            {
                height = width / ratio;
                if (height > bounds.Height)
                {
                    height = bounds.Height;
                    width = height * ratio;
                }
            }
            double left = edge < other ? other - width : other;
            double top = Math.Clamp(start.Y + start.Height / 2 - height / 2, 0, bounds.Height - height);
            _crop = new Rect(left, top, width, height);
        }
        else
        {
            double edge = Math.Clamp(pixel.Y, bounds.Top, bounds.Bottom);
            double other = drag.Top ? start.Bottom : start.Top;
            double height = Math.Max(1, Math.Abs(edge - other));
            double width = start.Width;
            if (ratio > 0)
            {
                width = height * ratio;
                if (width > bounds.Width)
                {
                    width = bounds.Width;
                    height = width / ratio;
                }
            }
            double top = edge < other ? other - height : other;
            double left = Math.Clamp(start.X + start.Width / 2 - width / 2, 0, bounds.Width - width);
            _crop = new Rect(left, top, width, height);
        }
        UpdateCropOverlay();
    }

    /// <summary>The frame from a fixed corner to the mouse, in the aspect (if any), inside the picture.</summary>
    private static Rect FromAnchor(Point anchor, Point to, double ratio, Rect bounds)
    {
        double dx = to.X - anchor.X, dy = to.Y - anchor.Y;
        double width = Math.Abs(dx), height = Math.Abs(dy);
        double maxWidth = dx >= 0 ? bounds.Right - anchor.X : anchor.X - bounds.Left;
        double maxHeight = dy >= 0 ? bounds.Bottom - anchor.Y : anchor.Y - bounds.Top;
        if (ratio > 0)
        {
            if (width / ratio > height)
                height = width / ratio;
            else
                width = height * ratio;
            double shrink = Math.Min(1, Math.Min(width > 0 ? maxWidth / width : 1, height > 0 ? maxHeight / height : 1));
            width *= shrink;
            height *= shrink;
        }
        else
        {
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
        }
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        return new Rect(dx >= 0 ? anchor.X : anchor.X - width, dy >= 0 ? anchor.Y : anchor.Y - height, width, height);
    }

    private static Point Clamp(Point point, Rect bounds) =>
        new(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));

    /// <summary>The darkened rest, the frame, its eight handles and its size in pixels.</summary>
    private void UpdateCropOverlay()
    {
        if (!_cropping || ShownPixels is not { } pixels)
            return;
        var frame = CropOnStage();
        CropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, Stage.ActualWidth, Stage.ActualHeight)), new RectangleGeometry(frame) },
        };
        CropFrame.Data = new RectangleGeometry(frame);
        var handles = new GeometryGroup();
        const double Half = 4;
        foreach (double x in new[] { frame.Left, frame.Left + frame.Width / 2, frame.Right })
        {
            foreach (double y in new[] { frame.Top, frame.Top + frame.Height / 2, frame.Bottom })
            {
                if (x == frame.Left + frame.Width / 2 && y == frame.Top + frame.Height / 2)
                    continue; // No handle in the middle.
                handles.Children.Add(new RectangleGeometry(new Rect(x - Half, y - Half, 2 * Half, 2 * Half)));
            }
        }
        CropHandles.Data = handles;
        var size = PixelRect(_crop, pixels);
        CropSizeText.Text = $"{size.Width:N0} × {size.Height:N0} px";
        Canvas.SetLeft(CropSizeBox, Math.Max(0, frame.Left));
        Canvas.SetTop(CropSizeBox, frame.Top >= 26 ? frame.Top - 24 : frame.Top + 6);
    }

    // ---------- Resize ----------

    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || _cropping)
            return;
        var (width, height) = OutputSize();
        if (ResizeDialog.Ask(Window.GetWindow(this), width, height) is not { } size)
            return;
        var (newWidth, newHeight) = size;
        PushUndo();
        BakeRotation();
        _edited = Resized(ShownPixels!, newWidth, newHeight);
        AfterEdit();
    }

    /// <summary>High-quality scaling (WPF's Fant filter for smaller pictures).</summary>
    private static BitmapSource Resized(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return WithDpiOf(source, target);
    }

    /// <summary>
    /// A frozen copy of a drawn picture with the DPI value of <paramref name="source"/>: drawing gives 96 dpi, but the
    /// picture keeps its printing size, as crop and rotation do.
    /// </summary>
    private static BitmapSource WithDpiOf(BitmapSource source, BitmapSource drawn)
    {
        int stride = (drawn.PixelWidth * drawn.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[(long)stride * drawn.PixelHeight];
        drawn.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(drawn.PixelWidth, drawn.PixelHeight, source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96, drawn.Format, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    private static BitmapSource Rotated(BitmapSource source, int degrees)
    {
        var turned = new TransformedBitmap(source, new RotateTransform(degrees));
        turned.Freeze();
        return turned;
    }

    // ---------- Save ----------

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    /// <summary>Writes the changes into the shown file (after asking); the original goes to the Recycle Bin first.</summary>
    private async Task<bool> SaveAsync()
    {
        if (CurrentPath is not { } path || !CanEdit || !HasChanges || _cropping)
            return false;
        if (!CanWrite(path))
            return await SaveAsAsync();
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"Save the changes into {Path.GetFileName(path)}?\n\nThe original goes to the Recycle Bin first, so you can get it back.",
            "AstraViewer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        // A WebP file stays what it was: lossless, or lossy.
        bool lossless = WebPEncoder.IsWebP(path) && WebPEncoder.Describe(path).Lossless;
        return answer == MessageBoxResult.Yes && await WriteAsync(path, path, lossless);
    }

    /// <summary>Asks for a name and a type (JPEG, PNG, WebP, BMP, TIFF, GIF), writes there and shows the new file.</summary>
    private async Task<bool> SaveAsAsync()
    {
        if (CurrentPath is not { } path || !CanEdit || _cropping)
            return false;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        const int WebPLossy = 3, WebPLossless = 4;
        int index = extension switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => 1,
            ".png" => 2,
            ".webp" => WebPEncoder.Describe(path).Lossless ? WebPLossless : WebPLossy,
            ".bmp" or ".dib" => 5,
            ".tif" or ".tiff" => 6,
            ".gif" => 7,
            _ => 2,
        };
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save as",
            Filter = "JPEG (*.jpg)|*.jpg|PNG (*.png)|*.png|WebP (*.webp)|*.webp|WebP, lossless (*.webp)|*.webp|" +
                     "BMP (*.bmp)|*.bmp|TIFF (*.tif)|*.tif|GIF (*.gif)|*.gif",
            FilterIndex = index,
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(path),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return false;
        if (!CanWrite(dialog.FileName))
        {
            MessageDialog.Show(Window.GetWindow(this),
                $"{Path.GetFileName(dialog.FileName)}: this file type cannot be written. Use .jpg, .png, .webp, .bmp, .tif or .gif.",
                "AstraViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return await WriteAsync(path, dialog.FileName, dialog.FilterIndex == WebPLossless);
    }

    /// <summary>
    /// Encodes the picture with its edits and rotation to <paramref name="target"/>: first a temporary file next to it,
    /// then a file already there goes to the Recycle Bin, then the new one takes its name. Shows the result.
    /// </summary>
    private async Task<bool> WriteAsync(string source, string target, bool webPLossless)
    {
        if (ShownPixels is not { } pixels || !CanWrite(target))
            return false;
        var output = _rotation == 0 ? pixels : Rotated(pixels, _rotation);
        // JPEG and BMP have no transparency; drawn here (WPF draws on the UI thread), encoded in the background.
        if (IsJpeg(target) || Path.GetExtension(target).ToLowerInvariant() is ".bmp" or ".dib")
            output = Opaque(output);
        string temp = target + ".astraviewer.tmp";
        try
        {
            await Task.Run(() => Encode(output, source, target, temp, webPLossless));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                   or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            TryDelete(temp);
            MessageDialog.Show(Window.GetWindow(this), $"Cannot save {Path.GetFileName(target)}:\n\n{ex.Message}", "AstraViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (File.Exists(target))
        {
            IntPtr owner = new WindowInteropHelper(Window.GetWindow(this)).Handle;
            await Shell.FileOperations.DeleteAsync(owner, [target], permanent: false);
            if (File.Exists(target))
            {
                TryDelete(temp);
                MessageDialog.Show(Window.GetWindow(this),
                    $"{Path.GetFileName(target)} could not go to the Recycle Bin (in use, or no access), so nothing was saved.",
                    "AstraViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        try
        {
            File.Move(temp, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot save {Path.GetFileName(target)}:\n\n{ex.Message}\n\nThe new picture is in {temp}.",
                "AstraViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ThumbnailCache.Forget(target);
        foreach (var item in _album.Concat(_history).Where(i => string.Equals(i.FullPath, target, StringComparison.OrdinalIgnoreCase)))
        {
            item.ResetThumbnail();
            item.RequestThumbnail();
        }
        ResetEdits();
        bool same = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        await ShowImageAsync(target, addToHistory: !same, reloadFolder: true);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A type AstraViewer can write: Windows' own encoders, and WebP through libwebp.</summary>
    private static bool CanWrite(string path) => WebPEncoder.IsWebP(path) || EncoderFor(path) is not null;

    /// <summary>Windows' encoder for a file name, or null when it has none for that type (WebP, ICO, HEIC, ...).</summary>
    private static Func<BitmapEncoder>? EncoderFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => () => new JpegBitmapEncoder { QualityLevel = JpegQuality },
        ".png" => () => new PngBitmapEncoder(),
        ".bmp" or ".dib" => () => new BmpBitmapEncoder(),
        ".tif" or ".tiff" => () => new TiffBitmapEncoder(),
        ".gif" => () => new GifBitmapEncoder(),
        ".jxr" or ".wdp" => () => new WmpBitmapEncoder(),
        _ => null,
    };

    /// <summary>
    /// Writes the picture to <paramref name="temp"/>. JPEG to JPEG keeps the photo's metadata (date taken, camera), with the
    /// orientation set to "as stored" (the pixels are turned already); if that fails, it is written without metadata.
    /// </summary>
    private static void Encode(BitmapSource pixels, string source, string target, string temp, bool webPLossless)
    {
        if (WebPEncoder.IsWebP(target))
        {
            File.WriteAllBytes(temp, WebPEncoder.Encode(pixels, webPLossless));
            return;
        }
        var create = EncoderFor(target)!;
        BitmapMetadata? metadata = IsJpeg(target) && IsJpeg(source) ? JpegMetadata(source, pixels.PixelWidth, pixels.PixelHeight) : null;
        try
        {
            Write(create(), pixels, metadata, temp);
        }
        catch (Exception) when (metadata is not null)
        {
            Write(create(), pixels, null, temp);
        }
    }

    private static void Write(BitmapEncoder encoder, BitmapSource pixels, BitmapMetadata? metadata, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(pixels, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    private static BitmapMetadata? JpegMetadata(string path, int width, int height)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames[0].Metadata is not BitmapMetadata original)
                return null;
            var metadata = original.Clone();
            Set(metadata, "/app1/ifd/{ushort=274}", (ushort)1);              // Orientation: as stored.
            Set(metadata, "/app1/ifd/exif/{ushort=40962}", (uint)width);     // Pixel width.
            Set(metadata, "/app1/ifd/exif/{ushort=40963}", (uint)height);    // Pixel height.
            return metadata;
        }
        catch (Exception)
        {
            return null; // No metadata kept rather than no picture saved.
        }
    }

    private static void Set(BitmapMetadata metadata, string query, object value)
    {
        try
        {
            if (metadata.ContainsQuery(query))
                metadata.SetQuery(query, value);
        }
        catch (Exception)
        {
            // A tag of another type or a read-only block: left as it is.
        }
    }

    /// <summary>JPEG and BMP have no transparency: transparent parts become white (not black).</summary>
    private static BitmapSource Opaque(BitmapSource source)
    {
        var format = source.Format;
        bool alpha = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 ||
                     format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float ||
                     (format.BitsPerPixel <= 8 && source.Palette?.Colors.Any(c => c.A < 255) == true);
        if (!alpha)
            return source;
        var visual = new DrawingVisual();
        var area = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, area);
            context.DrawImage(source, area);
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return WithDpiOf(source, new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0));
    }

    // ---------- Keys, leaving with changes ----------

    /// <summary>The edit keys; false = not an edit key.</summary>
    private bool EditKey(Key key, ModifierKeys modifiers)
    {
        if (_cropping)
        {
            switch (key)
            {
                case Key.Enter:
                    ApplyCrop();
                    return true;
                case Key.Escape:
                    EndCrop(restore: true);
                    AfterEdit();
                    return true;
                case Key.R:
                    return true; // No turning while the frame is up.
            }
        }
        switch (key)
        {
            case Key.S when modifiers == ModifierKeys.Control:
                _ = SaveAsync();
                return true;
            case Key.S when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _ = SaveAsAsync();
                return true;
            case Key.Z when modifiers == ModifierKeys.Control:
                if (_cropping)
                {
                    EndCrop(restore: true);
                    AfterEdit();
                }
                else
                {
                    UndoEdit();
                }
                return true;
        }
        return false;
    }

    /// <summary>Another picture is about to be shown: unsaved crop / resize asks first. False = stay.</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsEdited || CurrentPath is not { } path)
            return true;
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"{Path.GetFileName(path)} has changes that are not saved. Save them first?", "AstraViewer",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
            return await SaveAsync();
        if (answer == MessageBoxResult.No)
        {
            ResetEdits();
            return true;
        }
        return false;
    }

    /// <summary>DirectoryFable closes: unsaved crop / resize asks once (Yes = close and lose them).</summary>
    public bool ConfirmExit(Window owner)
    {
        if (!IsEdited || CurrentPath is not { } path)
            return true;
        return MessageDialog.Show(owner,
            $"AstraViewer: {Path.GetFileName(path)} has changes that are not saved.\n\nClose DirectoryFable and lose them?",
            "DirectoryFable", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
