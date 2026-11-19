using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>One row of This PC: a drive or a recent folder.</summary>
public abstract class ThisPcItem : ObservableObject
{
    /// <summary>What a double-click opens: "C:\" or the folder's path.</summary>
    public string Root { get; init; } = "";

    /// <summary>"Devices and drives", "Network locations" or "Recent folders" (in this order).</summary>
    public string Group { get; init; } = "";

    /// <summary>Order inside the group.</summary>
    public string SortKey { get; init; } = "";
}

/// <summary>A folder opened lately (<see cref="RecentFolders"/>).</summary>
public sealed class RecentTile : ThisPcItem
{
    public string Name { get; init; } = "";

    /// <summary>After the name, dimmed: the folder that holds it.</summary>
    public string Folder { get; init; } = "";

    public string OpenedText { get; init; } = "";

    public string ToolTip => Root;

    public ImageSource? Icon { get; init; }
}

/// <summary>One drive of This PC.</summary>
public sealed class DriveTile : ThisPcItem
{

    private string _name = "", _detail = "", _line1 = "", _line2 = "", _toolTip = "", _health = "";
    private Brush? _healthBrush;
    private ImageSource? _icon;
    private double _used;
    private bool _ready;
    private bool _keepsHealthLine;

    public string Name { get => _name; set => SetField(ref _name, value); }

    /// <summary>After the name, dimmed: the share of a network drive, or the kind of a removable drive.</summary>
    public string Detail { get => _detail; set => SetField(ref _detail, value); }

    /// <summary>File system and how full ("NTFS   ·   54 % used"), or "Not ready".</summary>
    public string Line1 { get => _line1; set => SetField(ref _line1, value); }

    /// <summary>"116 GB free of 250 GB".</summary>
    public string Line2 { get => _line2; set => SetField(ref _line2, value); }

    public string ToolTip { get => _toolTip; set => SetField(ref _toolTip, value); }

    /// <summary>The disk's health: "Healthy · 57 °C · 100 % life left", or "" (not known, or not read yet).</summary>
    public string Health
    {
        get => _health;
        set
        {
            if (SetField(ref _health, value))
                OnPropertyChanged(nameof(HealthVisibility));
        }
    }

    /// <summary>Green when healthy, orange for a warning, red when unhealthy.</summary>
    public Brush? HealthBrush { get => _healthBrush; set => SetField(ref _healthBrush, value); }

    /// <summary>
    /// A drive on a disk that can have a health line (local and removable drives): the line keeps its place while it
    /// is empty, so the tiles do not grow and push the rows below down when the disk details arrive a moment later.
    /// </summary>
    public bool KeepsHealthLine
    {
        get => _keepsHealthLine;
        set
        {
            if (SetField(ref _keepsHealthLine, value))
                OnPropertyChanged(nameof(HealthVisibility));
        }
    }

    public Visibility HealthVisibility =>
        _health.Length > 0 ? Visibility.Visible : _keepsHealthLine ? Visibility.Hidden : Visibility.Collapsed;

    public ImageSource? Icon { get => _icon; set => SetField(ref _icon, value); }

    /// <summary>Used share, 0..1.</summary>
    public double Used
    {
        get => _used;
        set
        {
            if (!SetField(ref _used, value))
                return;
            OnPropertyChanged(nameof(UsedWidth));
            OnPropertyChanged(nameof(FreeWidth));
            OnPropertyChanged(nameof(IsAlmostFull));
        }
    }

    public bool Ready
    {
        get => _ready;
        set
        {
            if (SetField(ref _ready, value))
                OnPropertyChanged(nameof(BarVisibility));
        }
    }

    public GridLength UsedWidth => new(Math.Max(0.0001, _used), GridUnitType.Star);
    public GridLength FreeWidth => new(Math.Max(0.0001, 1 - _used), GridUnitType.Star);

    /// <summary>90 % or more used: the bar is red (as in Windows).</summary>
    public bool IsAlmostFull => _used >= 0.9;

    public Visibility BarVisibility => _ready ? Visibility.Visible : Visibility.Hidden;
}

