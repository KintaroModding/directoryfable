using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Sync;

namespace DirectoryFable.Views;

/// <summary>A folder in one of the editor's lists: its name first (a long path is cut at its end), then the whole path.</summary>
public sealed record SyncPathRow(string Path)
{
    public string Name => SyncJob.TargetName(Path);
}

/// <summary>A line of the run log.</summary>
public sealed record SyncLogLine(string Text, bool IsProblem);

/// <summary>
/// FlashSync, the backup view: jobs that copy ticked folders into a destination folder (Windows' robocopy does the
/// copy, see <see cref="SyncRunner"/>), with the option to empty that folder first. The jobs are in the data folder
/// ("flashsync\jobs.json"); each run writes a log file there.
/// </summary>
public partial class FlashSyncView : UserControl
{
    private readonly ObservableCollection<SyncJobRow> _rows = [];
    private readonly ObservableCollection<SyncLogLine> _log = [];
    private List<SyncJob> _jobs = [];
    private bool _loaded;

    private CancellationTokenSource? _run;
    private Task? _runTask;
    private string _runningJob = "";

    private SyncTreeEditor? _editor;
    private SyncJob? _editing;

    /// <summary>"Show destination": the explorer goes to this folder.</summary>
    public event Action<string>? ShowFolderRequested;

    /// <summary>"Log": Notepad# opens this file.</summary>
    public event Action<string>? OpenInNotepadRequested;

    /// <summary>A run started or ended (the window title follows).</summary>
    public event Action? ActiveChanged;

    public FlashSyncView()
    {
        InitializeComponent();
        JobList.ItemsSource = _rows;
        RunLog.ItemsSource = _log;
        UpdateButtons();
    }

    public bool IsRunning => _run is not null;

    public string TitleText => IsRunning && _runningJob.Length > 0 ? $"FlashSync: {_runningJob}" : "FlashSync";

    /// <summary>The chosen job's id (kept in the session).</summary>
    public string? SelectedJobId { get; set; }

    private SyncJob? Selected => (JobList.SelectedItem as SyncJobRow)?.Job;

    /// <summary>The view came to the front: the jobs are read the first time.</summary>
    public void OnShown()
    {
        if (!_loaded)
        {
            _loaded = true;
            _jobs = SyncJobStore.Load();
            FillRows(SelectedJobId);
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (EditorPage.Visibility == Visibility.Visible)
                NameBox.Focus();
            else
                JobList.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- The job list ----------

    private void FillRows(string? selectId)
    {
        _rows.Clear();
        foreach (var job in _jobs)
            _rows.Add(new SyncJobRow(job));
        NoJobsText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JobsHeader.Text = _rows.Count == 0 ? "Jobs" : $"Jobs ({_rows.Count})";
        JobList.SelectedItem = _rows.FirstOrDefault(r => r.Job.Id == selectId) ?? _rows.FirstOrDefault();
        ShowJob(Selected);
        UpdateButtons();
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedJobId = Selected?.Id ?? SelectedJobId;
        ShowJob(Selected);
        UpdateButtons();
    }

    private void JobList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null && !IsRunning && e.OriginalSource is DependencyObject source && FindParent<ListBoxItem>(source) is not null)
            Edit_Click(sender, e);
    }

    /// <summary>The chosen job's cards (their texts and colors come from its row).</summary>
    private void ShowJob(SyncJob? job)
    {
        JobPanel.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        JobPanel.DataContext = job is null ? null : new SyncJobRow(job);
    }

    private void UpdateButtons()
    {
        bool editing = EditorPage.Visibility == Visibility.Visible;
        bool idle = !editing && !IsRunning;
        var job = editing ? null : Selected;
        NewButton.IsEnabled = idle;
        EditButton.IsEnabled = DuplicateButton.IsEnabled = DeleteButton.IsEnabled = RunButton.IsEnabled = idle && job is not null;
        RunAllButton.IsEnabled = idle && _jobs.Count > 0;
        StopButton.IsEnabled = IsRunning;
        LogButton.IsEnabled = job is { LastLog.Length: > 0 };
        DestinationButton.IsEnabled = job is not null;
    }

    private Window? Owner => Window.GetWindow(this);

