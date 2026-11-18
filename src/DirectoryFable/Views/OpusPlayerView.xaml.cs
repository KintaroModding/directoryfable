using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Player;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DirectoryFable.Views;

/// <summary>
/// OpusHC, the fifth title bar view: videos opened from DirectoryFable (double-click on the chosen types,
/// "Open in OpusHC", drops). Plays with VLC's engine (<see cref="VlcEngine"/>, downloaded on first use)
/// and draws into WPF (<see cref="VideoSurface"/>). Timeline with scrubbing, play / pause, volume and mute, audio
/// track and subtitles, previous / next video in the folder (and the next one at the end, a setting), resume where
/// a video was left (a setting), full screen. Hidden: the video pauses.
/// </summary>
public partial class OpusPlayerView : UserControl
{
    private const int PositionsKept = 300;
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5), BigSeekStep = TimeSpan.FromSeconds(30);

    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private VideoSurface? _surface;
    private Media? _media;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _current;
    private string? _pendingOpen;
    private List<string> _folderVideos = [];
    private readonly List<PlayerPositionState> _positions = []; // Newest first.
    private int _volume = 80;
    private bool _muted;
    private bool _dragging, _updatingTimeline, _updatingVolume;
    private DateTime _lastSeek;
    private bool _autoNext = true, _resume = true;
    private CancellationTokenSource? _download;
    private Point _lastMouse;

    // Filmstrip: the pictures of the last few videos stay in memory (nothing goes to disk).
    private int _stripCount = 40;
    private bool _filmstrip = true;
    private Thumbnailer? _thumbnailer;
    private CancellationTokenSource? _thumbCancel;
    private string? _stripFor;
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?[]> _strips = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stripOrder = [];

    public OpusPlayerView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateTime();
        _hideTimer.Tick += (_, _) => HideControls();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.SettingsChanged += ApplySettings;
        SettingsPage.EngineChanged += ShowEngineState;
        Strip.Hovered += share =>
        {
            if (share is double at)
                ShowHover(at);
            else
                HoverPopup.IsOpen = false;
        };
        Strip.Clicked += share => SeekTo(share * Timeline.Maximum, force: true);
        ApplySettings(AppSettings.Load());
        // Hidden (another view, the window to the tray): the video pauses.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _player is { IsPlaying: true })
                _player.Pause();
        };
        ShowEngineState();
        ShowFile();
    }

    /// <summary>The shown file changed (window title).</summary>
    public event Action? ActiveChanged;

    /// <summary>Volume, the file or the resume list changed (the session is saved a moment later).</summary>
    public event Action? StateChanged;

    /// <summary>Full screen on / off (the main window hides its title bar and covers the screen).</summary>
    public event Action<bool>? FullScreenRequested;

    public event Action<string>? ShowInExplorerRequested;

    public string? CurrentPath => _current;

    public bool IsFullScreen { get; private set; }

    public void FocusView() => Dispatcher.BeginInvoke(() => Focus(), DispatcherPriority.Input);

    public void ApplySettings(AppSettings settings)
    {
        VideoFileTypes.Configure(settings);
        _autoNext = settings.PlayerAutoNext;
        _resume = settings.PlayerResume;
        _filmstrip = settings.PlayerFilmstrip;
        int count = Array.IndexOf(AppSettings.FilmstripCounts, settings.PlayerFilmstripCount) >= 0 ? settings.PlayerFilmstripCount : 40;
        if (count != _stripCount)
        {
            // Another number of pictures: the kept strips no longer fit; the shown video gets a new one.
            _stripCount = count;
            _thumbCancel?.Cancel();
            _strips.Clear();
            _stripOrder.Clear();
            _stripFor = null;
        }
        StripRow.Visibility = _filmstrip ? Visibility.Visible : Visibility.Collapsed;
        if (_filmstrip && _player is not null && _current is not null)
            StartFilmstrip(); // Also for a paused video.
        else if (!_filmstrip)
        {
            _thumbCancel?.Cancel();
            _stripFor = null; // On again: the strip is made (or shown) again.
        }
    }

    // ---------- Engine ----------

    /// <summary>
    /// The engine card: shown in the view, with the full explanation, until VLC's engine is downloaded (why it is
    /// needed, the exact address, the exact folder, and that nothing is installed in Windows).
    /// </summary>
    private void ShowEngineState()
    {
        bool missing = !VlcEngine.IsInstalled;
        EnginePanel.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        StageText.Visibility = missing || _current is not null ? Visibility.Collapsed : Visibility.Visible;
        if (!missing)
            return;
        EngineWhy.Text = "OpusHC plays videos with VLC's engine (libVLC, from the VideoLAN project). Windows cannot play every " +
                         "video and sound format by itself, for example the Dolby sound of many MKV files; VLC's engine plays them " +
                         "all, with the choice of audio track and subtitles. It is not part of DirectoryFable, so it is downloaded once.";
        EngineFrom.Text = $"The official VLC download of VideoLAN (VLC {VlcEngine.Version}, {DisplayFormat.Size(VlcEngine.DownloadSize)}). " +
                          "DirectoryFable checks it against VideoLAN's published SHA-256 checksum and uses it only when it matches:";
        EngineUrl.Text = VlcEngine.DownloadUrl;
        EngineFolder.Text = VlcEngine.Folder;
        EngineClean.Text = "Only the engine files are kept (about 140 MB unpacked; the download itself is deleted). Nothing is installed " +
                           "in Windows: no registry entries, no Start menu, no system files, nothing outside that folder. To remove it, " +
                           "use Remove in OpusHC's settings or simply delete the folder.";
        EngineDownloadText.Text = $"Download ({DisplayFormat.Size(VlcEngine.DownloadSize)})";
    }

    /// <summary>Downloads VLC's engine (the first-visit question or the panel's button), then plays a video that waited.</summary>
    public async Task DownloadEngineAsync()
    {
        if (_download is not null || VlcEngine.IsInstalled)
            return;
        _download = new CancellationTokenSource();
        EngineDownloadButton.IsEnabled = false;
        EngineCancelButton.Visibility = Visibility.Visible;
        EngineBar.Visibility = Visibility.Visible;
        EngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
        var progress = new Progress<VlcEngine.Progress>(p =>
        {
            EngineBar.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0)
                EngineBar.Value = (double)p.Done / p.Total;
            EngineStatus.Text = p.Stage == "Downloading"
                ? $"Downloading {DisplayFormat.Size(p.Done)} of {DisplayFormat.Size(p.Total)}"
                : p.Stage == "Checking" ? "Checking the checksum..." : "Unpacking the engine files...";
        });
        try
        {
            await VlcEngine.InstallAsync(progress, _download.Token);
            EngineStatus.Text = "";
            ShowEngineState();
            if (_pendingOpen is { } waiting)
            {
                _pendingOpen = null;
                await OpenAsync(waiting);
            }
        }
        catch (OperationCanceledException)
        {
            EngineStatus.Text = "Cancelled. Nothing was kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Http.HttpRequestException)
        {
            EngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorTextBrush");
            EngineStatus.Text = $"The download did not work: {ex.Message}";
        }
        finally
        {
            _download.Dispose();
            _download = null;
            EngineDownloadButton.IsEnabled = true;
            EngineCancelButton.Visibility = Visibility.Collapsed;
            EngineBar.Visibility = Visibility.Collapsed;
            SettingsPage.ShowEngine();
        }
    }

    private void EngineDownload_Click(object sender, RoutedEventArgs e) => _ = DownloadEngineAsync();

    private void EngineCancel_Click(object sender, RoutedEventArgs e) => _download?.Cancel();

    /// <summary>The player on first use (VLC's engine loaded once).</summary>
    private bool EnsurePlayer()
    {
        if (_player is not null)
            return true;
        if (!VlcEngine.IsInstalled)
            return false;
        VlcEngine.Initialize();
        _vlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet", "--no-snapshot-preview");
        // Decoding on the CPU: with the graphics card's decoder VLC cannot draw subtitles into the picture it hands
        // over (its GPU frames cannot be blended). 1080p x264 / x265 is light work for a current CPU.
        _player = new MediaPlayer(_vlc) { EnableHardwareDecoding = false };
        _surface = new VideoSurface(Dispatcher);
        _surface.SourceChanged += source => Picture.Source = source;
        _surface.Attach(_player);
        _thumbnailer = new Thumbnailer(_vlc);
        // VLC raises its events on its own threads; calls back into the player only on the UI thread.
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyVolume();
            UpdatePlayButton();
            ShowTracks();
            StartFilmstrip(); // A video of the last session starts paused.
        });
        _player.Stopped += (_, _) => Dispatcher.BeginInvoke(UpdatePlayButton);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _ = OnEndAsync());
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => ShowToast("This video cannot be played."));
        _tick.Start();
        return true;
    }

    /// <summary>Before the app closes: remember the position, stop VLC.</summary>
    public void Shutdown()
    {
        _thumbCancel?.Cancel();
        RememberPosition();
        _tick.Stop();
        try
        {
            _player?.Stop();
        }
        catch (Exception)
        {
            // Closing anyway.
        }
        _player?.Dispose();
        _media?.Dispose();
        _vlc?.Dispose();
        _player = null;
    }

    // ---------- Opening ----------

    /// <summary>
    /// Plays a video (from the explorer, a drop, next / previous); <paramref name="paused"/>: shows its first picture
    /// and waits for Space. Without the engine: it waits for it.
    /// </summary>
    public async Task OpenAsync(string path, bool paused = false)
    {
        ShowSettings(false);
        if (!EnsurePlayer())
        {
            _pendingOpen = path;
            _current = path;
            ShowFile();
            ShowEngineState();
            ActiveChanged?.Invoke();
            return;
        }
        RememberPosition();
        _current = path;
        _folderVideos = await Task.Run(() => FolderVideos(path));
        var media = new Media(_vlc!, path, FromType.FromPath);
        double resumeAt = _resume ? SavedPosition(path) : 0;
        if (resumeAt > 0)
            media.AddOption($":start-time={resumeAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (paused)
            media.AddOption(":start-paused");
        _player!.Play(media);
        _media?.Dispose();
        _media = media;
        AspectScale.ScaleX = 1;
        ShowFile();
        StageText.Visibility = Visibility.Collapsed;
        if (resumeAt > 0)
            ShowToast($"Resumed at {Clock(TimeSpan.FromSeconds(resumeAt))}   ·   Home: from the start");
        ActiveChanged?.Invoke();
        StateChanged?.Invoke();
        FocusView();
    }

    /// <summary>The videos of the folder, in Explorer's name order (for previous / next).</summary>
    private static List<string> FolderVideos(string path)
    {
        try
        {
            string folder = Path.GetDirectoryName(path) ?? "";
            var files = Directory.EnumerateFiles(folder).Where(VideoFileTypes.IsVideo).ToList();
            files.Sort((a, b) => FileItemComparer.NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [path];
        }
    }

    private void ShowFile()
    {
        FileText.Text = _current is null ? "" : Path.GetFileName(_current);
        FileText.ToolTip = _current;
        ShowInExplorerButton.IsEnabled = OpenExternalButton.IsEnabled = _current is not null;
        int index = _current is null ? -1 : _folderVideos.FindIndex(p => string.Equals(p, _current, StringComparison.OrdinalIgnoreCase));
        CountText.Text = index >= 0 && _folderVideos.Count > 1 ? $"{index + 1} / {_folderVideos.Count}" : "";
    }

    private async Task StepAsync(int direction)
    {
        if (_current is null || _folderVideos.Count == 0)
            return;
        int index = _folderVideos.FindIndex(p => string.Equals(p, _current, StringComparison.OrdinalIgnoreCase));
        int next = index + direction;
        if (index < 0 || next < 0 || next >= _folderVideos.Count)
        {
            ShowToast(direction > 0 ? "This is the last video in the folder." : "This is the first video in the folder.");
            return;
        }
        await OpenAsync(_folderVideos[next]);
    }

    private void ApplyVolume()
    {
        if (_player is null)
            return;
        _player.Volume = _volume;
        _player.Mute = _muted;
    }

    private void OnPlaying()
    {
        if (_player is null)
            return;
        ApplyVolume();
        UpdatePlayButton();
        // Videos with non-square pixels: the picture is stretched to its real shape.
        var video = _media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        if (video is { } track && track.Data.Video.SarNum > 0 && track.Data.Video.SarDen > 0)
            AspectScale.ScaleX = (double)track.Data.Video.SarNum / track.Data.Video.SarDen;
        ShowTracks();
        StartFilmstrip();
    }

    private async Task OnEndAsync()
    {
        if (_current is { } ended)
            ForgetPosition(ended);
        UpdatePlayButton();
        if (_autoNext && _current is not null)
        {
            int index = _folderVideos.FindIndex(p => string.Equals(p, _current, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < _folderVideos.Count)
            {
                await OpenAsync(_folderVideos[index + 1]);
                return;
            }
        }
        StateChanged?.Invoke();
    }

    // ---------- Play, time, volume ----------

    private void TogglePlay()
    {
        if (_player is null)
            return;
        if (_player.State == VLCState.Ended && _media is not null)
            _player.Play(_media); // From the start again.
        else if (_player.IsPlaying)
            _player.Pause();
        else
            _player.Play();
        if (!_player.IsPlaying)
        {
            RememberPosition();
            StateChanged?.Invoke();
        }
    }

    private void UpdatePlayButton()
    {
        bool playing = _player?.IsPlaying == true;
        PlayIcon.Text = playing ? "" : ""; // Pause / Play
    }

    private void Seek(TimeSpan by)
    {
        if (_player is not { IsSeekable: true } player)
            return;
        long target = Math.Clamp(player.Time + (long)by.TotalMilliseconds, 0, Math.Max(0, player.Length - 500));
        player.Time = target;
        UpdateTime();
    }

    private void UpdateTime()
    {
        if (_player is null)
            return;
        long length = Math.Max(0, _player.Length), time = Math.Max(0, _player.Time);
        LengthText.Text = Clock(TimeSpan.FromMilliseconds(length));
        if (_dragging)
            return;
        TimeText.Text = Clock(TimeSpan.FromMilliseconds(time));
        _updatingTimeline = true;
        Timeline.Maximum = Math.Max(1, length / 1000.0);
        Timeline.Value = time / 1000.0;
        _updatingTimeline = false;
    }

    private void Timeline_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        SeekTo(Timeline.Value, force: true);
    }

    /// <summary>A click on the timeline jumps; dragging shows the picture there (a seek at most every 120 ms).</summary>
    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline)
            return;
        TimeText.Text = Clock(TimeSpan.FromSeconds(e.NewValue));
        SeekTo(e.NewValue, force: !_dragging);
    }

    private void SeekTo(double seconds, bool force)
    {
        if (_player is not { IsSeekable: true } player)
            return;
        if (!force && DateTime.UtcNow - _lastSeek < TimeSpan.FromMilliseconds(120))
            return;
        _lastSeek = DateTime.UtcNow;
        player.Time = (long)(seconds * 1000);
    }

    /// <summary>The time (and the filmstrip's picture) under the mouse, shown above the timeline.</summary>
    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_player is null || Timeline.ActualWidth <= 0 || Timeline.Maximum <= 1)
            return;
        var at = e.GetPosition(Timeline);
        ShowHover(Math.Clamp((at.X - 6) / Math.Max(1, Timeline.ActualWidth - 12), 0, 1)); // The thumb's half width at both ends.
    }

    /// <summary>The preview above the strip: the nearest picture of the filmstrip, bigger, and the time.</summary>
    private void ShowHover(double share)
    {
        if (_player is null || Timeline.Maximum <= 1)
            return;
        HoverText.Text = Clock(TimeSpan.FromSeconds(share * Timeline.Maximum));
        var picture = _filmstrip ? Strip.PictureAt(share) : null;
        HoverImage.Source = picture;
        HoverImage.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        double x = 6 + share * Math.Max(1, Timeline.ActualWidth - 12); // On the timeline.
        double imageHeight = picture is null ? 0 : 240.0 * picture.PixelHeight / Math.Max(1, picture.PixelWidth) + 4;
        double stripHeight = StripRow.IsVisible ? StripRow.ActualHeight + 4 : 0;
        HoverPopup.HorizontalOffset = x - (picture is null ? 20 : 124);
        HoverPopup.VerticalOffset = -(26 + imageHeight + stripHeight);
        HoverPopup.IsOpen = true;
    }

    /// <summary>
    /// The filmstrip of the shown video: kept ones at once, else made in the background (a second, silent VLC player;
    /// the strip fills while it works). The last 5 videos are kept in memory.
    /// </summary>
    private void StartFilmstrip()
    {
        if (!_filmstrip || _current is not { } path || _thumbnailer is null || _player is null || _player.Length <= 0)
            return;
        if (string.Equals(_stripFor, path, StringComparison.OrdinalIgnoreCase))
            return;
        _thumbCancel?.Cancel();
        _stripFor = path;
        if (_strips.TryGetValue(path, out var kept) && kept.All(p => p is not null))
        {
            Strip.Show(kept);
            return;
        }
        var pictures = new System.Windows.Media.Imaging.BitmapSource?[_stripCount];
        _strips[path] = pictures;
        _stripOrder.Remove(path);
        _stripOrder.Add(path);
        while (_stripOrder.Count > 5)
        {
            _strips.Remove(_stripOrder[0]);
            _stripOrder.RemoveAt(0);
        }
        Strip.Show(pictures);
        var cancel = _thumbCancel = new CancellationTokenSource();
        _ = _thumbnailer.RunAsync(path, _player.Length, _stripCount, (index, picture) => Dispatcher.BeginInvoke(() =>
        {
            pictures[index] = picture;
            if (string.Equals(_stripFor, path, StringComparison.OrdinalIgnoreCase))
                Strip.InvalidateVisual();
        }), cancel.Token);
    }

    private void Timeline_MouseLeave(object sender, MouseEventArgs e) => HoverPopup.IsOpen = false;

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || VolumeText is null)
            return;
        SetVolume((int)Math.Round(e.NewValue));
    }

    private void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_player is not null)
            _player.Volume = _volume;
        if (_muted && _volume > 0)
            SetMute(false);
        ShowVolume();
        StateChanged?.Invoke();
    }

    private void SetMute(bool muted)
    {
        _muted = muted;
        if (_player is not null)
            _player.Mute = muted;
        ShowVolume();
        StateChanged?.Invoke();
    }

    private void ShowVolume()
    {
        _updatingVolume = true;
        VolumeSlider.Value = _volume;
        _updatingVolume = false;
        VolumeText.Text = _muted ? "Muted" : $"{_volume} %";
        MuteIcon.Text = _muted || _volume == 0 ? "" : _volume < 34 ? "" : _volume < 67 ? "" : "";
    }

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Resume ----------

    private double SavedPosition(string path) =>
        _positions.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))?.Seconds ?? 0;

    private void ForgetPosition(string path) => _positions.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keeps where the current video is (newest first, 300 kept). Not for the first 10 seconds, and not in the last
    /// minute (the credits): a video watched to its end starts from the beginning next time.
    /// </summary>
    private void RememberPosition()
    {
        if (_current is null || _player is null)
            return;
        double time = _player.Time / 1000.0, length = _player.Length / 1000.0;
        ForgetPosition(_current);
        if (length <= 0 || time < 10 || time > length - 60 || _player.State == VLCState.Ended)
            return;
        _positions.Insert(0, new PlayerPositionState { Path = _current, Seconds = Math.Round(time, 1) });
        if (_positions.Count > PositionsKept)
            _positions.RemoveRange(PositionsKept, _positions.Count - PositionsKept);
    }

    // ---------- Audio track and subtitles ----------

    private void ShowTracks()
    {
        if (_player is null)
            return;
        var audio = _player.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
        AudioButton.IsEnabled = audio.Count > 1;
        AudioText.Text = audio.FirstOrDefault(t => t.Id == _player.AudioTrack).Name is { Length: > 0 } name ? Short(name) : "Audio";
        var spu = _player.SpuDescription.Where(t => t.Id >= 0).ToList();
        SubtitleButton.IsEnabled = spu.Count > 0;
        SubtitleText.Text = _player.Spu < 0 ? "Subtitles: off" : spu.FirstOrDefault(t => t.Id == _player.Spu).Name is { Length: > 0 } sub ? Short(sub) : "Subtitles";
    }

    private static string Short(string name) => name.Length > 28 ? name[..27] + "…" : name;

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;
        var menu = new ContextMenu { PlacementTarget = AudioButton, Placement = PlacementMode.Bottom };
        foreach (var track in _player.AudioTrackDescription.Where(t => t.Id >= 0))
        {
            int id = track.Id;
            var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = id == _player.AudioTrack };
            item.Click += (_, _) =>
            {
                _player.SetAudioTrack(id);
                ShowTracks();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = menu.Items.Count > 0;
    }

    private void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;
        var menu = new ContextMenu { PlacementTarget = SubtitleButton, Placement = PlacementMode.Bottom };
        var off = new MenuItem { Header = "Off", IsCheckable = true, IsChecked = _player.Spu < 0 };
        off.Click += (_, _) =>
        {
            _player.SetSpu(-1);
            ShowTracks();
        };
        menu.Items.Add(off);
        foreach (var track in _player.SpuDescription.Where(t => t.Id >= 0))
        {
            int id = track.Id;
            var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = id == _player.Spu };
            item.Click += (_, _) =>
            {
                _player.SetSpu(id);
                ShowTracks();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- Full screen ----------

    /// <summary>Full screen on / off: the bar goes, the controls lie over the picture and hide while the mouse rests.</summary>
    public void SetFullScreen(bool on)
    {
        if (IsFullScreen == on)
            return;
        IsFullScreen = on;
        TopBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(Controls, on ? 1 : 2);
        Controls.VerticalAlignment = on ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        FullScreenIcon.Text = on ? "" : "";
        FullScreenRequested?.Invoke(on);
        ShowControls();
        FocusView();
    }

    private void ShowControls()
    {
        Controls.Visibility = Visibility.Visible;
        Stage.Cursor = null;
        _hideTimer.Stop();
        if (IsFullScreen)
            _hideTimer.Start();
    }

    private void HideControls()
    {
        _hideTimer.Stop();
        if (!IsFullScreen || Controls.IsMouseOver || HoverPopup.IsOpen)
            return;
        Controls.Visibility = Visibility.Collapsed;
        Stage.Cursor = Cursors.None;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _lastMouse.X) + Math.Abs(at.Y - _lastMouse.Y) < 3)
            return; // A move of the picture under a resting mouse is not a move.
        _lastMouse = at;
        ShowControls();
    }

    private void Controls_MouseMove(object sender, MouseEventArgs e) => ShowControls();

    // ---------- Input ----------

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EnginePanel.IsVisible && EnginePanel.IsMouseOver)
            return;
        Focus();
        if (e.ClickCount == 2)
            SetFullScreen(!IsFullScreen);
        TogglePlay(); // A double-click toggles twice: it keeps playing, as in other players.
        e.Handled = true;
    }

    private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetVolume(_volume + (e.Delta > 0 ? 5 : -5));
        ShowToast(_muted ? "Muted" : $"Volume {_volume} %");
        e.Handled = true;
    }

    private void Stage_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedVideo(e.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Stage_Drop(object sender, DragEventArgs e)
    {
        if (DroppedVideo(e.Data) is { } video)
            _ = OpenAsync(video);
        e.Handled = true;
    }

    private static string? DroppedVideo(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(VideoFileTypes.IsVideo) : null;

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
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                Seek(-(modifiers == ModifierKeys.Control ? BigSeekStep : SeekStep));
                break;
            case Key.Right:
                Seek(modifiers == ModifierKeys.Control ? BigSeekStep : SeekStep);
                break;
            case Key.Up:
                SetVolume(_volume + 5);
                ShowToast($"Volume {_volume} %");
                break;
            case Key.Down:
                SetVolume(_volume - 5);
                ShowToast($"Volume {_volume} %");
                break;
            case Key.M:
                SetMute(!_muted);
                ShowToast(_muted ? "Muted" : $"Volume {_volume} %");
                break;
            case Key.F:
            case Key.F11:
            case Key.Enter when modifiers == ModifierKeys.Alt:
                SetFullScreen(!IsFullScreen);
                break;
            case Key.Escape when IsFullScreen:
                SetFullScreen(false);
                break;
            case Key.PageUp:
                _ = StepAsync(-1);
                break;
            case Key.PageDown:
                _ = StepAsync(1);
                break;
            case Key.Home when _player is { IsSeekable: true }:
                _player.Time = 0;
                UpdateTime();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        TogglePlay();
        FocusView();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => _ = StepAsync(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => _ = StepAsync(1);

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        SetMute(!_muted);
        FocusView();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!IsFullScreen);

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } path)
            ShowInExplorerRequested?.Invoke(path);
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } path)
            return;
        if (_player?.IsPlaying == true)
            _player.Pause();
        try
        {
            Process.Start(Shell.ShellLaunch.For(path));
        }
        catch (Exception)
        {
            ShowToast("Windows has no program for this file.");
        }
    }

    // ---------- Settings page ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool open)
    {
        SettingsButton.IsChecked = open;
        if (open)
        {
            if (_player?.IsPlaying == true)
                _player.Pause();
            SettingsPage.Open();
            SettingsPage.Visibility = Visibility.Visible;
        }
        else if (SettingsPage.IsVisible)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            ShowEngineState();
            FocusView();
        }
    }

    // ---------- Session ----------

    public PlayerState CaptureState()
    {
        RememberPosition();
        return new PlayerState { Current = _current, Volume = _volume, Muted = _muted, Positions = [.. _positions] };
    }

    public void RestoreState(PlayerState? state)
    {
        if (state is null)
            return;
        _volume = Math.Clamp(state.Volume, 0, 100);
        _muted = state.Muted;
        _positions.Clear();
        _positions.AddRange(state.Positions.Where(p => !string.IsNullOrEmpty(p.Path)).Take(PositionsKept));
        ShowVolume();
        if (state.Current is { } path && File.Exists(path))
        {
            _current = path;
            _pendingOpen = path; // Played when the view is shown (and the engine is there).
            ShowFile();
            StageText.Text = $"{Path.GetFileName(path)}\nPress Space to go on where you stopped.";
        }
    }

    /// <summary>The view is shown: a video from the last session is loaded (paused) so Space goes on with it.</summary>
    public async Task OnShownAsync()
    {
        if (_pendingOpen is { } waiting && VlcEngine.IsInstalled && _player is null)
        {
            _pendingOpen = null;
            await OpenAsync(waiting, paused: true);
        }
    }
}
