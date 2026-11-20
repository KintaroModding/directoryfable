using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DirectoryFable.Sync;

/// <summary>
/// One run of Windows' robocopy for a FlashSync source folder: builds its arguments, starts it without a window and
/// follows the log file it writes. That file is real Unicode (the text robocopy sends to a pipe is not), so file
/// names with any characters arrive exact.
/// </summary>
internal static partial class RobocopyRun
{
    /// <summary>What a run did. <see cref="Files"/> and <see cref="Bytes"/> count the listed files; the failed ones are part of them.</summary>
    public sealed record Outcome(int ExitCode, long Files, long Bytes, long FailedFiles, long FailedBytes, int Errors, bool Cancelled)
    {
        /// <summary>robocopy could not work at all (a bad path, no access to the source or the destination).</summary>
        public bool Fatal => (ExitCode & 16) != 0;
    }

    /// <summary>Always the one in Windows' own folder, never one found through the search path.</summary>
    public static string ExePath => Path.Combine(Environment.SystemDirectory, "robocopy.exe");

    /// <summary>
    /// The arguments for one source folder. All subfolders, also empty ones; data, attributes and times; no retries
    /// on a file in use; links to other folders are not followed; times compared with two seconds of slack (drives
    /// that store coarse times); files that are only in the destination are neither listed nor touched.
    /// <paramref name="listOnly"/>: nothing is copied, the files that would be copied are only listed.
    /// </summary>
    public static string Arguments(string source, string target, IEnumerable<string> excludedFolders,
        IEnumerable<string> excludedNames, bool listOnly, string logFile)
    {
        var text = new StringBuilder();
        text.Append(Quote(source)).Append(' ').Append(Quote(target));
        text.Append(" /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /XJ /FFT /XX /NP /NDL /NJH /NJS /BYTES /FP /NC");
        text.Append(listOnly ? " /L" : " /MT:8");
        text.Append(" /UNILOG:").Append(Quote(logFile));

        // Only real name patterns go to the tool: anything else could be read as one of its options.
        var names = excludedNames.Select(n => n.Trim()).Where(SyncSafety.IsNamePattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0)
        {
            text.Append(" /XF");
            foreach (string name in names)
                text.Append(' ').Append(Quote(name));
        }
        var folders = excludedFolders.Where(SyncSafety.IsFullPath).Select(SyncSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0 || folders.Count > 0)
        {
            text.Append(" /XD");
            foreach (string name in names)
                text.Append(' ').Append(Quote(name));
            foreach (string folder in folders)
                text.Append(' ').Append(Quote(folder));
        }
        return text.ToString();
    }

    /// <summary>In quotes; a "\" at the end is doubled, or it would hide the closing quote ("X:\" is "X:\\").</summary>
    private static string Quote(string value)
    {
        value = value.Replace("\"", "", StringComparison.Ordinal);
        int slashes = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', slashes) + "\"";
    }

