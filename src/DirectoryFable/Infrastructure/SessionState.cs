using System.IO;
using System.Text.Json;

namespace DirectoryFable.Infrastructure;

/// <summary>One shown column of a file list: which one and how wide (display order = list order).</summary>
public sealed class ColumnState
{
    public string Key { get; set; } = "";
    public double Width { get; set; }
}

/// <summary>One tab as saved in session.json.</summary>
public sealed class TabState
{
    public string Path { get; set; } = "";
    public string SortColumn { get; set; } = nameof(Infrastructure.SortColumn.Name);
    public bool SortDescending { get; set; }
    public bool GroupByType { get; set; } = true;
    /// <summary>Shown columns in display order with widths; null = default columns.</summary>
    public List<ColumnState>? Columns { get; set; }
    /// <summary>Closed group dividers (type names).</summary>
    public List<string>? CollapsedGroups { get; set; }
    /// <summary>The tab showed the Space view.</summary>
    public bool Space { get; set; }
}

/// <summary>One pane (left or right) as saved in session.json.</summary>
public sealed class PaneState
{
    public List<TabState> Tabs { get; set; } = [];
    public int SelectedIndex { get; set; }
}

/// <summary>
/// What the app restores at the next start: window, splitters, tabs.
/// Stored in <c>session.json</c> in the data folder; written by the app, separate from
/// settings.json so the user's settings file is never overwritten.
/// </summary>
/// <summary>One open Notepad# file in the session.</summary>
public sealed class NotepadFileState
{
    /// <summary>The file's path; "" for a new file that was never saved (see <see cref="Untitled"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>A new file without a path: its name ("new 1"); null for normal files.</summary>
    public string? Untitled { get; set; }

    /// <summary>A new file's id: its text is kept in data\notepad-cache under this id.</summary>
    public string? UntitledId { get; set; }

    /// <summary>
    /// True: the entry had unsaved changes; their text is in data\notepad-cache (with
    /// <see cref="CachedEncoding"/>) and is put back over the file's text at the next start.
    /// </summary>
    public bool Unsaved { get; set; }

    /// <summary>Write time (UTC ticks) and size of the file when Notepad# last read or wrote it.</summary>
    public long DiskTicks { get; set; }
    public long DiskLength { get; set; }

    /// <summary>The own group the file is in; null = its automatic group.</summary>
    public string? Group { get; set; }

    /// <summary>Language name; "" = Normal Text.</summary>
    public string? Language { get; set; }

    /// <summary>The icon chosen in the file list (Editor/FileIcons.cs id); null = the default symbol.</summary>
    public string? Icon { get; set; }
    public int Caret { get; set; }
    public double VerticalOffset { get; set; }

    /// <summary>The entry's text is in data\notepad-cache (file gone, unsaved changes, new file): its encoding key; null = not cached.</summary>
    public string? CachedEncoding { get; set; }
}

/// <summary>Search: the chosen drives / place, the text, filters, sorting and the time mark.</summary>
public sealed class SearchState
{
    /// <summary>Roots of the chosen drives ("C:\"); null = the default (all fixed drives).</summary>
    public List<string>? Drives { get; set; }

    /// <summary>Older sessions: one folder to search in (now part of <see cref="CheckedFolders"/>).</summary>
    public string? Place { get; set; }

    /// <summary>Folders pinned to the location list (ticked or not).</summary>
    public List<string>? Folders { get; set; }

    /// <summary>Ticked folders (searched).</summary>
    public List<string>? CheckedFolders { get; set; }

    public string Text { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool UseRegex { get; set; }
    public int Type { get; set; }
    public string Extensions { get; set; } = "";
    public int Size { get; set; }
    public int DateColumn { get; set; }
    public int Range { get; set; }
    public int Sort { get; set; }
    public bool Descending { get; set; }

    /// <summary>Result columns in display order with widths (key = column tag); null = default.</summary>
    public List<ColumnState>? Columns { get; set; }

    /// <summary>The time mark (UTC FILETIME); null = none.</summary>
    public long? Mark { get; set; }
}

/// <summary>AstraViewer: the image history (newest first) and the shown image.</summary>
public sealed class ViewerState
{
    public List<string> History { get; set; } = [];

    /// <summary>The image shown at the end (null = none).</summary>
    public string? Current { get; set; }

    public double ListWidth { get; set; }
}

/// <summary>OpusHC in the session.</summary>
public sealed class PlayerState
{
    /// <summary>The video shown at the end (null = none).</summary>
    public string? Current { get; set; }

    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }

    /// <summary>Where videos were left, the newest first.</summary>
    public List<PlayerPositionState> Positions { get; set; } = [];
}

/// <summary>A video and the second it was left at.</summary>
public sealed class PlayerPositionState
{
    public string Path { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>A group of the Notepad# file list, in list order.</summary>
public sealed class NotepadGroupState
{
    public string Name { get; set; } = "";

    /// <summary>Automatic groups: the extension or folder; own groups: null.</summary>
    public string? Key { get; set; }
    public bool Expanded { get; set; } = true;
}

public sealed class NotepadState
{
    public List<NotepadGroupState>? Groups { get; set; }

    /// <summary>"type" or "folder": how the automatic groups were made.</summary>
    public string? AutoGroupBy { get; set; }
    public List<NotepadFileState> Files { get; set; } = [];
    public int ActiveIndex { get; set; } = -1;
    public double ListWidth { get; set; }
}

public sealed class SessionState
{
    private const string FileName = "session.json";

    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool Maximized { get; set; }

    public double TreeWidth { get; set; }
    /// <summary>Star sizes of the left and right pane columns (their ratio is the splitter position).</summary>
    public double LeftPaneStars { get; set; } = 1;
    public double RightPaneStars { get; set; } = 1;

    public bool RightPaneActive { get; set; }

    /// <summary>Folder tree follows the active pane's folder (header toggle).</summary>
    public bool TreeFollowsPane { get; set; } = true;
    public PaneState Left { get; set; } = new();
    public PaneState Right { get; set; } = new();

    /// <summary>True when Notepad# was the shown view.</summary>
    public bool NotepadShown { get; set; }

    /// <summary>Open Notepad# files.</summary>
    public NotepadState? Notepad { get; set; }

    /// <summary>True when AstraViewer was the shown view.</summary>
    public bool ViewerShown { get; set; }

    /// <summary>AstraViewer's history and shown image.</summary>
    public ViewerState? Viewer { get; set; }

    /// <summary>True when OpusHC was the shown view.</summary>
    public bool PlayerShown { get; set; }

    /// <summary>OpusHC: the last video, volume, where videos were left.</summary>
    public PlayerState? Player { get; set; }

    /// <summary>True when Search was the shown view.</summary>
    public bool SearchShown { get; set; }

    /// <summary>Search: drives, text, filters, mark (the results are not kept).</summary>
    public SearchState? Search { get; set; }

    /// <summary>Column widths of the Space view (both panes).</summary>
    public List<ColumnState>? SpaceColumns { get; set; }

    /// <summary>The Space view's block chart is shown, and its share of the height.</summary>
    public bool SpaceBlocks { get; set; } = true;
    public double SpaceBlocksShare { get; set; } = 0.45;

    /// <summary>The folders opened last, the newest first (This PC's "Recent folders").</summary>
    public List<RecentFolderState>? RecentFolders { get; set; }

    private static string FilePath => System.IO.Path.Combine(AppSettings.DataFolder, FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    /// <summary>Null when there is no (readable) session yet.</summary>
    public static SessionState? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            // Write to a temp file first: a crash while writing must not leave a broken session.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the next start uses the defaults.
        }
    }
}
