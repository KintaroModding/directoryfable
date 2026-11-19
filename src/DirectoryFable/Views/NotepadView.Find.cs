using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DirectoryFable.Search;
using ICSharpCode.AvalonEdit.Search;

namespace DirectoryFable.Views;

/// <summary>One hit of Find in files, as the list shows it.</summary>
public sealed class FindHit(ContentMatch match, string relativePath)
{
    public ContentMatch Match { get; } = match;

    /// <summary>"Views\NotepadView.cs:42".</summary>
    public string Where { get; } = $"{relativePath}:{match.Line}";

    /// <summary>The line, without leading spaces, cut at 300 characters.</summary>
    public string Text { get; } = Shorten(match.LineText.TrimStart());

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>
/// Notepad#'s Replace (in the Ctrl+F box: Ctrl+H or its arrow) and Find in files (Ctrl+Shift+F: a panel above the
/// status line; a folder with its subfolders, a file pattern, the same options as the search box; a double-click
/// opens a hit at its line).
/// </summary>
public partial class NotepadView
{
    private const int MaxHits = 5000;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "node_modules", "bin", "obj" };

    private SearchPanel? _search;
    private TextBox? _replaceBox;
    private readonly ObservableCollection<FindHit> _hits = [];
    private CancellationTokenSource? _finding;

    // ---------- Replace ----------

