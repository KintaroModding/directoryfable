using System.IO;
using System.Windows;

namespace DirectoryFable.Views;

/// <summary>What the user chose in <see cref="DuplicateDialog"/>.</summary>
public enum DuplicateChoice
{
    Ok,
    Skip,
    Cancel,
}

/// <summary>
/// "Duplicate" (quick actions bar): asks for the name of the copy ("Copy As").
/// The new name is checked before the dialog closes (empty, invalid characters, already there).
/// </summary>
public partial class DuplicateDialog : Window
{
    private readonly string _folder;
    private DuplicateChoice _choice = DuplicateChoice.Cancel;

    private DuplicateDialog(string name, string folder, bool isDirectory, bool canSkip)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        _folder = folder;
        OldNameBox.Text = name;
        NewNameBox.Text = name;
        SkipButton.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NewNameBox.Focus();
            // The name before the extension is selected; folders: all of it.
            int dot = isDirectory ? -1 : name.LastIndexOf('.');
            NewNameBox.Select(0, dot > 0 ? dot : name.Length);
        };
    }

    /// <summary>Asks for the copy's name. <paramref name="canSkip"/>: more items follow (shows "Skip").</summary>
    public static (DuplicateChoice Choice, string NewName) Ask(Window? owner, string name, string folder, bool isDirectory, bool canSkip)
    {
        var dialog = new DuplicateDialog(name, folder, isDirectory, canSkip) { Owner = owner };
        dialog.ShowDialog();
        return (dialog._choice, dialog.NewNameBox.Text.Trim());
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string newName = NewNameBox.Text.Trim();
        string? error =
            newName.Length == 0 ? "Enter a name."
            : newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "The name contains a character that is not allowed (\\ / : * ? \" < > |)."
            : File.Exists(Path.Combine(_folder, newName)) || Directory.Exists(Path.Combine(_folder, newName)) ? "This name already exists in the folder."
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            NewNameBox.Focus();
            return;
        }
        _choice = DuplicateChoice.Ok;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _choice = DuplicateChoice.Skip;
        DialogResult = false;
    }

    private void NewNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ErrorText.Visibility = Visibility.Collapsed;
}
