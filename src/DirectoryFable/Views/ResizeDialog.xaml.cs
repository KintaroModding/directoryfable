using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DirectoryFable.Views;

/// <summary>AstraViewer's Resize: width and height in pixels (the other side follows with the aspect ratio kept).</summary>
public partial class ResizeDialog : Window
{
    /// <summary>Largest side and largest picture the resize makes (memory: 200 megapixels are 800 MB).</summary>
    private const int MaxSide = 30000;
    private const long MaxPixels = 200_000_000;

    private readonly int _width, _height;
    private bool _updating;

    private ResizeDialog(int width, int height)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        _width = width;
        _height = height;
        NowText.Text = $"Now: {width:N0} × {height:N0} px";
        _updating = true;
        WidthBox.Text = width.ToString(CultureInfo.CurrentCulture);
        HeightBox.Text = height.ToString(CultureInfo.CurrentCulture);
        _updating = false;
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    /// <summary>The new size, or null when cancelled.</summary>
    public static (int Width, int Height)? Ask(Window? owner, int width, int height)
    {
        var dialog = new ResizeDialog(width, height) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Read() is { } size ? size : null;
    }

    private (int Width, int Height)? Read()
    {
        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int width) ||
            !int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int height))
            return null;
        return (width, height);
    }

    private void WidthBox_TextChanged(object sender, TextChangedEventArgs e) => Follow(fromWidth: true);

    private void HeightBox_TextChanged(object sender, TextChangedEventArgs e) => Follow(fromWidth: false);

    /// <summary>With the aspect ratio kept, the other side follows the one typed in.</summary>
    private void Follow(bool fromWidth)
    {
        if (_updating || KeepRatio?.IsChecked != true)
        {
            Check();
            return;
        }
        var box = fromWidth ? WidthBox : HeightBox;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) || value <= 0)
        {
            Check();
            return;
        }
        _updating = true;
        if (fromWidth)
            HeightBox.Text = Math.Max(1, (int)Math.Round((double)value * _height / _width)).ToString(CultureInfo.CurrentCulture);
        else
            WidthBox.Text = Math.Max(1, (int)Math.Round((double)value * _width / _height)).ToString(CultureInfo.CurrentCulture);
        _updating = false;
        Check();
    }

    private void Percent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int percent))
            return;
        _updating = true;
        WidthBox.Text = Math.Max(1, (int)Math.Round(_width * percent / 100.0)).ToString(CultureInfo.CurrentCulture);
        HeightBox.Text = Math.Max(1, (int)Math.Round(_height * percent / 100.0)).ToString(CultureInfo.CurrentCulture);
        _updating = false;
        Check();
    }

    /// <summary>Shows what is wrong with the typed size (and keeps the Resize button off until it is right).</summary>
    private bool Check()
    {
        if (ProblemText is null || OkButton is null)
            return false;
        string problem = Read() switch
        {
            null => "Type whole numbers.",
            var (w, h) when w <= 0 || h <= 0 => "Width and height must be at least 1 px.",
            var (w, h) when w > MaxSide || h > MaxSide => $"At most {MaxSide:N0} px per side.",
            var (w, h) when (long)w * h > MaxPixels => $"At most {MaxPixels / 1_000_000:N0} megapixels.",
            var (w, h) when w == _width && h == _height => "That is the size it has now.",
            _ => "",
        };
        ProblemText.Text = problem;
        OkButton.IsEnabled = problem.Length == 0;
        return problem.Length == 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Check())
            DialogResult = true;
    }
}
