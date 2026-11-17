using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DirectoryFable.Shell;

/// <summary>One file or folder directly inside an archive folder.</summary>
public sealed record ZipItem(string Name, bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// Archives as folders. A path like
/// <c>C:\x\a.zip\sub\file.txt</c> means "file.txt in folder sub of a.zip".
/// ZIP: System.IO.Compression (part of .NET), read and write. 7z and RAR:
/// read only, through the installed 7-Zip (<c>7z.exe l / x</c>); without 7-Zip they stay
/// normal files. All methods except <see cref="TrySplit"/> and the Is* checks do file I/O:
/// call them off the UI thread.
/// </summary>
public static class ZipBrowser
{
    private static readonly string TempBase = Path.Combine(Path.GetTempPath(), "DirectoryFable", "zip");

    /// <summary>Archive types shown as folders. 7z and RAR need 7-Zip.</summary>
    private static readonly string[] ReadOnlyExtensions = [".7z", ".rar"];

    /// <summary>
    /// Temporary folder for files unpacked to open, copy or drag them. One per running copy
    /// (process id), so one copy never deletes files another copy still has open.
    /// </summary>
    public static string TempRoot { get; } = Path.Combine(TempBase, Environment.ProcessId.ToString());

    /// <summary>True for a file DirectoryFable opens like a folder (ZIP; 7z and RAR when 7-Zip is installed).</summary>
    public static bool IsZipFile(string path) => IsArchiveName(path) && File.Exists(path);

    private static bool IsArchiveName(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        (SevenZip.ConsolePath is not null && IsReadOnlyName(path));

    private static bool IsReadOnlyName(string path) =>
        ReadOnlyExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="path"/> is inside a 7z or RAR archive: nothing can be added, deleted or renamed there.</summary>
    public static bool IsInReadOnlyArchive(string path) => TrySplit(path, out string archive, out _) && IsReadOnlyName(archive);

    /// <summary>
    /// Splits "C:\x\a.zip\sub\f.txt" into the archive "C:\x\a.zip" and the inner path
    /// "sub/f.txt" (entries use "/"). False when the path is not inside an archive.
    /// </summary>
    public static bool TrySplit(string path, out string zipFile, out string inner)
    {
        zipFile = inner = "";
        string p = path.TrimEnd('\\');
        // Check each path part ending ("...\a.7z", "...\a.7z\sub") for an archive file name.
        for (int end = p.IndexOf('\\'); ; end = p.IndexOf('\\', end + 1))
        {
            int stop = end < 0 ? p.Length : end;
            string candidate = p[..stop];
            if (IsArchiveName(candidate) && File.Exists(candidate))
            {
                zipFile = candidate;
                inner = p[stop..].TrimStart('\\').Replace('\\', '/');
                return true;
            }
            if (end < 0)
                return false;
        }
    }

    public static bool IsInArchive(string path) => TrySplit(path, out _, out _);

    /// <summary>The direct children of a folder inside the archive (folders are also inferred from file paths).</summary>
    public static List<ZipItem> List(string zipFile, string inner)
    {
        string prefix = inner.Length > 0 ? inner.TrimEnd('/') + "/" : "";
        var folders = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ZipItem>();
        bool found = prefix.Length == 0;

        foreach (var entry in ReadEntries(zipFile))
        {
            string name = entry.Name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            found = true;
            string rest = name[prefix.Length..];
            if (rest.Length == 0)
                continue; // The folder entry itself.

            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                string folder = rest[..slash];
                // A real folder entry ("sub/") carries a date; inferred folders have none.
                DateTime date = slash == rest.Length - 1 ? entry.Modified : DateTime.MinValue;
                if (!folders.TryGetValue(folder, out var known) || known == DateTime.MinValue)
                    folders[folder] = date;
            }
            else
            {
                files.Add(new ZipItem(rest, false, entry.Size, entry.Modified));
            }
        }

        if (!found)
            throw new DirectoryNotFoundException($"Folder not found in {Path.GetFileName(zipFile)}: {inner}");

        return [.. folders.Select(f => new ZipItem(f.Key, true, 0, f.Value)), .. files];
    }

    /// <summary>One archive entry with "/" separators; folders end with "/".</summary>
    private sealed record Entry(string Name, long Size, DateTime Modified);

    private static IEnumerable<Entry> ReadEntries(string archivePath)
    {
        if (IsReadOnlyName(archivePath))
            return SevenZipEntries(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        return [.. archive.Entries.Select(e => new Entry(e.FullName.Replace('\\', '/'), e.Length, e.LastWriteTime.LocalDateTime))];
    }

    // ---------- 7z / RAR through 7-Zip (read only) ----------

    private static readonly Dictionary<string, (DateTime Stamp, List<Entry> Entries)> ListCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// All entries of a 7z / RAR archive ("7z l -slt"). Cached per archive and write time, so
    /// moving between folders inside one archive does not start 7-Zip every time.
    /// </summary>
    private static List<Entry> SevenZipEntries(string archivePath)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(archivePath);
        lock (CacheLock)
        {
            if (ListCache.TryGetValue(archivePath, out var hit) && hit.Stamp == stamp)
                return hit.Entries;
        }

        string output = RunSevenZip(["l", "-slt", "-sccUTF-8", "-scsUTF-8", "-p-", archivePath], workingDirectory: null);
        var entries = new List<Entry>();
        // Records after the "----------" line, separated by blank lines: "Path = ...", "Folder = +", ...
        int start = output.IndexOf("\n----------", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException($"7-Zip could not list {Path.GetFileName(archivePath)}.");
        foreach (string block in output[start..].Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in block.Split('\n'))
            {
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq > 0)
                    fields[line[..eq].Trim()] = line[(eq + 3)..].TrimEnd('\r');
            }
            if (!fields.TryGetValue("Path", out string? path) || path.Length == 0)
                continue;
            bool folder = fields.TryGetValue("Folder", out string? f) && f == "+" ||
                          fields.TryGetValue("Attributes", out string? a) && a.StartsWith('D');
            long size = fields.TryGetValue("Size", out string? s) && long.TryParse(s, out long n) ? n : 0;
            DateTime modified = fields.TryGetValue("Modified", out string? m) && m.Length >= 19 &&
                DateTime.TryParseExact(m[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DateTime.MinValue;
            string name = path.Replace('\\', '/');
            entries.Add(new Entry(folder ? name.TrimEnd('/') + "/" : name, folder ? 0 : size, modified));
        }

        lock (CacheLock)
        {
            if (ListCache.Count > 8)
                ListCache.Clear();
            ListCache[archivePath] = (stamp, entries);
        }
        return entries;
    }

    /// <summary>
    /// Runs 7z.exe and returns its output. "-p-" gives a wrong password instead of a prompt, and
    /// standard input is closed, so an encrypted archive fails at once instead of waiting.
    /// </summary>
    private static string RunSevenZip(IEnumerable<string> arguments, string? workingDirectory)
    {
        if (SevenZip.ConsolePath is not { } exe)
            throw new InvalidOperationException("7-Zip is not installed.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("7-Zip did not start.");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill();
            throw new IOException("7-Zip took too long.");
        }
        if (process.ExitCode != 0)
        {
            string all = error.Result + "\n" + output;
            string message = all.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "The archive is password protected (not supported)."
                : FirstErrorLine(error.Result, output);
            throw new IOException(message);
        }
        return output;
    }

    private static string FirstErrorLine(string error, string output) =>
        (error + "\n" + output).Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("Can not open", StringComparison.OrdinalIgnoreCase))
        ?? "7-Zip reported an error.";

    // ---------- Unpack ----------

    /// <summary>
    /// Unpacks items of one archive folder (inner paths) into a new temporary folder and
    /// returns the unpacked top-level paths (for opening, the clipboard or a drag).
    /// </summary>
    public static List<string> ExtractToTemp(string zipFile, IReadOnlyList<string> innerPaths)
    {
        string target = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        return IsReadOnlyName(zipFile)
            ? ExtractWithSevenZip(zipFile, innerPaths, target)
            : ExtractZip(zipFile, innerPaths, target);
    }

    /// <summary>
    /// 7z / RAR: "7z x" with a UTF-8 list file of the inner paths (a folder comes with its
    /// content). Full paths are kept below the temp folder; 7-Zip itself refuses "../" names.
    /// </summary>
    private static List<string> ExtractWithSevenZip(string archivePath, IReadOnlyList<string> innerPaths, string target)
    {
        var items = innerPaths.Select(p => p.Trim('/')).Where(p => p.Length > 0).ToList();
        string listFile = Path.Combine(TempRoot, $"list-{Path.GetFileName(target)}.txt");
        File.WriteAllLines(listFile, items.Select(i => i.Replace('/', '\\')), new UTF8Encoding(false));
        try
        {
            RunSevenZip(["x", "-y", "-scsUTF-8", "-sccUTF-8", "-p-", "-o" + target, archivePath, "@" + listFile], workingDirectory: null);
        }
        finally
        {
            File.Delete(listFile);
        }

        var result = new List<string>();
        foreach (string item in items)
        {
            string unpacked = Path.Combine(target, item.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(unpacked) || Directory.Exists(unpacked))
                result.Add(unpacked);
        }
        return result;
    }

    private static List<string> ExtractZip(string zipFile, IReadOnlyList<string> innerPaths, string target)
    {
        string targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipFile);
        var result = new List<string>();
        foreach (string inner in innerPaths)
        {
            string item = inner.Trim('/');
            string top = item[(item.LastIndexOf('/') + 1)..];
            string baseInZip = item.Contains('/') ? item[..item.LastIndexOf('/')] + "/" : "";

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                bool match = name.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                             name.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase);
                if (!match)
                    continue;

                string relative = name[baseInZip.Length..];
                string dest = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
                // Never write outside the temp folder ("zip slip": entries like "../../x").
                if (!dest.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            string topPath = Path.Combine(target, top);
            if (File.Exists(topPath) || Directory.Exists(topPath))
                result.Add(topPath);
        }
        return result;
    }

    // ---------- Write (ZIP only) ----------

    /// <summary>Adds files and folders (with their content) into a folder of the ZIP; same names are replaced.</summary>
    public static void Add(string zipFile, string innerFolder, IReadOnlyList<string> sources)
    {
        ThrowIfReadOnly(zipFile);
        string prefix = innerFolder.Length > 0 ? innerFolder.TrimEnd('/') + "/" : "";
        using var archive = ZipFile.Open(zipFile, ZipArchiveMode.Update);

        foreach (string source in sources)
        {
            string name = Path.GetFileName(source.TrimEnd('\\'));
            if (File.Exists(source))
            {
                AddFile(archive, source, prefix + name);
            }
            else if (Directory.Exists(source))
            {
                string root = prefix + name + "/";
                Remove(archive, prefix + name);
                archive.CreateEntry(root);
                foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                    archive.CreateEntry(root + Path.GetRelativePath(source, dir).Replace('\\', '/') + "/");
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    AddFile(archive, file, root + Path.GetRelativePath(source, file).Replace('\\', '/'));
            }
        }
    }

    /// <summary>Removes items (files, or folders with all their content) from the ZIP.</summary>
    public static void Delete(string zipFile, IReadOnlyList<string> innerPaths)
    {
        ThrowIfReadOnly(zipFile);
        using var archive = ZipFile.Open(zipFile, ZipArchiveMode.Update);
        foreach (string inner in innerPaths)
            Remove(archive, inner.Trim('/'));
    }

    private static void ThrowIfReadOnly(string archivePath)
    {
        if (IsReadOnlyName(archivePath))
            throw new IOException($"{Path.GetExtension(archivePath).TrimStart('.').ToUpperInvariant()} archives can only be read here (look inside, open, copy out).");
    }

    // ---------- Temp folder ----------

    /// <summary>At exit: deletes this copy's temp folder.</summary>
    public static void CleanOwnTemp() => TryDelete(TempRoot);

    /// <summary>At start: deletes temp folders of copies that no longer run (after a crash).</summary>
    public static void CleanStaleTemp()
    {
        if (!Directory.Exists(TempBase))
            return;
        foreach (string dir in Directory.EnumerateDirectories(TempBase))
        {
            if (int.TryParse(Path.GetFileName(dir), out int pid) && !IsRunning(pid))
                TryDelete(dir);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file may still be open in another program; try again next time.
        }
    }

    private static void AddFile(ZipArchive archive, string file, string entryName)
    {
        archive.GetEntry(entryName)?.Delete();
        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
    }

    private static void Remove(ZipArchive archive, string item)
    {
        foreach (var entry in archive.Entries.ToList())
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase))
                entry.Delete();
        }
    }
}
