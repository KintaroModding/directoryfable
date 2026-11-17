using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DirectoryFable.Infrastructure;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// AstraViewer settings page: start zoom, background, checkerboard, history size, and the image
/// types that a double-click opens here. Every valid change is saved to settings.json at once
/// (on top of the newest file, see AppSettings.Update) and applied.
/// </summary>
public partial class AstraViewerSettingsView : UserControl
{
    private static readonly int[] Sizes = [10, 25, 50, 100, 200, 500, 1000];
    private AppSettings _settings = new();
    private bool _filling;

    public AstraViewerSettingsView()
    {
        InitializeComponent();
        foreach (int size in Sizes)
            HistorySizeBox.Items.Add(size.ToString(CultureInfo.CurrentCulture));
    }

    public event Action? CloseRequested;

    /// <summary>A setting changed and was saved: apply it.</summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>"Clear history" was clicked.</summary>
    public event Action? ClearHistoryRequested;

    /// <summary>Reads settings.json again and fills the controls.</summary>
    public void Open()
    {
        _settings = AppSettings.Load();
        _filling = true;
        try
        {
            ZoomActual.IsChecked = _settings.ViewerZoom == "actual";
            ZoomFit.IsChecked = _settings.ViewerZoom != "actual";
            BackBlack.IsChecked = _settings.ViewerBackground == "black";
            BackLight.IsChecked = _settings.ViewerBackground == "light";
            BackDark.IsChecked = _settings.ViewerBackground is not ("black" or "light");
            CheckerBox.IsChecked = _settings.ViewerCheckerboard;
            ThumbSmall.IsChecked = _settings.ViewerThumbnailSize == "small";
            ThumbLarge.IsChecked = _settings.ViewerThumbnailSize == "large";
            ThumbMedium.IsChecked = _settings.ViewerThumbnailSize is not ("small" or "large");
            HistorySizeBox.Text = _settings.ViewerHistorySize.ToString(CultureInfo.CurrentCulture);
            OpensTypes.IsChecked = _settings.ViewerOpensFileTypes;
            TypesBox.Text = _settings.ViewerFileTypes;
            TypesBox.IsEnabled = _settings.ViewerOpensFileTypes;
        }
        finally
        {
            _filling = false;
        }
    }

    private void Zoom_Checked(object sender, RoutedEventArgs e)
    {
        string zoom = ZoomActual.IsChecked == true ? "actual" : "fit";
        SaveAndApply(s => s.ViewerZoom = zoom);
    }

    private void Background_Checked(object sender, RoutedEventArgs e)
    {
        string background = BackBlack.IsChecked == true ? "black" : BackLight.IsChecked == true ? "light" : "dark";
        SaveAndApply(s => s.ViewerBackground = background);
    }

    private void Checker_Changed(object sender, RoutedEventArgs e)
    {
        bool checker = CheckerBox.IsChecked == true;
        SaveAndApply(s => s.ViewerCheckerboard = checker);
    }

    private void ThumbSize_Checked(object sender, RoutedEventArgs e)
    {
        string size = ThumbSmall.IsChecked == true ? "small" : ThumbLarge.IsChecked == true ? "large" : "medium";
        SaveAndApply(s => s.ViewerThumbnailSize = size);
    }

    // ---------- History ----------

    private void HistorySize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && HistorySizeBox.SelectedItem is string text)
            ApplyHistorySize(text);
    }

    private void HistorySize_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHistorySize(HistorySizeBox.Text);

    private void HistorySize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyHistorySize(HistorySizeBox.Text);
        }
    }

    private void ApplyHistorySize(string text)
    {
        if (_filling)
            return;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int size) || size < 10 || size > 1000)
        {
            SaveStatus.Text = "Images in the history: use a number from 10 to 1000.";
            HistorySizeBox.Text = _settings.ViewerHistorySize.ToString(CultureInfo.CurrentCulture);
            return;
        }
        if (size != _settings.ViewerHistorySize)
            SaveAndApply(s => s.ViewerHistorySize = size);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        ClearHistoryRequested?.Invoke();
        SaveStatus.Text = $"History cleared at {DateTime.Now:HH:mm:ss}";
    }

    // ---------- File types ----------

    private void OpensTypes_Changed(object sender, RoutedEventArgs e)
    {
        bool opens = OpensTypes.IsChecked == true;
        TypesBox.IsEnabled = opens;
        SaveAndApply(s => s.ViewerOpensFileTypes = opens);
    }

    private void TypesBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveTypes(TypesBox.Text);

    private void TypesDefault_Click(object sender, RoutedEventArgs e) => SaveTypes(ImageFileTypes.DefaultList);

    private void SaveTypes(string text)
    {
        if (_filling)
            return;
        string normalized = Editor.EditorFileTypes.Normalize(text);
        TypesBox.Text = normalized;
        if (normalized != _settings.ViewerFileTypes)
            SaveAndApply(s => s.ViewerFileTypes = normalized);
    }

    private void SaveAndApply(Action<AppSettings> change)
    {
        if (_filling)
            return;
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ApplyHistorySize(HistorySizeBox.Text);
        SaveTypes(TypesBox.Text);
        CloseRequested?.Invoke();
    }
}
