using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// Own file properties window (row "ⓘ" button, Alt+Enter): the "Details" tab of Windows'
/// properties, read by <see cref="FileProperties"/>. Not modal, so several can stay open.
/// </summary>
public partial class PropertiesWindow : Window
{
    private readonly string _path;
    private List<PropertyGroup> _groups = [];

    private PropertiesWindow(string path, ImageSource? icon)
    {
        InitializeComponent();
        _path = path;
        string name = PathHelper.GetName(path);
        Title = $"Properties - {name}";
        ItemIcon.Source = icon;
        ItemName.Text = name;
        ItemFolder.Text = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        Loaded += async (_, _) => await LoadAsync();
    }

    public static void Open(Window? owner, string path, ImageSource? icon) =>
        new PropertiesWindow(path, icon) { Owner = owner }.Show();

    private async Task LoadAsync()
    {
        try
        {
            _groups = await FileProperties.ReadAsync(_path);
            Groups.ItemsSource = _groups;
            StatusText.Visibility = _groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = "Windows has no properties for this item.";
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = $"Cannot read the properties: {ex.Message}";
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
