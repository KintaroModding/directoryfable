using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using DirectoryFable.Infrastructure;
using DirectoryFable.Shell;

namespace DirectoryFable.Views;

/// <summary>
/// "The name is already taken": the new and the existing item side by side (size, modified; the newer and the
/// larger one marked), and Replace / Replace if newer / Skip / Keep both, with "do the same for the other
/// conflicts" ("if newer" is then decided for each item by its own dates). Asked by
/// DirectoryFable before a copy or move starts, so the Windows copy engine never shows its own dialog.
/// </summary>
public partial class ConflictDialog : Window
{
    private ConflictChoice? _choice;

    private ConflictDialog(Conflict conflict, int number, int count, string verb)
    {
        InitializeComponent();
        DirectoryFable.Shell.DarkMode.PrepareWindow(this); // No white flash when it comes up.
        string name = Path.GetFileName(conflict.Target);
        string targetFolder = Path.GetFileName(Path.GetDirectoryName(conflict.Target)?.TrimEnd('\\') ?? "") is { Length: > 0 } f ? f : Path.GetDirectoryName(conflict.Target) ?? "";
        string sourceFolder = Path.GetFileName(Path.GetDirectoryName(conflict.Source)?.TrimEnd('\\') ?? "") is { Length: > 0 } s ? s : Path.GetDirectoryName(conflict.Source) ?? "";
        Title = $"{verb}: name already taken";
        CountText.Text = count > 1 ? $"Conflict {number} of {count}" : "";
        CountText.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HeadText.Text = conflict.TypeMismatch
            ? $"\"{name}\" in \"{targetFolder}\" is a {(Directory.Exists(conflict.Target) ? "folder" : "file")}, the new one a {(Directory.Exists(conflict.Source) ? "folder" : "file")}."
            : $"\"{name}\" is already in \"{targetFolder}\".";
        SourceTitle.Text = $"New (from {sourceFolder})";
        SourceTitle.ToolTip = conflict.Source;
        TargetTitle.Text = $"Already there (in {targetFolder})";
        TargetTitle.ToolTip = conflict.Target;

        var (sourceSize, sourceDate) = Info(conflict.Source);
        var (targetSize, targetDate) = Info(conflict.Target);
        SourceSize.Text = SizeText(sourceSize) + (sourceSize > targetSize && targetSize >= 0 ? "   larger" : "");
        TargetSize.Text = SizeText(targetSize) + (targetSize > sourceSize && sourceSize >= 0 ? "   larger" : "");
        // "newer" only from 2 seconds on (FAT drives keep times in 2-second steps).
        bool sourceNewer = sourceDate - targetDate >= TimeSpan.FromSeconds(2), targetNewer = targetDate - sourceDate >= TimeSpan.FromSeconds(2);
        SourceDate.Text = DateText(sourceDate) + (sourceNewer ? "   newer" : "");
        TargetDate.Text = DateText(targetDate) + (targetNewer ? "   newer" : "");
        SameText.Text = sourceSize == targetSize && !sourceNewer && !targetNewer && sourceSize >= 0 ? "Both have the same size and date." : "";
        SameText.Visibility = SameText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ReplaceButton.IsEnabled = !conflict.TypeMismatch;
        ReplaceHint.Text = conflict.TypeMismatch ? "Not possible: a file cannot replace a folder (or a folder a file)." : "The new one takes its place.";
        NewerButton.IsEnabled = !conflict.TypeMismatch;
        NewerHint.Text = conflict.TypeMismatch ? "Not possible here."
            : sourceNewer ? "Only when the new one is newer, else skip. Here: it replaces (the new one is newer)."
            : targetNewer ? "Only when the new one is newer, else skip. Here: it is skipped (the one there is newer)."
            : "Only when the new one is newer, else skip. Here: it is skipped (same date).";
        string folder = Path.GetDirectoryName(conflict.Target) ?? "";
        KeepBothHint.Text = $"The new one is named \"{ConflictPlanner.FreeName(folder, name)}\".";
        int others = count - number;
        AllBox.Content = others == 1 ? "Do the same for the other conflict" : $"Do the same for the other {others:N0} conflicts";
        AllBox.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Asks about one conflict; null = cancel the whole operation.</summary>
    public static (ConflictChoice Choice, bool ForAll)? Ask(IntPtr owner, Conflict conflict, int number, int count, bool move)
    {
        var dialog = new ConflictDialog(conflict, number, count, move ? "Move" : "Copy");
        if (HwndSource.FromHwnd(owner)?.RootVisual is Window window && window.IsVisible)
            dialog.Owner = window;
        else if (Application.Current?.MainWindow is { IsVisible: true } main)
            dialog.Owner = main;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._choice is { } choice ? (choice, dialog.AllBox.IsChecked == true) : null;
    }

    /// <summary>Size in bytes (-1 for a folder) and last write time; (-1, MinValue) when it cannot be read.</summary>
    private static (long Size, DateTime Date) Info(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return (-1, Directory.GetLastWriteTime(path));
            var file = new FileInfo(path);
            return (file.Length, file.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-1, DateTime.MinValue);
        }
    }

    private static string SizeText(long size) => size < 0 ? "Folder" : DisplayFormat.Size(size);

    private static string DateText(DateTime date) => date == DateTime.MinValue ? "" : date.ToString("G", CultureInfo.CurrentCulture);

    private void Choose(ConflictChoice choice)
    {
        _choice = choice;
        Close();
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Replace);

    private void ReplaceIfNewer_Click(object sender, RoutedEventArgs e) => Choose(ConflictChoice.ReplaceIfNewer);

    private void Skip_Click(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Skip);

    private void KeepBoth_Click(object sender, RoutedEventArgs e) => Choose(ConflictChoice.KeepBoth);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
