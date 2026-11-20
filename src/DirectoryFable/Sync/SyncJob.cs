using System.IO;
using System.Text.Json;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Sync;

/// <summary>
/// One FlashSync job: folders to back up, what to leave out, and the folder that gets the copy. Each source folder
/// goes into its own folder of the destination, named after it ("D:\Games\Saves" to "destination\Saves").
/// </summary>
public sealed class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>The folder that gets the copy (one per job; a wipe empties exactly this folder).</summary>
    public string Destination { get; set; } = "";

    /// <summary>Delete everything in the destination folder before the copy. Off: new and changed files are copied, nothing is deleted.</summary>
    public bool Wipe { get; set; }

    /// <summary>The folders to copy (full paths).</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>Folders below a source that are left out (full paths).</summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>Names or patterns of files and folders that are left out everywhere ("Thumbs.db", "*.tmp", "caches").</summary>
    public List<string> ExcludedNames { get; set; } = [];

    /// <summary>When the job ran last (local time); null = never.</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>The last run in one line ("Done: 1.234 files ...").</summary>
    public string LastResult { get; set; } = "";

    /// <summary>The log file of the last run.</summary>
    public string LastLog { get; set; } = "";

    /// <summary>How the last run ended (a <see cref="SyncStatus"/> name); empty = not known.</summary>
    public string LastStatus { get; set; } = "";

    /// <summary>The last run's numbers: files and bytes copied, its time, its problems (null = not known).</summary>
    public long? LastFiles { get; set; }
    public long? LastBytes { get; set; }
    public double? LastSeconds { get; set; }
    public int? LastErrors { get; set; }

    /// <summary>
    /// The serial number of the drive the destination was on at the last run. A wipe on another drive (a drive
    /// letter that now belongs to another disk) gets a clear warning first.
    /// </summary>
    public string DestinationVolume { get; set; } = "";

    public SyncJob Clone() => new()
    {
        Id = Id,
        Name = Name,
        Destination = Destination,
        Wipe = Wipe,
        Sources = [.. Sources],
        ExcludedFolders = [.. ExcludedFolders],
        ExcludedNames = [.. ExcludedNames],
        LastRun = LastRun,
        LastResult = LastResult,
        LastLog = LastLog,
        LastStatus = LastStatus,
        LastFiles = LastFiles,
        LastBytes = LastBytes,
        LastSeconds = LastSeconds,
        LastErrors = LastErrors,
        DestinationVolume = DestinationVolume,
    };

    /// <summary>
    /// The name a source folder gets in the destination: its own name; a whole drive gets its letter ("X"), a
    /// network share its share name.
    /// </summary>
    public static string TargetName(string source)
    {
        string trimmed = source.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed[..1].ToUpperInvariant();
        // The last part of the path (also for "\\server\share", where Path.GetFileName finds no name).
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    /// <summary>Where a source folder is copied to.</summary>
    public string TargetOf(string source) => Path.Combine(Destination, TargetName(source));

    /// <summary>The excluded folders that lie inside one source.</summary>
    public IEnumerable<string> ExcludedIn(string source) =>
        ExcludedFolders.Where(folder => SyncSafety.IsSameOrInside(folder, source));
}

/// <summary>FlashSync's jobs on disk: one file in the data folder, and the folder for the run logs.</summary>
public static class SyncJobStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>How many run logs are kept; older ones are deleted when a run starts.</summary>
    private const int KeptLogs = 60;

    public static string Folder => Path.Combine(AppSettings.DataFolder, "flashsync");

    private static string FilePath => Path.Combine(Folder, "jobs.json");

    public static string LogFolder => Path.Combine(Folder, "logs");

    /// <summary>The saved jobs; an empty list when there is no file yet or it cannot be read.</summary>
    public static List<SyncJob> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            return JsonSerializer.Deserialize<List<SyncJob>>(File.ReadAllText(FilePath), Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the jobs (to a new file first, so a crash never leaves half a file).</summary>
    public static void Save(IEnumerable<SyncJob> jobs)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(jobs.ToList(), Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>A new log file path for a run of a job ("2026-11-19_23-45-10 name.log"); old logs go.</summary>
    public static string NewLogPath(SyncJob job)
    {
        Directory.CreateDirectory(LogFolder);
        try
        {
            foreach (var old in new DirectoryInfo(LogFolder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptLogs - 1))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old logs that cannot be deleted stay.
        }
        string name = string.Concat(job.Name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)).Trim();
        if (name.Length > 60)
            name = name[..60];
        return Path.Combine(LogFolder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss} {name}.log");
    }
}
