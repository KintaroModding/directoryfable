using System.Windows;
using System.Windows.Controls;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>App-wide action bar. "Undo" follows <see cref="UndoManager"/>.</summary>
public partial class ActionBar : UserControl
{
    public ActionBar()
    {
        InitializeComponent();
        UndoManager.Changed += Update;
        Update();
    }

    /// <summary>The main window runs the undo (it knows the window handle and the active pane).</summary>
    public event Action? UndoRequested;

    private void Update()
    {
        var next = UndoManager.Next;
        UndoButton.IsEnabled = next is not null;
        UndoButton.ToolTip = next is null ? "Nothing to undo" : $"Undo: {next.Description} (Ctrl+Z)";
        UndoHint.Text = next?.Description ?? "";
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();

    public event Action? RefreshBothRequested;

    /// <summary>The settings page shall open (true) or close (false).</summary>
    public event Action<bool>? SettingsToggled;

    /// <summary>Shows the gear button pressed while the page is open (also when Esc closed it).</summary>
    public bool SettingsOpen
    {
        get => SettingsButton.IsChecked == true;
        set => SettingsButton.IsChecked = value;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsToggled?.Invoke(SettingsButton.IsChecked == true);

    private void RefreshBoth_Click(object sender, RoutedEventArgs e) => RefreshBothRequested?.Invoke();
}
