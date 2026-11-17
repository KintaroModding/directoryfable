using System.Windows;

namespace DirectoryFable.Views;

/// <summary>A small "enter a text" dialog (e.g. rename a favorite).</summary>
public partial class InputDialog : Window
{
    private InputDialog(string prompt, string text)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = text;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null when cancelled.</summary>
    public static string? Ask(Window? owner, string prompt, string text)
    {
        var dialog = new InputDialog(prompt, text) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InputBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
