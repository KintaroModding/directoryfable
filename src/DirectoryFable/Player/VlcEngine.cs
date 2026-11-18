using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Player;

/// <summary>
/// VLC's engine (libVLC, from the VideoLAN project) for OpusHC. It is not shipped with DirectoryFable:
/// on first use it is downloaded once from VideoLAN's official server, checked against VideoLAN's SHA-256
/// checksum, and only the engine files are unpacked into DirectoryFable's own data folder
/// ("data\vlc\3.0.24": libvlc.dll, libvlccore.dll, the plugins folder and VLC's license). Nothing is installed:
/// no registry entries, no Start menu, no files anywhere else. Deleting the folder (or "Remove" in the settings)
/// removes the engine completely.
/// </summary>
public static class VlcEngine
{
    public const string Version = "3.0.24";

    /// <summary>The size of VideoLAN's zip file (bytes), for the progress before the server tells it.</summary>
    public const long DownloadSize = 82_765_861;

    /// <summary>VideoLAN's published SHA-256 of vlc-3.0.24-win64.zip (the .sha256 file next to it).</summary>
    private const string Sha256 = "fcf30850371ad10c9373cc4f0f4501e7dee49e3e9ae9f20c72fb2661a1ca6323";

    public static string DownloadUrl => $"https://download.videolan.org/pub/videolan/vlc/{Version}/win64/vlc-{Version}-win64.zip";

    /// <summary>"…\data\vlc": everything of the engine is in here.</summary>
    public static string BaseFolder => Path.Combine(AppSettings.DataFolder, "vlc");

    /// <summary>"…\data\vlc\3.0.24": the engine files.</summary>
    public static string Folder => Path.Combine(BaseFolder, Version);

    private static string RemoveMark => Path.Combine(AppSettings.DataFolder, "vlc-remove.txt");

    public static bool IsInstalled =>
        !File.Exists(RemoveMark) && File.Exists(Path.Combine(Folder, "libvlc.dll")) &&
        File.Exists(Path.Combine(Folder, "libvlccore.dll")) && Directory.Exists(Path.Combine(Folder, "plugins"));

    /// <summary>Bytes on disk of the engine folder (0 when there is none).</summary>
    public static long InstalledSize()
    {
        try
        {
            return Directory.Exists(BaseFolder)
                ? new DirectoryInfo(BaseFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Progress of <see cref="InstallAsync"/>: bytes so far, bytes in all, and what happens ("Downloading", ...).</summary>
    public readonly record struct Progress(long Done, long Total, string Stage);

    /// <summary>Downloads, checks and unpacks the engine. A failed or cancelled try leaves no files behind.</summary>
    public static async Task InstallAsync(IProgress<Progress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(BaseFolder);
        File.Delete(RemoveMark);
        string part = Path.Combine(BaseFolder, $"vlc-{Version}-win64.zip.part");
        string unpacked = Folder + ".new";
        try
        {
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"DirectoryFable/{Updates.UpdateSource.CurrentVersion}");
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? DownloadSize;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(part);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    progress?.Report(new Progress(done, total, "Downloading"));
                }
            }

            progress?.Report(new Progress(0, 0, "Checking"));
            string hash;
            await using (var file = File.OpenRead(part))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
            if (hash != Sha256)
                throw new InvalidDataException("The download does not match VideoLAN's checksum, so it was not used (and was deleted). Try again later.");

            progress?.Report(new Progress(0, 0, "Unpacking"));
            await Task.Run(() => Unpack(part, unpacked, token), token);
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
            Directory.Move(unpacked, Folder);
            File.WriteAllText(Path.Combine(BaseFolder, "README.txt"),
                $"VLC's video engine (libVLC {Version}, VideoLAN, https://www.videolan.org) for DirectoryFable's OpusHC.{Environment.NewLine}" +
                $"Downloaded from {DownloadUrl} and checked against VideoLAN's SHA-256.{Environment.NewLine}" +
                $"Nothing was installed in Windows. To remove the engine, delete this folder (or use \"Remove\" in OpusHC's settings).{Environment.NewLine}");
        }
        finally
        {
            TryDelete(part);
            if (Directory.Exists(unpacked))
            {
                try
                {
                    Directory.Delete(unpacked, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next try (it deletes it first).
                }
            }
        }
    }

    /// <summary>Only the engine: libvlc.dll, libvlccore.dll, plugins\..., COPYING.txt (VLC.exe, skins, lua, locale stay out).</summary>
    private static void Unpack(string zipFile, string target, CancellationToken token)
    {
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0)
                continue; // A folder entry.
            // The zip holds everything in "vlc-3.0.24/".
            string name = entry.FullName.Replace('\\', '/');
            int slash = name.IndexOf('/');
            string relative = slash >= 0 ? name[(slash + 1)..] : name;
            bool wanted = relative is "libvlc.dll" or "libvlccore.dll" or "COPYING.txt" ||
                          relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase);
            if (!wanted)
                continue;
            string path = Path.GetFullPath(Path.Combine(target, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue; // A name that points outside the folder: never.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    /// <summary>
    /// Removes the engine folder. While the engine is loaded (a video was played since the start) Windows keeps its
    /// files open: then it is marked and removed at the next start. Returns true when it is gone now.
    /// </summary>
    public static bool Remove()
    {
        try
        {
            if (Directory.Exists(BaseFolder))
                Directory.Delete(BaseFolder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(RemoveMark, "OpusHC: remove data\\vlc at the next start." + Environment.NewLine);
            return false;
        }
    }

    /// <summary>At the start: a removal that had to wait for the files to be free.</summary>
    public static void CleanUpPending()
    {
        if (!File.Exists(RemoveMark))
            return;
        try
        {
            if (Directory.Exists(BaseFolder))
                Directory.Delete(BaseFolder, recursive: true);
            File.Delete(RemoveMark);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use (a second copy of DirectoryFable plays a video): next start.
        }
    }

    private static bool _initialized;

    /// <summary>Points LibVLCSharp at the engine folder (once, before the first LibVLC object).</summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        LibVLCSharp.Shared.Core.Initialize(Folder);
        _initialized = true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left; the next try overwrites it.
        }
    }
}
