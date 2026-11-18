using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// The dark copy / move progress window: from / to, the current item, progress, current and average speed,
/// elapsed and remaining time, items and size left; Pause and Cancel. Closing the window cancels. Several
/// operations at once each get a window, placed one below the other.
/// </summary>
public partial class FileProgressWindow : Window
{
    private static readonly List<FileProgressWindow> Open = [];

    /// <summary>Details open or closed: the next window starts the way the last one was left.</summary>
    private static bool _showDetails;

    private readonly OperationProgress _progress;
    private readonly DispatcherTimer _timer;
    private bool _finished;

    internal FileProgressWindow(OperationProgress progress)
    {
        InitializeComponent();
        _progress = progress;
        TaskbarItemInfo = new TaskbarItemInfo { ProgressState = TaskbarItemProgressState.Normal };
        if (Application.Current?.MainWindow is { IsVisible: true } main && main != this)
        {
            Owner = main;
            Loaded += (_, _) => Place(main);
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Open.Add(this);
        if (_showDetails)
        {
            DetailsButton.IsChecked = true;
            DetailsPanel.Visibility = Visibility.Visible;
            DetailsScale.ScaleY = 1;
            DetailsChevron.Text = "\uE70E"; // Chevron up
        }
        Refresh();
    }

    /// <summary>Details: the full paths slide open below (or closed again).</summary>
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        bool open = DetailsButton.IsChecked == true;
        _showDetails = open;
        DetailsChevron.Text = open ? "\uE70E" : "\uE70D";
        var slide = new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (open)
            DetailsPanel.Visibility = Visibility.Visible;
        else
            slide.Completed += (_, _) =>
            {
                if (DetailsButton.IsChecked != true)
                    DetailsPanel.Visibility = Visibility.Collapsed;
            };
        DetailsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, slide);
    }

    /// <summary>Right-click a full path: copy it.</summary>
    private void Path_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Text.Length: > 0 } text)
            return;
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text.Tag as string ?? text.Text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard is busy.
            }
        };
        text.ContextMenu = new ContextMenu { Items = { copy }, PlacementTarget = text, IsOpen = true };
        e.Handled = true;
    }

    /// <summary>Centered on the main window; a second one below the first.</summary>
    private void Place(Window main)
    {
        int index = Open.IndexOf(this);
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(main.Left + (main.ActualWidth - ActualWidth) / 2, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        double top = main.Top + (main.ActualHeight - ActualHeight) / 2 + index * (ActualHeight + 12);
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        if (main.WindowState == WindowState.Maximized)
        {
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = Math.Clamp(area.Top + (area.Height - ActualHeight) / 2 + index * (ActualHeight + 12), area.Top, area.Bottom - ActualHeight);
        }
    }

    /// <summary>The operation ended: close without cancelling.</summary>
    internal void Finish()
    {
        _finished = true;
        Close();
    }

    private void Refresh()
    {
        var s = _progress.Snapshot();
        string verb = s.Move ? "Moving" : "Copying";
        string count = s.ItemsTotal == 1 ? "1 item" : $"{s.ItemsTotal:N0} items";
        double share = s.SizeTotal > 0 ? Math.Min(1, (double)s.SizeDone / s.SizeTotal)
            : s.ItemsTotal > 0 ? Math.Min(1, (double)s.ItemsDone / s.ItemsTotal) : 0;
        HeadText.Text = s.Paused ? $"Paused - {verb.ToLowerInvariant()} {count}"
            : s.Counting ? $"{verb} - counting items..." : $"{verb} {count}";
        Title = s.Counting ? verb : $"{share:P0} - {verb}";
        FromRun.Text = s.From.Length > 0 ? s.From : "...";
        ToRun.Text = s.To.Length > 0 ? s.To : "...";
        FromPathText.Text = s.FromPath.Length > 0 ? s.FromPath : s.From;
        ToPathText.Text = s.ToPath.Length > 0 ? s.ToPath : s.To;
        string itemPath = s.ItemPath.Length > 0 ? s.ItemPath : s.Item;
        ItemPathText.Text = Relative(itemPath, s.FromPath);
        ItemPathText.Tag = itemPath; // "Copy path" copies the full path.
        ItemText.Text = s.Item;

        Bar.IsIndeterminate = s.Counting;
        Bar.Value = share;
        PercentText.Text = s.Counting ? "" : share.ToString("P0", CultureInfo.CurrentCulture);
        TaskbarItemInfo.ProgressState = s.Counting ? TaskbarItemProgressState.Indeterminate
            : s.Paused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
        TaskbarItemInfo.ProgressValue = share;

        SpeedText.Text = s.Counting || s.Elapsed.TotalSeconds < 0.5 ? "..."
            : $"{Speed(s.Paused ? 0 : s.CurrentSpeed)} now   ·   {Speed(s.AverageSpeed)} average";
        // Remaining time from the speed of the last seconds (the average reacts too slowly when it changes):
        // by bytes and by items, the longer one (many small files are slow although they hold few bytes).
        double speed = s.CurrentSpeed > 0 ? s.CurrentSpeed : s.AverageSpeed;
        double byBytes = speed > 0 && s.SizeTotal > s.SizeDone ? (s.SizeTotal - s.SizeDone) / speed : 0;
        double byItems = s.ItemsPerSecond > 0 && s.ItemsTotal > s.ItemsDone ? (s.ItemsTotal - s.ItemsDone) / s.ItemsPerSecond : 0;
        double left = Math.Max(byBytes, byItems);
        string remaining = s.Counting || left <= 0 ? "..." : "about " + Duration(TimeSpan.FromSeconds(Math.Ceiling(left)));
        TimeText.Text = $"{Duration(s.Elapsed)} elapsed   ·   {remaining} left";
        ulong itemsLeft = s.ItemsTotal > s.ItemsDone ? s.ItemsTotal - s.ItemsDone : 0;
        ItemsText.Text = s.Counting ? "..." : $"{itemsLeft:N0} {(itemsLeft == 1 ? "item" : "items")} ({DisplayFormat.Size((long)(s.SizeTotal - Math.Min(s.SizeTotal, s.SizeDone)))})";
        DoneText.Text = s.Counting ? "..." : $"{DisplayFormat.Size((long)s.SizeDone)} of {DisplayFormat.Size((long)s.SizeTotal)}";

        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseIcon.Text = s.Paused ? "" : ""; // Play / Pause
    }

    /// <summary>
    /// The item's path below the From folder, after "..." ("...\Game\Bundles\a.unit"): the From line above
    /// already shows the start. An item elsewhere keeps its full path.
    /// </summary>
    private static string Relative(string path, string from)
    {
        string root = from.TrimEnd('\\');
        return root.Length > 0 && path.Length > root.Length + 1 && path[root.Length] == '\\'
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? "..." + path[root.Length..]
                : path;
    }

    private static string Speed(double bytesPerSecond) => DisplayFormat.Size((long)Math.Max(0, bytesPerSecond)) + "/s";

    private static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _progress.TogglePause();
        Refresh();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _progress.Cancel();
        HeadText.Text = "Cancelling...";
        PauseButton.IsEnabled = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The X cancels the operation, as in Windows; the window closes when the engine has stopped.
        if (!_finished)
        {
            _progress.Cancel();
            HeadText.Text = "Cancelling...";
            PauseButton.IsEnabled = false;
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Open.Remove(this);
        base.OnClosed(e);
    }
}