/// <summary>
/// This PC in a pane: the drives in two groups ("Devices and drives", "Network locations") with icon, name, a
/// usage bar (red from 90 % used), file system, how full, and free of total; then "Recent folders", the folders
/// opened last (<see cref="RecentFolders"/>; how many is a setting, folders that are gone are left out).
/// Double-click or Enter opens a drive or folder, a middle click opens it in a new tab, right-click shows Windows'
/// menu for it (Properties, Eject, ...) with "Open in new tab", "Space usage" and, for a recent folder, "Remove
/// from this list".
/// While shown, it reads the drives again every few seconds (free space changes, drives come and go); a slow
/// network drive is read off the UI thread.
/// </summary>
public partial class DrivesView : UserControl
{
    private const string DevicesGroup = "Devices and drives";
    private const string NetworkGroup = "Network locations";
    private const string RecentGroup = "Recent folders";
    private const int CmdOpenInNewTab = ShellContextMenu.ExtraCommandFirst;
    private const int CmdSpace = ShellContextMenu.ExtraCommandFirst + 1;
    private const int CmdForget = ShellContextMenu.ExtraCommandFirst + 2;

    private readonly System.Collections.ObjectModel.ObservableCollection<ThisPcItem> _tiles = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _reading;
    private string? _focusRoot;

