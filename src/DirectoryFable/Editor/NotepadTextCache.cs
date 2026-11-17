using System.IO;
using System.Security.Cryptography;
using System.Text;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Editor;

/// <summary>
/// Keeps Notepad# texts over an app restart (and a crash): unsaved changes, new files that
/// were never saved, and files that no longer exist on disk.
/// <c>data\notepad-cache\&lt;hash of the path or new-file id&gt;.txt</c>, always UTF-8; the
/// entry's own encoding is stored in the session. Written with the session (also a few seconds
/// after typing stops); a text is only written again when it changed. Cache files of entries
/// that are saved, closed or dismissed are deleted then.
/// </summary>
public static class NotepadTextCache
{
    private static string Folder => Path.Combine(AppSettings.DataFolder, "notepad-cache");

    /// <summary>Cache file -> (entry, its change count) as last written.</summary>
    private static readonly Dictionary<string, (EditorDocument Document, int ChangeCount)> Written = new(StringComparer.OrdinalIgnoreCase);

    private static string FileFor(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash, 0, 12) + ".txt");
    }

    /// <summary>
    /// Writes the texts of the given entries (when changed since the last write) and removes all
    /// other cache files. Never throws: a cache that cannot be written only loses that text.
    /// </summary>
    public static void Update(IEnumerable<EditorDocument> keep)
    {
        try
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in keep)
            {
                string file = FileFor(document.CacheKey);
                files.Add(file);
                if (Written.TryGetValue(file, out var last) && last.Document == document &&
                    last.ChangeCount == document.ChangeCount && File.Exists(file))
                    continue;
                Directory.CreateDirectory(Folder);
                string temp = file + ".tmp";
                File.WriteAllText(temp, document.Document.Text, new UTF8Encoding(false));
                File.Move(temp, file, overwrite: true);
                Written[file] = (document, document.ChangeCount);
            }
            foreach (string gone in Written.Keys.Where(f => !files.Contains(f)).ToList())
                Written.Remove(gone);
            if (!Directory.Exists(Folder))
                return;
            foreach (string old in Directory.EnumerateFiles(Folder, "*.txt"))
            {
                if (!files.Contains(old))
                    File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the entry still shows after a restart, only without its kept text.
        }
    }

    /// <summary>The kept text of an entry (its <see cref="EditorDocument.CacheKey"/>), or null.</summary>
    public static string? Read(string key)
    {
        try
        {
            string file = FileFor(key);
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
