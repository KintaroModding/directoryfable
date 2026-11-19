using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Views;

/// <summary>
/// KeeSeek's own settings page: when it locks, how long a copy stays in the clipboard, where the vault is, and a new
/// master password. The sliders are saved to settings.json at once (on top of the newest file) and applied.
/// </summary>
public partial class KeeSeekSettingsView : UserControl
{
    private AppSettings _settings = new();
    private bool _filling;

    public KeeSeekSettingsView()
    {
        InitializeComponent();
    }

    public event Action? CloseRequested;

    public event Action<AppSettings>? SettingsChanged;

    /// <summary>Set by KeeSeek: (current, new) -> null when changed, else the reason.</summary>
    public Func<string, string, Task<string?>>? ChangePassword { get; set; }

    public void Open(string vaultPath, bool unlocked)
    {
        _settings = AppSettings.Load();
        _filling = true;
        try
        {
            LockSlider.Value = Math.Clamp(_settings.KeeSeekLockMinutes, 0, 60);
            ClipboardSlider.Value = Math.Clamp(_settings.KeeSeekClipboardSeconds, 5, 120);
            RowHeightSlider.Value = Math.Clamp(_settings.KeeSeekRowHeight, KeeSeekView.MinRowHeight, KeeSeekView.MaxRowHeight);
        }
        finally
        {
            _filling = false;
        }
        ShowLock();
        ShowClipboard();
        ShowRowHeight();
        VaultPathBox.Text = vaultPath;
        OpenFolderButton.IsEnabled = Directory.Exists(Path.GetDirectoryName(vaultPath));
        PasswordFields.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        PasswordLocked.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
        CurrentBox.Clear();
        NewBox.Clear();
        RepeatBox.Clear();
        ChangeStatus.Text = "";
    }

    private void LockSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LockText is null)
            return;
        ShowLock();
        int minutes = (int)Math.Round(e.NewValue);
        if (!_filling && minutes != _settings.KeeSeekLockMinutes)
            SaveAndApply(s => s.KeeSeekLockMinutes = minutes);
    }

    private void ShowLock()
    {
        int minutes = (int)Math.Round(LockSlider.Value);
        LockText.Text = minutes == 0 ? "Off: locks when you leave KeeSeek"
            : minutes == 1 ? "After 1 minute without use" : $"After {minutes} minutes without use";
    }

    private void ClipboardSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ClipboardText is null)
            return;
        ShowClipboard();
        int seconds = (int)Math.Round(e.NewValue);
        if (!_filling && seconds != _settings.KeeSeekClipboardSeconds)
            SaveAndApply(s => s.KeeSeekClipboardSeconds = seconds);
    }

    private void ShowClipboard() => ClipboardText.Text = $"After {(int)Math.Round(ClipboardSlider.Value)} seconds";

    private void RowHeightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RowHeightText is null)
            return;
        ShowRowHeight();
        int height = (int)Math.Round(e.NewValue);
        if (!_filling && height != _settings.KeeSeekRowHeight)
            SaveAndApply(s => s.KeeSeekRowHeight = height);
    }

    private void ShowRowHeight()
    {
        int height = (int)Math.Round(RowHeightSlider.Value);
        RowHeightText.Text = height == 52 ? "52 pixels (default)" : $"{height} pixels";
    }

    private void SaveAndApply(Action<AppSettings> change)
    {
        change(_settings);
        try
        {
            _settings = AppSettings.Update(change);
            SaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            SettingsChanged?.Invoke(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = $"Cannot save: {ex.Message}";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(VaultPathBox.Text)}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SaveStatus.Text = $"Cannot open the folder: {ex.Message}";
        }
    }

    private async void Change_Click(object sender, RoutedEventArgs e)
    {
        if (ChangePassword is null)
            return;
        ChangeStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        if (NewBox.Password.Length == 0)
        {
            ChangeStatus.Text = "Enter the new master password.";
            return;
        }
        if (NewBox.Password != RepeatBox.Password)
        {
            ChangeStatus.Text = "The two new passwords are not the same.";
            return;
        }
        ChangeButton.IsEnabled = false;
        ChangeStatus.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
        ChangeStatus.Text = "Changing...";
        string? error;
        try
        {
            error = await ChangePassword(CurrentBox.Password, NewBox.Password);
        }
        finally
        {
            ChangeButton.IsEnabled = true;
        }
        if (error is not null)
        {
            ChangeStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            ChangeStatus.Text = error;
            return;
        }
        CurrentBox.Clear();
        NewBox.Clear();
        RepeatBox.Clear();
        ChangeStatus.Text = $"The master password was changed at {DateTime.Now:HH:mm:ss}. Use the new one from now on.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
