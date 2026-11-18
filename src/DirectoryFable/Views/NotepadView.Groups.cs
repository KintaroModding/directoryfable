using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Editor;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Views;

/// <summary>
/// Groups in Notepad#'s file list: own groups (named by the user, their files
/// in the user's order) and automatic groups for all other files, by type or by folder
/// (setting). Every group collapses with a click on its header and moves by drag and drop; files
/// move by drag and drop or "Move to group".
/// </summary>
public partial class NotepadView
{
    /// <summary>Groups from top to bottom.</summary>
    public ObservableCollection<EditorGroup> Groups { get; } = [];

    /// <summary>What the list shows: group headers and the files of open groups.</summary>
    private readonly ObservableCollection<object> _rows = [];
    private bool _rebuilding;

    /// <summary>"type" or "folder" (settings "notepadAutoGroupBy").</summary>
    private string _autoGroupBy = "type";

    // ---------- Model ----------

    private EditorGroup? GroupOf(EditorDocument document) => Groups.FirstOrDefault(g => g.Documents.Contains(document));

    /// <summary>Automatic group of new files that have no path yet ("New files").</summary>
    private const string NewFilesKey = "<new>";

    private string AutoKey(EditorDocument document) => document.IsUntitled ? NewFilesKey
        : _autoGroupBy == "folder"
        ? Path.GetDirectoryName(document.FilePath) ?? ""
        : Path.GetExtension(document.FilePath).ToLowerInvariant();

    /// <summary>The automatic group for a file (created at the end of the list when missing).</summary>
    private EditorGroup AutoGroupFor(EditorDocument document) => AutoGroup(AutoKey(document));

