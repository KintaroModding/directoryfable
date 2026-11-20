using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DirectoryFable.Views;

/// <summary>
/// The app's message box: the same buttons, icons and results as <see cref="MessageBox"/>, in
/// the app's dark look. Enter = the default button, Esc = Cancel (or No / OK when there is no
/// Cancel), Ctrl+C copies the text.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageBoxResult _result;

    private MessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        Title = caption;
        MessageText.Text = text;

        (string glyph, Color color) = image switch
        {
            MessageBoxImage.Error => ("", Color.FromRgb(0xFF, 0x7B, 0x72)),
            MessageBoxImage.Warning => ("", Color.FromRgb(0xFF, 0xC8, 0x3D)),
            MessageBoxImage.Question => ("", Color.FromRgb(0x33, 0x99, 0xFF)),
            MessageBoxImage.Information => ("", Color.FromRgb(0x33, 0x99, 0xFF)),
            _ => ("", Colors.Transparent),
        };
        IconText.Text = glyph;
        IconText.Foreground = new SolidColorBrush(color);
        if (glyph.Length == 0)
            IconText.Visibility = Visibility.Collapsed;

        MessageBoxResult[] results = buttons switch
        {
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        // Esc and the window's X: Cancel, else No, else OK.
        _result = results.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
            : results.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        if (!results.Contains(defaultResult))
            defaultResult = results[0];

        Button? focus = null;
        foreach (var result in results)
        {
            var button = new Button
            {
                Content = result.ToString(),
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = result == defaultResult,
                IsCancel = result == _result,
            };
            button.Click += (_, _) =>
            {
                _result = result;
                DialogResult = true;
            };
            ButtonPanel.Children.Add(button);
            if (result == defaultResult)
                focus = button;
        }
        Loaded += (_, _) => focus?.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                try
                {
                    Clipboard.SetText($"{caption}\n\n{text}");
                }
                catch (COMException)
                {
                }
            }
        };
    }

    /// <summary>
    /// Shows the dark message box and returns the clicked button, like <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult)"/>.
    /// Without a visible owner it uses the main window, or the middle of the screen.
    /// </summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption = "DirectoryFable",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new MessageDialog(text, caption, buttons, image, defaultResult);
        owner = owner is { IsVisible: true } ? owner
            : Application.Current?.MainWindow is { IsVisible: true } main && main != dialog ? main : null;
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        switch (image)
        {
            case MessageBoxImage.Error:
                SystemSounds.Hand.Play();
                break;
            case MessageBoxImage.Warning:
                SystemSounds.Exclamation.Play();
                break;
            case MessageBoxImage.Information:
                SystemSounds.Asterisk.Play();
                break;
        }
        dialog.ShowDialog();
        return dialog._result;
    }
}