    /// <summary>The replace row's parts live in the search box's template (Themes/Controls.xaml); hooked once.</summary>
    private void HookReplace(SearchPanel panel)
    {
        if (_replaceBox is not null)
            return;
        panel.ApplyTemplate();
        if (panel.Template?.FindName("ReplaceBox", panel) is not TextBox box)
            return;
        _replaceBox = box;
        if (panel.Template.FindName("ReplaceToggle", panel) is ButtonBase toggle)
            toggle.Click += (_, _) => SetReplaceMode(panel.Tag as string != "replace", focusReplace: true);
        if (panel.Template.FindName("ReplaceOneButton", panel) is ButtonBase one)
            one.Click += (_, _) => ReplaceNext();
        if (panel.Template.FindName("ReplaceAllButton", panel) is ButtonBase all)
            all.Click += (_, _) => ReplaceAll();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
                ReplaceAll();
            else
                ReplaceNext();
        };
    }

    /// <summary>
    /// Opens the search box with (or without) its replace row. focusReplace null: the replace box gets the focus
    /// when there is something to find already, else the find box.
    /// </summary>
    private void SetReplaceMode(bool replace, bool? focusReplace = null)
    {
        if (_search is null || Editor.Visibility != Visibility.Visible)
            return;
        _search.Tag = replace ? "replace" : null;
        if (_search.IsClosed)
        {
            string selected = Editor.SelectedText;
            if (selected.Length > 0 && !selected.Contains('\n'))
                _search.SearchPattern = selected;
            _search.Open();
        }
        HookReplace(_search);
        bool toReplace = focusReplace ?? _search.SearchPattern.Length > 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (replace && toReplace && _replaceBox is not null)
            {
                _replaceBox.Focus();
                _replaceBox.SelectAll();
            }
            else
            {
                _search.Reactivate();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The search box's pattern as AvalonEdit's search (null + a message when it is empty or a broken regex).</summary>
    private ISearchStrategy? Strategy()
    {
        if (_search is null || _search.SearchPattern.Length == 0)
            return null;
        try
        {
            return SearchStrategyFactory.Create(_search.SearchPattern, !_search.MatchCase, _search.WholeWords,
                _search.UseRegex ? SearchMode.RegEx : SearchMode.Normal);
        }
        catch (SearchPatternException ex)
        {
            ShowMessage($"Not a valid regular expression: {ex.Message}");
            return null;
        }
    }

    /// <summary>Replaces the selected match (when the selection is one) and goes to the next match.</summary>
    private void ReplaceNext()
    {
        if (Strategy() is not { } strategy || _search is null || Editor.IsReadOnly)
            return;
        var document = Editor.Document;
        var selection = Editor.TextArea.Selection;
        if (!selection.IsEmpty)
        {
            var segment = selection.SurroundingSegment;
            var match = strategy.FindAll(document, segment.Offset, segment.Length)
                .FirstOrDefault(r => r.Offset == segment.Offset && r.Length == segment.Length);
            if (match is not null)
            {
                string replacement = match.ReplaceWith(_replaceBox?.Text ?? "");
                document.Replace(match.Offset, match.Length, replacement);
                Editor.Select(match.Offset + replacement.Length, 0);
            }
        }
        _search.FindNext();
    }

    /// <summary>Replaces every match in the file (one step for Undo).</summary>
    private void ReplaceAll()
    {
        if (Strategy() is not { } strategy || Editor.IsReadOnly)
            return;
        var document = Editor.Document;
        var matches = strategy.FindAll(document, 0, document.TextLength).ToList();
        if (matches.Count == 0)
        {
            ShowMessage("No matches");
            return;
        }
        string replaceWith = _replaceBox?.Text ?? "";
        using (document.RunUpdate())
        {
            for (int i = matches.Count - 1; i >= 0; i--)
                document.Replace(matches[i].Offset, matches[i].Length, matches[i].ReplaceWith(replaceWith));
        }
        ShowMessage($"{matches.Count:N0} {(matches.Count == 1 ? "match" : "matches")} replaced");
    }

    // ---------- Find in files ----------

    private void ShowFindPanel()
    {
        FindPanel.Visibility = Visibility.Visible;
        if (FindFolderBox.Text.Trim().Length == 0 || !Directory.Exists(FindFolderBox.Text.Trim()))
        {
            string? folder = Active is { IsUntitled: false } document ? Path.GetDirectoryName(document.FilePath) : null;
            if (folder is not null)
                FindFolderBox.Text = folder;
        }
        string selected = Editor.Visibility == Visibility.Visible ? Editor.SelectedText : "";
        if (selected.Length > 0 && !selected.Contains('\n'))
            FindTextBox.Text = selected;
        Dispatcher.BeginInvoke(() =>
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseFindPanel_Click(object sender, RoutedEventArgs e)
    {
        _finding?.Cancel();
        FindPanel.Visibility = Visibility.Collapsed;
        FocusEditor();
    }

    private void BrowseFindFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Find in this folder (and its subfolders)", InitialDirectory = FindFolderBox.Text.Trim() };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            FindFolderBox.Text = dialog.FolderName;
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = FindInFilesAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFindPanel_Click(sender, e);
        }
    }

    private void FindInFiles_Click(object sender, RoutedEventArgs e) => _ = FindInFilesAsync();

    /// <summary>Searches the folder and its subfolders (not .git, .vs, node_modules, bin, obj); at most 5,000 hits.</summary>
    private async Task FindInFilesAsync()
    {
        _finding?.Cancel();
        string folder = FindFolderBox.Text.Trim().Trim('"');
        Regex? pattern;
        try
        {
            pattern = ContentSearch.Build(FindTextBox.Text, FindCaseToggle.IsChecked == true, FindWordsToggle.IsChecked == true,
                FindRegexToggle.IsChecked == true);
        }
        catch (ArgumentException ex)
        {
            FindStatus.Text = $"Not a valid regular expression: {ex.Message}";
            return;
        }
        if (pattern is null)
        {
            FindStatus.Text = "Type what to find.";
            return;
        }
        if (!Directory.Exists(folder))
        {
            FindStatus.Text = "Choose a folder that exists.";
            return;
        }
        var masks = FindMaskBox.Text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (masks.Length == 0)
            masks = ["*"];

        var cancel = _finding = new CancellationTokenSource();
        var token = cancel.Token;
        _hits.Clear();
        FindStatus.Text = "Searching...";
        int files = 0, filesWithHits = 0, hits = 0;
        var batch = new List<FindHit>();
        var progress = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        void Flush()
        {
            lock (batch)
            {
                foreach (var hit in batch)
                    _hits.Add(hit);
                batch.Clear();
            }
            FindStatus.Text = $"Searching... {hits:N0} {(hits == 1 ? "hit" : "hits")} in {filesWithHits:N0} of {files:N0} files";
        }
        progress.Tick += (_, _) => Flush();
        progress.Start();
        try
        {
            await Task.Run(() =>
            {
                foreach (string file in Files(folder, masks, token))
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref files);
                    var found = ContentSearch.Matches(file, pattern, MaxHits - hits, token);
                    if (found.Count == 0)
                        continue;
                    Interlocked.Increment(ref filesWithHits);
                    string relative = Path.GetRelativePath(folder, file);
                    lock (batch)
                        batch.AddRange(found.Select(m => new FindHit(m, relative)));
                    Interlocked.Add(ref hits, found.Count);
                    if (hits >= MaxHits)
                        break;
                }
            }, token);
            progress.Stop();
            Flush();
            FindStatus.Text = (hits == 0 ? "Nothing found" : $"{hits:N0} {(hits == 1 ? "hit" : "hits")} in {filesWithHits:N0} {(filesWithHits == 1 ? "file" : "files")}")
                              + $"   ·   {files:N0} {(files == 1 ? "file" : "files")} searched" + (hits >= MaxHits ? $"   ·   stopped at {MaxHits:N0} hits" : "");
            if (_hits.Count > 0)
                FindResults.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            progress.Stop();
            if (_finding == cancel)
                FindStatus.Text = "Stopped.";
        }
    }

    /// <summary>The files below the folder that match one of the masks ("*.cs"); folders that cannot be read are left out.</summary>
    private static IEnumerable<string> Files(string folder, string[] masks, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(current);
                folders = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                if (masks.Any(m => m == "*" || FileSystemName(name, m)))
                    yield return file;
            }
            foreach (string sub in folders.OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
            }
        }
    }

    private static bool FileSystemName(string name, string mask) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(mask, name, ignoreCase: true);

    private void FindResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindResults.SelectedItem is FindHit hit)
            _ = OpenHitAsync(hit);
    }

    private void FindResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindResults.SelectedItem is FindHit hit)
        {
            e.Handled = true;
            _ = OpenHitAsync(hit);
        }
    }

    /// <summary>Opens the hit's file (or shows it when it is open) with the hit selected.</summary>
    private async Task OpenHitAsync(FindHit hit)
    {
        await OpenFilesAsync([hit.Match.Path]);
        if (Active is not { } document || !string.Equals(document.FilePath, hit.Match.Path, StringComparison.OrdinalIgnoreCase))
            return;
        var text = Editor.Document;
        if (hit.Match.Line > text.LineCount)
            return;
        var line = text.GetLineByNumber(hit.Match.Line);
        int offset = line.Offset + Math.Min(hit.Match.Column, line.Length);
        Editor.Select(offset, Math.Min(hit.Match.Length, line.EndOffset - offset));
        Editor.ScrollTo(hit.Match.Line, hit.Match.Column + 1);
        FocusEditor();
    }
}
