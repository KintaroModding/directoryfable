using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Updates;

namespace DirectoryFable.Views;

/// <summary>
/// The update drop-down: asks GitHub for the app's releases, shows what changed since this version (the
/// release notes of every newer release, newest first) and updates on a click: download the release zip,
/// check it, put its exe in place of this one, start again. Without a newer release it shows what is new in
/// this version.
/// </summary>
public partial class UpdatePanel : UserControl
{
    private static readonly HttpClient Http = CreateClient();

    private List<Release> _releases = [];
    private Release? _newest;               // The newest release, when it is newer than this version.
    private string? _installed;             // A version this copy installed but has not started yet.
    private Task? _checking;
    private DateTime _checkedAt = DateTime.MinValue;
    private bool _checkedOk;                // The last check got GitHub's answer.
    private bool _busy;
    private bool _filling;

    public UpdatePanel()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateSource.CurrentVersion}";
        StatusText.Text = "Not checked yet.";
        Loaded += (_, _) => AppIcon.Source ??= IconFrame(48 * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>
    /// The app icon's picture for this size: an Image shows an .ico's first (16 px) picture, which looks
    /// blurry when enlarged; this takes the smallest picture that is at least <paramref name="pixels"/> wide.
    /// </summary>
    private static ImageSource? IconFrame(double pixels)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= pixels)
                ?? decoder.Frames.MaxBy(f => f.PixelWidth);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>The check found a newer version, the update was installed, or a step changed the state.</summary>
    public event Action? StateChanged;

    /// <summary>The new exe is in place: close and start it. False when the user cancelled closing.</summary>
    public event Func<bool>? RestartRequested;

    /// <summary>The "check at start" switch was changed in the drop-down.</summary>
    public event Action<bool>? CheckAtStartChanged;

    /// <summary>"Remove the update check..." was clicked (the main window asks and removes the button).</summary>
    public event Action? RemoveRequested;

    /// <summary>The last check succeeded and found no newer version.</summary>
    public bool IsUpToDate => _checkedOk && _newest is null && _installed is null;

    /// <summary>Shows the "check at start" setting (without saving it again).</summary>
    public void SetCheckAtStart(bool on)
    {
        _filling = true;
        CheckAtStartBox.IsChecked = on;
        _filling = false;
    }

    private void CheckAtStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling)
            CheckAtStartChanged?.Invoke(CheckAtStartBox.IsChecked == true);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke();

    /// <summary>A newer release is out (and not installed yet).</summary>
    public string? AvailableVersion => _installed is null ? _newest?.Version : null;

    /// <summary>A newer version is installed but this copy still runs.</summary>
    public string? InstalledVersion => _installed;

    public bool IsBusy => _busy;

    /// <summary>The last check is older than a minute (opening the drop-down checks again then).</summary>
    public bool IsCheckStale => DateTime.UtcNow - _checkedAt > TimeSpan.FromMinutes(1);

    /// <summary>Asks GitHub for the releases; a check that is running is joined.</summary>
    public Task CheckAsync()
    {
        if (_busy)
            return Task.CompletedTask;
        return _checking is { IsCompleted: false } running ? running : _checking = RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        StatusText.Text = "Checking GitHub for a new version...";
        CheckButton.IsEnabled = false;
        try
        {
            _releases = await Releases.FetchAsync(Http, UpdateSource.FeedUrl);
            _newest = _releases.FirstOrDefault(r => Releases.IsNewer(r.Version, UpdateSource.CurrentVersion));
            _checkedOk = true;
            ShowResult(failure: null);
        }
        catch (UpdateException ex)
        {
            _checkedOk = false;
            ShowResult(failure: ex.Message);
        }
        finally
        {
            _checkedAt = DateTime.UtcNow;
            CheckButton.IsEnabled = !_busy;
            StateChanged?.Invoke();
        }
    }

    private void ShowResult(string? failure)
    {
        ChangelogPanel.Children.Clear();
        HintText.Visibility = Visibility.Collapsed;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        if (_installed is { } installed)
        {
            StatusText.Text = $"Version {installed} is installed. Close DirectoryFable and start it again to use it.";
            return;
        }
        if (failure is not null)
        {
            StatusText.Text = $"The update check failed: {failure}";
            ChangelogBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (_releases.Count == 0)
        {
            StatusText.Text = "No release is published yet.";
            ChangelogBox.Visibility = Visibility.Collapsed;
            return;
        }

        if (_newest is { } newest)
        {
            StatusText.Text = newest.Date is { } date
                ? $"Version {newest.Version} is available (released {date.LocalDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})."
                : $"Version {newest.Version} is available.";
            // Every release since this version: someone who skipped one sees its changes too.
            AddChangelog(_releases.Where(r => Releases.IsNewer(r.Version, UpdateSource.CurrentVersion)));
            UpdateNowButton.Visibility = Visibility.Visible;
            UpdateNowText.Text = $"Update to {newest.Version}";
            string? reason = !UpdateSource.CanUpdateItself
                ? "This copy is a development build, so it cannot replace itself. Get the update from the release page."
                : newest.Asset is null
                    ? $"This release has no {UpdateSource.AssetName(newest.Version)}, so DirectoryFable cannot update itself. Use the release page."
                    : null;
            UpdateNowButton.IsEnabled = reason is null && !_busy;
            ShowHint(reason ?? "DirectoryFable downloads the release, checks it, replaces its program file and starts again. Settings, favorites and the session stay.");
            return;
        }

        StatusText.Text = "You have the newest version.";
        // What is new in this version (or in the newest release, when this build is none of them).
        var current = _releases.FirstOrDefault(r => Releases.Compare(r.Version, UpdateSource.CurrentVersion) == 0) ?? _releases[0];
        AddChangelog([current]);
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;
    }

    private void AddChangelog(IEnumerable<Release> releases)
    {
        var strong = (Brush)FindResource("ListTextBrush");
        bool first = true;
        foreach (var release in releases)
        {
            string date = release.Date is { } d ? "   ·   " + d.LocalDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = $"Version {release.Version}{date}",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = strong,
                Margin = new Thickness(0, first ? 0 : 18, 0, 2),
            });
            if (string.IsNullOrWhiteSpace(release.Notes))
                ChangelogPanel.Children.Add(new TextBlock { Text = "No notes for this release.", Margin = new Thickness(0, 2, 0, 0) });
            else
                ReleaseNotes.AddTo(ChangelogPanel, release.Notes, strong, OpenLink);
            first = false;
        }
        ChangelogBox.Visibility = ChangelogPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Buttons ----------

    private void Check_Click(object sender, RoutedEventArgs e) => _ = CheckAsync();

    private void ReleasePage_Click(object sender, RoutedEventArgs e) => OpenLink(_newest?.PageUrl ?? UpdateSource.ReleasesPageUrl);

    /// <summary>Downloads the release zip, checks it, puts its exe in place of this one and starts again.</summary>
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _newest is not { Asset: { } asset } release || Environment.ProcessPath is not { } exe || !UpdateSource.CanUpdateItself)
            return;
        _busy = true;
        UpdateNowButton.IsEnabled = CheckButton.IsEnabled = false;
        HintText.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StateChanged?.Invoke();
        string folder = Path.Combine(Path.GetTempPath(), "DirectoryFable-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            StatusText.Text = $"Downloading version {release.Version}...";
            var progress = new Progress<double>(share =>
            {
                Progress.Value = share;
                StatusText.Text = $"Downloading version {release.Version}...  {share:P0}";
            });
            string zip = await SelfUpdate.DownloadAsync(Http, asset, folder, progress);
            StatusText.Text = $"Installing version {release.Version}...";
            Progress.IsIndeterminate = true;
            await Task.Run(() => SelfUpdate.Replace(exe, SelfUpdate.ExtractExe(zip, Path.Combine(folder, "unpacked"), release.Version)));
            _installed = release.Version;
            StatusText.Text = $"Version {release.Version} is installed. DirectoryFable starts again...";
        }
        catch (UpdateException ex)
        {
            StatusText.Text = $"The update failed: {ex.Message}";
        }
        finally
        {
            SelfUpdate.TryDeleteFolder(folder);
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            _busy = false;
            CheckButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = _installed is null;
            StateChanged?.Invoke();
        }
        if (_installed is not null && RestartRequested?.Invoke() != true)
            ShowResult(failure: null); // Closing was cancelled: say how to finish.
    }

    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser: nothing to do.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            // Each request has its own time limit (release list 20 s, download: 60 s without data).
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // GitHub's API needs a User-Agent: the app's name and version, nothing about the user.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DirectoryFable/{UpdateSource.CurrentVersion}");
        return client;
    }
}
