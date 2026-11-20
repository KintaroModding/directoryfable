using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Sync;

public enum SyncPhase { Starting, Wiping, Counting, Copying }

/// <summary>Where a running FlashSync job is, for the progress display.</summary>
public sealed record SyncProgress(
    string Job, int JobIndex, int JobCount, SyncPhase Phase, string Source, int SourceIndex, int SourceCount,
    long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, long Deleted, string Current, int Errors,
    double BytesPerSecond, TimeSpan Elapsed);

public enum SyncStatus { Done, DoneWithErrors, Cancelled, Failed }

/// <summary>How a run of one job ended.</summary>
public sealed record SyncResult(SyncJob Job, SyncStatus Status, long Files, long Bytes, int Errors, TimeSpan Duration, string LogPath, string Summary);

/// <summary>
/// Runs one FlashSync job: the wipe (when the job has it), a count of what is to copy (for the progress), then
/// robocopy for each source folder. Everything goes into the run's log file; problems also go to <see cref="Line"/>.
/// The question before a wipe is asked by the view, before this starts.
/// </summary>
public sealed class SyncRunner
{
    /// <summary>A line for the live log (a step, a problem). Raised on a worker thread.</summary>
    public event Action<string>? Line;

    private StreamWriter? _log;
    private int _errors;

