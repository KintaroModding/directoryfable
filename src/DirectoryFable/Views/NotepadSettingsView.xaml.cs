using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Views;

/// <summary>
/// Notepad# settings page: editor font and size. Every valid change is
/// saved to settings.json at once and applied to the editor.
/// </summary>
public partial class NotepadSettingsView : UserControl
{
    private static readonly double[] Sizes = [8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32];
    private AppSettings _settings = new();
    private bool _filling;

    public NotepadSettingsView()
    {
        InitializeComponent();
        foreach (double size in Sizes)
            SizeBox.Items.Add(size.ToString(CultureInfo.CurrentCulture));
    }

    public event Action? CloseRequested;

    /// <summary>A setting changed and was saved: apply it.</summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>Reads settings.json again and fills the controls.</summary>
    public void Open()
    {
        _settings = AppSettings.Load();
        _filling = true;
        try
        {
            if (FontBox.ItemsSource is null)
                FontBox.ItemsSource = SortedFonts();
            FontBox.SelectedItem = FontBox.Items.Cast<FontFamily>()
                .FirstOrDefault(f => string.Equals(f.Source, _settings.EditorFontFamily, StringComparison.OrdinalIgnoreCase));
            SizeBox.Text = _settings.EditorFontSize.ToString(CultureInfo.CurrentCulture);
            GroupByFolder.IsChecked = _settings.NotepadAutoGroupBy == "folder";
            GroupByType.IsChecked = _settings.NotepadAutoGroupBy != "folder";
            OpensTypes.IsChecked = _settings.NotepadOpensFileTypes;
            TypesBox.Text = _settings.NotepadFileTypes;
            TypesBox.IsEnabled = _settings.NotepadOpensFileTypes;
            UpdatePreview();
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>All installed fonts, fixed-width ones first (they suit code), each group by name.</summary>
    private static List<FontFamily> SortedFonts()
    {
        var fonts = Fonts.SystemFontFamilies.ToList();
        return [.. fonts.OrderBy(f => IsFixedWidth(f) ? 0 : 1).ThenBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>"i" and "W" equally wide = a fixed-width (monospace) font.</summary>
    private static bool IsFixedWidth(FontFamily family)
    {
        try
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double Width(string text) => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 12, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
            return Math.Abs(Width("iiiii") - Width("WWWWW")) < 0.5;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || FontBox.SelectedItem is not FontFamily family)
            return;
        _settings.EditorFontFamily = family.Source;
        SaveAndApply();
    }

    private void SizeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && SizeBox.SelectedItem is string text)
            ApplySize(text);
    }

    private void SizeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplySize(SizeBox.Text);

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplySize(SizeBox.Text);
        }
    }

    private void ApplySize(string text)
    {
        if (_filling)
            return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double size) || size < 6 || size > 72)
        {
            SaveStatus.Text = "Font size: use a number from 6 to 72.";
            SizeBox.Text = _settings.EditorFontSize.ToString(CultureInfo.CurrentCulture);
            return;
        }
        if (Math.Abs(size - _settings.EditorFontSize) < 0.01)
            return;
        _settings.EditorFontSize = size;
        SaveAndApply();
    }

    // ---------- File list ----------

    private void AutoGroup_Checked(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        _settings.NotepadAutoGroupBy = GroupByFolder.IsChecked == true ? "folder" : "type";
        SaveAndApply();
    }

    // ---------- File types ----------

    private void OpensTypes_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling)
            return;
        _settings.NotepadOpensFileTypes = OpensTypes.IsChecked == true;
        TypesBox.IsEnabled = _settings.NotepadOpensFileTypes;
        SaveAndApply();
    }

    private void TypesBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveTypes(TypesBox.Text);

    private void TypesDefault_Click(object sender, RoutedEventArgs e) => SaveTypes(Editor.EditorFileTypes.DefaultList);

    private void SaveTypes(string text)
    {
        if (_filling)
            return;
        string normalized = Editor.EditorFileTypes.Normalize(text);
        TypesBox.Text = normalized;
        if (normalized == _settings.NotepadFileTypes)
            return;
        _settings.NotepadFileTypes = normalized;
        SaveAndApply();
    }

    private void SaveAndApply()
    {
        UpdatePreview();
        try
        {
            _settings.Save();
            SaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            SettingsChanged?.Invoke(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = $"Cannot save: {ex.Message}";
        }
    }

    private void UpdatePreview()
    {
        Preview.FontFamily = new FontFamily(_settings.EditorFontFamily);
        Preview.FontSize = Math.Clamp(_settings.EditorFontSize, 6, 72);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ApplySize(SizeBox.Text);
        SaveTypes(TypesBox.Text);
        CloseRequested?.Invoke();
    }
}
