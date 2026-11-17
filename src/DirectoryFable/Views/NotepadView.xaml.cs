using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Editor;
using DirectoryFable.Infrastructure;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;

namespace DirectoryFable.Views;

/// <summary>
/// Notepad#: a text editor view next to the explorer. Open files are listed
/// vertically on the left in groups (no horizontal tabs, see NotepadView.Groups.cs); the editor
/// is AvalonEdit (MIT). Each file keeps its own text, undo history, caret, scroll position and
/// language.
/// </summary>
public partial class NotepadView : UserControl
{
    private bool _switching;
    private EditorDocument? _active;

    public NotepadView()
    {
        InitializeComponent();
        EditorHighlighting.EnsureReady();
        FileList.ItemsSource = _rows;

        // Dark editor look; Ctrl+F opens AvalonEdit's search box.
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.CurrentLineBackground = (Brush)FindResource("EditorCurrentLineBrush");
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        Editor.TextArea.SelectionBrush = (Brush)FindResource("EditorSelectionBrush");
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.Caret.CaretBrush = Brushes.White;
        // Ctrl+F: the dark search box (style in Themes/Controls.xaml); matches in a dim orange.
        var search = SearchPanel.Install(Editor);
        search.MarkerBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xB3, 0x5C, 0x1E));
        search.MarkerCornerRadius = 2;
        Minimap.Attach(Editor);
        // The scroll bar right of the minimap follows the editor (and the editor follows it).
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => SyncScrollBar()), handledEventsToo: true);
        Editor.SizeChanged += (_, _) => SyncScrollBar();
        Editor.IsVisibleChanged += (_, _) => SyncScrollBar();
        InitScrollBarDrag();
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateStatus();
            if (Editor.WordWrap && !_caretFixPending)
            {
                _caretFixPending = true;
                Dispatcher.BeginInvoke(KeepCaretVisibleWrapped, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };

        // Files deleted, moved or changed outside Notepad#: checked every 2 s, off the UI thread.
        var existence = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        existence.Tick += async (_, _) => await CheckFilesOnDiskAsync();
        existence.Start();

        // Unsaved texts are kept (data\notepad-cache) 3 s after typing stops, not only at exit,
        // so a crash or power loss does not lose them.
        _backupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _backupTimer.Tick += (_, _) =>
        {
            _backupTimer.Stop();
            if (!_restoring)
                BackupRequested?.Invoke();
        };

        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.SettingsChanged += ApplySettings;
        ApplySettings(AppSettings.Load());
    }

    /// <summary>Copies the editor's scroll state to the scroll bar right of the minimap.</summary>
    private void SyncScrollBar()
    {
        double scrollable = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        bool show = Editor.IsVisible && scrollable > 0.5;
        EditorScrollBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;
        EditorScrollBar.Minimum = 0;
        EditorScrollBar.Maximum = scrollable;
        EditorScrollBar.ViewportSize = Editor.ViewportHeight;
        EditorScrollBar.LargeChange = Math.Max(1, Editor.ViewportHeight - Editor.TextArea.TextView.DefaultLineHeight);
        EditorScrollBar.SmallChange = Math.Max(1, Editor.TextArea.TextView.DefaultLineHeight * 3);
        EditorScrollBar.Value = Editor.VerticalOffset;
    }

    private void EditorScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        // While the thumb is dragged, ThumbDragFrame scrolls once per frame instead.
        if (e.ScrollEventType == ScrollEventType.ThumbTrack && _thumbDragStartY is not null)
            return;
        Editor.ScrollToVerticalOffset(e.NewValue);
    }

    // Thumb drag of the scroll bar, frame-synced like the minimap (see FrameSyncedDrag).
    private FrameSyncedDrag? _thumbDrag;
    private double? _thumbDragStartY;
    private double _thumbDragStartValue;

    private void InitScrollBarDrag()
    {
        _thumbDrag = new FrameSyncedDrag(EditorScrollBar, ThumbDragFrame);
        EditorScrollBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            _thumbDragStartY = _thumbDrag.CursorY();
            _thumbDragStartValue = EditorScrollBar.Value;
            if (_thumbDragStartY is not null)
                _thumbDrag.Start();
        }), handledEventsToo: true);
        EditorScrollBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _thumbDrag.Stop();
            _thumbDragStartY = null;
        }), handledEventsToo: true);
    }

    private void ThumbDragFrame(double y)
    {
        if (_thumbDragStartY is not double startY || EditorScrollBar.Track is not { } track)
            return;
        double value = Math.Clamp(_thumbDragStartValue + track.ValueFromDistance(0, y - startY),
            EditorScrollBar.Minimum, EditorScrollBar.Maximum);
        EditorScrollBar.Value = value;
        Editor.ScrollToVerticalOffset(value);
    }

    /// <summary>All open files in list order (groups from top to bottom).</summary>
    public IEnumerable<EditorDocument> Documents => Groups.SelectMany(g => g.Documents);

    /// <summary>The file shown in the editor.</summary>
    public EditorDocument? Active => _active;

    /// <summary>The shown file changed (the main window updates its title).</summary>
    public event Action? ActiveChanged;

    /// <summary>"Show in explorer" from the file list menu.</summary>
    public event Action<string>? ShowInExplorerRequested;

    public bool HasUnsaved => Documents.Any(d => d.IsDirty);

    /// <summary>Texts changed a moment ago: the main window saves the session (with the kept texts).</summary>
    public event Action? BackupRequested;

    private readonly System.Windows.Threading.DispatcherTimer _backupTimer;

    /// <summary>
    /// The session (open files, groups, kept texts) is saved 3 s after the last change: typing,
    /// open, close, save, save as, groups. So a crash loses at most those 3 s.
    /// </summary>
    private void ScheduleBackup()
    {
        _backupTimer.Stop();
        _backupTimer.Start();
    }

    /// <summary>Font, size, word wrap, file types and the automatic grouping from settings.json.</summary>
    public void ApplySettings(AppSettings settings)
    {
        Editor.FontFamily = new FontFamily(settings.EditorFontFamily);
        Editor.FontSize = Math.Clamp(settings.EditorFontSize, 6, 72);
        ApplyWordWrap(settings.EditorWordWrap);
        EditorFileTypes.Configure(settings);
        SetAutoGrouping(settings.NotepadAutoGroupBy);
    }

    // ---------- Word wrap ----------

    /// <summary>Wraps long lines (or not); the first shown line stays at the top.</summary>
    private void ApplyWordWrap(bool wrap)
    {
        WrapButton.IsChecked = wrap;
        if (Editor.WordWrap == wrap)
            return;
        int firstLine = Editor.Document is not null && Editor.TextArea.TextView.GetDocumentLineByVisualTop(Editor.VerticalOffset) is { } line
            ? line.LineNumber : 0;
        Editor.WordWrap = wrap;
        if (firstLine > 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Editor.Document is not null && firstLine <= Editor.Document.LineCount)
                    Editor.ScrollToVerticalOffset(Editor.TextArea.TextView.GetVisualTopByDocumentLine(firstLine));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private bool _caretFixPending;

    /// <summary>
    /// With word wrap, AvalonEdit guesses one row for every line it has not drawn yet, so a long
    /// jump (Ctrl+End, Page Down, search) can stop short and leave the caret below the view.
    /// Once the newly shown lines are measured, scroll to the caret again (a few rounds at most).
    /// </summary>
    private void KeepCaretVisibleWrapped()
    {
        _caretFixPending = false;
        if (!Editor.WordWrap || Editor.Document is null || !Editor.IsVisible)
            return;
        var caret = Editor.TextArea.Caret;
        for (int round = 0; round < 3; round++)
        {
            Editor.UpdateLayout();
            double top = Editor.TextArea.TextView.GetVisualTopByDocumentLine(caret.Line);
            double lineHeight = Editor.TextArea.TextView.DefaultLineHeight;
            if (top >= Editor.VerticalOffset && top + lineHeight <= Editor.VerticalOffset + Editor.ViewportHeight)
                return;
            caret.BringCaretToView();
        }
    }

    /// <summary>The bar button and Alt+Z: switch word wrap and save it (settings.json "editorWordWrap").</summary>
    private void SetWordWrap(bool wrap)
    {
        ApplyWordWrap(wrap);
        try
        {
            var settings = AppSettings.Load();
            settings.EditorWordWrap = wrap;
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"Cannot save the setting: {ex.Message}");
        }
    }

    private void Wrap_Click(object sender, RoutedEventArgs e) => SetWordWrap(WrapButton.IsChecked == true);

    public void FocusEditor()
    {
        if (Active is not null)
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        else
            FileList.Focus();
    }

    // ---------- Open ----------

    /// <summary>Opens files (an open file is just shown); the last one becomes the shown file.</summary>
    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        EditorDocument? last = null;
        foreach (string path in paths)
            last = await LoadOrFindAsync(path) ?? last;
        if (last is not null)
            ShowDocument(last);
    }

    /// <summary>
    /// The open document of this file, or the file loaded and added to the list (into its
    /// automatic group, or <paramref name="group"/>; not shown yet). Null on errors.
    /// </summary>
    private async Task<EditorDocument?> LoadOrFindAsync(string path, EditorGroup? group = null)
    {
        var existing = Documents.FirstOrDefault(d => !d.IsUntitled && PathHelper.IsSamePath(d.FilePath, path));
        if (existing is not null)
            return existing;
        try
        {
            var document = await EditorDocument.LoadAsync(path);
            AddDocument(document, group);
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowMessage($"Cannot open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Puts a document into the list (its automatic group, or <paramref name="group"/>).</summary>
    private void AddDocument(EditorDocument document, EditorGroup? group)
    {
        document.PropertyChanged += (_, e) =>
        {
            if (document != Active)
                return;
            if (e.PropertyName is nameof(EditorDocument.IsMissing) or nameof(EditorDocument.HasContent))
                ApplyEditorState(document);
            if (e.PropertyName is nameof(EditorDocument.IsDirty) or nameof(EditorDocument.IsMissing) or nameof(EditorDocument.HasContent))
                UpdateStatus();
            if (e.PropertyName is nameof(EditorDocument.FileName))
                ActiveChanged?.Invoke(); // Saved under a new name: the window title follows.
        };
        // Keep unsaved texts a moment after typing stops (see _backupTimer).
        document.Document.TextChanged += (_, _) => ScheduleBackup();
        (group ?? AutoGroupFor(document)).Documents.Add(document);
        RebuildRows();
    }

    // ---------- New file ----------

    /// <summary>Ctrl+N: a new, empty file ("new 1", "new 2", ...) in the "New files" group.</summary>
    private void NewFile()
    {
        var used = Documents.Where(d => d.IsUntitled).Select(d => d.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int number = 1;
        while (used.Contains($"new {number}"))
            number++;
        var document = EditorDocument.CreateUntitled($"new {number}");
        AddDocument(document, null);
        ShowDocument(document);
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewFile();

    /// <summary>A missing file (or one without its text) is read-only until it is saved again.</summary>
    private void ApplyEditorState(EditorDocument document) =>
        Editor.IsReadOnly = document.IsMissing || !document.HasContent;

    // ---------- Files changed or gone outside Notepad# ----------

    private bool _checkingFiles;
    private bool _askingReload;

    /// <summary>
    /// Every 2 s: marks files that were deleted or moved outside Notepad# (red, read-only) and
    /// clears the mark when they come back (an entry without text then loads it). A file that
    /// another program changed is read again when it has no unsaved changes; with unsaved
    /// changes Notepad# asks first (when the file is shown).
    /// </summary>
    private async Task CheckFilesOnDiskAsync()
    {
        if (_checkingFiles || _restoring)
            return;
        var documents = Documents.Where(d => !d.IsUntitled).ToList();
        if (documents.Count > 0)
        {
            _checkingFiles = true;
            try
            {
                var states = await Task.Run(() => documents.Select(d => DiskState(d.FilePath)).ToList());
                for (int i = 0; i < documents.Count; i++)
                    await CheckFileAsync(documents[i], states[i]);
            }
            finally
            {
                _checkingFiles = false;
            }
        }
        AskReloadIfChangedOutside();
    }

    private static (bool Exists, DateTime WriteTimeUtc, long Length) DiskState(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, default, 0);
        }
    }

    private async Task CheckFileAsync(EditorDocument document, (bool Exists, DateTime WriteTimeUtc, long Length) disk)
    {
        if (GroupOf(document) is null)
            return; // Closed meanwhile.
        if (!disk.Exists)
        {
            document.IsMissing = true;
            return;
        }
        try
        {
            if (document.IsMissing)
            {
                document.IsMissing = false;
                if (!document.HasContent)
                {
                    await document.LoadContentAsync();
                    return;
                }
            }
            if (!document.HasContent || !document.DiffersFromDisk(disk.WriteTimeUtc, disk.Length))
                return;
            if (document.IsDirty)
            {
                // Unsaved changes here: ask before they are replaced (see AskReloadIfChangedOutside).
                document.AcceptDiskState(disk.WriteTimeUtc, disk.Length);
                document.ChangedOutside = true;
                return;
            }
            await ReloadAsync(document);
            ShowMessage($"Reloaded {document.FileName}: another program changed it ({DateTime.Now:HH:mm:ss})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Locked or half written by the other program: try again at the next check.
            if (!document.HasContent || document.IsMissing)
                ShowMessage($"Cannot read {document.FileName}: {ex.Message}");
        }
    }

    /// <summary>Reads a file again; the shown file keeps its caret and scroll position.</summary>
    private async Task ReloadAsync(EditorDocument document)
    {
        bool shown = document == Active && Editor.Document == document.Document;
        if (shown)
            RememberView(document);
        await document.ReloadAsync();
        if (shown && Editor.Document == document.Document)
        {
            Editor.TextArea.Caret.Offset = Math.Min(document.CaretOffset, document.Document.TextLength);
            Editor.ScrollToVerticalOffset(document.VerticalOffset);
            Editor.ScrollToHorizontalOffset(document.HorizontalOffset);
        }
        UpdateStatus();
    }

    /// <summary>
    /// The shown file was changed by another program while it has unsaved changes: ask once
    /// whether to reload it (only while Notepad# is visible and the window is active).
    /// </summary>
    private async void AskReloadIfChangedOutside()
    {
        if (_askingReload || Active is not { ChangedOutside: true } document || !IsVisible ||
            Window.GetWindow(this) is not { IsActive: true } owner)
            return;
        _askingReload = true;
        try
        {
            document.ChangedOutside = false;
            var answer = MessageDialog.Show(owner,
                $"{document.FileName} was changed by another program.\n\nReload it from disk? Your unsaved changes in Notepad# are lost " +
                "(Ctrl+Z still brings them back).\n\nNo keeps your version; saving it then replaces the other program's changes.",
                "Notepad#", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes && GroupOf(document) is not null)
            {
                await ReloadAsync(document);
                ShowMessage($"Reloaded {document.FileName} ({DateTime.Now:HH:mm:ss})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowMessage($"Cannot read {document.FileName}: {ex.Message}");
        }
        finally
        {
            _askingReload = false;
        }
    }

    /// <summary>Removes the entry of a file that is gone (unsaved changes ask first).</summary>
    private void DismissDocument(EditorDocument document)
    {
        if (document.IsDirty && document.HasContent)
        {
            var answer = MessageDialog.Show(Window.GetWindow(this),
                $"{document.FileName} no longer exists and has unsaved changes.\n\nDismiss it anyway (the changes are lost)?",
                "Notepad#", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }
        RemoveDocument(document);
    }

    private void RowDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditorDocument document })
            DismissDocument(document);
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();

    private async Task OpenWithDialogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Open in Notepad#" };
        if (Active is { IsUntitled: false })
            dialog.InitialDirectory = Path.GetDirectoryName(Active.FilePath) ?? "";
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await OpenFilesAsync(dialog.FileNames);
        FocusEditor();
    }

    // ---------- Show a file ----------

    /// <summary>Shows a file in the editor (its collapsed group opens) and selects its row.</summary>
    /// <param name="focusEditor">False for a right-click: the editor taking the focus would close the menu at once.</param>
    private void ShowDocument(EditorDocument? document, bool focusEditor = true)
    {
        if (document is not null && GroupOf(document) is { IsExpanded: false } group)
        {
            group.IsExpanded = true;
            RebuildRows();
        }

        if (document != _active)
        {
            // Remember where the previous file was.
            if (_active is not null && Editor.Document == _active.Document)
                RememberView(_active);
            _active = document;
            _switching = true;
            try
            {
                if (document is null)
                {
                    Editor.Document = null;
                    Editor.Visibility = Visibility.Collapsed;
                    EmptyText.Visibility = Visibility.Visible;
                }
                else
                {
                    Editor.Document = document.Document;
                    Editor.SyntaxHighlighting = document.Highlighting;
                    ApplyEditorState(document);
                    Editor.Visibility = Visibility.Visible;
                    EmptyText.Visibility = Visibility.Collapsed;
                    Editor.TextArea.Caret.Offset = Math.Min(document.CaretOffset, document.Document.TextLength);
                    // Scroll after the new text is laid out.
                    Dispatcher.BeginInvoke(() =>
                    {
                        Editor.ScrollToVerticalOffset(document.VerticalOffset);
                        Editor.ScrollToHorizontalOffset(document.HorizontalOffset);
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            finally
            {
                _switching = false;
            }
            UpdateStatus();
            ActiveChanged?.Invoke();
        }
        SelectActiveRow();
        if (focusEditor)
            FocusEditor();
        // Changed by another program while it has unsaved changes: ask now that it is shown.
        if (document is { ChangedOutside: true })
            Dispatcher.BeginInvoke(AskReloadIfChangedOutside, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
            return;
        if (FileList.SelectedItem is EditorDocument document)
        {
            if (document != _active)
                ShowDocument(document);
        }
        else if (FileList.SelectedItem is EditorGroup)
        {
            // Headers are not selectable (keyboard): go back to the shown file's row.
            Dispatcher.BeginInvoke(SelectActiveRow);
        }
    }

    private void RememberView(EditorDocument document)
    {
        document.CaretOffset = Editor.TextArea.Caret.Offset;
        document.VerticalOffset = Editor.VerticalOffset;
        document.HorizontalOffset = Editor.HorizontalOffset;
    }

    private void UpdateStatus()
    {
        if (_switching)
            return;
        var document = Active;
        SaveButton.IsEnabled = document is { HasContent: true };
        LanguageButton.IsEnabled = document is not null;
        LanguageText.Text = document?.LanguageName ?? "Normal Text";
        if (document is null)
        {
            StatusText.Text = "";
            return;
        }
        var caret = Editor.TextArea.Caret;
        StatusText.Text = $"Ln {caret.Line:N0}, Col {caret.Column:N0}     {document.Document.LineCount:N0} lines     " +
                          $"{document.EncodingName}     {document.LineEnding}     {document.LanguageName}" +
                          (document.IsDirty ? "     changed" : "") +
                          (!document.HasContent ? "     FILE IS GONE: Dismiss removes the entry"
                           : document.IsMissing ? "     FILE IS GONE: read-only, Save writes it back" : "");
    }

    private void ShowMessage(string text) => MessageText.Text = text;

    // ---------- Save / close ----------

    private void Save_Click(object sender, RoutedEventArgs e) => SaveDocument(Active);

    /// <summary>Saves one file (a new file asks for its name); false (with a message) when it failed or was cancelled.</summary>
    private bool SaveDocument(EditorDocument? document)
    {
        if (document is null)
            return true;
        if (document.IsUntitled)
            return SaveDocumentAs(document);
        try
        {
            document.Save();
            ShowMessage($"Saved {document.FileName} at {DateTime.Now:HH:mm:ss}");
            UpdateStatus();
            ScheduleBackup(); // Its kept text is no longer needed.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot save {document.FilePath}:\n\n{ex.Message}", "Notepad#",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// "Save as" (Ctrl+Alt+S; Save of a new file): asks for a path and saves there; from then on
    /// the entry is that file. False when cancelled or failed.
    /// </summary>
    private bool SaveDocumentAs(EditorDocument document)
    {
        if (!document.HasContent)
            return false;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Save {document.FileName} as",
            FileName = document.IsUntitled ? document.FileName + ".txt" : document.FileName,
            Filter = "All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        string? folder = !document.IsUntitled ? Path.GetDirectoryName(document.FilePath)
            : Documents.FirstOrDefault(d => !d.IsUntitled) is { } other ? Path.GetDirectoryName(other.FilePath) : null;
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return false;

        string path = dialog.FileName;
        if (Documents.Any(d => d != document && !d.IsUntitled && PathHelper.IsSamePath(d.FilePath, path)))
        {
            MessageDialog.Show(Window.GetWindow(this), $"{Path.GetFileName(path)} is open in Notepad# already.\n\nClose it there first, or choose another name.",
                "Notepad#", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            var group = GroupOf(document);
            document.SaveAs(path);
            // In an automatic group: move to the group of its new type / folder.
            if (group is { IsAuto: true } && AutoGroupFor(document) is { } target && target != group)
                MoveDocument(document, target, target.Documents.Count);
            ShowMessage($"Saved {document.FileName} at {DateTime.Now:HH:mm:ss}");
            if (document == Active)
                Editor.SyntaxHighlighting = document.Highlighting;
            UpdateStatus();
            ActiveChanged?.Invoke();
            ScheduleBackup();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot save {path}:\n\n{ex.Message}", "Notepad#",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Closes a file; a changed file asks first. False when the user cancelled.</summary>
    private bool CloseDocument(EditorDocument document)
    {
        if (document.IsDirty && document.HasContent)
        {
            var answer = MessageDialog.Show(Window.GetWindow(this), $"Save the changes to {document.FileName}?", "Notepad#",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveDocument(document)))
                return false;
        }
        RemoveDocument(document);
        return true;
    }

    /// <summary>Takes a file out of the list without asking; the next file is shown.</summary>
    private void RemoveDocument(EditorDocument document)
    {
        var all = Documents.ToList();
        int index = all.IndexOf(document);
        GroupOf(document)?.Documents.Remove(document);
        RemoveEmptyAutoGroups();
        all.Remove(document);
        if (document == _active)
        {
            if (Editor.Document == document.Document)
                Editor.Document = null;
            _active = null;
            ShowDocument(all.Count > 0 ? all[Math.Min(index, all.Count - 1)] : null);
            if (all.Count == 0)
            {
                Editor.Visibility = Visibility.Collapsed;
                EmptyText.Visibility = Visibility.Visible;
                UpdateStatus();
                ActiveChanged?.Invoke();
            }
        }
        RebuildRows();
    }

    /// <summary>
    /// Before the app exits. With "restore last session" on, unsaved changes and new files are
    /// kept (data\notepad-cache) and come back at the next start: no question. Otherwise changed
    /// files ask once for all. False = do not exit.
    /// </summary>
    public bool ConfirmExit(Window owner)
    {
        var changed = Documents.Where(d => d.IsDirty).ToList();
        if (changed.Count == 0 || AppSettings.Load().RestoreSession)
            return true;
        string names = string.Join("\n", changed.Take(10).Select(d => "  " + d.FileName)) + (changed.Count > 10 ? "\n  ..." : "");
        var answer = MessageDialog.Show(owner, $"Notepad# has unsaved changes:\n\n{names}\n\nSave them before closing?", "Notepad#",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel)
            return false;
        if (answer == MessageBoxResult.Yes)
            return changed.All(SaveDocument);
        return true;
    }

    // ---------- Language menu ----------

    /// <summary>"Normal Text", then one submenu per first letter (A, B, C, ...), like Notepad++.</summary>
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } document)
            return;
        var menu = NewMenu(LanguageButton, PlacementMode.Bottom);
        menu.Items.Add(LanguageItem(document, null, "Normal Text"));
        menu.Items.Add(new Separator());
        foreach (var letter in EditorHighlighting.Languages.GroupBy(d => char.ToUpperInvariant(d.Name[0])).OrderBy(g => g.Key))
        {
            var group = new MenuItem { Header = letter.Key.ToString() };
            foreach (var definition in letter)
                group.Items.Add(LanguageItem(document, definition, definition.Name));
            menu.Items.Add(group);
        }
        menu.IsOpen = true;
    }

    private MenuItem LanguageItem(EditorDocument document, IHighlightingDefinition? definition, string header)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = document.Highlighting == definition };
        item.Click += (_, _) =>
        {
            document.Highlighting = definition;
            if (document == Active)
                Editor.SyntaxHighlighting = definition;
            UpdateStatus();
        };
        return item;
    }

    private ContextMenu NewMenu(UIElement target, PlacementMode placement) => new()
    {
        PlacementTarget = target,
        Placement = placement,
        FontFamily = (FontFamily)FindResource("UiFont"),
        FontSize = (double)FindResource("UiFontSize"),
    };

    // ---------- Keys ----------

    private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // With Alt, WPF reports Key.System.
        if (e.Key == Key.Escape && SettingsPage.IsVisible)
        {
            e.Handled = true;
            ShowSettings(false);
        }
        else if (key == Key.N && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            NewFile();
        }
        else if (key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            e.Handled = true;
            if (Active is { } current)
                SaveDocumentAs(current);
        }
        else if (key == Key.Z && modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            SetWordWrap(!Editor.WordWrap);
        }
        else if (e.Key == Key.S && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveDocument(Active);
        }
        else if (e.Key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            foreach (var document in Documents.Where(d => d.IsDirty).ToList())
            {
                if (!SaveDocument(document))
                    break;
            }
        }
        else if (e.Key == Key.W && modifiers == ModifierKeys.Control && Active is { } active)
        {
            e.Handled = true;
            CloseDocument(active);
        }
        else if (e.Key == Key.O && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await OpenWithDialogAsync();
        }
        else if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+Tab / Ctrl+Shift+Tab: next / previous file in list order.
            var all = Documents.ToList();
            if (all.Count < 2)
                return;
            e.Handled = true;
            int step = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            int index = Active is null ? 0 : all.IndexOf(Active);
            ShowDocument(all[(index + step + all.Count) % all.Count]);
        }
    }

    // ---------- Files dropped from the explorer or other programs ----------

    /// <summary>
    /// The files (not folders) in a drag that Notepad# can open; files that were unpacked from an
    /// archive just for the drag are left out (they are deleted when the app exits).
    /// </summary>
    public static List<string> DroppableFiles(IDataObject data) =>
        FileDrop.GetPaths(data) is { } paths
            ? [.. paths.Where(p => File.Exists(p) && !p.StartsWith(Shell.ZipBrowser.TempRoot, StringComparison.OrdinalIgnoreCase))]
            : [];

    /// <summary>A file drag anywhere over Notepad# (list or editor) opens the files; text drags stay the editor's.</summary>
    private void View_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (FileDrop.GetPaths(e.Data) is null)
            return;
        e.Effects = DroppableFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void View_PreviewDrop(object sender, DragEventArgs e)
    {
        if (FileDrop.GetPaths(e.Data) is null)
            return;
        e.Handled = true;
        var files = DroppableFiles(e.Data);
        if (files.Count > 0)
            await OpenFilesAsync(files);
    }

    // ---------- Settings page ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool open)
    {
        SettingsButton.IsChecked = open;
        if (open)
        {
            SettingsPage.Open();
            SettingsPage.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            FocusEditor();
        }
    }
}