    private EditorGroup AutoGroup(string key)
    {
        var group = Groups.FirstOrDefault(g => g.IsAuto && string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
            return group;
        if (key == NewFilesKey)
        {
            group = new EditorGroup("New files", key, "New files that are not saved yet");
            Groups.Add(group);
            return group;
        }
        string name = _autoGroupBy == "folder"
            ? (PathHelper.GetName(key) is { Length: 2 } drive && drive[1] == ':' ? drive + "\\" : PathHelper.GetName(key))
            : (key.Length > 0 ? key : "(no extension)");
        group = new EditorGroup(name, key, _autoGroupBy == "folder" ? key : $"Files of type {name}");
        Groups.Add(group);
        return group;
    }

    private void RemoveEmptyAutoGroups()
    {
        foreach (var empty in Groups.Where(g => g.IsAuto && g.Documents.Count == 0).ToList())
            Groups.Remove(empty);
    }

    /// <summary>
    /// Moves a file into a group at a position. Into an automatic group only when it is the
    /// file's own (a .txt cannot go into ".json"); a file from an own group dropped on another
    /// automatic group goes back to its own automatic group instead.
    /// </summary>
    private void MoveDocument(EditorDocument document, EditorGroup target, int index)
    {
        var source = GroupOf(document);
        if (target.IsAuto && !string.Equals(target.Key, AutoKey(document), StringComparison.OrdinalIgnoreCase))
        {
            if (source is null || source.IsAuto)
                return;
            target = AutoGroupFor(document);
            index = target.Documents.Count;
        }

        if (source == target)
        {
            int from = target.Documents.IndexOf(document);
            index = Math.Clamp(index, 0, target.Documents.Count - 1);
            if (from != index)
                target.Documents.Move(from, index);
        }
        else
        {
            source?.Documents.Remove(document);
            target.Documents.Insert(Math.Clamp(index, 0, target.Documents.Count), document);
            target.IsExpanded = true;
        }
        RemoveEmptyAutoGroups();
        RebuildRows();
    }

    /// <summary>Settings: automatic groups by "type" or "folder"; files in own groups stay where they are.</summary>
    private void SetAutoGrouping(string? method)
    {
        method = string.Equals(method, "folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "type";
        if (method == _autoGroupBy)
            return;
        var loose = Groups.Where(g => g.IsAuto).SelectMany(g => g.Documents).ToList();
        foreach (var auto in Groups.Where(g => g.IsAuto).ToList())
            Groups.Remove(auto);
        _autoGroupBy = method;
        foreach (var document in loose)
            AutoGroupFor(document).Documents.Add(document);
        RebuildRows();
    }

    /// <summary>Makes the rows match the groups (headers, and the files of open groups).</summary>
    private void RebuildRows()
    {
        var wanted = new List<object>();
        EditorGroup? previous = null;
        foreach (var group in Groups)
        {
            // A short dashed divider where own groups and automatic groups meet.
            if (previous is not null && previous.IsAuto != group.IsAuto)
                wanted.Add(GroupDivider.Instance);
            previous = group;
            wanted.Add(group);
            if (group.IsExpanded)
                wanted.AddRange(group.Documents);
        }
        _rebuilding = true;
        try
        {
            if (!wanted.SequenceEqual(_rows))
            {
                _rows.Clear();
                foreach (var row in wanted)
                    _rows.Add(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }
        SelectActiveRow();
        ScheduleBackup(); // Files or groups changed: save the session soon.
    }

    private void SelectActiveRow()
    {
        _rebuilding = true;
        try
        {
            FileList.SelectedItem = _active is not null && _rows.Contains(_active) ? _active : null;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    // ---------- Own groups ----------

    /// <summary>Asks for a name; a new own group goes below the other own groups. Null when cancelled.</summary>
    private EditorGroup? NewGroup()
    {
        string? name = AskGroupName("Name of the new group:", "New group", except: null);
        if (name is null)
            return null;
        var group = new EditorGroup(name);
        int index = Groups.Count(g => !g.IsAuto);
        Groups.Insert(index, group);
        RebuildRows();
        return group;
    }

    private void RenameGroup(EditorGroup group)
    {
        if (AskGroupName("New name of the group:", group.Name, except: group) is { } name)
            group.Name = name;
    }

    /// <summary>Deletes an own group; its files go to their automatic groups.</summary>
    private void DeleteGroup(EditorGroup group)
    {
        var files = group.Documents.ToList();
        Groups.Remove(group);
        foreach (var document in files)
            AutoGroupFor(document).Documents.Add(document);
        RebuildRows();
    }

    /// <summary>A unique, non-empty name (own groups), or null when cancelled.</summary>
    private string? AskGroupName(string prompt, string text, EditorGroup? except)
    {
        while (true)
        {
            string? name = InputDialog.Ask(Window.GetWindow(this), prompt, text)?.Trim();
            if (name is null)
                return null;
            if (name.Length == 0)
                continue;
            if (Groups.Any(g => !g.IsAuto && g != except && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                prompt = $"A group \"{name}\" exists already. Another name:";
                text = name;
                continue;
            }
            return name;
        }
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var group in Groups)
            group.IsExpanded = expanded;
        RebuildRows();
    }

    // ---------- Rows under the mouse ----------

    /// <summary>The "- - - - - -" row between own and automatic groups (not selectable, not a drop target).</summary>
    public sealed class GroupDivider
    {
        public static readonly GroupDivider Instance = new();

        /// <summary>For the row style: like a header, never selected or focused.</summary>
        public bool IsGroupHeader => true;
    }

    private static object? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem item)
                return item.DataContext;
        }
        return null;
    }

    /// <summary>The row at a height in the list (headers and files), or null below the last row.</summary>
    private object? RowAtY(double y)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (FileList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            double top = row.TranslatePoint(new Point(0, 0), FileList).Y;
            if (y >= top && y < top + row.ActualHeight)
                return _rows[i];
        }
        return null;
    }

    // ---------- Context menus ----------

    /// <summary>"Icon": the icons in a grid of six, the file's current one ticked; a click gives it to the file.</summary>
    private MenuItem IconMenu(EditorDocument document)
    {
        var menu = new MenuItem { Header = "Icon" };
        var grid = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
        grid.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, 6);
        menu.ItemsPanel = new ItemsPanelTemplate(grid);
        foreach (var icon in FileIcons.All)
        {
            var item = new MenuItem
            {
                Header = IconVisual(icon, 24),
                ToolTip = icon.Name,
                IsChecked = document.IconId == icon.Id,
                Padding = new Thickness(6, 4, 6, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            item.Click += (_, _) => SetIcon(document, icon.Id);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>The icon's picture, or its stand-in symbol while the picture files are missing.</summary>
    private static FrameworkElement IconVisual(FileIcon icon, double size) =>
        icon.ImageFor(size) is { } image
            ? new Image { Source = image, Width = size, Height = size }
            : new TextBlock
            {
                Text = icon.Glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = size * 0.8,
                Foreground = icon.Brush,
                Width = size,
                Height = size,
                TextAlignment = TextAlignment.Center,
            };

    private void SetIcon(EditorDocument document, string? id)
    {
        document.IconId = id;
        ScheduleBackup(); // The icon is part of the session.
    }

    /// <summary>Right-click: a file, a group header, or empty space. Menus are built in code
    /// (click handlers on menu items inside a style break WPF's XAML loader).</summary>
    private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = NewMenu(FileList, PlacementMode.MousePoint);
        void Add(ItemsControl parent, string header, string gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            parent.Items.Add(item);
        }

        switch (RowAt(e.OriginalSource))
        {
            case EditorDocument document:
            {
                ShowDocument(document, focusEditor: false);
                var current = GroupOf(document);
                if (document.IsMissing)
                {
                    Add(menu, "Dismiss (the file is gone)", "", () => DismissDocument(document));
                    menu.Items.Add(new Separator());
                }
                Add(menu, "Save", "Ctrl+S", () => SaveDocument(document), enabled: document.HasContent);
                Add(menu, "Save as...", "Ctrl+Alt+S", () => SaveDocumentAs(document), enabled: document.HasContent);
                Add(menu, "Close", "Ctrl+W", () => CloseDocument(document));
                Add(menu, "Close others", "", () =>
                {
                    foreach (var other in Documents.Where(d => d != document).ToList())
                    {
                        if (!CloseDocument(other))
                            break;
                    }
                });
                menu.Items.Add(new Separator());
                menu.Items.Add(IconMenu(document));
                Add(menu, "Default icon", "", () => SetIcon(document, null), enabled: document.HasIcon);
                menu.Items.Add(new Separator());

                var moveTo = new MenuItem { Header = "Move to group" };
                foreach (var own in Groups.Where(g => !g.IsAuto && g != current))
                    Add(moveTo, own.Name, "", () => MoveDocument(document, own, own.Documents.Count));
                if (moveTo.Items.Count > 0)
                    moveTo.Items.Add(new Separator());
                Add(moveTo, "New group...", "", () =>
                {
                    if (NewGroup() is { } created)
                        MoveDocument(document, created, 0);
                });
                if (current is { IsAuto: false })
                {
                    moveTo.Items.Add(new Separator());
                    Add(moveTo, "Remove from group", "", () =>
                    {
                        var auto = AutoGroupFor(document);
                        MoveDocument(document, auto, auto.Documents.Count);
                    });
                }
                menu.Items.Add(moveTo);
                menu.Items.Add(new Separator());
                Add(menu, "Copy path", "", () =>
                {
                    try
                    {
                        Clipboard.SetText(document.FilePath);
                    }
                    catch (COMException)
                    {
                    }
                }, enabled: !document.IsUntitled);
                Add(menu, "Show in explorer", "", () => ShowInExplorerRequested?.Invoke(document.FilePath), enabled: !document.IsUntitled);
                break;
            }

            case EditorGroup group:
                Add(menu, "New group...", "", () => NewGroup());
                if (!group.IsAuto)
                {
                    Add(menu, "Rename group...", "", () => RenameGroup(group));
                    Add(menu, "Delete group", "", () => DeleteGroup(group));
                }
                menu.Items.Add(new Separator());
                Add(menu, "Collapse all", "", () => SetAllExpanded(false));
                Add(menu, "Expand all", "", () => SetAllExpanded(true));
                break;

            default:
                Add(menu, "New file", "Ctrl+N", NewFile);
                menu.Items.Add(new Separator());
                Add(menu, "New group...", "", () => NewGroup());
                Add(menu, "Collapse all", "", () => SetAllExpanded(false), enabled: Groups.Count > 0);
                Add(menu, "Expand all", "", () => SetAllExpanded(true), enabled: Groups.Count > 0);
                break;
        }
        menu.IsOpen = true;
    }

    /// <summary>Middle-click on a file in the list closes it (like browser tabs).</summary>
    private void FileList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && RowAt(e.OriginalSource) is EditorDocument document)
        {
            e.Handled = true;
            CloseDocument(document);
        }
    }

    // ---------- Collapse (click on a header) and reorder (drag) ----------

    private object? _dragItem;
    private Point _dragStart;
    private bool _reordering;
    private DragGhost? _ghost;

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A drag whose drop never arrived (see EndReorder) ends before a new press starts.
        if (_reordering)
            EndReorder();
        _dragStart = e.GetPosition(FileList);
        _dragItem = RowAtY(_dragStart.Y);
        if (_dragItem is GroupDivider)
        {
            _dragItem = null;
            e.Handled = true;
            return;
        }
        // A header is never selected: its click collapses / expands it (on mouse up).
        if (_dragItem is EditorGroup)
            e.Handled = true;
    }

    /// <summary>
    /// Dragging a file moves it live: within its group, into an own group, or out of an own group
    /// (onto its automatic group). Dragging a header moves the whole group. The row stays faded
    /// at its new place; a picture of it follows the mouse.
    /// </summary>
    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // The button is up but the drop never reached the list: end the drag now.
            if (_reordering)
                EndReorder();
            return;
        }

