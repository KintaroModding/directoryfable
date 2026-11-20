using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DirectoryFable.Editor;
using DirectoryFable.Search;
using ICSharpCode.AvalonEdit.Search;

namespace DirectoryFable.Views;

/// <summary>
/// One row of the find panel: a hit of Find in files (a file and a line), or a line of the shown file with its
/// matches (Find all). The line is shown without its leading spaces, cut at 300 characters, the matches marked.
/// </summary>
public sealed class FindHit
{
    private const int MaxShown = 300;

    /// <summary>Find in files: the file's hit.</summary>
    public FindHit(ContentMatch match, string relativePath)
    {
        Match = match;
        Where = $"{relativePath}:{match.Line}";
        Line = match.Line;
        Column = match.Column;
        Length = match.Length;
        Segments = Split(match.LineText, [(match.Column, match.Length)]);
    }

    /// <summary>Find all: a line of an open file with all its matches (columns in the line).</summary>
    public FindHit(EditorDocument document, int line, string lineText, IReadOnlyList<(int Column, int Length)> matches)
    {
        Document = document;
        Where = $"Line {line}:";
        Line = line;
        Column = matches[0].Column;
        Length = matches[0].Length;
        Segments = Split(lineText, matches);
    }

    public ContentMatch? Match { get; }

    /// <summary>Find all: the open file the line belongs to.</summary>
    public EditorDocument? Document { get; }

    /// <summary>"Views\NotepadView.cs:42" or "Line 42:".</summary>
    public string Where { get; }

    public int Line { get; }

    /// <summary>The first match in the line (0-based column) and its length: what is selected when the hit is opened.</summary>
    public int Column { get; }
    public int Length { get; }

    /// <summary>The shown line in pieces; true = a match.</summary>
    public IReadOnlyList<(string Text, bool IsMatch)> Segments { get; }

    private static List<(string Text, bool IsMatch)> Split(string line, IReadOnlyList<(int Column, int Length)> matches)
    {
        int start = line.Length - line.TrimStart().Length;
        var parts = new List<(string, bool)>();
        int at = start, shown = 0;
        void Add(string text, bool isMatch)
        {
            if (text.Length == 0 || shown >= MaxShown)
                return;
            if (shown + text.Length > MaxShown)
                text = text[..(MaxShown - shown)] + "…";
            parts.Add((text, isMatch));
            shown += text.Length;
        }
        foreach (var (column, length) in matches.OrderBy(m => m.Column))
        {
            int from = Math.Clamp(column, at, line.Length), to = Math.Clamp(column + length, from, line.Length);
            Add(line[at..from], false);
            Add(line[from..to], true);
            at = to;
        }
        Add(line[at..], false);
        return parts;
    }

    // ---------- The colored line ----------

    private static readonly System.Windows.Media.Brush MatchText = Frozen(0x4F, 0xC1, 0xFF);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Fills a list row's text block with the line, the matches bright blue and underlined (also readable on the selected row).</summary>
    public static void ShowSegments(TextBlock? block, IReadOnlyList<(string Text, bool IsMatch)>? segments)
    {
        if (block is null)
            return;
        block.Inlines.Clear();
        foreach (var (text, isMatch) in segments ?? [])
        {
            block.Inlines.Add(isMatch
                ? new System.Windows.Documents.Run(text) { Foreground = MatchText, FontWeight = FontWeights.SemiBold, TextDecorations = TextDecorations.Underline }
                : new System.Windows.Documents.Run(text));
        }
    }
}

