using System.IO;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Viewer;

/// <summary>
/// File types that AstraViewer shows: images and PDF. A double-click (or Enter) on them in DirectoryFable opens
/// AstraViewer instead of their Windows program, when switched on. Only inside DirectoryFable:
/// Windows' own file associations are never changed.
/// </summary>
public static class ImageFileTypes
{
    /// <summary>
    /// Types that Windows decodes (WIC): built in, plus WebP (the Windows 11 codec), and PDF (drawn by PDFium).
    /// HEIC / AVIF need extra Windows codecs and are left out; the user can add them.
    /// </summary>
    public const string DefaultList = ".png .jpg .jpeg .jpe .jfif .bmp .dib .gif .tif .tiff .ico .webp .jxr .wdp .pdf";

    private static bool _enabled = true;
    private static HashSet<string> _extensions = Editor.EditorFileTypes.Parse(DefaultList);

    /// <summary>From settings.json (at start and when the AstraViewer settings change).</summary>
    public static void Configure(AppSettings settings)
    {
        _enabled = settings.ViewerOpensFileTypes;
        _extensions = Editor.EditorFileTypes.Parse(settings.ViewerFileTypes ?? DefaultList);
    }

    /// <summary>A type that AstraViewer shows, image or PDF (folder strip, drops, "Open in AstraViewer").</summary>
    public static bool IsImage(string path) => _extensions.Contains(Path.GetExtension(path));

    /// <summary>A PDF: AstraViewer shows its pages (not decoded as an image).</summary>
    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a double-click on this file opens it in AstraViewer.</summary>
    public static bool OpensInViewer(string path) => _enabled && IsImage(path);
}