    /// <summary>
    /// Runs robocopy and reads its log while it works. <paramref name="onFile"/> gets each listed file (size, path),
    /// <paramref name="onError"/> each problem in one line. A cancel ends robocopy at once.
    /// </summary>
    public static async Task<Outcome> RunAsync(string arguments, string logFile, Action<long, string>? onFile,
        Action<string>? onError, CancellationToken token)
    {
        if (File.Exists(logFile))
            File.Delete(logFile);
        var start = new ProcessStartInfo(ExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, _) => { }; // Read and dropped: a full pipe would stop robocopy.
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var reader = new LogReader(logFile, onFile, onError);
        bool cancelled = false;
        try
        {
            while (!process.HasExited)
            {
                reader.Read();
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // It ended by itself in this moment.
                    }
                    break;
                }
                await Task.Delay(150, CancellationToken.None);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            reader.Read();
            reader.Finish();
        }
        finally
        {
            reader.Dispose();
        }
        return new Outcome(cancelled ? 16 : process.ExitCode, reader.Files, reader.Bytes, reader.FailedFiles, reader.FailedBytes,
            reader.Errors, cancelled);
    }

    /// <summary>A listed file: tabs and spaces, the size in bytes, a tab, the full path.</summary>
    [GeneratedRegex(@"^\t[ \t]*(\d+)\t(.+)$")]
    private static partial Regex FileLine();

    /// <summary>The head of a problem: "... ERROR 32 (0x00000020) Copying File X:\..." (the words depend on Windows' language).</summary>
    [GeneratedRegex(@"\s(\d+) \(0x[0-9A-Fa-f]{8}\)\s+(.*)$")]
    private static partial Regex ErrorLine();

    /// <summary>The path at the end of a problem's head.</summary>
    [GeneratedRegex(@"((?:[A-Za-z]:\\|\\\\).*)$")]
    private static partial Regex PathPart();

    /// <summary>Follows robocopy's Unicode log file: reads what was added, line by line.</summary>
    private sealed class LogReader(string path, Action<long, string>? onFile, Action<string>? onError) : IDisposable
    {
        private FileStream? _stream;
        private readonly Decoder _decoder = Encoding.Unicode.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly byte[] _bytes = new byte[64 * 1024];
        private readonly char[] _chars = new char[64 * 1024];

        // The newest listed files with their sizes: a problem line names the file, and its size is then known.
        private readonly Dictionary<string, long> _recent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _recentOrder = new();

        private string? _errorHead;   // A problem's head; the next text line is its reason.
        private string? _errorPath;

        public long Files, Bytes, FailedFiles, FailedBytes;
        public int Errors;

        public void Read()
        {
            if (_stream is null)
            {
                if (!File.Exists(path))
                    return;
                try
                {
                    _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    return; // Not readable in this moment: the next round reads it.
                }
            }
            int read;
            while ((read = _stream.Read(_bytes, 0, _bytes.Length)) > 0)
            {
                int count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                for (int i = 0; i < count; i++)
                {
                    char c = _chars[i];
                    if (c == '\n')
                    {
                        Take(_line.ToString());
                        _line.Clear();
                    }
                    else if (c != '\r' && c != '\uFEFF')
                    {
                        _line.Append(c);
                    }
                }
            }
        }

        /// <summary>The end of the log: a last line without a line end, and a problem that got no reason line.</summary>
        public void Finish()
        {
            if (_line.Length > 0)
            {
                Take(_line.ToString());
                _line.Clear();
            }
            FlushError(null);
        }

        private void Take(string line)
        {
            if (FileLine().Match(line) is { Success: true } file && long.TryParse(file.Groups[1].Value, out long size))
            {
                FlushError(null);
                string name = file.Groups[2].Value;
                Files++;
                Bytes += size;
                _recent[name] = size;
                _recentOrder.Enqueue(name);
                if (_recentOrder.Count > 512)
                    _recent.Remove(_recentOrder.Dequeue());
                onFile?.Invoke(size, name);
                return;
            }
            if (ErrorLine().Match(line) is { Success: true } error)
            {
                FlushError(null);
                _errorHead = $"{error.Groups[2].Value.Trim()} (error {error.Groups[1].Value})";
                _errorPath = PathPart().Match(error.Groups[2].Value) is { Success: true } part ? part.Groups[1].Value.Trim() : null;
                return;
            }
            string text = line.Trim();
            if (text.Length == 0)
                return;
            // The line after a problem's head is its reason. Other text (the tool's "retry limit" line) says nothing new.
            if (_errorHead is not null)
                FlushError(text);
        }

        private void FlushError(string? reason)
        {
            if (_errorHead is null)
                return;
            Errors++;
            if (_errorPath is not null && _recent.Remove(_errorPath, out long size))
            {
                FailedFiles++;
                FailedBytes += size;
            }
            // "X:\folder\file - reason (error 32)" reads better than the tool's own word order.
            string head = _errorHead;
            if (_errorPath is not null)
                head = _errorPath + head[head.LastIndexOf(" (error ", StringComparison.Ordinal)..];
            onError?.Invoke(reason is null ? head : $"{head}: {reason}");
            _errorHead = _errorPath = null;
        }

        public void Dispose() => _stream?.Dispose();
    }
}
