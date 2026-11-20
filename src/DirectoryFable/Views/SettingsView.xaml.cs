using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Views;

/// <summary>
/// Settings page: opened by the gear button in the action bar, it covers
/// everything below that bar. Every valid change is written to settings.json at once.
/// </summary>
public partial class SettingsView : UserControl
{
    private const string DefaultArchiveBackground = "#0C0C0C";

    private static readonly int[] RecentCounts = [0, 5, 10, 15, 20, 30, 50, 100];
    private static readonly int[] CheckSeconds = [5, 10, 30, 60, 120, 300];
    private AppSettings _settings = new();
    private bool _filling;

    public SettingsView()
    {
        InitializeComponent();
        foreach (int count in RecentCounts)
            RecentCountBox.Items.Add(count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        foreach (int seconds in CheckSeconds)
            CheckSecondsBox.Items.Add(seconds.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>The seconds between two journal checks changed (the folder sizes follow at once).</summary>
    public event Action<int>? CheckSecondsChanged;

    /// <summary>The number of recent folders changed (This PC follows at once).</summary>
    public event Action<int>? RecentFoldersCountChanged;

    /// <summary>"Close" button or Esc.</summary>
    public event Action? CloseRequested;

    /// <summary>The update settings changed (the title bar's Update button follows at once).</summary>
    public event Action<AppSettings>? UpdateSettingsChanged;

    /// <summary>Folder sizes were switched on or off (the panes follow at once).</summary>
    public event Action<bool>? FolderSizesChanged;

    /// <summary>"Quick bar: icons only" was switched (the panes follow at once).</summary>
    public event Action<bool>? IconsOnlyChanged;

    /// <summary>"Folder tree: Network" was switched (the tree follows at once).</summary>
    public event Action<bool>? NetworkInTreeChanged;

    /// <summary>Reads settings.json again (it may have been edited by hand) and shows it.</summary>
    public void Open()
    {
        _settings = AppSettings.Load();
        _filling = true;
        try
        {
            FilePathRun.Text = Path.Combine(AppSettings.DataFolder, "settings.json");
            TrayMode.IsChecked = _settings.IsTrayMode;
            TaskbarMode.IsChecked = !_settings.IsTrayMode;
            StartMaximized.IsChecked = _settings.StartMaximized;
            StartHidden.IsChecked = _settings.StartHidden;
            RestoreSession.IsChecked = _settings.RestoreSession;
            FolderSizes.IsChecked = _settings.FolderSizes;
            CheckSecondsBox.Text = _settings.FolderSizesCheckSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);
            RecentCountBox.Text = _settings.RecentFoldersCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ShowUpdateButton.IsChecked = _settings.UpdateButton;
            UpdateCheckAtStart.IsChecked = _settings.UpdateCheckAtStart;
            UpdateCheckAtStart.IsEnabled = _settings.UpdateButton;
            LeftPath.Text = _settings.LeftPath;
            RightPath.Text = _settings.RightPath;
            SevenZipFolder.Text = _settings.SevenZipFolder;
            ShowSevenZipStatus();
            ArchiveBackground.Text = _settings.ArchiveBackground;
            IconsOnly.IsChecked = _settings.QuickBarIconsOnly;
            NetworkInTree.IsChecked = _settings.NetworkInTree;
            NewTabThisPc.IsChecked = _settings.NewTabThisPc;
            ShowSwatch(_settings.ArchiveBackground);
            SaveStatus.Text = "";
            ColumnsStatus.Text = "";
        }
        finally
        {
            _filling = false;
        }
        Dispatcher.BeginInvoke(() => (TrayMode.IsChecked == true ? TrayMode : TaskbarMode).Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Saves one change (on top of the newest settings.json, see AppSettings.Update).</summary>
    private void Save(Action<AppSettings> change)
    {
        if (_filling)
            return;
        change(_settings);
        try
        {
            _settings = AppSettings.Update(change);
            SaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = $"Cannot save: {ex.Message}";
        }
    }

    // ---------- Window ----------

    private void WindowMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return; // Open() is still filling the controls: their values are not complete yet.
        string mode = TaskbarMode.IsChecked == true ? "taskbar" : "tray";
        Save(s => s.WindowMode = mode);
    }

    private void Flag_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool maximized = StartMaximized.IsChecked == true, restore = RestoreSession.IsChecked == true;
        bool hidden = StartHidden.IsChecked == true;
        Save(s =>
        {
            s.StartMaximized = maximized;
            s.StartHidden = hidden;
            s.RestoreSession = restore;
        });
    }

    // ---------- Folder sizes: journal checks ----------

    private void CheckSeconds_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && CheckSecondsBox.SelectedItem is string text)
            ApplyCheckSeconds(text);
    }

    private void CheckSeconds_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyCheckSeconds(CheckSecondsBox.Text);