    public async Task<SyncResult> RunAsync(SyncJob job, int jobIndex, int jobCount, IProgress<SyncProgress> progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        string logPath = SyncJobStore.NewLogPath(job);
        _errors = 0;
        long files = 0, bytes = 0, failedFiles = 0, failedBytes = 0;
        SyncStatus status;
        string? failure = null;
        string tempLog = Path.Combine(SyncJobStore.LogFolder, $"robocopy-{Environment.ProcessId}.tmp");

        var state = new State(job.Name, jobIndex, jobCount, watch);
        void Report() => progress.Report(state.Snapshot(_errors));

        try
        {
            _log = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Write($"FlashSync job \"{job.Name}\"");
            Write($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Write($"Destination: {job.Destination}{(job.Wipe ? "   (wipe before the copy)" : "")}");
            foreach (string source in job.Sources)
                Write($"Source: {source}  ->  {job.TargetOf(source)}");
            if (job.ExcludedFolders.Count > 0)
                Write("Excluded folders: " + string.Join(" | ", job.ExcludedFolders));
            if (job.ExcludedNames.Count > 0)
                Write("Excluded names: " + string.Join(" | ", job.ExcludedNames));
            Write("");

            if (SyncSafety.Problem(job) is { } problem)
                throw new InvalidOperationException(problem);
            Report();

            // The sources that are there. A wipe job needs all of them: the old copy is never deleted while a
            // source is missing (a drive that is not connected would leave no copy at all).
            var sources = new List<string>();
            var missing = new List<string>();
            foreach (string source in job.Sources)
            {
                if (await Task.Run(() => Directory.Exists(source), token))
                    sources.Add(source);
                else
                    missing.Add(source);
            }
            if (job.Wipe && missing.Count > 0)
                throw new InvalidOperationException($"The source folder {missing[0]} is not there. Nothing was deleted and nothing was copied: " +
                                                    "a job with the wipe option runs only when all its folders are there.");
            await Task.Run(() => Directory.CreateDirectory(job.Destination), token);

            // 1. Wipe.
            if (job.Wipe)
            {
                state.Phase = SyncPhase.Wiping;
                Say($"Wipe: deleting everything in {job.Destination}");
                var wiped = await Task.Run(() => SyncWipe.Run(job.Destination, (count, folder) =>
                {
                    state.Deleted = count;
                    state.Current = folder;
                    Report();
                }, token), token);
                state.Deleted = wiped.Deleted;
                Say($"Wipe: {Items(wiped.Deleted)} deleted" + (wiped.FailedCount > 0 ? $", {wiped.FailedCount:N0} could not be deleted" : ""));
                foreach (string failed in wiped.Failed)
                    Problem("Not deleted: " + failed);
                if (wiped.FailedCount > wiped.Failed.Count)
                    Problem($"Not deleted: {wiped.FailedCount - wiped.Failed.Count:N0} more");
            }
            await Task.Run(() => SyncSafety.WriteMarker(job), token);

            // 2. Without the wipe option, the folders that are there are copied; a missing one is a problem of the run.
            foreach (string source in missing)
                Problem($"Source folder does not exist: {source}");
            state.SourceCount = sources.Count;

            // 3. Count what is to copy.
            state.Phase = SyncPhase.Counting;
            state.Current = "";
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                state.Source = source;
                Report();
                string arguments = RobocopyRun.Arguments(source, job.TargetOf(source), job.ExcludedIn(source), job.ExcludedNames, listOnly: true, tempLog);
                var listed = await RobocopyRun.RunAsync(arguments, tempLog, (size, _) =>
                {
                    state.FilesTotal++;
                    state.BytesTotal += size;
                    state.Touch(Report);
                }, null, token);
                if (listed.Cancelled)
                    throw new OperationCanceledException(token);
            }
            Say($"To copy: {Files(state.FilesTotal)}, {DisplayFormat.Size(state.BytesTotal)}");
            // Enough room? After a wipe the destination is empty, so the count is what the copy needs: stop before
            // hours of copying end with a full drive. Without a wipe, changed files take the place of old ones, so
            // the count is only an upper limit: a note, and the copy runs.
            if (FreeSpace(job.Destination) is { } free && state.BytesTotal > free)
            {
                string need = $"The files to copy need {DisplayFormat.Size(state.BytesTotal)}; {DisplayFormat.Size(free)} are free on the destination's drive.";
                if (job.Wipe)
                    throw new InvalidOperationException(need + " Nothing was copied.");
                Say("Note: " + need + " The copy can stop with a full drive.");
            }

            // 4. Copy.
            state.Phase = SyncPhase.Copying;
            using var speed = new SpeedMeter(job.Destination, value => state.BytesPerSecond = value);
            for (int i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string source = sources[i], target = job.TargetOf(source);
                state.Source = source;
                state.SourceIndex = i + 1;
                Report();
                string arguments = RobocopyRun.Arguments(source, target, job.ExcludedIn(source), job.ExcludedNames, listOnly: false, tempLog);
                Write($"robocopy {arguments}");
                var copied = await RobocopyRun.RunAsync(arguments, tempLog, (size, path) =>
                {
                    state.FilesDone++;
                    state.BytesDone += size;
                    state.Current = path;
                    state.Touch(Report);
                }, Problem, token);
                files += copied.Files;
                bytes += copied.Bytes;
                failedFiles += copied.FailedFiles;
                failedBytes += copied.FailedBytes;
                if (copied.Cancelled)
                    throw new OperationCanceledException(token);
                if (copied.Fatal)
                    Problem($"robocopy could not work on {source} (exit code {copied.ExitCode}).");
                // A whole drive as the source hands its "hidden, system" marks to its folder in the destination.
                await Task.Run(() => ShowFolder(target), CancellationToken.None);
                Say($"{source}: {Files(copied.Files - copied.FailedFiles)}, {DisplayFormat.Size(copied.Bytes - copied.FailedBytes)}" +
                    (copied.Errors > 0 ? $", {Problems(copied.Errors)}" : ""));
            }
            state.Current = "";
            Report();
            status = _errors > 0 ? SyncStatus.DoneWithErrors : SyncStatus.Done;
        }
        catch (OperationCanceledException)
        {
            status = SyncStatus.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
        {
            status = SyncStatus.Failed;
            failure = ex.Message;
            Problem(ex.Message);
        }

        try
        {
            if (File.Exists(tempLog))
                File.Delete(tempLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        watch.Stop();
        long copiedFiles = Math.Max(0, files - failedFiles), copiedBytes = Math.Max(0, bytes - failedBytes);
        string summary = status switch
        {
            SyncStatus.Done => $"Done: {Files(copiedFiles)}, {DisplayFormat.Size(copiedBytes)}, {Duration(watch.Elapsed)}",
            SyncStatus.DoneWithErrors => $"Done with {Problems(_errors)}: {Files(copiedFiles)}, {DisplayFormat.Size(copiedBytes)}, {Duration(watch.Elapsed)}",
            SyncStatus.Cancelled => $"Stopped after {Files(copiedFiles)}, {DisplayFormat.Size(copiedBytes)}, {Duration(watch.Elapsed)}",
            _ => $"Failed: {failure}",
        };
        Say(summary);
        Write($"Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _log?.Dispose();
        _log = null;

        job.LastRun = DateTime.Now;
        job.LastResult = summary;
        job.LastLog = logPath;
        job.LastStatus = status.ToString();
        job.LastFiles = copiedFiles;
        job.LastBytes = copiedBytes;
        job.LastSeconds = watch.Elapsed.TotalSeconds;
        job.LastErrors = _errors;
        if (status != SyncStatus.Failed && SyncSafety.Volume(job.Destination) is { } volume)
            job.DestinationVolume = volume.Serial;
        return new SyncResult(job, status, copiedFiles, copiedBytes, _errors, watch.Elapsed, logPath, summary);
    }

    /// <summary>"12 min 3 s", "45 s", "2 h 5 min".</summary>
    public static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{time.Minutes} min {time.Seconds} s"
        : $"{Math.Max(1, (int)Math.Round(time.TotalSeconds))} s";

    /// <summary>"1 problem", "3 problems".</summary>
    public static string Problems(long count) => count == 1 ? "1 problem" : $"{count:N0} problems";

    /// <summary>"1 file", "3 files".</summary>
    public static string Files(long count) => count == 1 ? "1 file" : $"{count:N0} files";

    /// <summary>"1 file or folder", "3 files and folders".</summary>
    public static string Items(long count) => count == 1 ? "1 file or folder" : $"{count:N0} files and folders";

    /// <summary>The free bytes on the drive of a folder; null when they cannot be read.</summary>
    private static long? FreeSpace(string folder) =>
        GetDiskFreeSpaceEx(folder, out ulong free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string folder, out ulong freeForCaller, out ulong total, out ulong free);

    private void Write(string text)
    {
        try
        {
            _log?.WriteLine(text);
            _log?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The log's drive is full or gone: the copy still goes on.
        }
    }

    /// <summary>A step: to the log file and the live log.</summary>
    private void Say(string text)
    {
        Write($"{DateTime.Now:HH:mm:ss}  {text}");
        Line?.Invoke(text);
    }

    /// <summary>A problem: counted, to the log file and the live log.</summary>
    private void Problem(string text)
    {
        Interlocked.Increment(ref _errors);
        Write($"{DateTime.Now:HH:mm:ss}  PROBLEM  {text}");
        Line?.Invoke("Problem: " + text);
    }

    private static void ShowFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                info.Attributes &= ~(FileAttributes.Hidden | FileAttributes.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The numbers of the run so far (written by the worker, read for each progress report).</summary>
    private sealed class State(string job, int jobIndex, int jobCount, Stopwatch watch)
    {
        public SyncPhase Phase = SyncPhase.Starting;
        public string Source = "", Current = "";
        public int SourceIndex, SourceCount;
        public long FilesDone, FilesTotal, BytesDone, BytesTotal, Deleted;
        public double BytesPerSecond;
        private long _reported;

        /// <summary>Reports now and then (a run lists thousands of files in a second).</summary>
        public void Touch(Action report)
        {
            long now = Environment.TickCount64;
            if (now - _reported < 200)
                return;
            _reported = now;
            report();
        }

        public SyncProgress Snapshot(int errors) => new(job, jobIndex, jobCount, Phase, Source, SourceIndex, SourceCount,
            FilesDone, FilesTotal, BytesDone, BytesTotal, Deleted, Current, errors, BytesPerSecond, watch.Elapsed);
    }

    /// <summary>
    /// The write speed to the destination, from its drive's free space once a second (robocopy says nothing while it
    /// writes one large file). Smoothed, so one second of other disk work does not make the number jump.
    /// </summary>
    private sealed class SpeedMeter : IDisposable
    {
        private readonly Timer _timer;
        private readonly string _folder;
        private readonly Action<double> _report;
        private long _free = -1, _ticks;
        private double _smooth = -1;

        public SpeedMeter(string folder, Action<double> report)
        {
            _folder = folder;
            _report = report;
            _timer = new Timer(_ => Sample(), null, 0, 1000);
        }

        private void Sample()
        {
            if (!GetDiskFreeSpaceEx(_folder, out ulong free, out _, out _))
                return;
            long now = Environment.TickCount64;
            if (_free >= 0 && now > _ticks)
            {
                double speed = Math.Max(0, _free - (long)free) / ((now - _ticks) / 1000.0);
                _smooth = _smooth < 0 ? speed : 0.3 * speed + 0.7 * _smooth;
                _report(_smooth);
            }
            _free = (long)free;
            _ticks = now;
        }

        public void Dispose() => _timer.Dispose();
    }
}