/// <summary>
/// Notepad#'s Replace (in the Ctrl+F box: Ctrl+H or its arrow), Find all (in the Ctrl+F box: every line of the shown
/// file with a match, in the panel above the status line) and Find in files (Ctrl+Shift+F: the same panel; a folder
/// with its subfolders, a file pattern, the same options as the search box; a double-click opens a hit at its line).
/// </summary>
public partial class NotepadView
{
    private const int MaxHits = 5000;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "node_modules", "bin", "obj" };

    private SearchPanel? _search;
    private TextBox? _replaceBox;
    private readonly ObservableCollection<FindHit> _hits = [];
    private CancellationTokenSource? _finding;

    /// <summary>The panel shows Find all (the shown file) instead of Find in files.</summary>
    private bool _findAllMode;

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
        // Find all: the button, and Alt+Enter in the find box.
        if (panel.Template.FindName("FindAllButton", panel) is ButtonBase findAll)
            findAll.Click += (_, _) => FindAllInDocument();
        if (panel.Template.FindName("PART_searchTextBox", panel) is TextBox findBox)
        {
            findBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.System && e.SystemKey == Key.Enter)
                {
                    e.Handled = true;
                    FindAllInDocument();
                }
            };
        }
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

    /// <summary>The panel's head for Find in files (a folder and file pattern) or for Find all (the shown file only).</summary>
    private void SetFindPanelMode(bool findAll)
    {
        if (_findAllMode != findAll)
        {
            _finding?.Cancel();
            _hits.Clear();
            FindStatus.Text = findAll ? "" : "Type what to find and press Enter.";
        }
        _findAllMode = findAll;
        FindPanelTitle.Text = findAll ? "Find all" : "Find in files";
        var folderParts = findAll ? Visibility.Collapsed : Visibility.Visible;
        FindInLabel.Visibility = FindFolderBox.Visibility = FindBrowseButton.Visibility = FindFilesLabel.Visibility = FindMaskBox.Visibility = folderParts;
        FindTextBox.ToolTip = findAll ? "What to find in the shown file (Enter lists it again)" : "What to find (Enter searches)";
    }

    private void ShowFindPanel()
    {
        SetFindPanelMode(false);
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
            if (_findAllMode)
                ListMatches(FindTextBox.Text, FindCaseToggle.IsChecked == true, FindWordsToggle.IsChecked == true, FindRegexToggle.IsChecked == true);
            else
                _ = FindInFilesAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFindPanel_Click(sender, e);
        }
    }

    private void FindInFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_findAllMode)
            ListMatches(FindTextBox.Text, FindCaseToggle.IsChecked == true, FindWordsToggle.IsChecked == true, FindRegexToggle.IsChecked == true);
        else
            _ = FindInFilesAsync();
    }

    // ---------- Find all (the shown file) ----------

    /// <summary>"Find all" in the Ctrl+F box: its text and options, every matching line of the shown file in the panel.</summary>
    private void FindAllInDocument()
    {
        if (_search is null)
            return;
        ListMatches(_search.SearchPattern, _search.MatchCase, _search.WholeWords, _search.UseRegex);
    }

    /// <summary>Lists every line of the shown file that has a match (one row per line, all its matches marked).</summary>
    private void ListMatches(string text, bool matchCase, bool wholeWords, bool regex)
    {
        SetFindPanelMode(true);
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Text = text;
        FindCaseToggle.IsChecked = matchCase;
        FindWordsToggle.IsChecked = wholeWords;
        FindRegexToggle.IsChecked = regex;
        _hits.Clear();
        if (Active is not { } document || Editor.Visibility != Visibility.Visible)
        {
            FindStatus.Text = "Open a file first.";
            return;
        }
        if (text.Length == 0)
        {
            FindStatus.Text = "Type what to find.";
            return;
        }
        ISearchStrategy strategy;
        try
        {
            strategy = SearchStrategyFactory.Create(text, !matchCase, wholeWords, regex ? SearchMode.RegEx : SearchMode.Normal);
        }
        catch (SearchPatternException ex)
        {
            FindStatus.Text = $"Not a valid regular expression: {ex.Message}";
            return;
        }
        var lines = Editor.Document;
        int count = 0;
        ICSharpCode.AvalonEdit.Document.DocumentLine? line = null;
        var inLine = new List<(int Column, int Length)>();
        void FlushLine()
        {
            if (line is not null && inLine.Count > 0)
                _hits.Add(new FindHit(document, line.LineNumber, lines.GetText(line), [.. inLine]));
            inLine.Clear();
        }
        foreach (var match in strategy.FindAll(lines, 0, lines.TextLength))
        {
            if (match.Length == 0)
                continue;
            var at = lines.GetLineByOffset(match.Offset);
            if (at != line)
            {
                FlushLine();
                line = at;
            }
            // A match over a line end is marked up to the end of its first line.
            inLine.Add((match.Offset - at.Offset, Math.Min(match.Length, at.EndOffset - match.Offset)));
            if (++count >= MaxHits)
                break;
        }
        FlushLine();
        string name = document.FileName;
        FindStatus.Text = count == 0
            ? $"\"{text}\": nothing found in {name}"
            : $"\"{text}\": {count:N0} {(count == 1 ? "hit" : "hits")} on {_hits.Count:N0} {(_hits.Count == 1 ? "line" : "lines")} in {name}" +
              (count >= MaxHits ? $"   ·   stopped at {MaxHits:N0} hits" : "") + "   ·   a click goes to the line";
    }

    /// <summary>Find all: a click (or the arrow keys) on a row shows its line in the editor; the list keeps the focus.</summary>
    private void FindResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_findAllMode && FindResults.SelectedItem is FindHit { Document: not null } hit)
            GoToLine(hit, focus: false);
    }

    /// <summary>Shows a Find all row's line with its first match selected (the file is shown first when another one is).</summary>
    private void GoToLine(FindHit hit, bool focus)
    {
        if (hit.Document is not { } document || !Documents.Contains(document))
        {
            FindStatus.Text = "That file is closed.";
            return;
        }
        if (document != Active)
            ShowDocument(document, focusEditor: false);
        var text = Editor.Document;
        if (hit.Line > text.LineCount)
            return;
        var line = text.GetLineByNumber(hit.Line);
        int offset = line.Offset + Math.Min(hit.Column, line.Length);
        Editor.Select(offset, Math.Min(hit.Length, line.EndOffset - offset));
        Editor.ScrollTo(hit.Line, hit.Column + 1);
        if (focus)
            FocusEditor();
    }

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

    private void HitText_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        FindHit.ShowSegments(sender as TextBlock, (e.NewValue as FindHit)?.Segments);

    private void HitText_Loaded(object sender, RoutedEventArgs e) =>
        FindHit.ShowSegments(sender as TextBlock, ((sender as FrameworkElement)?.DataContext as FindHit)?.Segments);

    private void FindResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindResults.SelectedItem is FindHit { Document: not null } line)
            GoToLine(line, focus: true);
        else if (FindResults.SelectedItem is FindHit hit)
            _ = OpenHitAsync(hit);
    }

    private void FindResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindResults.SelectedItem is FindHit { Document: not null } line)
        {
            e.Handled = true;
            GoToLine(line, focus: true);
        }
        else if (e.Key == Key.Enter && FindResults.SelectedItem is FindHit hit)
        {
            e.Handled = true;
            _ = OpenHitAsync(hit);
        }
    }

    /// <summary>Opens the hit's file (or shows it when it is open) with the hit selected.</summary>
    private async Task OpenHitAsync(FindHit hit)
    {
        if (hit.Match is null)
            return;
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