    private void CheckSeconds_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyCheckSeconds(CheckSecondsBox.Text);
        }
    }

    private void ApplyCheckSeconds(string text)
    {
        if (_filling)
            return;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, culture, out int seconds) || seconds < 2 || seconds > 600)
        {
            SaveStatus.Text = "Folder sizes: use a number of seconds from 2 to 600.";
            CheckSecondsBox.Text = _settings.FolderSizesCheckSeconds.ToString(culture);
            return;
        }
        if (seconds == _settings.FolderSizesCheckSeconds)
            return;
        Save(s => s.FolderSizesCheckSeconds = seconds);
        CheckSecondsChanged?.Invoke(seconds);
    }

    // ---------- This PC: recent folders ----------

    private void RecentCount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && RecentCountBox.SelectedItem is string text)
            ApplyRecentCount(text);
    }

    private void RecentCount_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyRecentCount(RecentCountBox.Text);

    private void RecentCount_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyRecentCount(RecentCountBox.Text);
        }
    }

    private void ApplyRecentCount(string text)
    {
        if (_filling)
            return;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, culture, out int count) || count < 0 || count > 100)
        {
            SaveStatus.Text = "Recent folders: use a number from 0 to 100.";
            RecentCountBox.Text = _settings.RecentFoldersCount.ToString(culture);
            return;
        }
        if (count == _settings.RecentFoldersCount)
            return;
        Save(s => s.RecentFoldersCount = count);
        RecentFoldersCountChanged?.Invoke(count);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        RecentFolders.Clear();
        SaveStatus.Text = $"Recent folders cleared at {DateTime.Now:HH:mm:ss}";
    }

    // ---------- Quick bar ----------

    private void IconsOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool on = IconsOnly.IsChecked == true;
        Save(s => s.QuickBarIconsOnly = on);
        IconsOnlyChanged?.Invoke(on);
    }

    // ---------- Tabs ----------

    private void NewTabThisPc_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool on = NewTabThisPc.IsChecked == true;
        Save(s => s.NewTabThisPc = on);
        Views.PaneView.NewTabThisPc = on;
    }

    // ---------- Folder tree ----------

    private void NetworkInTree_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool on = NetworkInTree.IsChecked == true;
        Save(s => s.NetworkInTree = on);
        NetworkInTreeChanged?.Invoke(on);
    }

    // ---------- Folder sizes ----------

    private void FolderSizes_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool on = FolderSizes.IsChecked == true;
        Save(s => s.FolderSizes = on);
        FolderSizesChanged?.Invoke(on);
    }

    // ---------- Updates ----------

    private void Update_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        bool button = ShowUpdateButton.IsChecked == true, atStart = UpdateCheckAtStart.IsChecked == true;
        UpdateCheckAtStart.IsEnabled = button;
        Save(s =>
        {
            s.UpdateButton = button;
            s.UpdateCheckAtStart = atStart;
        });
        UpdateSettingsChanged?.Invoke(_settings);
    }

    // ---------- Start folders ----------

    private void Path_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SavePaths();

    private void Path_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SavePaths();
        }
    }

    private void SavePaths()
    {
        string left = LeftPath.Text.Trim();
        string right = RightPath.Text.Trim();
        string sevenZip = SevenZipFolder.Text.Trim();
        if (left == _settings.LeftPath && right == _settings.RightPath && sevenZip == _settings.SevenZipFolder)
            return;
        // An empty box keeps the previous folder (an empty start folder makes no sense).
        if (left.Length == 0)
            left = _settings.LeftPath;
        if (right.Length == 0)
            right = _settings.RightPath;
        bool sevenZipChanged = sevenZip != _settings.SevenZipFolder;
        Save(s =>
        {
            s.LeftPath = left;
            s.RightPath = right;
            s.SevenZipFolder = sevenZip; // Empty is allowed (= automatic).
        });
        LeftPath.Text = _settings.LeftPath;
        RightPath.Text = _settings.RightPath;
        if (sevenZipChanged)
        {
            Shell.SevenZip.Configure(sevenZip); // Applies at once.
            ShowSevenZipStatus();
        }
    }

    /// <summary>Which 7-Zip is used now (or why none).</summary>
    private void ShowSevenZipStatus()
    {
        string? gui = Shell.SevenZip.GuiPath;
        string used = gui is null ? "7-Zip was not found." : $"Using {System.IO.Path.GetDirectoryName(gui)}";
        if (gui is not null && Shell.SevenZip.ConsolePath is null)
            used += " (7z.exe missing: 7z and RAR files cannot be opened)";
        SevenZipStatus.Text = _settings.SevenZipFolder.Length == 0
            ? $"Automatic. {used}"
            : Shell.SevenZip.ConfiguredFolderInvalid
                ? $"No 7zG.exe in this folder. {used}"
                : used;
        SevenZipStatus.Foreground = gui is null || Shell.SevenZip.ConfiguredFolderInvalid
            ? (System.Windows.Media.Brush)FindResource("ErrorTextBrush")
            : (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox box })
            return;
        string current = Environment.ExpandEnvironmentVariables(box.Text.Trim());
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = box == SevenZipFolder ? "Choose the 7-Zip folder" : "Choose the start folder",
            InitialDirectory = Directory.Exists(current) ? current : "",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            box.Text = dialog.FolderName;
            SavePaths();
        }
    }

    // ---------- Appearance ----------

    private void ArchiveBackground_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ArchiveBackground.Text.Trim();
        bool valid = ShowSwatch(text);
        ArchiveError.Visibility = valid || text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!valid || _filling || text == _settings.ArchiveBackground)
            return;
        Save(s => s.ArchiveBackground = text);
        App.ApplyColors(_settings);
    }

    private void ArchiveDefault_Click(object sender, RoutedEventArgs e) => ArchiveBackground.Text = DefaultArchiveBackground;

    /// <summary>Shows the color sample; false when the text is not a #RRGGBB color.</summary>
    private bool ShowSwatch(string text)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
        {
            ArchiveSwatch.Background = Brushes.Transparent;
            return false;
        }
        ArchiveSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        return true;
    }

    // ---------- Columns ----------

    /// <summary>Set by the main window: copies the left columns to the right pane, returns a message.</summary>
    public Func<string>? CopyColumnsLeftToRight { get; set; }

    private void CopyColumns_Click(object sender, RoutedEventArgs e) =>
        ColumnsStatus.Text = CopyColumnsLeftToRight?.Invoke() ?? "";

    // ---------- Page ----------

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.DataFolder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SaveStatus.Text = $"Cannot open the folder: {ex.Message}";
        }
    }

    /// <summary>Before the page closes: a path still being typed counts.</summary>
    public void Commit() => SavePaths();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
