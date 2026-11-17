using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Infrastructure;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// AstraViewer: an image viewer next to the explorer and Notepad# (like VS Code's image preview).
/// It opens only from inside DirectoryFable (double-click on the chosen image types, "Open in
/// AstraViewer", drops); Windows' file associations are not changed. Left: the history of opened
/// images, newest first. Right: the image (fit / 100 %, zoom at the mouse, drag to move, view-only
/// rotation), the other images of its folder as thumbnails with "n / N", and its properties.
/// </summary>
public partial class AstraViewerView : UserControl
{
    private const double MinZoom = 0.02, MaxZoom = 32;

    private readonly ObservableCollection<ViewerItem> _history = [];
    private readonly ObservableCollection<ViewerItem> _album = [];
    private string? _albumFolder;
    private bool _syncing; // Selections set by code, not by the user.

    private LoadedImage? _image;
    private CancellationTokenSource? _loading;
    private int _loadVersion;

    // View: zoom 1 = one image pixel per screen pixel; pan = offset of the image center from the stage center (DIP).
    private double _zoom = 1;
    private bool _fit = true;
    private int _rotation;
    private Vector _pan;
    private Point? _dragFrom;

    private string _startZoom = "fit";
    private int _historySize = 100;
    private readonly System.Windows.Threading.DispatcherTimer _stateTimer;

