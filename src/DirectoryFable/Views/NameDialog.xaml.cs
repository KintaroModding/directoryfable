using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DirectoryFable.Views;

/// <summary>
/// Asks for the name of a file or folder, for "New folder" (Ctrl+Shift+N, the right-click menu, also Windows' own
/// "New > Folder") and for "Rename" (F2, the right-click menu). The name is checked while it is typed.
/// </summary>
public partial class NameDialog : Window
{
    private readonly string _folder;
    private readonly string? _oldName;      // Rename: the item's full name; null for a new folder.
    private readonly string _hidden;        // Rename of a shortcut: its ".lnk", not shown and added back.
    private readonly bool _isDirectory;

    private NameDialog(string folder, string? oldName, string hidden, bool isDirectory)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        _folder = folder;
        _oldName = oldName;
        _hidden = hidden;
        _isDirectory = isDirectory;
    }

    /// <summary>The name for a new folder in <paramref name="folder"/>, or null when cancelled.</summary>
    public static string? AskNewFolder(Window? owner, string folder)
    {
        var dialog = new NameDialog(folder, null, "", isDirectory: true) { Owner = owner };
        string shown = Path.GetFileName(folder.TrimEnd('\\'));
        dialog.Title = "New folder";
        dialog.PromptText.Text = $"Name of the new folder in {(shown.Length > 0 ? shown : folder)}:";
        dialog.PromptText.ToolTip = folder;
        dialog.OkButton.Content = "Create";
        dialog.Start(FreeName(folder), selectStem: false);
        return dialog.ShowDialog() == true ? dialog.FullName() : null;
    }

    /// <summary>
    /// The new name for a file or folder, or null when cancelled or unchanged. <paramref name="hidden"/>: an extension
    /// that is not shown (".lnk" of a shortcut); it is edited without it and gets it back.
    /// </summary>
    public static string? AskRename(Window? owner, string path, bool isDirectory, string hidden)
    {
        string folder = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        string name = Path.GetFileName(path.TrimEnd('\\'));
        var dialog = new NameDialog(folder, name, hidden, isDirectory) { Owner = owner };
        string shown = hidden.Length > 0 && name.EndsWith(hidden, StringComparison.OrdinalIgnoreCase) ? name[..^hidden.Length] : name;
        dialog.Title = "Rename";
        dialog.PromptText.Text = $"New name for {shown}:";
        dialog.PromptText.ToolTip = path;
        dialog.OkButton.Content = "Rename";
        // Explorer selects a file's name without its extension.
        dialog.Start(shown, selectStem: !isDirectory && hidden.Length == 0);
        string? result = dialog.ShowDialog() == true ? dialog.FullName() : null;
        return result is not null && result != name ? result : null;
    }

    private void Start(string text, bool selectStem)
    {
        NameBox.Text = text;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            int dot = selectStem ? text.LastIndexOf('.') : -1;
            NameBox.Select(0, dot > 0 ? dot : text.Length);
        };
    }

    /// <summary>The typed name with the hidden extension added back (unless it was typed).</summary>
    private string FullName()
    {
        string name = NameBox.Text.Trim();
        return name.Length > 0 && _hidden.Length > 0 && !name.EndsWith(_hidden, StringComparison.OrdinalIgnoreCase) ? name + _hidden : name;
    }

    /// <summary>"New folder", or "New folder (2)", ... when that name is taken.</summary>
    public static string FreeName(string folder)
    {
        string name = "New folder";
        for (int n = 2; Taken(folder, name) is not null && n < 10000; n++)
            name = $"New folder ({n})";
        return name;
    }

    private static string? Taken(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        return Directory.Exists(path) ? "A folder with this name is already here."
            : File.Exists(path) ? "A file with this name is already here."
            : null;
    }

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Why <paramref name="name"/> cannot be used in <paramref name="folder"/>; null = it can.
    /// <paramref name="oldName"/>: a rename's own old name (it may get other capital letters).
    /// </summary>
    public static string? Problem(string folder, string name, string? oldName = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Enter a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "These characters are not allowed in a name: \\ / : * ? \" < > |";
        if (name is "." or "..")
            return "This name is not allowed.";
        // Windows takes a dot at the end away: the item would get another name than typed.
        if (name.EndsWith('.'))
            return "A name cannot end with a dot.";
        string stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return $"\"{stem}\" is a name that Windows keeps for itself.";
        if (Path.Combine(folder, name).Length >= 32000)
            return "The path would be too long.";
        if (oldName is not null && name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            return null; // The item itself (only its capital letters change).
        return Taken(folder, name);
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string name = FullName();
        string? problem = Problem(_folder, name, _oldName);
        // An empty box is no mistake yet: only the button is off. An unchanged name has nothing to do.
        ProblemText.Text = problem is null || NameBox.Text.Trim().Length == 0 ? "" : problem;
        OkButton.IsEnabled = problem is null && name != _oldName;

        // Rename of a file: a note when its type (extension) changes, as Explorer warns.
        string note = "";
        if (problem is null && _oldName is not null && !_isDirectory && _hidden.Length == 0)
        {
            string before = Path.GetExtension(_oldName), after = Path.GetExtension(name);
            if (!before.Equals(after, StringComparison.OrdinalIgnoreCase))
                note = after.Length == 0 ? $"The name loses its file type ({before}): Windows may not know how to open the file."
                    : before.Length == 0 ? $"The file gets the type {after}: it may then open with another program."
                    : $"The file type changes from {before} to {after}: the file may then open with another program.";
        }
        NoteText.Text = note;
        NoteText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // The folder may have changed since the last key (another program made an item with this name).
        if (Problem(_folder, FullName(), _oldName) is { } problem)
        {
            ProblemText.Text = problem;
            OkButton.IsEnabled = false;
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
