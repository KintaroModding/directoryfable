using System.IO;
using System.Security.Cryptography;
using System.Text;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Editor;

/// <summary>
/// Keeps the text of Notepad# files that no longer exist on disk over an app restart:
/// <c>data\notepad-cache\&lt;hash of the path&gt;.txt</c>, always UTF-8; the
/// file's own encoding is stored in the session. Written when the session is saved; cache files
/// of entries that were dismissed or whose file is back are deleted then.
/// </summary>
public static class MissingFileCache
{
    private static string Folder => Path.Combine(AppSettings.DataFolder, "notepad-cache");

    private static string FileFor(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash, 0, 12) + ".txt");
    }

    /// <summary>
    /// Writes the texts of the given missing files and removes all other cache files.
    /// Returns nothing to throw: a cache that cannot be written only loses that text.
    /// </summary>
    public static void Update(IEnumerable<EditorDocument> missing)
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in missing)
            {
                Directory.CreateDirectory(Folder);
                string file = FileFor(document.FilePath);
                File.WriteAllText(file, document.Document.Text, new UTF8Encoding(false));
                keep.Add(file);
            }
            if (!Directory.Exists(Folder))
                return;
            foreach (string old in Directory.EnumerateFiles(Folder, "*.txt"))
            {
                if (!keep.Contains(old))
                    File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the entry still shows after a restart, only without its text.
        }
    }

    /// <summary>The cached text of a missing file, or null.</summary>
    public static string? Read(string path)
    {
        try
        {
            string file = FileFor(path);
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
