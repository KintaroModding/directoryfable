using System.IO;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Editor;

/// <summary>
/// File types that a double-click (or Enter) in DirectoryFable opens in Notepad# instead of
/// their Windows program. Only inside DirectoryFable: Windows' own file
/// associations are never changed.
/// </summary>
public static class EditorFileTypes
{
    /// <summary>
    /// Text and config types. Script types that Windows runs on a double-click (.ahk, .bat, .cmd,
    /// .ps1, .vbs, .reg) and web pages are left out on purpose; the user can add them.
    /// </summary>
    public const string DefaultList =
        ".txt .log .ini .cfg .conf .config .json .jsonc .xml .md .markdown .yml .yaml .toml .csv .tsv " +
        ".nfo .diz .cs .csproj .props .targets .xaml .py .js .ts .css .scss .sql .editorconfig .gitignore .gitattributes";

    private static bool _enabled = true;
    private static HashSet<string> _extensions = Parse(DefaultList);

    /// <summary>From settings.json (at start and when the Notepad# settings change).</summary>
    public static void Configure(AppSettings settings)
    {
        _enabled = settings.NotepadOpensFileTypes;
        _extensions = Parse(settings.NotepadFileTypes);
    }

    /// <summary>True when a double-click on this file opens it in Notepad#.</summary>
    public static bool OpensInNotepad(string path)
    {
        if (!_enabled)
            return false;
        string extension = Path.GetExtension(path);
        // Dot files like ".gitignore" have no name part: the whole name is the "extension".
        return extension.Length > 0 && _extensions.Contains(extension);
    }

    /// <summary>"txt, .log  json" -> {".txt", ".log", ".json"}.</summary>
    public static HashSet<string> Parse(string list) =>
        new(list.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.StartsWith('.') ? x : "." + x),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The list in a normal form: ".txt .log .json" (for the settings box and file).</summary>
    public static string Normalize(string list) => string.Join(" ", Parse(list));
}
