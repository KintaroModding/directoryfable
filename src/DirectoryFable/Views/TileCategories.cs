using System.Collections;
using DirectoryFable.Models;
using DirectoryFable.Player;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// The categories of the file list's tile view, always in this order: folders, videos (OpusHC's video
/// types), images (AstraViewer's image types), other files. <see cref="TilePanel"/> starts each with a header.
/// </summary>
public static class TileCategories
{
    public const int Folders = 0;
    public const int Videos = 1;
    public const int Images = 2;
    public const int Others = 3;

    public static int Of(object? item) => item switch
    {
        FileItem { IsDirectory: true } => Folders,
        FileItem file when VideoFileTypes.IsVideo(file.FullPath) => Videos,
        FileItem file when ImageFileTypes.IsImage(file.FullPath) => Images,
        _ => Others,
    };

    public static string Name(int category) => category switch
    {
        Folders => "Folders",
        Videos => "Videos",
        Images => "Images",
        _ => "Other files",
    };

    /// <summary>Category first, then the tab's own sort order inside each category.</summary>
    public sealed class Comparer(IComparer inner) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            int category = Of(x).CompareTo(Of(y));
            return category != 0 ? category : inner.Compare(x, y);
        }
    }
}
