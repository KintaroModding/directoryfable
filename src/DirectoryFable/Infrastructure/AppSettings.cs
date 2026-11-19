using System.IO;
using System.Text.Json;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// User settings from <c>settings.json</c> in the data folder (default: <c>data</c> next to
/// the exe, so the app is portable). The app creates the file when it is missing and writes
/// it only from the settings page (<see cref="Save"/>).
/// </summary>
public sealed class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>
    /// "taskbar" (default): a normal window. "tray": no taskbar button, tray icon, close = hide.
    /// Any other value counts as "taskbar", so a new user always sees the app in the taskbar.
    /// </summary>
    public string WindowMode { get; set; } = "taskbar";

    public bool IsTrayMode => string.Equals(WindowMode, "tray", StringComparison.OrdinalIgnoreCase);

    /// <summary>Start with a maximized window (default).</summary>
    public bool StartMaximized { get; set; } = true;

    /// <summary>Reopen the last tabs, window and splitters (session.json).</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>The Update button in the title bar (GitHub releases). False: no button and no update checks at all.</summary>
    public bool UpdateButton { get; set; } = true;

    /// <summary>Ask GitHub for a new version a few seconds after each start.</summary>
    public bool UpdateCheckAtStart { get; set; } = true;

    /// <summary>
    /// Folder sizes in the Size column, from the drive's file table (NTFS drives, as administrator).
    /// The first folder on a drive reads the whole drive once (a few seconds); then sizes show at once.
    /// </summary>
    public bool FolderSizes { get; set; } = true;

    /// <summary>Folder sizes: seconds between two checks of the drives' change journals (2 to 600).</summary>
    public int FolderSizesCheckSeconds { get; set; } = 10;

    /// <summary>How many recently opened folders This PC shows ("Recent folders"); 0 = none. 0 to 100.</summary>
    public int RecentFoldersCount { get; set; } = 10;

    /// <summary>The quick bar of each pane shows only the icons of its buttons (no labels).</summary>
    public bool QuickBarIconsOnly { get; set; }

    /// <summary>
    /// File list background while inside a ZIP file ("#RRGGBB"). Default almost pure dark
    /// (the normal list is #191919).
    /// </summary>
    public string ArchiveBackground { get; set; } = "#0C0C0C";

    /// <summary>Notepad# editor font.</summary>
    public string EditorFontFamily { get; set; } = "Consolas";

    /// <summary>Notepad# editor font size.</summary>
    public double EditorFontSize { get; set; } = 14;

    /// <summary>Notepad#: long lines wrap at the editor's right edge (no horizontal scrolling).</summary>
    public bool EditorWordWrap { get; set; }

    /// <summary>Notepad# file list: files in no own group are grouped by "type" or by "folder".</summary>
    public string NotepadAutoGroupBy { get; set; } = "type";

    /// <summary>A double-click in DirectoryFable opens <see cref="NotepadFileTypes"/> in Notepad# (Windows' defaults stay untouched).</summary>
    public bool NotepadOpensFileTypes { get; set; } = true;

    /// <summary>File types for <see cref="NotepadOpensFileTypes"/>, separated by spaces (".txt .log .json").</summary>
    public string NotepadFileTypes { get; set; } = Editor.EditorFileTypes.DefaultList;

    /// <summary>A double-click in DirectoryFable opens <see cref="ViewerFileTypes"/> in AstraViewer (Windows' defaults stay untouched).</summary>
    public bool ViewerOpensFileTypes { get; set; } = true;

    /// <summary>Image types for AstraViewer, separated by spaces (".png .jpg .gif").</summary>
    public string ViewerFileTypes { get; set; } = Viewer.ImageFileTypes.DefaultList;

    /// <summary>A double-click in DirectoryFable opens <see cref="PlayerFileTypes"/> in OpusHC (Windows' defaults stay untouched).</summary>
    public bool PlayerOpensFileTypes { get; set; } = true;

    /// <summary>Video types for OpusHC, separated by spaces (".mp4 .mkv").</summary>
    public string PlayerFileTypes { get; set; } = Player.VideoFileTypes.DefaultList;

    /// <summary>OpusHC: at the end of a video, play the next one of the folder.</summary>
    public bool PlayerAutoNext { get; set; } = true;

    /// <summary>OpusHC: a video starts where it was left.</summary>
    public bool PlayerResume { get; set; } = true;

    /// <summary>OpusHC: the filmstrip (small pictures of the whole video) above the timeline.</summary>
    public bool PlayerFilmstrip { get; set; } = true;

    /// <summary>OpusHC: how many pictures the filmstrip has (one of <see cref="FilmstripCounts"/>).</summary>
    public int PlayerFilmstripCount { get; set; } = 40;

    /// <summary>The steps of the filmstrip slider on OpusHC's settings page.</summary>
    public static readonly int[] FilmstripCounts = [20, 30, 40, 50, 60, 80, 100, 120, 160, 200];

    /// <summary>KeeSeek: lock after this many minutes without use (1-60); 0 = no timer, it locks when KeeSeek is left.</summary>
    public int KeeSeekLockMinutes { get; set; } = 10;

    /// <summary>KeeSeek: a copied user name or password is cleared from the clipboard after this many seconds (5-120).</summary>
    public int KeeSeekClipboardSeconds { get; set; } = 20;

    /// <summary>KeeSeek: entries as cards instead of rows.</summary>
    public bool KeeSeekCards { get; set; }

    /// <summary>KeeSeek: the height of an entry row in pixels (DIP), 16-88.</summary>
    public int KeeSeekRowHeight { get; set; } = 52;

    /// <summary>AstraViewer: how a newly shown image starts, "fit" (into the window, never enlarged) or "actual" (100 %).</summary>
    public string ViewerZoom { get; set; } = "fit";

    /// <summary>AstraViewer: background around the image, "dark", "black" or "light".</summary>
    public string ViewerBackground { get; set; } = "dark";

    /// <summary>AstraViewer: a checkerboard behind the image shows its transparent parts.</summary>
    public bool ViewerCheckerboard { get; set; } = true;

    /// <summary>AstraViewer: size of the thumbnails below the image, "small", "medium" or "large".</summary>
    public string ViewerThumbnailSize { get; set; } = "medium";

    /// <summary>AstraViewer: how many images the history keeps (10 to 1000).</summary>
    public int ViewerHistorySize { get; set; } = 100;

    /// <summary>Folder with 7zG.exe / 7z.exe ("7z" button, 7z and RAR browsing); empty = find 7-Zip automatically.</summary>
    public string SevenZipFolder { get; set; } = "";

    /// <summary>Start folders when there is no session (environment variables allowed).</summary>
    public string LeftPath { get; set; } = "%USERPROFILE%";
    public string RightPath { get; set; } = "C:\\";

    /// <summary>
    /// Folder for settings.json, favorites.json, session.json: "data" next to the exe
    /// (portable). <c>--data=</c> changes it (tests).
    /// </summary>
    public static string DataFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        string path = Path.Combine(DataFolder, FileName);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(DataFolder);
                File.WriteAllText(path, FileText(new AppSettings()));
            }
            return FillNulls(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOptions) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken settings file must not stop the app; defaults apply.
            return new AppSettings();
        }
    }

    /// <summary>A hand-edited <c>"key": null</c> gets the default value (text settings are never null).</summary>
    private static AppSettings FillNulls(AppSettings settings)
    {
        var defaults = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.PropertyType == typeof(string) && property.CanWrite && property.GetValue(settings) is null)
                property.SetValue(settings, property.GetValue(defaults));
        }
        return settings;
    }

    /// <summary>
    /// Changes settings.json: reads the newest file, applies <paramref name="change"/> and
    /// saves. Every settings page saves this way, so a page never writes back an older copy of
    /// settings that another page (or a key like Alt+Z) changed meanwhile. Returns the saved settings.
    /// </summary>
    public static AppSettings Update(Action<AppSettings> change)
    {
        var settings = Load();
        change(settings);
        settings.Save();
        return settings;
    }

    /// <summary>
    /// Writes settings.json (settings page). The whole file is written in the
    /// same commented layout as the first one, via a temp file so a crash cannot leave half a file.
    /// </summary>
    public void Save()
    {
        string path = Path.Combine(DataFolder, FileName);
        Directory.CreateDirectory(DataFolder);
        string temp = path + ".tmp";
        File.WriteAllText(temp, FileText(this));
        File.Move(temp, path, overwrite: true);
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Json(bool value) => value ? "true" : "false";

    private static string FileText(AppSettings s) => $$"""
        {
          // DirectoryFable settings. Change them on the settings page (gear button) or here.
          // "taskbar" (default): a normal window with a taskbar button; closing the window exits.
          // "tray": no taskbar button, only a tray icon; closing the window hides it.
          "windowMode": {{Json(s.WindowMode)}},
          // true: the window starts maximized.
          "startMaximized": {{Json(s.StartMaximized)}},
          // true: reopen the last tabs, window position and splitters.
          "restoreSession": {{Json(s.RestoreSession)}},
          // The Update button in the title bar (new versions from GitHub); false = no button and no update checks.
          "updateButton": {{Json(s.UpdateButton)}},
          // true: ask GitHub for a new version a few seconds after each start.
          "updateCheckAtStart": {{Json(s.UpdateCheckAtStart)}},
          // true: folders show their size (all files below) in the Size column. Needs an NTFS drive and administrator rights.
          "folderSizes": {{Json(s.FolderSizes)}},
          // Folder sizes follow the drives' change journals: seconds between two checks (2 to 600).
          "folderSizesCheckSeconds": {{s.FolderSizesCheckSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // This PC: how many recently opened folders it shows (0 = none, up to 100).
          "recentFoldersCount": {{s.RecentFoldersCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // true: the quick bar of each pane shows only the icons of its buttons (the tooltips name them).
          "quickBarIconsOnly": {{Json(s.QuickBarIconsOnly)}},
          // File list background while inside a ZIP file ("#RRGGBB"; normal list: "#191919").
          "archiveBackground": {{Json(s.ArchiveBackground)}},
          // Folder of 7-Zip (7zG.exe, 7z.exe) for the "7z" button and 7z / RAR archives; "" = find it automatically.
          "sevenZipFolder": {{Json(s.SevenZipFolder)}},
          // Notepad#: editor font and size.
          "editorFontFamily": {{Json(s.EditorFontFamily)}},
          "editorFontSize": {{s.EditorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // Notepad#: true = long lines wrap at the right edge (Alt+Z switches it).
          "editorWordWrap": {{Json(s.EditorWordWrap)}},
          // Notepad#: a double-click in DirectoryFable opens these file types in Notepad# (Windows' defaults are not changed).
          "notepadOpensFileTypes": {{Json(s.NotepadOpensFileTypes)}},
          // Notepad# file list: files in no own group are grouped "type" or "folder".
          "notepadAutoGroupBy": {{Json(s.NotepadAutoGroupBy)}},
          "notepadFileTypes": {{Json(s.NotepadFileTypes)}},
          // AstraViewer: a double-click in DirectoryFable opens these image types in AstraViewer (Windows' defaults are not changed).
          "viewerOpensFileTypes": {{Json(s.ViewerOpensFileTypes)}},
          "viewerFileTypes": {{Json(s.ViewerFileTypes)}},
          // AstraViewer: start zoom "fit" (into the window) or "actual" (100 %); background "dark", "black" or "light";
          // checkerboard behind transparent images; images kept in the history (10 to 1000).
          "viewerZoom": {{Json(s.ViewerZoom)}},
          "viewerBackground": {{Json(s.ViewerBackground)}},
          "viewerCheckerboard": {{Json(s.ViewerCheckerboard)}},
          // AstraViewer: thumbnails below the image "small", "medium" or "large".
          "viewerThumbnailSize": {{Json(s.ViewerThumbnailSize)}},
          "viewerHistorySize": {{s.ViewerHistorySize.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // OpusHC: a double-click in DirectoryFable opens these video types there (Windows' defaults are not changed).
          "playerOpensFileTypes": {{Json(s.PlayerOpensFileTypes)}},
          "playerFileTypes": {{Json(s.PlayerFileTypes)}},
          // OpusHC: play the next video of the folder at the end; start a video where it was left.
          "playerAutoNext": {{Json(s.PlayerAutoNext)}},
          "playerResume": {{Json(s.PlayerResume)}},
          // OpusHC: the filmstrip above the timeline (small pictures of the whole video; made in memory only).
          "playerFilmstrip": {{Json(s.PlayerFilmstrip)}},
          // OpusHC: pictures in the filmstrip: 20, 30, 40, 50, 60, 80, 100, 120, 160 or 200.
          "playerFilmstripCount": {{s.PlayerFilmstripCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // KeeSeek: lock after this many minutes without use (1 to 60); 0 = no timer, it locks when you leave KeeSeek.
          "keeSeekLockMinutes": {{s.KeeSeekLockMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // KeeSeek: a copied user name or password leaves the clipboard after this many seconds (5 to 120).
          "keeSeekClipboardSeconds": {{s.KeeSeekClipboardSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // KeeSeek: entries as cards (true) or rows (false).
          "keeSeekCards": {{Json(s.KeeSeekCards)}},
          // KeeSeek: the height of an entry row in pixels (16 to 88).
          "keeSeekRowHeight": {{s.KeeSeekRowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          // Start folders when there is no saved session.
          "leftPath": {{Json(s.LeftPath)}},
          "rightPath": {{Json(s.RightPath)}}
        }

        """;
}
