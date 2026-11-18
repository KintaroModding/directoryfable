using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;

namespace DirectoryFable;

/// <summary>
/// The Update button left of the caption buttons and its drop-down (<see cref="Views.UpdatePanel"/>). With
/// "check at start" on, a quiet check a few seconds after the start lets the button show a green checkmark
/// (up to date) or the new version. Opening the drop-down checks again when the last check is older than a
/// minute. The button can be removed (no checks at all); the settings page brings it back.
/// </summary>
public partial class MainWindow
{
    private DateTime _updatePopupClosedAt;
    private DispatcherTimer? _startCheckTimer;

    private void InitUpdate(AppSettings settings)
    {
        UpdatePage.StateChanged += ShowUpdateState;
        UpdatePage.RestartRequested += () => Application.Current is App app && app.RestartAfterUpdate();
        UpdatePage.CheckAtStartChanged += on => SaveUpdateSettings(s => s.UpdateCheckAtStart = on);
        UpdatePage.RemoveRequested += RemoveUpdateButton;
        SettingsPage.UpdateSettingsChanged += s => ApplyUpdateSettings(s.UpdateButton, s.UpdateCheckAtStart, atStart: false);
        UpdatePopup.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                UpdatePopup.IsOpen = false;
                e.Handled = true;
            }
        };
        ShowUpdateState();
        ApplyUpdateSettings(settings.UpdateButton, settings.UpdateCheckAtStart, atStart: true);
    }

    /// <summary>
    /// Shows or hides the button and keeps the drop-down's switch in step. At the start (or when the button comes
    /// back) with "check at start" on, a quiet check follows a few seconds later.
    /// </summary>
    private void ApplyUpdateSettings(bool button, bool checkAtStart, bool atStart)
    {
        bool wasHidden = UpdateButton.Visibility != Visibility.Visible;
        UpdateButton.Visibility = button ? Visibility.Visible : Visibility.Collapsed;
        UpdatePage.SetCheckAtStart(checkAtStart);
        if (!button)
        {
            UpdatePopup.IsOpen = false;
            _startCheckTimer?.Stop();
            return;
        }
        if (checkAtStart && (atStart || wasHidden) && UpdatePage.IsCheckStale)
        {
            _startCheckTimer?.Stop();
            _startCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(atStart ? 4 : 1) };
            _startCheckTimer.Tick += (_, _) =>
            {
                _startCheckTimer.Stop();
                _ = UpdatePage.CheckAsync();
            };
            _startCheckTimer.Start();
        }
    }

    private void SaveUpdateSettings(Action<AppSettings> change)
    {
        try
        {
            AppSettings.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Views.MessageDialog.Show(this, $"The setting could not be saved: {ex.Message}", image: MessageBoxImage.Warning);
        }
    }

    /// <summary>"Remove the update check...": asks, then hides the button for good (settings.json "updateButton": false).</summary>
    private void RemoveUpdateButton()
    {
        UpdatePopup.IsOpen = false;
        var answer = Views.MessageDialog.Show(this,
            "Remove the Update button?\n\nDirectoryFable will no longer check GitHub for new versions, and the button leaves the title bar.\n\n" +
            "You can bring it back on the settings page (gear button), section Updates.",
            "Remove the update check", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        SaveUpdateSettings(s => s.UpdateButton = false);
        ApplyUpdateSettings(button: false, checkAtStart: false, atStart: false);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // A click on the button while the drop-down is open closes it (it closes on the press already).
        if (DateTime.UtcNow - _updatePopupClosedAt < TimeSpan.FromMilliseconds(300))
        {
            UpdateButton.IsChecked = false;
            return;
        }
        UpdatePopup.IsOpen = UpdateButton.IsChecked == true;
        if (UpdatePopup.IsOpen && UpdatePage.IsCheckStale)
            _ = UpdatePage.CheckAsync();
    }

    private void UpdatePopup_Closed(object sender, EventArgs e)
    {
        _updatePopupClosedAt = DateTime.UtcNow;
        UpdateButton.IsChecked = false;
    }

    /// <summary>
    /// "Update" with a download arrow (not checked, or the check failed), a green checkmark when up to date, and
    /// the new version in green when one is out.
    /// </summary>
    private void ShowUpdateState()
    {
        var green = (Brush)FindResource("ExpanderSignBrush");
        UpdateButtonIcon.Text = ""; // Download
        UpdateButtonIcon.ClearValue(ForegroundProperty);
        UpdateButton.ClearValue(ForegroundProperty);
        UpdateButtonText.Text = "Update";
        if (UpdatePage.InstalledVersion is { } installed)
        {
            UpdateButtonText.Text = "Restart to update";
            UpdateButton.ToolTip = $"Version {installed} is installed: close DirectoryFable and start it again";
            UpdateButton.Foreground = green;
        }
        else if (UpdatePage.AvailableVersion is { } version)
        {
            UpdateButtonText.Text = UpdatePage.IsBusy ? "Updating..." : $"Update {version}";
            UpdateButton.ToolTip = $"Version {version} is available: see what changed and update";
            UpdateButton.Foreground = green;
        }
        else if (UpdatePage.IsUpToDate)
        {
            UpdateButtonIcon.Text = ""; // CheckMark
            UpdateButtonIcon.Foreground = green;
            UpdateButton.ToolTip = $"Version {Updates.UpdateSource.CurrentVersion} is up to date";
        }
        else
        {
            UpdateButton.ToolTip = $"Version {Updates.UpdateSource.CurrentVersion}: check GitHub for a new version";
        }
    }
}
