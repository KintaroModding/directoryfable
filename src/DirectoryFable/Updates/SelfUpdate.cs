using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace DirectoryFable.Updates;

/// <summary>
/// Downloads a release's zip and puts its exe in the place of the running one. Windows refuses to delete or
/// overwrite a running exe, but lets it be renamed: the running exe becomes "DirectoryFable.exe.old", the new
/// exe takes its name, and the next start deletes the old one. Settings and data stay as they are.
/// </summary>
internal static class SelfUpdate
{
    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    /// <summary>A download that receives no data for this long stops.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads the asset into <paramref name="folder"/> and returns the file. A file whose size or SHA-256
    /// differs from the release's is deleted and refused.
    /// </summary>
    /// <exception cref="UpdateException">The download failed or the file is not the expected one.</exception>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseAsset asset, string folder, IProgress<double>? progress,
        CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, asset.Name);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            stall.CancelAfter(StallTimeout);
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            long? total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var output = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1, (double)done / total.Value));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            TryDelete(target);
            throw new UpdateException($"The download stopped: no data arrived for {StallTimeout.TotalSeconds:0} seconds. Check the connection, then try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            throw new UpdateException($"The download failed: {ex.Message}");
        }

        long size = new FileInfo(target).Length;
        if (asset.Size > 0 && size != asset.Size)
        {
            TryDelete(target);
            throw new UpdateException($"The download has {size:N0} bytes instead of {asset.Size:N0}, so it was not installed. Try again later.");
        }
        if (asset.Sha256 is { } expected && !string.Equals(Sha256(target), expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            throw new UpdateException("The download is not the file of the release (its checksum differs), so it was not installed.");
        }
        return target;
    }

    /// <summary>
    /// Takes the one exe out of the release zip into <paramref name="folder"/> and checks that it is
    /// <paramref name="version"/>. Only that entry is read; its path inside the zip is not used.
    /// </summary>
    /// <exception cref="UpdateException">The zip holds no exe, several, or one of another version.</exception>
    public static string ExtractExe(string zip, string folder, string version)
    {
        string exe = Path.Combine(folder, "DirectoryFable.exe");
        try
        {
            Directory.CreateDirectory(folder);
            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count != 1)
                throw new UpdateException("The release zip does not hold exactly one program file, so it was not installed.");
            entries[0].ExtractToFile(exe, overwrite: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The release zip could not be opened: {ex.Message}");
        }
        // The product version carries the build's commit after a "+".
        string? found = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0].Trim();
        if (!string.Equals(found, version, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"The release zip holds version {found ?? "unknown"} instead of {version}, so it was not installed.");
        return exe;
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in the place of <paramref name="currentExe"/>, which is running. When a
    /// step fails, everything is put back.
    /// </summary>
    /// <exception cref="UpdateException">The exe could not be replaced.</exception>
    public static void Replace(string currentExe, string newExe)
    {
        string old = currentExe + OldSuffix;
        if (File.Exists(old))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by a copy that has not ended yet: take another name.
                old = $"{currentExe}.{Guid.NewGuid():N}{OldSuffix}";
            }
        }

        // The new exe goes next to the current one first, so the swap is two renames on one drive.
        string staged = currentExe + NewSuffix;
        try
        {
            File.Copy(newExe, staged, overwrite: true);
            File.Move(currentExe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            throw new UpdateException($"DirectoryFable could not replace its own file ({ex.Message}). Download the update from the release page instead.");
        }
        try
        {
            File.Move(staged, currentExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(old, currentExe);
            }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; the message says where the update is.
            }
            TryDelete(staged);
            throw new UpdateException($"DirectoryFable could not replace its own file ({ex.Message}). Download the update from the release page instead.");
        }
    }

    /// <summary>
    /// Deletes what an update left next to the exe: the old version and an unused new one. Only the names
    /// <see cref="Replace"/> gives are touched.
    /// </summary>
    /// <returns>Whether nothing is left (the old version stays while the copy it ran is still ending).</returns>
    public static bool CleanUp(string currentExe)
    {
        string? folder = Path.GetDirectoryName(currentExe);
        if (folder is null || !Directory.Exists(folder))
            return true;
        string name = Path.GetFileName(currentExe);
        bool clean = true;
        var leftovers = Directory.EnumerateFiles(folder, name + "*" + OldSuffix)
            .Where(file => IsOldVersion(Path.GetFileName(file), name))
            .Append(currentExe + NewSuffix)
            .Where(File.Exists);
        foreach (string leftover in leftovers)
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                clean = false;
            }
        }
        return clean;
    }

    /// <summary>"&lt;exe&gt;.old", or "&lt;exe&gt;.&lt;32 hex digits&gt;.old" when that name was taken.</summary>
    private static bool IsOldVersion(string fileName, string exeName)
    {
        if (fileName.Equals(exeName + OldSuffix, StringComparison.OrdinalIgnoreCase))
            return true;
        int middle = fileName.Length - exeName.Length - 1 - OldSuffix.Length;
        return middle == 32
            && fileName.StartsWith(exeName + ".", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase)
            && fileName.Substring(exeName.Length + 1, middle).All(Uri.IsHexDigit);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
