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

    private AppSettings _settings = new();
    private bool _filling;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>"Close" button or Esc.</summary>
    public event Action? CloseRequested;

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
            RestoreSession.IsChecked = _settings.RestoreSession;
            LeftPath.Text = _settings.LeftPath;
            RightPath.Text = _settings.RightPath;
            SevenZipFolder.Text = _settings.SevenZipFolder;
            ShowSevenZipStatus();
            ArchiveBackground.Text = _settings.ArchiveBackground;
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
        Save(s =>
        {
            s.StartMaximized = maximized;
            s.RestoreSession = restore;
        });
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