    private void SaveJobs()
    {
        try
        {
            SyncJobStore.Save(_jobs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Owner, $"The jobs could not be saved: {ex.Message}", "FlashSync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Bar ----------

    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(new SyncJob(), isNew: true);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } job && !IsRunning)
            OpenEditor(job.Clone(), isNew: false);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } job || IsRunning)
            return;
        // Each job has its own destination folder: the copy starts without one, and has not run.
        var copy = job.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = job.Name + " (copy)";
        copy.Destination = "";
        copy.LastRun = null;
        copy.LastResult = copy.LastLog = copy.DestinationVolume = copy.LastStatus = "";
        copy.LastFiles = copy.LastBytes = null;
        copy.LastSeconds = null;
        copy.LastErrors = null;
        OpenEditor(copy, isNew: true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } job || IsRunning)
            return;
        var answer = MessageDialog.Show(Owner, $"Delete the job \"{job.Name}\"?\n\nOnly the job goes. The files in {job.Destination} stay.",
            "FlashSync", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        _jobs.Remove(job);
        SaveJobs();
        FillRows(null);
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } job)
            _ = RunAsync([job]);
    }

    private void RunAll_Click(object sender, RoutedEventArgs e) => _ = RunAsync([.. _jobs]);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_run is { } run && !run.IsCancellationRequested)
        {
            run.Cancel();
            AddLog("Stopping...");
        }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { LastLog.Length: > 0 } job)
            return;
        if (File.Exists(job.LastLog))
            OpenInNotepadRequested?.Invoke(job.LastLog);
        else
            MessageDialog.Show(Owner, "The log file of the last run is no longer there (FlashSync keeps the newest logs only).", "FlashSync");
    }

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } job)
            return;
        if (Directory.Exists(job.Destination))
            ShowFolderRequested?.Invoke(job.Destination);
        else
            MessageDialog.Show(Owner, $"{job.Destination} is not there (the drive is not connected, or the job has not run yet).", "FlashSync");
    }

    // ---------- Run ----------

    private async Task RunAsync(IReadOnlyList<SyncJob> jobs)
    {
        if (IsRunning || jobs.Count == 0 || EditorPage.Visibility == Visibility.Visible)
            return;
        foreach (var job in jobs)
        {
            string? problem = SyncSafety.Problem(job);
            string? root = problem is null ? Path.GetPathRoot(job.Destination) : null;
            if (problem is null && (string.IsNullOrEmpty(root) || !await Task.Run(() => Directory.Exists(root))))
                problem = $"{root} is not there. Connect the drive and run the job again.";
            // A wipe never runs while a source is missing: the old copy would go, and nothing would take its place.
            if (problem is null && job.Wipe && await Task.Run(() => job.Sources.FirstOrDefault(s => !Directory.Exists(s))) is { } missing)
                problem = $"The source folder {missing} is not there. A job with the wipe option runs only when all its folders are there.";
            if (problem is not null)
            {
                MessageDialog.Show(Owner, $"The job \"{job.Name}\" cannot run.\n\n{problem}", "FlashSync", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // A wipe deletes a whole folder: always asked, with what is in the folder now and what looks different.
        var wipes = jobs.Where(j => j.Wipe).ToList();
        if (wipes.Count > 0)
        {
            string question = await Task.Run(() => WipeQuestion(wipes));
            if (IsRunning)
                return;
            if (MessageDialog.Show(Owner, question, "FlashSync", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        var run = new CancellationTokenSource();
        _run = run;
        _log.Clear();
        RunPanel.Visibility = Visibility.Visible;
        RunPanel.BorderBrush = SyncColors.Blue;
        RunTitle.Foreground = (Brush)FindResource("ListTextBrush");
        RunBar.Foreground = SyncColors.Blue;
        RunBar.IsIndeterminate = true;
        RunNumbers.Text = RunCurrent.Text = "";
        UpdateButtons();
        var progress = new Progress<SyncProgress>(ShowProgress);
        var results = new List<SyncResult>();
        try
        {
            for (int i = 0; i < jobs.Count && !run.IsCancellationRequested; i++)
            {
                var job = jobs[i];
                int index = i + 1;
                _runningJob = job.Name;
                RunTitle.Text = jobs.Count > 1 ? $"Job {index} of {jobs.Count}: {job.Name}" : job.Name;
                ActiveChanged?.Invoke();
                if (jobs.Count > 1)
                    AddLog($"--- Job {index} of {jobs.Count}: {job.Name} ---");
                var runner = new SyncRunner();
                runner.Line += line => Dispatcher.BeginInvoke(() => AddLog(line));
                // On a worker thread: the log of a large folder is read and counted there, not on the window's thread.
                var task = Task.Run(() => runner.RunAsync(job, index, jobs.Count, progress, run.Token));
                _runTask = task;
                results.Add(await task);
                SaveJobs();
                RefreshRows();
            }
        }
        finally
        {
            _run = null;
            _runTask = null;
            _runningJob = "";
            run.Dispose();
        }

        // Lines of the last moments are still on their way to the log list: the end text comes after them.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        RunBar.IsIndeterminate = false;
        bool allDone = results.Count == jobs.Count && results.All(r => r.Status is SyncStatus.Done or SyncStatus.DoneWithErrors);
        RunBar.Value = allDone ? 1 : RunBar.Value;
        // The run's card takes the color of how it ended: green, orange (problems), red (failed), gray (stopped).
        var ended = results.Any(r => r.Status == SyncStatus.Failed) ? SyncStatus.Failed
            : results.Count < jobs.Count || results.Any(r => r.Status == SyncStatus.Cancelled) ? SyncStatus.Cancelled
            : results.Any(r => r.Status == SyncStatus.DoneWithErrors) ? SyncStatus.DoneWithErrors
            : SyncStatus.Done;
        var color = SyncColors.Of(ended);
        RunPanel.BorderBrush = color;
        RunBar.Foreground = color;
        RunTitle.Foreground = color;
        RunCurrent.Text = "";
        if (results.Count == 1 && jobs.Count == 1)
        {
            RunTitle.Text = $"{results[0].Job.Name}: {results[0].Summary}";
            RunNumbers.Text = results[0].Errors > 0 ? "The problems are in the list below and in the log file (Log)." : "";
        }
        else
        {
            int good = results.Count(r => r.Status == SyncStatus.Done), problems = results.Sum(r => r.Errors);
            RunTitle.Text = $"{results.Count} of {jobs.Count} jobs ran: {good} without problems" +
                            (problems > 0 ? $", {SyncRunner.Problems(problems)} in all" : "") +
                            (results.Any(r => r.Status == SyncStatus.Cancelled) ? " (stopped)" : "");
            RunNumbers.Text = "";
        }
        ShowJob(Selected);
        UpdateButtons();
        ActiveChanged?.Invoke();
    }

    /// <summary>The text of the question before a wipe: each folder that is emptied, what it holds, and what looks different from the last run.</summary>
    private static string WipeQuestion(IReadOnlyList<SyncJob> jobs)
    {
        var text = new StringBuilder();
        text.AppendLine(jobs.Count == 1
            ? "This folder is emptied before the copy. Everything in it is deleted for good (not to the Recycle Bin):"
            : "These folders are emptied before the copy. Everything in them is deleted for good (not to the Recycle Bin):");
        foreach (var job in jobs)
        {
            text.AppendLine();
            text.AppendLine(job.Destination + (jobs.Count > 1 ? $"     (job \"{job.Name}\")" : ""));
            var volume = SyncSafety.Volume(job.Destination);
            var (folders, files, more) = CountTop(job.Destination);
            string drive = volume is { } v ? $"Drive \"{(v.Label.Length > 0 ? v.Label : "no name")}\" ({v.Serial}). " : "";
            text.AppendLine(folders + files == 0
                ? $"     {drive}The folder is empty or not there yet."
                : $"     {drive}Now in it, at the top level: {Count(folders, more, "folder")} and {Count(files, more, "file")}.");
            if (volume is { } now && job.DestinationVolume.Length > 0 && now.Serial != job.DestinationVolume)
                text.AppendLine($"     ATTENTION: this is another drive than at the job's last run (then {job.DestinationVolume}).");
            if (folders + files > 0)
            {
                string? marker = SyncSafety.MarkerJobId(job.Destination);
                if (marker is null)
                    text.AppendLine("     ATTENTION: the folder has files, and this job has not written to it before.");
                else if (marker != job.Id)
                    text.AppendLine("     ATTENTION: this folder was written by another FlashSync job.");
            }
        }
        text.AppendLine();
        text.Append(jobs.Count == 1 ? "Delete everything in this folder and copy?" : "Delete everything in these folders and copy?");
        return text.ToString();
    }

    /// <summary>"1 folder", "12 folders", "2.000+ files".</summary>
    private static string Count(int count, bool more, string what) =>
        count == 1 && !more ? $"1 {what}" : $"{count:N0}{(more ? "+" : "")} {what}s";

    /// <summary>The folders and files directly in a folder (the count stops at 2000 of each).</summary>
    private static (int Folders, int Files, bool More) CountTop(string folder)
    {
        const int Limit = 2000;
        int folders = 0, files = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (entry is DirectoryInfo)
                    folders++;
                else if (!entry.Name.Equals(SyncSafety.MarkerName, StringComparison.OrdinalIgnoreCase))
                    files++;
                if (folders >= Limit || files >= Limit)
                    return (folders, files, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (folders, files, false);
    }

    private void ShowProgress(SyncProgress p)
    {
        if (!IsRunning)
            return;
        string job = p.JobCount > 1 ? $"Job {p.JobIndex} of {p.JobCount}: {p.Job}" : p.Job;
        string problems = p.Errors > 0 ? $"     {SyncRunner.Problems(p.Errors)}" : "";
        switch (p.Phase)
        {
            case SyncPhase.Wiping:
                RunTitle.Text = $"{job}  -  wiping the destination";
                RunBar.IsIndeterminate = true;
                RunNumbers.Text = $"{SyncRunner.Items(p.Deleted)} deleted{problems}";
                RunCurrent.Text = p.Current;
                break;
            case SyncPhase.Counting:
                RunTitle.Text = $"{job}  -  counting what is to copy";
                RunBar.IsIndeterminate = true;
                RunNumbers.Text = $"{p.FilesTotal:N0} files, {DisplayFormat.Size(p.BytesTotal)} so far{problems}";
                RunCurrent.Text = p.Source;
                break;
            case SyncPhase.Copying:
                RunTitle.Text = $"{job}  -  copying {p.Source}" + (p.SourceCount > 1 ? $"   (folder {p.SourceIndex} of {p.SourceCount})" : "");
                RunBar.IsIndeterminate = false;
                RunBar.Value = p.BytesTotal > 0 ? Math.Min(1, (double)p.BytesDone / p.BytesTotal)
                    : p.FilesTotal > 0 ? Math.Min(1, (double)p.FilesDone / p.FilesTotal) : 0;
                RunNumbers.Text = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files     {DisplayFormat.Size(p.BytesDone)} of {DisplayFormat.Size(p.BytesTotal)}" +
                                  $"     {DisplayFormat.Size((long)p.BytesPerSecond)}/s     {SyncRunner.Duration(p.Elapsed)}{problems}";
                RunCurrent.Text = p.Current;
                break;
            default:
                RunTitle.Text = $"{job}  -  starting";
                RunBar.IsIndeterminate = true;
                break;
        }
    }

    private void AddLog(string text)
    {
        const int Kept = 5000;
        if (_log.Count >= Kept)
        {
            for (int i = 0; i < 500; i++)
                _log.RemoveAt(0);
        }
        var line = new SyncLogLine(text, text.StartsWith("Problem:", StringComparison.Ordinal));
        _log.Add(line);
        RunLog.ScrollIntoView(line);
    }

    private void RefreshRows()
    {
        string? id = Selected?.Id;
        _rows.Clear();
        foreach (var job in _jobs)
            _rows.Add(new SyncJobRow(job));
        JobList.SelectedItem = _rows.FirstOrDefault(r => r.Job.Id == id) ?? _rows.FirstOrDefault();
    }

    /// <summary>
    /// The app is about to close. A running job is asked about; "yes" stops it and waits a moment, so the copy tool
    /// does not stay behind. False = do not close.
    /// </summary>
    public bool ConfirmExit(Window owner)
    {
        if (!IsRunning)
            return true;
        var answer = MessageDialog.Show(owner, $"The FlashSync job \"{_runningJob}\" is running.\n\nStop it and close DirectoryFable?",
            "FlashSync", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;
        Shutdown();
        return true;
    }

    /// <summary>Stops a running job and waits (at most a few seconds) until the copy tool has ended.</summary>
    public void Shutdown()
    {
        if (_run is not { } run)
            return;
        var task = _runTask;
        try
        {
            run.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            task?.Wait(4000);
        }
        catch (AggregateException)
        {
        }
    }

    // ---------- Editor ----------

    private async void OpenEditor(SyncJob job, bool isNew)
    {
        if (IsRunning)
            return;
        _editing = job;
        var editor = new SyncTreeEditor();
        _editor = editor;
        editor.Changed += ShowEditorLists;
        SourceTree.ItemsSource = editor.Roots;
        EditorHeader.Text = isNew ? "New job" : $"Edit: {job.Name}";
        NameBox.Text = job.Name;
        DestinationBox.Text = job.Destination;
        WipeBox.IsChecked = job.Wipe;
        NamesBox.Text = string.Join("\r\n", job.ExcludedNames);
        EditorProblem.Visibility = TreeHint.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Collapsed;
        EditorPage.Visibility = Visibility.Visible;
        UpdateButtons();
        ShowEditorLists();
        ShowWipeNote();
        // The editor was just made visible: the cursor can go into the Name box only after it is laid out.
        _ = Dispatcher.BeginInvoke(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        await editor.LoadAsync(job);
    }

    private void CloseEditor()
    {
        if (_editor is { } editor)
            editor.Changed -= ShowEditorLists;
        _editor = null;
        _editing = null;
        SourceTree.ItemsSource = null;
        EditorPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        UpdateButtons();
    }

    private void ShowEditorLists()
    {
        if (_editor is not { } editor)
            return;
        SourceRows.ItemsSource = editor.Sources.Select(path => new SyncPathRow(path)).ToList();
        ExcludedRows.ItemsSource = editor.Excluded.Select(path => new SyncPathRow(path)).ToList();
        SourcesCaption.Text = editor.Sources.Count == 0 ? "Folders to copy" : $"Folders to copy ({editor.Sources.Count})";
        ExcludedCaption.Text = editor.Excluded.Count == 0 ? "Left-out folders" : $"Left-out folders ({editor.Excluded.Count})";
        NoSourcesText.Visibility = editor.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoExcludedText.Visibility = editor.Excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TreeBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SyncTreeNode node })
        {
            e.Handled = true;
            ToggleNode(node);
        }
    }

    private void SourceTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && SourceTree.SelectedItem is SyncTreeNode node)
        {
            e.Handled = true;
            ToggleNode(node);
        }
    }

    private void ToggleNode(SyncTreeNode node)
    {
        if (_editor is not { } editor || node.IsPlaceholder)
            return;
        string? hint = editor.Toggle(node.Path);
        TreeHint.Text = hint ?? "";
        TreeHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PathRow_Remove(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor || sender is not FrameworkElement { Tag: SyncPathRow { Path: var path } } button)
            return;
        if (FindParent<ItemsControl>(button) == SourceRows)
            editor.RemoveSource(path);
        else
            editor.RemoveExclusion(path);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Destination folder of the job" };
        string current = DestinationBox.Text.Trim();
        if (SyncSafety.IsFullPath(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;
        if (dialog.ShowDialog(Owner) == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private void Destination_TextChanged(object sender, TextChangedEventArgs e) => ShowWipeNote();

    private void Wipe_Changed(object sender, RoutedEventArgs e) => ShowWipeNote();

    /// <summary>What the wipe option means for the typed destination, or why it cannot be used there.</summary>
    private void ShowWipeNote()
    {
        if (WipeNote is null || WipeBox is null || DestinationBox is null)
            return; // Still being built.
        string destination = DestinationBox.Text.Trim();
        if (WipeBox.IsChecked != true)
        {
            WipeNote.Text = "Off: a run copies new and changed files. Nothing is deleted in the destination.";
            WipeNote.Foreground = (Brush)FindResource("SecondaryTextBrush");
        }
        else if (destination.Length > 0 && SyncSafety.WipeProblem(destination) is { } problem)
        {
            WipeNote.Text = problem;
            WipeNote.Foreground = (Brush)FindResource("ErrorTextBrush");
        }
        else
        {
            WipeNote.Text = "On: each run deletes everything in the destination folder for good, then copies. FlashSync asks before each run.";
            WipeNote.Foreground = (Brush)FindResource("WarningBrush");
        }
    }

    private void EditorCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void EditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor || _editing is not { } job)
            return;
        string destination = DestinationBox.Text.Trim();
        string oldDestination = job.Destination;
        job.Name = NameBox.Text.Trim();
        job.Destination = SyncSafety.IsFullPath(destination) ? SyncSafety.Normalize(destination) : destination;
        job.Wipe = WipeBox.IsChecked == true;
        job.Sources = [.. editor.Sources];
        job.ExcludedFolders = [.. editor.Excluded];
        job.ExcludedNames = [.. NamesBox.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

        string? problem = SyncSafety.Problem(job);
        if (problem is null)
        {
            foreach (var other in _jobs.Where(j => j.Id != job.Id))
            {
                if (string.Equals(other.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    problem = "Another job has this name.";
                else if (SyncSafety.IsSameOrInside(job.Destination, other.Destination) || SyncSafety.IsSameOrInside(other.Destination, job.Destination))
                    problem = $"The job \"{other.Name}\" uses {other.Destination}. Give each job its own destination folder (not the same one, not one inside the other).";
                if (problem is not null)
                    break;
            }
        }
        if (problem is not null)
        {
            EditorProblem.Text = problem;
            EditorProblem.Visibility = Visibility.Visible;
            return;
        }

        // Another destination: the job has not written there yet (the first wipe there says so).
        if (!string.Equals(oldDestination, job.Destination, StringComparison.OrdinalIgnoreCase))
            job.DestinationVolume = "";
        int at = _jobs.FindIndex(j => j.Id == job.Id);
        if (at >= 0)
            _jobs[at] = job;
        else
            _jobs.Add(job);
        SaveJobs();
        CloseEditor();
        FillRows(job.Id);
        JobList.Focus();
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
