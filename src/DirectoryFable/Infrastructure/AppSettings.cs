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
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken settings file must not stop the app; defaults apply.
            return new AppSettings();
        }
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
          // Start folders when there is no saved session.
          "leftPath": {{Json(s.LeftPath)}},
          "rightPath": {{Json(s.RightPath)}}
        }

        """;
}