        Point pos = e.GetPosition(FileList);
        if (!_reordering)
        {
            if (Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _reordering = true;
            FileList.CaptureMouse();
            if (FileList.ItemContainerGenerator.ContainerFromItem(_dragItem) is FrameworkElement source)
                _ghost = new DragGhost(source, FileList, grabPoint: FileList.TranslatePoint(_dragStart, source));
            SetDragging(_dragItem, true);
        }
        // The list itself would select the row under the mouse while the button is down.
        e.Handled = true;
        _ghost?.Move(pos);

        var target = RowAtY(pos.Y);
        if (target is null or GroupDivider || target == _dragItem)
            return;

        if (_dragItem is EditorDocument document)
        {
            if (target is EditorDocument other && GroupOf(other) is { } group)
                MoveDocument(document, group, group.Documents.IndexOf(other));
            else if (target is EditorGroup header)
                MoveDocument(document, header, 0);
        }
        else if (_dragItem is EditorGroup dragged)
        {
            var over = target as EditorGroup ?? GroupOf((EditorDocument)target);
            if (over is not null && over != dragged)
            {
                Groups.Move(Groups.IndexOf(dragged), Groups.IndexOf(over));
                RebuildRows();
            }
        }
    }

    private static void SetDragging(object item, bool dragging)
    {
        if (item is EditorDocument document)
            document.IsDragging = dragging;
        else if (item is EditorGroup group)
            group.IsDragging = dragging;
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_reordering)
        {
            e.Handled = true; // The drop is not a click.
        }
        // By position: a mouse capture left from a drag would report the list as the source.
        else if (_dragItem is EditorGroup group && RowAtY(e.GetPosition(FileList).Y) == group)
        {
            e.Handled = true;
            group.IsExpanded = !group.IsExpanded;
            RebuildRows();
        }
        EndReorder();
    }

    private void FileList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Only our own capture ends a drag (the list's items take and drop capture on every click).
        if (_reordering && e.OriginalSource == FileList)
            EndReorder();
    }

    private void EndReorder()
    {
        bool wasReordering = _reordering;
        _reordering = false;
        _ghost?.Close();
        _ghost = null;
        if (_dragItem is not null)
            SetDragging(_dragItem, false);
        _dragItem = null;
        // The ListBox also captures the mouse when a drag starts on a file row and never sees
        // the button go up (the drop is handled here): release every capture.
        if (wasReordering && Mouse.Captured is not null)
            Mouse.Capture(null);
    }

    // ---------- Session ----------

    /// <summary>True while the last session is put back (no backups then: they would miss entries).</summary>
    private bool _restoring;

    /// <summary>Entries whose text is kept in data\notepad-cache: gone files, unsaved changes, new files with text.</summary>
    private static bool KeepsText(EditorDocument d) =>
        d.HasContent && (d.IsMissing || d.IsDirty || (d.IsUntitled && d.Document.TextLength > 0));

    public NotepadState CaptureState()
    {
        // Texts that must survive a restart (data\notepad-cache): files that are gone,
        // unsaved changes, and new files that were never saved.
        var kept = Documents.Where(KeepsText).ToList();
        if (!_restoring)
            NotepadTextCache.Update(kept);
        if (Active is { } active && Editor.Document == active.Document)
            RememberView(active);
        var all = Documents.ToList();
        return new NotepadState
        {
            Groups = [.. Groups.Select(g => new NotepadGroupState { Name = g.Name, Key = g.IsAuto ? g.Key : null, Expanded = g.IsExpanded })],
            AutoGroupBy = _autoGroupBy,
            Files = [.. all.Select(d => new NotepadFileState
            {
                Path = d.FilePath,
                Untitled = d.IsUntitled ? d.FileName : null,
                UntitledId = d.IsUntitled ? d.UntitledId : null,
                Unsaved = kept.Contains(d) && !d.IsMissing,
                DiskTicks = d.DiskWriteTimeUtc.Ticks,
                DiskLength = d.DiskLength,
                Group = GroupOf(d) is { IsAuto: false } own ? own.Name : null,
                Language = d.Highlighting?.Name ?? "",
                Icon = d.IconId,
                Caret = d.CaretOffset,
                VerticalOffset = d.VerticalOffset,
                CachedEncoding = kept.Contains(d) ? d.EncodingKey : null,
            })],
            ActiveIndex = Active is null ? -1 : all.IndexOf(Active),
            ListWidth = ListColumn.ActualWidth,
        };
    }

    /// <summary>
    /// Reopens the files and groups of the last session, with their unsaved changes and the new
    /// files (their text was kept in data\notepad-cache). Files that are gone stay listed in red.
    /// </summary>
    public async Task RestoreAsync(NotepadState? state)
    {
        if (state is null)
            return;
        _restoring = true;
        try
        {
            await RestoreFilesAsync(state);
        }
        finally
        {
            _restoring = false;
        }
    }

    private async Task RestoreFilesAsync(NotepadState state)
    {
        if (state.ListWidth >= 80)
            ListColumn.Width = new GridLength(state.ListWidth);

        // Groups in their saved order; automatic ones only when they were made the same way.
        foreach (var saved in state.Groups ?? [])
        {
            if (saved.Key is null)
                Groups.Add(new EditorGroup(saved.Name) { IsExpanded = saved.Expanded });
            else if (string.Equals(state.AutoGroupBy, _autoGroupBy, StringComparison.OrdinalIgnoreCase) || saved.Key == NewFilesKey)
                AutoGroup(saved.Key).IsExpanded = saved.Expanded;
        }

        var restored = new List<EditorDocument?>();
        foreach (var file in state.Files)
        {
            var own = file.Group is null ? null : Groups.FirstOrDefault(g => !g.IsAuto && g.Name == file.Group);
            EditorDocument? document;
            if (file.Untitled is not null)
            {
                // A new file that was never saved, with its text.
                document = EditorDocument.CreateUntitled(file.Untitled, file.UntitledId);
                if (file.CachedEncoding is not null && NotepadTextCache.Read(document.CacheKey) is { } text)
                    document.ApplyUnsavedText(text, file.CachedEncoding);
                AddDocument(document, own);
            }
            else if (File.Exists(file.Path))
            {
                document = await LoadOrFindAsync(file.Path, own);
                if (document is not null && file.Unsaved && file.CachedEncoding is not null &&
                    NotepadTextCache.Read(file.Path) is { } text)
                {
                    // The unsaved changes come back over the file's text (Ctrl+Z = the saved text).
                    document.ApplyUnsavedText(text, file.CachedEncoding);
                    // Another program changed the file since: ask when it is shown.
                    if (file.DiskTicks != 0 && document.DiffersFromDisk(new DateTime(file.DiskTicks, DateTimeKind.Utc), file.DiskLength))
                        document.ChangedOutside = true;
                }
            }
            else
            {
                // Gone: keep the entry (red) until the user dismisses it, with its cached text if any.
                document = file.CachedEncoding is not null && NotepadTextCache.Read(file.Path) is { } text
                    ? EditorDocument.CreateMissing(file.Path, text, file.CachedEncoding)
                    : EditorDocument.CreateMissing(file.Path);
                AddDocument(document, own);
            }
            restored.Add(document);
            if (document is null)
                continue;
            // Language "" = the user chose Normal Text; null = by extension (already set).
            if (file.Language is not null)
                document.Highlighting = file.Language.Length == 0 ? null : EditorHighlighting.ByName(file.Language);
            document.IconId = file.Icon;
            document.CaretOffset = file.Caret;
            document.VerticalOffset = file.VerticalOffset;
        }
        RemoveEmptyAutoGroups();
        RebuildRows();

        var reopened = state.ActiveIndex >= 0 && state.ActiveIndex < restored.Count ? restored[state.ActiveIndex] : null;
        ShowDocument(reopened ?? Documents.FirstOrDefault());
    }
}
