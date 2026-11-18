using System.IO;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Player;

/// <summary>
/// Video types that OpusHC plays. A double-click (or Enter) on them in DirectoryFable opens OpusHC instead of
/// their Windows program, when switched on. Only inside DirectoryFable: Windows' own file associations are never
/// changed.
/// </summary>
public static class VideoFileTypes
{
    public const string DefaultList = ".mp4 .mkv .m4v .mov .avi .wmv .webm .ts .m2ts .mts .mpg .mpeg .flv .3gp .ogv";

    private static bool _enabled = true;
    private static HashSet<string> _extensions = Editor.EditorFileTypes.Parse(DefaultList);

    /// <summary>From settings.json (at start and when the player's settings change).</summary>
    public static void Configure(AppSettings settings)
    {
        _enabled = settings.PlayerOpensFileTypes;
        _extensions = Editor.EditorFileTypes.Parse(settings.PlayerFileTypes ?? DefaultList);
    }

    /// <summary>A video type of the list (next / previous in the folder, "Open in OpusHC").</summary>
    public static bool IsVideo(string path) => _extensions.Contains(Path.GetExtension(path));

    /// <summary>True when a double-click on this file opens it in OpusHC.</summary>
    public static bool OpensInPlayer(string path) => _enabled && IsVideo(path);
}