    public DrivesView()
    {
        InitializeComponent();
        var view = new ListCollectionView(_tiles);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DriveTile.Group)));
        view.SortDescriptions.Add(new SortDescription(nameof(DriveTile.Group), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ThisPcItem.SortKey), ListSortDirection.Ascending));
        List.ItemsSource = view;
        _timer.Tick += (_, _) => _ = ReadAsync();
        RecentFolders.Changed += () =>
        {
            if (IsVisible)
                _ = ReadAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _ = ReadAsync();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    /// <summary>A drive was chosen: open it (bool = in a new tab).</summary>
    public event Action<string, bool>? OpenRequested;

    /// <summary>Backspace: back to the last folder.</summary>
    public event Action? BackRequested;

    /// <summary>"Space usage" in a drive's menu: open its Space view.</summary>
    public event Action<string>? SpaceRequested;

    /// <summary>The selected drive ("C:\") or recent folder, or null.</summary>
    public string? SelectedRoot => (List.SelectedItem as ThisPcItem)?.Root;

    /// <summary>Shows the drives; <paramref name="focusRoot"/> (the drive one came up from) is selected.</summary>
    public void Show(string? focusRoot)
    {
        _focusRoot = focusRoot is { Length: >= 2 } ? focusRoot[..2].ToUpperInvariant() + "\\" : null;
        _ = ReadAsync();
    }

    public void FocusList()
    {
        if (List.SelectedItem is null && List.Items.Count > 0)
            List.SelectedIndex = 0;
        if (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            List.Focus();
    }

    // ---------- Reading ----------

    private sealed record DriveData(string Root, DriveType Type, bool Ready, string Label, string Format, long Total, long Free, string? Unc);

    private async Task ReadAsync()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            var recent = RecentFolders.List.ToList();
            var (drives, folders) = await Task.Run(() => (ReadDrives(), Existing(recent)));
            Apply(drives);
            ApplyRecent(folders);
        }
        finally
        {
            _reading = false;
        }
    }

    private static List<DriveData> ReadDrives()
    {
        var list = new List<DriveData>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? unc = drive.DriveType == DriveType.Network ? NetworkComputers.MappedTarget(drive.Name[..2]) : null;
            try
            {
                list.Add(drive.IsReady
                    ? new DriveData(drive.Name, drive.DriveType, true, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, unc)
                    : new DriveData(drive.Name, drive.DriveType, false, "", "", 0, 0, unc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                list.Add(new DriveData(drive.Name, drive.DriveType, false, "", "", 0, 0, unc));
            }
        }
        return list;
    }

    /// <summary>The recent folders that still exist (a network folder is not checked: an offline server is slow).</summary>
    private static List<RecentFolderState> Existing(List<RecentFolderState> recent) =>
        [.. recent.Where(r => IsNetworkPath(r.Path) || Directory.Exists(r.Path))];

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            return path.Length >= 2 && path[1] == ':' && new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The recent folder rows; built again only when the list changed (so a selected row stays selected).</summary>
    private void ApplyRecent(List<RecentFolderState> folders)
    {
        var shown = _tiles.OfType<RecentTile>().ToList();
        string Key(RecentFolderState r) => r.Path + "|" + r.Opened.Ticks;
        if (shown.Select(t => t.SortKey[5..]).SequenceEqual(folders.Select(Key), StringComparer.OrdinalIgnoreCase))
            return;
        bool wasSelected = List.SelectedItem is RecentTile;
        foreach (var tile in shown)
            _tiles.Remove(tile);
        for (int i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            string parent = Path.GetDirectoryName(folder.Path.TrimEnd('\\')) ?? "";
            _tiles.Add(new RecentTile
            {
                Root = folder.Path,
                Group = RecentGroup,
                SortKey = $"{i:D4}|{Key(folder)}",
                Name = PathHelper.GetName(folder.Path),
                Folder = parent.Length > 0 ? "   " + parent : "",
                OpenedText = DisplayFormat.Date(folder.Opened),
                Icon = ShellInfo.GetFolderIconFast(folder.Path),
            });
        }
        if (wasSelected && List.SelectedItem is null && _tiles.Count > 0)
            List.SelectedIndex = 0;
    }

    private void Apply(List<DriveData> drives)
    {
        var roots = new HashSet<string>(drives.Select(d => d.Root), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _tiles.OfType<DriveTile>().Where(t => !roots.Contains(t.Root)).ToList())
            _tiles.Remove(gone);
        foreach (var data in drives)
        {
            var tile = _tiles.OfType<DriveTile>().FirstOrDefault(t => string.Equals(t.Root, data.Root, StringComparison.OrdinalIgnoreCase));
            bool isNew = tile is null;
            tile ??= new DriveTile { Root = data.Root, SortKey = data.Root, Group = data.Type == DriveType.Network ? NetworkGroup : DevicesGroup };
            Fill(tile, data);
            if (isNew)
            {
                tile.Icon = ShellImages.ExtraLarge(data.Root);
                _tiles.Add(tile);
            }
        }
        if (_focusRoot is not null && _tiles.OfType<DriveTile>().FirstOrDefault(t => string.Equals(t.Root, _focusRoot, StringComparison.OrdinalIgnoreCase)) is { } focus)
        {
            List.SelectedItem = focus;
            _focusRoot = null;
        }
        else if (List.SelectedItem is null && _tiles.Count > 0)
        {
            List.SelectedIndex = 0;
        }
    }

    private static void Fill(DriveTile tile, DriveData data)
    {
        string letter = data.Root[..2].ToUpperInvariant();
        string share = data.Unc is { } unc ? unc.TrimEnd('\\')[(unc.TrimEnd('\\').LastIndexOf('\\') + 1)..] : "";
        string kind = data.Type switch
        {
            DriveType.Removable => "Removable drive",
            DriveType.CDRom => "CD / DVD drive",
            DriveType.Network => "Network drive",
            DriveType.Ram => "RAM disk",
            _ => "Local disk",
        };
        string label = data.Label.Length > 0 ? data.Label : share.Length > 0 ? share : kind;
        tile.Name = $"{label} ({letter})";
        // The disk behind it (not for network and CD / DVD drives): "SSD · NVMe", and its health.
        var disk = data.Type is DriveType.Fixed or DriveType.Removable ? DiskInfo.For(data.Root) : null;
        string diskKind = disk?.KindText ?? "";
        tile.Detail = data.Unc is { } path ? $"   {path}"
            : data.Type is DriveType.Removable or DriveType.CDRom or DriveType.Ram
                ? $"   {kind}{(diskKind.Length > 0 ? " · " + diskKind : "")}"
            : diskKind.Length > 0 ? $"   {diskKind}" : "";
        tile.KeepsHealthLine = data.Type is DriveType.Fixed or DriveType.Removable;
        tile.Health = disk?.HealthText ?? "";
        tile.HealthBrush = disk?.Health switch
        {
            "Healthy" => HealthyBrush,
            "Warning" => WarningBrush,
            "Unhealthy" => UnhealthyBrush,
            _ => null,
        };
        string diskTip = disk is null ? ""
            : $"\nDisk: {disk.Model}{(diskKind.Length > 0 ? $" ({diskKind})" : "")}" +
              (disk.HealthText.Length > 0 ? $"\nHealth: {disk.HealthText}" : "");
        tile.Ready = data.Ready && data.Total > 0;
        if (tile.Ready)
        {
            double used = 1 - (double)data.Free / data.Total;
            tile.Used = Math.Clamp(used, 0, 1);
            tile.Line1 = $"{data.Format}   ·   {used:P0} used";
            tile.Line2 = $"{DisplayFormat.Size(data.Free)} free of {DisplayFormat.Size(data.Total)}";
            tile.ToolTip = $"{data.Root}\n{DisplayFormat.Size(data.Total - data.Free)} used, {DisplayFormat.Size(data.Free)} free, {DisplayFormat.Size(data.Total)} in all{diskTip}";
        }
        else
        {
            tile.Used = 0;
            tile.Line1 = data.Type == DriveType.CDRom ? "No disc" : "Not ready";
            tile.Line2 = "";
            tile.ToolTip = data.Root + diskTip;
        }
    }

    private static readonly Brush HealthyBrush = Frozen(0x6C, 0xCB, 0x5F);
    private static readonly Brush WarningBrush = Frozen(0xF0, 0xB4, 0x29);
    private static readonly Brush UnhealthyBrush = Frozen(0xFF, 0x6B, 0x6B);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---------- Input ----------

    private ThisPcItem? TileAt(object source) =>
        ItemsControl.ContainerFromElement(List, (DependencyObject)source) is ListBoxItem { DataContext: ThisPcItem tile } ? tile : null;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && TileAt(e.OriginalSource) is { } tile)
            OpenRequested?.Invoke(tile.Root, false);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TileAt(e.OriginalSource) is { } tile)
        {
            e.Handled = true;
            OpenRequested?.Invoke(tile.Root, true);
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is ThisPcItem tile)
        {
            e.Handled = true;
            OpenRequested?.Invoke(tile.Root, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        else if (e.Key == Key.Back)
        {
            e.Handled = true;
            BackRequested?.Invoke();
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = ReadAsync();
        }
    }

    /// <summary>
    /// Windows' menu for the drive (Properties, Eject, Format, ...) or recent folder, plus "Open in new tab", "Space
    /// usage" and, for a recent folder, "Remove from this list".
    /// </summary>
    private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TileAt(e.OriginalSource) is not { } tile)
            return;
        e.Handled = true;
        List.SelectedItem = tile;
        IntPtr hwnd = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        Point screen = PointToScreen(e.GetPosition(this));
        try
        {
            bool recent = tile is RecentTile;
            string folder = recent ? Path.GetDirectoryName(tile.Root.TrimEnd('\\')) ?? tile.Root : tile.Root;
            List<(int, string)> extras = [(CmdOpenInNewTab, "Open in new tab"), (CmdSpace, "Space usage")];
            if (recent)
                extras.Add((CmdForget, "Remove from this list"));
            var result = ShellContextMenu.Show(hwnd, folder, [tile.Root], (int)screen.X, (int)screen.Y, extras,
                extendedVerbs: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                ctrlDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                shiftDown: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdOpenInNewTab)
                OpenRequested?.Invoke(tile.Root, true);
            else if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdSpace)
                SpaceRequested?.Invoke(tile.Root);
            else if (result.Outcome == ShellMenuOutcome.Extra && result.ExtraCommandId == CmdForget)
                RecentFolders.Remove(tile.Root);
        }
        catch (Exception)
        {
            // A failing shell extension must not take the app down.
        }
    }
}
