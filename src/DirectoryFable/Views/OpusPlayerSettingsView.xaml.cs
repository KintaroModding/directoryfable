using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DirectoryFable.Infrastructure;
using DirectoryFable.Player;

namespace DirectoryFable.Views;

/// <summary>
/// OpusHC's own settings page (its Settings button): resume, the next video at the end, the video types
/// it opens, and VLC's engine (where it is, how big, Remove). Every change is saved to settings.json at once (on top
/// of the newest file, see AppSettings.Update) and applied.
/// </summary>
public partial class OpusPlayerSettingsView : UserControl
{
    private AppSettings _settings = new();
    private bool _filling;

    public OpusPlayerSettingsView()
    {
        InitializeComponent();
    }

    public event Action? CloseRequested;

    /// <summary>A setting changed and was saved: apply it.</summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>The engine was removed (the player shows its download panel again).</summary>
    public event Action? EngineChanged;

    /// <summary>Reads settings.json again and fills the controls.</summary>
    public void Open()
    {
        _settings = AppSettings.Load();
        _filling = true;
        try
        {
            ResumeBox.IsChecked = _settings.PlayerResume;
            AutoNextBox.IsChecked = _settings.PlayerAutoNext;
            FilmstripBox.IsChecked = _settings.PlayerFilmstrip;
            int step = Array.IndexOf(AppSettings.FilmstripCounts, _settings.PlayerFilmstripCount);
            StripCountSlider.Value = step >= 0 ? step : Array.IndexOf(AppSettings.FilmstripCounts, 40);
            ShowStripCount();
            OpensTypes.IsChecked = _settings.PlayerOpensFileTypes;
            TypesBox.Text = _settings.PlayerFileTypes;
            TypesBox.IsEnabled = _settings.PlayerOpensFileTypes;
        }
        finally
        {
            _filling = false;
        }
        ShowEngine();
    }

    /// <summary>Installed or not, where, how big.</summary>
    public void ShowEngine()
    {
        bool installed = VlcEngine.IsInstalled;
        EngineState.Text = installed
            ? $"Installed: VLC {VlcEngine.Version}, {DisplayFormat.Size(VlcEngine.InstalledSize())} in"
            : "Not downloaded yet: OpusHC shows the download. It would go to:";
        EngineFolder.Text = installed ? VlcEngine.BaseFolder : VlcEngine.Folder;
        RemoveButton.IsEnabled = installed;
        OpenFolderButton.IsEnabled = Directory.Exists(VlcEngine.BaseFolder);
    }

    private void Flag_Changed(object sender, RoutedEventArgs e)
    {
        bool resume = ResumeBox.IsChecked == true, next = AutoNextBox.IsChecked == true, strip = FilmstripBox.IsChecked == true;
        SaveAndApply(s =>
        {
            s.PlayerResume = resume;
            s.PlayerAutoNext = next;
            s.PlayerFilmstrip = strip;
        });
    }

    /// <summary>A step of the filmstrip slider: saved and applied at once (the shown video's strip is made again).</summary>
    private void StripCount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StripCountText is null)
            return;
        ShowStripCount();
        int count = AppSettings.FilmstripCounts[Math.Clamp((int)Math.Round(e.NewValue), 0, AppSettings.FilmstripCounts.Length - 1)];
        if (!_filling && count != _settings.PlayerFilmstripCount)
            SaveAndApply(s => s.PlayerFilmstripCount = count);
    }

    /// <summary>"80 pictures: one every 18 s of a 24-minute episode".</summary>
    private void ShowStripCount()
    {
        int count = AppSettings.FilmstripCounts[Math.Clamp((int)Math.Round(StripCountSlider.Value), 0, AppSettings.FilmstripCounts.Length - 1)];
        double every = 24 * 60.0 / count;
        StripCountText.Text = $"{count} pictures: one every {every:0} s of a 24-minute episode";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"Remove VLC's video engine?\n\nThe folder {VlcEngine.BaseFolder} is deleted. OpusHC then asks to download it again before it plays a video.",
            "OpusHC", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        SaveStatus.Text = VlcEngine.Remove()
            ? "VLC's engine was removed."
            : "VLC's engine is in use since a video was played: it is removed at the next start of DirectoryFable.";
        ShowEngine();
        EngineChanged?.Invoke();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{VlcEngine.BaseFolder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SaveStatus.Text = $"Cannot open the folder: {ex.Message}";
        }
    }

    // ---------- File types ----------

    private void OpensTypes_Changed(object sender, RoutedEventArgs e)
    {
        bool opens = OpensTypes.IsChecked == true;
        TypesBox.IsEnabled = opens;
        SaveAndApply(s => s.PlayerOpensFileTypes = opens);
    }

    private void TypesBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveTypes(TypesBox.Text);

    private void TypesDefault_Click(object sender, RoutedEventArgs e) => SaveTypes(VideoFileTypes.DefaultList);

    private void SaveTypes(string text)
    {
        if (_filling)
            return;
        string normalized = Editor.EditorFileTypes.Normalize(text);
        TypesBox.Text = normalized;
        if (normalized != _settings.PlayerFileTypes)
            SaveAndApply(s => s.PlayerFileTypes = normalized);
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
        SaveTypes(TypesBox.Text);
        CloseRequested?.Invoke();
    }
}