    public AstraViewerView()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;
        Strip.ItemsSource = _album;
        _stateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _stateTimer.Tick += (_, _) =>
        {
            _stateTimer.Stop();
            StateChanged?.Invoke();
        };
        // Files in the history that were deleted or moved show red; checked whenever the view appears.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _ = CheckHistoryAsync();
        };
        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.SettingsChanged += ApplySettings;
        SettingsPage.ClearHistoryRequested += ClearHistory;
        ApplySettings(AppSettings.Load());
        ShowEmpty();
    }

    /// <summary>The shown image's path, or null.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>The shown image changed (the main window updates its title).</summary>
    public event Action? ActiveChanged;

    /// <summary>"Show in explorer": the main window shows the explorer at this file.</summary>
    public event Action<string>? ShowInExplorerRequested;

    /// <summary>History or shown image changed: the main window saves the session (a moment later).</summary>
    public event Action? StateChanged;

    /// <summary>Start zoom, background, checkerboard, history size, file types (settings.json).</summary>
    public void ApplySettings(AppSettings settings)
    {
        ImageFileTypes.Configure(settings);
        _startZoom = settings.ViewerZoom == "actual" ? "actual" : "fit";
        _historySize = Math.Clamp(settings.ViewerHistorySize, 10, 1000);
        Stage.Background = settings.ViewerBackground switch
        {
            "black" => Brushes.Black,
            "light" => new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            _ => (Brush)FindResource("EditorBackgroundBrush"),
        };
        Checker.Tag = settings.ViewerCheckerboard;
        // Thumbnail tiles 4:3; the thumbnails are decoded 128 px high, sharp up to "large".
        (double width, double height) = settings.ViewerThumbnailSize switch
        {
            "small" => (52d, 39d),
            "large" => (104d, 78d),
            _ => (72d, 54d),
        };
        Resources["ThumbWidth"] = width;
        Resources["ThumbHeight"] = height;
        TrimHistory();
        UpdateView();
    }

    public void FocusView() => Dispatcher.BeginInvoke(() => Focus(), System.Windows.Threading.DispatcherPriority.Input);

    // ---------- Open ----------

    /// <summary>From the explorer (double-click, menu): show the image and put it at the top of the history.</summary>
    public Task OpenAsync(string path) => ShowImageAsync(path, addToHistory: true);

    /// <summary>Dropped images: all go into the history, the first one is shown (and is at the top).</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        for (int i = paths.Count - 1; i > 0; i--)
            AddToHistory(paths[i]);
        await ShowImageAsync(paths[0], addToHistory: true);
    }

    /// <summary>
    /// Shows an image. Its folder's images fill the strip (when it is another folder). Only an
    /// image that could be shown goes into the history (when <paramref name="addToHistory"/>).
    /// Navigating (strip, arrows, history clicks) never reorders the history.
    /// </summary>
    private async Task ShowImageAsync(string path, bool addToHistory, bool reloadFolder = false)
    {
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        int version = ++_loadVersion;

        CurrentPath = path;
        ActiveChanged?.Invoke();
        string? folder = Path.GetDirectoryName(path);
        if (reloadFolder || !string.Equals(folder, _albumFolder, StringComparison.OrdinalIgnoreCase) || AlbumIndexOf(path) < 0)
            await LoadAlbumAsync(folder);
        if (version != _loadVersion)
            return;
        SyncSelections();

        // The old picture stays until the new one is ready; a slow load says so after a moment.
        _pending = version;
        _ = ShowLoadingLaterAsync(version);

        try
        {
            var image = await ImageLoader.LoadAsync(path, loading.Token);
            if (version != _loadVersion)
                return;
            _pending = 0;
            _image = image;
            Picture.Source = image.Image;
            MessageText.Text = "";
            _rotation = 0;
            _pan = default;
            _fit = _startZoom == "fit";
            _zoom = 1;
            UpdateView();
            UpdateDetails();
            if (addToHistory)
                AddToHistory(path);
            MarkMissing(path, false);
        }
        catch (OperationCanceledException)
        {
            // Another image was chosen meanwhile.
        }
        catch (Exception ex)
        {
            // Any decoding problem (broken file, missing codec, no access) is shown, never thrown.
            if (version != _loadVersion)
                return;
            _pending = 0;
            _image = null;
            Picture.Source = null;
            MessageText.Text = $"Cannot show {Path.GetFileName(path)}.\n\n{Explain(ex)}";
            UpdateView();
            DetailsText.Text = Path.GetFileName(path);
            if (ex is FileNotFoundException or DirectoryNotFoundException)
                MarkMissing(path, true);
        }
        finally
        {
            if (_loading == loading)
                _loading = null;
            loading.Dispose();
        }
        ScheduleStateSave();
    }

    /// <summary>A plain reason first, Windows' own message below it.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "The file no longer exists.",
        UnauthorizedAccessException => $"No access to the file.\n({ex.Message})",
        NotSupportedException or FileFormatException or COMException or ArgumentException =>
            $"Windows cannot read this image: the file is damaged, or its format needs a codec that is not installed.\n({ex.Message})",
        _ => ex.Message,
    };

    /// <summary>The load that has not finished yet (0 = none).</summary>
    private int _pending;

    private async Task ShowLoadingLaterAsync(int version)
    {
        await Task.Delay(250);
        if (_pending == version)
            MessageText.Text = "Loading…";
    }

    /// <summary>The images of a folder for the strip, sorted like the explorer (natural name order).</summary>
    private async Task LoadAlbumAsync(string? folder)
    {
        _albumFolder = folder;
        List<string> paths;
        try
        {
            paths = folder is null ? [] : await Task.Run(() =>
            {
                var list = Directory.EnumerateFiles(folder).Where(ImageFileTypes.IsImage).ToList();
                list.Sort((a, b) => FileItemComparer.NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
                return list;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            paths = [];
        }
        foreach (var item in _album)
            item.CancelThumbnail();
        _syncing = true;
        try
        {
            _album.Clear();
            foreach (string path in paths)
                _album.Add(new ViewerItem(path));
            // The shown image may be of another type than the list (e.g. dropped): keep it in the strip.
            if (CurrentPath is not null && AlbumIndexOf(CurrentPath) < 0 &&
                string.Equals(Path.GetDirectoryName(CurrentPath), folder, StringComparison.OrdinalIgnoreCase))
                _album.Insert(0, new ViewerItem(CurrentPath));
        }
        finally
        {
            _syncing = false;
        }
    }

    private int AlbumIndexOf(string path)
    {
        for (int i = 0; i < _album.Count; i++)
        {
            if (string.Equals(_album[i].FullPath, path, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Strip and history select the shown image (without opening it again); "n / N" follows.</summary>
    private void SyncSelections()
    {
        _syncing = true;
        try
        {
            int index = CurrentPath is null ? -1 : AlbumIndexOf(CurrentPath);
            Strip.SelectedIndex = index;
            if (index >= 0)
                Strip.ScrollIntoView(_album[index]);
            CounterText.Text = index >= 0 ? $"{index + 1} / {_album.Count}" : "";
            StripArea.Visibility = _album.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.SelectedItem = _history.FirstOrDefault(h => string.Equals(h.FullPath, CurrentPath, StringComparison.OrdinalIgnoreCase));
            PreviousButton.IsEnabled = index > 0;
            NextButton.IsEnabled = index >= 0 && index < _album.Count - 1;
            ShowInExplorerButton.IsEnabled = OpenExternalButton.IsEnabled = CurrentPath is not null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowEmpty()
    {
        _loadVersion++;
        _loading?.Cancel();
        CurrentPath = null;
        _image = null;
        Picture.Source = null;
        _album.Clear();
        _albumFolder = null;
        MessageText.Text = "No image is open.\n\nDouble-click an image in the explorer (or right-click it: \"Open in AstraViewer\"),\nor drop images here or on the AstraViewer tab.";
        DetailsText.Text = "";
        UpdateView();
        SyncSelections();
        HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveChanged?.Invoke();
    }

    /// <summary>Previous (-1) / next (+1) image of the folder; Home / End = first / last.</summary>
    private void Step(int step)
    {
        if (CurrentPath is null || _album.Count == 0)
            return;
        int index = AlbumIndexOf(CurrentPath);
        int target = step switch
        {
            int.MinValue => 0,
            int.MaxValue => _album.Count - 1,
            _ => Math.Clamp(index + step, 0, _album.Count - 1),
        };
        if (target != index)
            _ = ShowImageAsync(_album[target].FullPath, addToHistory: false);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Strip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && Strip.SelectedItem is ViewerItem item && item.FullPath != CurrentPath)
            _ = ShowImageAsync(item.FullPath, addToHistory: false);
    }

    /// <summary>The mouse wheel scrolls the strip sideways.</summary>
    private void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindChild<ScrollViewer>(Strip) is { } scroller)
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Thumbnails load only for rows in view (and are cancelled when they scroll away).</summary>
    private void Thumb_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewerItem item })
            item.RequestThumbnail();
    }

    private void Thumb_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewerItem item })
            item.CancelThumbnail();
    }

    // ---------- History ----------

    /// <summary>To the top of the history (an image already in it moves up); the list keeps its size limit.</summary>
    private void AddToHistory(string path)
    {
        var existing = _history.FirstOrDefault(h => string.Equals(h.FullPath, path, StringComparison.OrdinalIgnoreCase));
        _syncing = true;
        try
        {
            if (existing is not null)
                _history.Move(_history.IndexOf(existing), 0);
            else
                _history.Insert(0, new ViewerItem(path));
            TrimHistory();
        }
        finally
        {
            _syncing = false;
        }
        HistoryEmptyText.Visibility = Visibility.Collapsed;
        SyncSelections();
        ScheduleStateSave();
    }

    private void TrimHistory()
    {
        while (_history.Count > _historySize)
        {
            _history[^1].CancelThumbnail();
            _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>A click (or arrow keys) in the history shows that image; the history order stays.</summary>
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && HistoryList.SelectedItem is ViewerItem item && item.FullPath != CurrentPath)
            _ = ShowImageAsync(item.FullPath, addToHistory: false);
    }

    private void RemoveFromHistory(ViewerItem item)
    {
        item.CancelThumbnail();
        _history.Remove(item);
        HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleStateSave();
    }

    /// <summary>Empties the history (the image files are not touched).</summary>
    public void ClearHistory()
    {
        foreach (var item in _history)
            item.CancelThumbnail();
        _history.Clear();
        HistoryEmptyText.Visibility = Visibility.Visible;
        ScheduleStateSave();
    }

    private void MarkMissing(string path, bool missing)
    {
        foreach (var item in _history.Where(h => string.Equals(h.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            item.IsMissing = missing;
    }

    private async Task CheckHistoryAsync()
    {
        var items = _history.ToList();
        var exists = await Task.Run(() => items.Select(i => File.Exists(i.FullPath)).ToList());
        for (int i = 0; i < items.Count; i++)
            items[i].IsMissing = !exists[i];
    }

    private void HistoryList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var item = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(HistoryList, source) is ListBoxItem row
            ? row.DataContext as ViewerItem : null;
        var menu = NewMenu(HistoryList);
        if (item is not null)
        {
            AddFileItems(menu, item.FullPath, item.IsMissing);
            menu.Items.Add(new Separator());
            Add(menu, "Remove from history", "", () => RemoveFromHistory(item));
        }
        Add(menu, "Clear history", "", ClearHistory, enabled: _history.Count > 0);
        menu.IsOpen = true;
    }

    // ---------- Menus and file actions ----------

    private ContextMenu NewMenu(UIElement target) => new()
    {
        PlacementTarget = target,
        Placement = PlacementMode.MousePoint,
        FontFamily = (FontFamily)FindResource("UiFont"),
        FontSize = (double)FindResource("UiFontSize"),
    };

    private static void Add(ItemsControl menu, string header, string gesture, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void AddFileItems(ContextMenu menu, string path, bool missing)
    {
        Add(menu, "Show in explorer", "", () => ShowInExplorerRequested?.Invoke(path), enabled: !missing);
        Add(menu, "Open with Windows", "", () => OpenExternal(path), enabled: !missing);
        Add(menu, "Copy path", "", () => CopyText(path));
    }

    private void Stage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CurrentPath is not { } path)
            return;
        e.Handled = true;
        var menu = NewMenu(Stage);
        AddHistoryItem(menu, path, enabled: _image is not null);
        menu.Items.Add(new Separator());
        Add(menu, "Copy image", "Ctrl+C", CopyImage, enabled: _image is not null);
        AddFileItems(menu, path, missing: false);
        menu.IsOpen = true;
    }

    /// <summary>Right-click on a thumbnail: the same file items, for that image.</summary>
    private void Strip_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is not { } source ||
            ItemsControl.ContainerFromElement(Strip, source) is not ListBoxItem { DataContext: ViewerItem item })
            return;
        e.Handled = true;
        var menu = NewMenu(Strip);
        AddHistoryItem(menu, item.FullPath, enabled: true);
        menu.Items.Add(new Separator());
        AddFileItems(menu, item.FullPath, missing: false);
        menu.IsOpen = true;
    }

    /// <summary>"Add to history" (an image already in it moves to the top; disabled when it is the top one).</summary>
    private void AddHistoryItem(ContextMenu menu, string path, bool enabled)
    {
        bool onTop = _history.Count > 0 && string.Equals(_history[0].FullPath, path, StringComparison.OrdinalIgnoreCase);
        bool listed = _history.Any(h => string.Equals(h.FullPath, path, StringComparison.OrdinalIgnoreCase));
        Add(menu, listed ? "Move to the top of the history" : "Add to history", "", () => AddToHistory(path), enabled && !onTop);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is { } path)
            ShowInExplorerRequested?.Invoke(path);
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is { } path)
            OpenExternal(path);
    }

    /// <summary>Opens the image with its Windows program (the default for its type).</summary>
    private void OpenExternal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot open {Path.GetFileName(path)}:\n\n{ex.Message}", "AstraViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // The clipboard is busy (another program holds it).
        }
    }

    private void CopyImage()
    {
        if (_image is null)
            return;
        try
        {
            Clipboard.SetImage(_image.Image);
        }
        catch (COMException)
        {
        }
    }

    // ---------- View: fit, zoom, pan, rotation ----------

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Places the image (and the checkerboard behind it) for zoom, pan and rotation.</summary>
    private void UpdateView()
    {
        if (_image is null || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
        {
            Picture.Visibility = Checker.Visibility = Visibility.Collapsed;
            ZoomText.Text = "";
            return;
        }
        double scale = DpiScale;
        // Size at 100 % in DIP: one image pixel per screen pixel (also for images decoded smaller).
        double width = _image.Details.PixelWidth / scale, height = _image.Details.PixelHeight / scale;
        bool turned = _rotation % 180 != 0;
        double shownWidth = turned ? height : width, shownHeight = turned ? width : height;
        double stageWidth = Stage.ActualWidth, stageHeight = Stage.ActualHeight;
        if (_fit)
            _zoom = Math.Min(1, Math.Min(stageWidth / shownWidth, stageHeight / shownHeight));

        // An image smaller than the stage stays centered; a larger one cannot be moved out of view.
        double roomX = Math.Max(0, (shownWidth * _zoom - stageWidth) / 2), roomY = Math.Max(0, (shownHeight * _zoom - stageHeight) / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -roomX, roomX), Math.Clamp(_pan.Y, -roomY, roomY));

        var matrix = Matrix.Identity;
        matrix.Translate(-width / 2, -height / 2);
        matrix.Rotate(_rotation);
        matrix.Scale(_zoom, _zoom);
        matrix.Translate(stageWidth / 2 + _pan.X, stageHeight / 2 + _pan.Y);
        // Whole screen pixels: at 100 % every image pixel lands on one screen pixel (sharp).
        matrix.OffsetX = Math.Round(matrix.OffsetX * scale) / scale;
        matrix.OffsetY = Math.Round(matrix.OffsetY * scale) / scale;
        Picture.Width = width;
        Picture.Height = height;
        Picture.RenderTransform = new MatrixTransform(matrix);
        // Zoomed in far: show the pixels as squares (like VS Code), not smoothed.
        bool pixels = _zoom >= 3;
        RenderOptions.SetBitmapScalingMode(Picture, pixels ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(Picture, pixels ? EdgeMode.Aliased : EdgeMode.Unspecified);
        Picture.Visibility = Visibility.Visible;

        bool checker = Checker.Tag is true && HasTransparency(_image.Image);
        Checker.Visibility = checker ? Visibility.Visible : Visibility.Collapsed;
        if (checker)
        {
            var bounds = new Rect(0, 0, width, height);
            bounds.Transform(matrix);
            Canvas.SetLeft(Checker, bounds.Left);
            Canvas.SetTop(Checker, bounds.Top);
            Checker.Width = bounds.Width;
            Checker.Height = bounds.Height;
        }
        ZoomText.Text = $"{_zoom * 100:0.#} %";
    }

    private static bool HasTransparency(BitmapSource image) =>
        image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Rgba64 ||
        image.Format == PixelFormats.Prgba64 || image.Format == PixelFormats.Rgba128Float || image.Format == PixelFormats.Prgba128Float ||
        (image.Format.BitsPerPixel <= 8 && image.Palette?.Colors.Any(c => c.A < 255) == true) ||
        image is TransformedBitmap { Source: { } inner } && HasTransparency(inner);

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateView();

    private void SetFit()
    {
        _fit = true;
        _pan = default;
        UpdateView();
    }

    /// <summary>New zoom; the image point under <paramref name="anchor"/> (stage coordinates, default: center) stays there.</summary>
    private void ZoomTo(double zoom, Point? anchor = null)
    {
        if (_image is null)
            return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var center = new Point(Stage.ActualWidth / 2 + _pan.X, Stage.ActualHeight / 2 + _pan.Y);
        var at = anchor ?? new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2);
        var newCenter = at - (at - center) * (zoom / _zoom);
        _pan = new Vector(newCenter.X - Stage.ActualWidth / 2, newCenter.Y - Stage.ActualHeight / 2);
        _zoom = zoom;
        _fit = false;
        UpdateView();
    }

    /// <summary>Zoom steps like most viewers: 1.25 times per wheel notch / key press.</summary>
    private void ZoomStep(int steps, Point? anchor = null) => ZoomTo(_zoom * Math.Pow(1.25, steps), anchor);

    private void Rotate(int degrees)
    {
        if (_image is null)
            return;
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        UpdateView();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => SetFit();

    private void Actual_Click(object sender, RoutedEventArgs e) => ZoomTo(1);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);

    private void Rotate_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomStep(Math.Sign(e.Delta), e.GetPosition(Stage));
    }

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_image is null)
            return;
        if (e.ClickCount == 2)
        {
            // Double-click: fit <-> 100 % at the mouse.
            if (_fit || Math.Abs(_zoom - 1) > 0.001)
                ZoomTo(1, e.GetPosition(Stage));
            else
                SetFit();
            return;
        }
        _dragFrom = e.GetPosition(Stage);
        Stage.CaptureMouse();
        Stage.Cursor = Cursors.SizeAll;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var at = e.GetPosition(Stage);
        _pan += at - from;
        _dragFrom = at;
        UpdateView();
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void Stage_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragFrom = null;
        Stage.Cursor = null;
        if (Stage.IsMouseCaptured)
            Stage.ReleaseMouseCapture();
    }

    /// <summary>Mouse side buttons: previous / next image.</summary>
    private void Stage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = true;
            Step(e.ChangedButton == MouseButton.XButton1 ? -1 : 1);
        }
    }

    // ---------- Properties ----------

    /// <summary>"name · 4000 × 3000 px (12.0 MP) · JPEG · 24-bit · 72 dpi · 3,2 MB · modified ...".</summary>
    private void UpdateDetails()
    {
        if (_image is not { Details: var d } || CurrentPath is null)
        {
            DetailsText.Text = "";
            return;
        }
        var parts = new List<string>
        {
            Path.GetFileName(CurrentPath),
            $"{d.PixelWidth:N0} × {d.PixelHeight:N0} px ({d.PixelWidth * (double)d.PixelHeight / 1_000_000:0.0} MP)",
            d.Format,
            $"{d.BitsPerPixel}-bit",
        };
        if (d.DpiX > 0)
            parts.Add($"{d.DpiX:0} dpi");
        parts.Add(DisplayFormat.Size(d.FileSize));
        parts.Add($"modified {DisplayFormat.Date(d.Modified)}");
        if (d.FramesNote is not null)
            parts.Add(d.FramesNote);
        if (!string.IsNullOrEmpty(d.DateTaken))
            parts.Add($"taken {d.DateTaken}");
        if (!string.IsNullOrEmpty(d.Camera))
            parts.Add(d.Camera);
        if (_image.Reduced)
            parts.Add("very large: shown reduced");
        DetailsText.Text = string.Join("   ·   ", parts);
        DetailsText.ToolTip = string.Join("\n", parts);
    }

    // ---------- Keys ----------

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsPage.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowSettings(false);
            }
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase)
            return;
        var modifiers = Keyboard.Modifiers;
        bool plain = modifiers == ModifierKeys.None;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Left or Key.PageUp when plain:
                Step(-1);
                break;
            case Key.Right or Key.PageDown when plain:
                Step(1);
                break;
            case Key.Home when plain:
                Step(int.MinValue);
                break;
            case Key.End when plain:
                Step(int.MaxValue);
                break;
            case Key.F when plain:
                SetFit();
                break;
            case Key.D1 or Key.NumPad1 when plain:
            case Key.D0 or Key.NumPad0 when modifiers == ModifierKeys.Control:
                ZoomTo(1);
                break;
            case Key.OemPlus or Key.Add:
                ZoomStep(1);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomStep(-1);
                break;
            case Key.R when plain:
                Rotate(90);
                break;
            case Key.R when modifiers == ModifierKeys.Shift:
                Rotate(-90);
                break;
            case Key.F5 when plain:
                Refresh();
                break;
            case Key.C when modifiers == ModifierKeys.Control:
                CopyImage();
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    /// <summary>F5: the folder's images and the shown image read again (thumbnails too).</summary>
    private void Refresh()
    {
        ThumbnailCache.Clear();
        foreach (var item in _history)
            item.ResetThumbnail();
        _ = CheckHistoryAsync();
        if (CurrentPath is { } path)
            _ = ShowImageAsync(path, addToHistory: false, reloadFolder: true);
    }

    // ---------- Drops ----------

    /// <summary>
    /// The image files in a drag (types of the AstraViewer list); files unpacked from an archive
    /// just for the drag are left out (they are deleted when the app exits).
    /// </summary>
    public static List<string> DroppedImages(IDataObject data) =>
        FileDrop.GetPaths(data) is { } paths
            ? [.. paths.Where(p => ImageFileTypes.IsImage(p) && File.Exists(p) &&
                                   !p.StartsWith(Shell.ZipBrowser.TempRoot, StringComparison.OrdinalIgnoreCase))]
            : [];

    private void View_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (FileDrop.GetPaths(e.Data) is null)
            return;
        e.Effects = DroppedImages(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void View_PreviewDrop(object sender, DragEventArgs e)
    {
        if (FileDrop.GetPaths(e.Data) is null)
            return;
        e.Handled = true;
        await OpenFilesAsync(DroppedImages(e.Data));
    }

    // ---------- Settings page ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool open)
    {
        SettingsButton.IsChecked = open;
        if (open)
        {
            SettingsPage.Open();
            SettingsPage.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            FocusView();
        }
    }

    // ---------- Session ----------

    private void ScheduleStateSave()
    {
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    public ViewerState CaptureState() => new()
    {
        History = [.. _history.Select(h => h.FullPath)],
        Current = CurrentPath,
        ListWidth = ListColumn.ActualWidth,
    };

    /// <summary>The history and the last shown image (which shows even when it is gone: with a message).</summary>
    public async Task RestoreAsync(ViewerState? state)
    {
        if (state is null)
            return;
        if (state.ListWidth >= 80)
            ListColumn.Width = new GridLength(state.ListWidth);
        foreach (string path in state.History.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Take(_historySize))
            _history.Add(new ViewerItem(path));
        HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(state.Current))
            await ShowImageAsync(state.Current, addToHistory: false);
    }
}
