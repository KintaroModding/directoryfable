using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// Own file properties window (row "ⓘ" button, Alt+Enter): first "General" as Windows' General tab
/// (<see cref="GeneralProperties"/>: type, location, size and size on disk, what a folder contains, dates,
/// attributes; a folder is counted in the background), then the "Details" tab of Windows' properties, read by
/// <see cref="FileProperties"/>. Below the name, the attributes Read-only, Hidden, System and Archive as check boxes
/// that change the item at once (a folder: only the folder itself, without Read-only). Not modal, so several can
/// stay open.
/// </summary>
public partial class PropertiesWindow : Window
{
    private readonly string _path;
    private readonly System.Collections.ObjectModel.ObservableCollection<PropertyGroup> _groups = [];
    private readonly GeneralProperties.FolderTotals? _totals;
    private readonly CancellationTokenSource _closing = new();
    private System.Windows.Threading.DispatcherTimer? _countTimer;

    private PropertiesWindow(string path, ImageSource? icon)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        _path = path;
        string name = PathHelper.GetName(path);
        Title = $"Properties - {name}";
        ItemIcon.Source = icon;
        ItemName.Text = name;
        ItemFolder.Text = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        Groups.ItemsSource = _groups;
        if (Directory.Exists(path) && Path.GetPathRoot(path)?.TrimEnd('\\') != path.TrimEnd('\\'))
            _totals = new GeneralProperties.FolderTotals();
        ShowGeneral();
        ShowAttributes();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _countTimer?.Stop();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>The General section (first); while a folder is counted, it is renewed four times a second.</summary>
    private void ShowGeneral()
    {
        PropertyGroup general;
        try
        {
            general = GeneralProperties.For(_path, _totals);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            general = new PropertyGroup("General", [new PropertyLine("Error", ex.Message)]);
        }
        if (_groups.Count > 0 && _groups[0].Name == "General")
            _groups[0] = general;
        else
            _groups.Insert(0, general);
    }

    // ---------- Attributes ----------

    private bool _isFolder;

    /// <summary>The check boxes from the item's attributes (not for drive roots, which have none to change).</summary>
    private void ShowAttributes()
    {
        FileAttributes attributes;
        try
        {
            if (Path.GetPathRoot(_path)?.TrimEnd('\\') == _path.TrimEnd('\\'))
                return;
            attributes = File.GetAttributes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }
        _isFolder = (attributes & FileAttributes.Directory) != 0;
        AttributesPanel.Visibility = Visibility.Visible;
        ReadOnlyBox.Visibility = _isFolder ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyBox.IsChecked = (attributes & FileAttributes.ReadOnly) != 0;
        HiddenBox.IsChecked = (attributes & FileAttributes.Hidden) != 0;
        SystemBox.IsChecked = (attributes & FileAttributes.System) != 0;
        ArchiveBox.IsChecked = (attributes & FileAttributes.Archive) != 0;
    }

    /// <summary>A click on an attribute: set or clear it on the item now; on an error the box goes back.</summary>
    private void Attribute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { Tag: string tag } box || !Enum.TryParse(tag, out FileAttributes flag))
            return;
        bool on = box.IsChecked == true;
        try
        {
            var attributes = File.GetAttributes(_path);
            attributes = on ? attributes | flag : attributes & ~flag;
            File.SetAttributes(_path, attributes);
            ShowStatus($"{box.Content} {(on ? "set" : "cleared")}.", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            box.IsChecked = !on;
            ShowStatus($"Cannot change \"{box.Content}\": {ex.Message}", error: true);
        }
        ShowAttributes();
        ShowGeneral(); // The "Attributes" line follows.
    }

    private void ShowStatus(string text, bool error)
    {
        AttributesStatus.Text = text;
        AttributesStatus.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "SecondaryTextBrush");
        AttributesStatus.Visibility = Visibility.Visible;
    }

    private void StartCounting()
    {
        if (_totals is null)
            return;
        _ = GeneralProperties.CountAsync(_path, _totals, _closing.Token);
        _countTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _countTimer.Tick += (_, _) =>
        {
            ShowGeneral();
            if (_totals.Done)
                _countTimer.Stop();
        };
        _countTimer.Start();
    }

    public static void Open(Window? owner, string path, ImageSource? icon) =>
        new PropertiesWindow(path, icon) { Owner = owner }.Show();

    private async Task LoadAsync()
    {
        StartCounting();
        StatusText.Visibility = Visibility.Collapsed; // General is there at once.
        try
        {
            foreach (var group in await FileProperties.ReadAsync(_path))
                _groups.Add(group);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            _groups.Add(new PropertyGroup("Details", [new PropertyLine("Error", $"Cannot read the properties: {ex.Message}")]));
        }
    }

    /// <summary>All properties as text: "Group", then "Name: value" lines.</summary>
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder().AppendLine(_path);
        foreach (var group in _groups)
        {
            text.AppendLine().AppendLine(group.Name);
            foreach (var line in group.Lines)
                text.AppendLine($"{line.Name}: {line.Value}");
        }
        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (COMException)
        {
            // Clipboard busy: nothing copied.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
